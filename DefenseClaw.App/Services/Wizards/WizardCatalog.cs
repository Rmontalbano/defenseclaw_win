using System.Collections.Concurrent;
using System.Diagnostics;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>Raised when a target's per-target help has landed and its card can be re-rendered.</summary>
public sealed class WizardDefinitionChangedEventArgs : EventArgs
{
    public WizardDefinitionChangedEventArgs(WizardDefinition definition)
    {
        Definition = definition;
    }

    public WizardDefinition Definition { get; }
}

/// <summary>
/// Every <c>defenseclaw setup</c> flow available on the installed CLI, discovered at runtime.
/// <para>
/// <b>Nothing here is a hard-coded list of targets.</b> The catalog reads
/// <c>defenseclaw setup --help</c>, takes the <c>Commands:</c> block as the roster, and
/// builds a <see cref="WizardDefinition"/> per entry — the same philosophy as the runtime-
/// generated config sections. A setup target added in a future 0.8.x shows up as a card the
/// day the CLI ships it, with generated fields, and can be promoted to a curated layout later
/// by editing <see cref="WizardStepFactory"/> alone.
/// </para>
/// <para>
/// <b>Two phases, because help probes are not free.</b> Phase one is a single top-level probe
/// (~0.8 s) that yields every target plus the certification hint Click prints in the command
/// summaries (<c>Cursor: not_certified on windows.</c>), so the hub can render complete and
/// correctly badged almost immediately. Phase two fans out one probe per target in the
/// background and replaces each definition with the fully parsed version — real description,
/// the authoritative <c>Platform status on windows:</c> line, and the option list the wizard
/// pages are built from. Groups additionally probe their subcommands, lazily, the first time
/// that wizard is opened.
/// </para>
/// <para>
/// Successful results are cached until <see cref="ReloadAsync"/>, which forgets everything —
/// including the probe's cached help screens, without which a Refresh would just re-serve the
/// text it already had. Failures are not kept: a target whose help could not be read is retried
/// the next time its wizard is opened, and a failed roster read is retried by Refresh. Loads
/// are never owned by the caller that started them (a cancelled first caller must not poison
/// the shared result); a caller's token only ends that caller's wait.
/// </para>
/// <para>
/// <b>The catalog describes one build of the CLI.</b> When the installed CLI changes under a running app — an
/// in-app upgrade (<see cref="NotifyCliChangedAsync"/>) or the gateway reporting a different binary version
/// (<see cref="ObserveBinaryVersionAsync"/>) — everything cached is forgotten and, if the catalog was in use,
/// re-read and re-warmed (<see cref="RefreshAfterCliChangeAsync"/>): otherwise an upgraded CLI's new targets
/// and flags would stay invisible until the operator found the Re-read catalog button.
/// </para>
/// </summary>
public sealed class WizardCatalog
{
    /// <summary>Targets that are plumbing rather than a configuration flow.</summary>
    private static readonly IReadOnlySet<string> HiddenTargets = new HashSet<string>(StringComparer.Ordinal)
    {
        // Nothing is hidden today. Kept as the one place to make that decision if the CLI
        // ever grows a setup entry that is not an operator-facing flow.
    };

    private static readonly object InstanceGate = new();
    private static WizardCatalog? _shared;

    private readonly SetupHelpProbe _probe;
    private readonly ConcurrentDictionary<string, WizardDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _detailLoads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private Task<IReadOnlyList<WizardDefinition>>? _load;

    // Bumped by ReloadAsync. A load that began before a reload describes a CLI the operator just
    // asked us to forget; it must not write its answer into the fresh catalog when it lands.
    private int _generation;

    // The last non-empty gateway BinaryVersion seen; a different one means the CLI was replaced.
    private string? _observedBinaryVersion;

    public WizardCatalog(DefenseClawPaths paths)
        : this(new SetupHelpProbe(paths))
    {
    }

    /// <summary>Test seam: a catalog over a probe whose CLI is a fake.</summary>
    internal WizardCatalog(SetupHelpProbe probe)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>Raised (off the UI thread) when a definition is replaced by a richer one.</summary>
    public event EventHandler<WizardDefinitionChangedEventArgs>? DefinitionChanged;

    /// <summary>Null until the first load; otherwise why the top-level probe failed.</summary>
    public string? LoadError { get; private set; }

    /// <summary>The help probe behind this catalog; the command palette reads its curated commands through it.</summary>
    internal SetupHelpProbe Probe => _probe;

    /// <summary>Help screens read so far — shown in the hub footer so the cost is visible.</summary>
    public int ProbeCount => _probe.CachedProbeCount;

    /// <summary>
    /// Process-wide instance. The catalog is a pure cache over CLI output with no per-window
    /// state, and a wizard opened from the hub must see the same definitions the hub does.
    /// </summary>
    public static WizardCatalog Shared(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (InstanceGate)
        {
            if (_shared is null)
            {
                var catalog = new WizardCatalog(services.Paths);

                // Seeded with what is running now, so only a later, different version counts as a change.
                _ = catalog.ObserveBinaryVersionAsync(services.Monitor.Current.BinaryVersion);
                services.Monitor.StateChanged += (_, e) => _ = catalog.ObserveBinaryVersionAsync(e.Snapshot.BinaryVersion);
                _shared = catalog;
            }

            return _shared;
        }
    }

    /// <summary>
    /// Tells the shared catalog, if one exists, that the installed CLI was just replaced. Does nothing when no
    /// wizard or Setup hub was ever opened: there is nothing cached to be stale, and the first load reads the new CLI.
    /// Never faults.
    /// </summary>
    public static Task NotifyCliChangedAsync()
    {
        WizardCatalog? catalog;
        lock (InstanceGate)
        {
            catalog = _shared;
        }

        return catalog?.RefreshAfterCliChangeAsync() ?? Task.CompletedTask;
    }

    /// <summary>
    /// Phase one: the roster, badged from the summary hints. Cached until
    /// <see cref="ReloadAsync"/>. <paramref name="cancellationToken"/> ends this caller's wait
    /// only; the shared load runs on for whoever else asks.
    /// </summary>
    public Task<IReadOnlyList<WizardDefinition>> LoadAsync(CancellationToken cancellationToken = default)
    {
        Task<IReadOnlyList<WizardDefinition>> load;
        lock (InstanceGate)
        {
            // Task.Run so the synchronous head of the load (locating and starting the CLI) runs
            // on the pool, not on the UI thread that asked and not inside this lock.
            var generation = _generation;
            load = _load ??= Task.Run(() => LoadCoreAsync(generation));
        }

        return cancellationToken.CanBeCanceled ? load.WaitAsync(cancellationToken) : load;
    }

    /// <summary>
    /// Drops every cached definition <b>and every cached help screen</b>, then re-probes from
    /// scratch. Clearing only the definitions would make this a no-op: the probe would answer
    /// the roster and every target from its own cache, and a CLI upgraded since the hub opened
    /// would still show yesterday's cards.
    /// </summary>
    public Task<IReadOnlyList<WizardDefinition>> ReloadAsync(CancellationToken cancellationToken = default)
    {
        Forget();
        return LoadAsync(cancellationToken);
    }

    private void Forget()
    {
        lock (InstanceGate)
        {
            _generation++;
            _definitions.Clear();
            _detailLoads.Clear();
            _probe.Clear();
            _load = null;
        }
    }

    /// <summary>
    /// The installed CLI is a different build than the one this catalog was read from. Forgets everything (as
    /// <see cref="ReloadAsync"/> does, help screens and the on-disk copy included); and, if the catalog had been
    /// loaded at all, reads the roster again and warms every target's detail in the background — the shared
    /// definitions must not be left as phase-one stubs that nothing would ever complete. Never faults: a failed
    /// re-read is the same "load error, retried by Refresh" the first read would have been.
    /// </summary>
    public async Task RefreshAfterCliChangeAsync()
    {
        try
        {
            bool inUse;
            lock (InstanceGate)
            {
                inUse = _load is not null;
            }

            if (!inUse)
            {
                Forget();
                return;
            }

            var definitions = await ReloadAsync().ConfigureAwait(false);
            await Task.WhenAll(definitions.Select(d => EnsureDetailAsync(d.Target))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Refreshing the setup catalog after a CLI change failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Feeds the gateway's reported binary version to the catalog. The first non-empty version is remembered; a
    /// later, different one means the CLI was replaced (an upgrade run outside this app, or a rollback), and the
    /// catalog is refreshed as by <see cref="RefreshAfterCliChangeAsync"/>. An empty version (gateway down) says
    /// nothing and is ignored. The returned task is complete when nothing needed doing.
    /// </summary>
    public Task ObserveBinaryVersionAsync(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Task.CompletedTask;
        }

        string? previous;
        lock (InstanceGate)
        {
            previous = _observedBinaryVersion;
            _observedBinaryVersion = version;
        }

        return previous is null || string.Equals(previous, version, StringComparison.Ordinal)
            ? Task.CompletedTask
            : RefreshAfterCliChangeAsync();
    }

    public WizardDefinition? Find(string target) =>
        _definitions.TryGetValue(target, out var definition) ? definition : null;

    /// <summary>
    /// Phase two for one target, on demand: parses its <c>--help</c> (and, for a group, every
    /// subcommand's) and rebuilds its pages. Idempotent and safe to call from the hub's
    /// background warm-up and from the wizard launcher at the same time.
    /// <para>
    /// A target whose help could not be read is <b>not remembered as failed</b>: the definition
    /// it leaves behind carries <see cref="WizardDefinition.DetailError"/> so the hub can say
    /// why, but the next call for that target probes again — opening the wizard is the natural
    /// moment to retry a timeout, and a "not on PATH" answer must not outlive the install that
    /// fixed it.
    /// </para>
    /// </summary>
    public async Task<WizardDefinition> EnsureDetailAsync(string target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);

        Task load;
        lock (InstanceGate)
        {
            // Under the gate so two callers cannot both start the load for one target, and so
            // the generation read here is the one ReloadAsync would clear this entry under.
            var generation = _generation;
            load = _detailLoads.GetOrAdd(target, key => Task.Run(() => LoadDetailAsync(key, generation)));
        }

        await (cancellationToken.CanBeCanceled ? load.WaitAsync(cancellationToken) : load).ConfigureAwait(false);

        var definition = Find(target) ?? Placeholder(target);
        if (definition.DetailError is not null)
        {
            _ = _detailLoads.TryRemove(new KeyValuePair<string, Task>(target, load));
        }

        return definition;
    }

    private async Task<IReadOnlyList<WizardDefinition>> LoadCoreAsync(int generation)
    {
        await _loadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var result = await _probe.HelpAsync(Array.Empty<string>()).ConfigureAwait(false);

            // A reload began while the probe ran: this answer belongs to a catalog that no longer
            // exists, and the load that replaced it will write the real one.
            if (generation != Volatile.Read(ref _generation))
            {
                return Array.Empty<WizardDefinition>();
            }

            if (!result.Succeeded)
            {
                LoadError = result.Error;
                return Array.Empty<WizardDefinition>();
            }

            LoadError = null;
            var help = SetupHelpParser.Parse(result.Text);

            lock (InstanceGate)
            {
                // Re-checked under the lock ReloadAsync clears under, so a reload cannot slip
                // between the check above and these writes and inherit stale stubs.
                if (generation != _generation)
                {
                    return Array.Empty<WizardDefinition>();
                }

                foreach (var command in help.Commands)
                {
                    if (HiddenTargets.Contains(command.Name))
                    {
                        continue;
                    }

                    _definitions[command.Name] = Stub(command);
                }
            }

            return Ordered();
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Phase-one definition: everything the top-level help already knows. The card is
    /// complete and correctly badged; only the pages are still a placeholder.
    /// </summary>
    private static WizardDefinition Stub(ParsedCommand command) => new()
    {
        Target = command.Name,
        Title = TitleFor(command.Name),
        Group = GroupFor(command.Name, command.Summary),
        Description = command.Summary,
        PlatformStatus = SetupHelpParser.StatusFromSummary(command.Summary),
        Steps = Array.Empty<WizardStep>(),
        IsDetailLoaded = false,
    };

    private static WizardDefinition Placeholder(string target) => new()
    {
        Target = target,
        Title = TitleFor(target),
        Group = WizardGroups.Other,
        Description = string.Empty,
        PlatformStatus = PlatformStatus.Unknown,
        DetailError = "This target is not present in the installed CLI's setup help.",
        IsDetailLoaded = true,
    };

    private async Task LoadDetailAsync(string target, int generation)
    {
        var existing = Find(target);
        var result = await _probe.HelpAsync(new[] { target }).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // A target whose help cannot be read gets a neutral badge, never an optimistic
            // one: "unknown" is an honest answer, "certified" would be a guess.
            var failed = new WizardDefinition
            {
                Target = target,
                Title = existing?.Title ?? TitleFor(target),
                Group = existing?.Group ?? WizardGroups.Other,
                Description = existing?.Description ?? string.Empty,
                PlatformStatus = PlatformStatus.Unknown,
                IsDetailLoaded = true,
                DetailError = result.Error,
            };

            Replace(failed, generation);
            return;
        }

        var help = SetupHelpParser.Parse(result.Text);
        IReadOnlyList<WizardStep> steps;
        var curated = false;

        if (help.HasSubcommands)
        {
            var subcommands = await LoadSubcommandsAsync(target, help).ConfigureAwait(false);
            steps = WizardStepFactory.BuildGroup(help, subcommands);
        }
        else
        {
            (steps, curated) = WizardStepFactory.Build(target, help);
        }

        // What this platform must not offer comes out first (a suppressed flag simply is not rendered —
        // the CLI's own default applies), then every secret-taking flag is given the route that tells the
        // operator where its value lives. A secret the CLI reads from its environment but has no flag for (galileo's
        // API key) is added between the two so it gets a route too. None of these steps needs the network or the CLI.
        steps = WizardWindowsPolicy.Filter(target, steps);
        steps = WizardSyntheticSecrets.Add(target, steps);
        steps = SecretRoutes.Annotate(target, steps);

        // The per-target help is authoritative for certification; the summary hint from the
        // top-level screen was only ever a stand-in until this landed. For a target that is not
        // a connector the parser reports NotApplicable rather than Certified (see
        // SetupHelpParser.ExtractPlatformStatus).
        Replace(new WizardDefinition
        {
            Target = target,
            Title = TitleFor(target),
            Group = GroupFor(target, help.Summary.Length > 0 ? help.Summary : existing?.Description ?? string.Empty),
            Description = help.Summary.Length > 0 ? help.Summary : existing?.Description ?? string.Empty,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            PlatformNote = help.PlatformNote,
            HelpText = result.Text,
            IsCurated = curated,
            IsDetailLoaded = true,
            CrossValidator = WizardWindowsPolicy.CrossValidatorFor(target),
        }, generation);
    }

    private async Task<IReadOnlyDictionary<string, ParsedHelp>> LoadSubcommandsAsync(
        string target,
        ParsedHelp help)
    {
        var results = new ConcurrentDictionary<string, ParsedHelp>(StringComparer.Ordinal);

        var probes = help.Commands.Select(async command =>
        {
            var probe = await _probe
                .HelpAsync(new[] { target, command.Name })
                .ConfigureAwait(false);

            // A subcommand whose help will not parse simply contributes no fields; the
            // operator can still select it and run it bare.
            results[command.Name] = probe.Succeeded
                ? SetupHelpParser.Parse(probe.Text, commandDepth: 2)
                : new ParsedHelp { Summary = command.Summary };
        });

        await Task.WhenAll(probes).ConfigureAwait(false);
        return results;
    }

    private void Replace(WizardDefinition definition, int generation)
    {
        lock (InstanceGate)
        {
            // Dropped, silently, when a reload happened since this load began: its help text came
            // from before the operator asked the catalog to forget, and writing it now would put a
            // stale card into the catalog the reload just rebuilt.
            if (generation != _generation)
            {
                return;
            }

            _definitions[definition.Target] = definition;
        }

        DefinitionChanged?.Invoke(this, new WizardDefinitionChangedEventArgs(definition));
    }

    private IReadOnlyList<WizardDefinition> Ordered() =>
        _definitions.Values
            .OrderBy(d => WizardGroups.IndexOf(d.Group))
            .ThenBy(d => d.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    /// <summary>Card title: the CLI noun, prettified, with the spellings operators recognise.</summary>
    private static string TitleFor(string target) => target switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        "cursor" => "Cursor",
        "windsurf" => "Windsurf",
        "geminicli" => "Gemini CLI",
        "copilot" => "GitHub Copilot CLI",
        "openhands" => "OpenHands",
        "antigravity" => "Antigravity",
        "opencode" => "OpenCode",
        "omnigent" => "OmniGent",
        "hermes" => "Hermes",
        "openclaw" => "OpenClaw",
        "zeptoclaw" => "ZeptoClaw",
        "mcp-scanner" => "MCP scanner",
        "skill-scanner" => "Skill scanner",
        "llm" => "LLM providers",
        "migrate-llm" => "Migrate LLM config",
        "local-observability" => "Local observability stack",
        "galileo" => "Galileo",
        "splunk" => "Splunk",
        "rotate-token" => "Rotate gateway token",
        "trusted-paths" => "Trusted binary paths",
        "notifications-set" => "Notification categories",
        _ => WizardFieldBuilder.Humanize(target),
    };

    /// <summary>
    /// Hub grouping. Connectors are recognised by name because the CLI does not label them:
    /// their summaries are the only hint, and a wrong group is a worse answer than a
    /// deliberate one. Unknown future targets land in <see cref="WizardGroups.Other"/>,
    /// which is exactly where an operator would look for something new.
    /// </summary>
    private static string GroupFor(string target, string summary) => target switch
    {
        "claude-code" or "codex" or "cursor" or "windsurf" or "geminicli" or "copilot" or
        "openhands" or "antigravity" or "opencode" or "omnigent" or "hermes" or "openclaw" or
        "zeptoclaw" or "remove" => WizardGroups.Connectors,

        "guardrail" or "trusted-paths" or "provider" or "registry" => WizardGroups.GuardrailAndPolicy,

        "skill-scanner" or "mcp-scanner" => WizardGroups.Scanners,

        "observability" or "local-observability" or "splunk" or "galileo" or "webhook" or
        "notifications" or "notifications-set" => WizardGroups.Observability,

        "llm" or "rotate-token" or "gateway" or "migrate-llm" => WizardGroups.Credentials,

        _ => summary.Contains("connector", StringComparison.OrdinalIgnoreCase)
            ? WizardGroups.Connectors
            : WizardGroups.Other,
    };
}
