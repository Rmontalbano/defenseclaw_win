using System.Collections.Concurrent;
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
/// <para>Results are cached for the life of the process; <see cref="ReloadAsync"/> starts over.</para>
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

    public WizardCatalog(DefenseClawPaths paths)
    {
        _probe = new SetupHelpProbe(paths);
    }

    /// <summary>Raised (off the UI thread) when a definition is replaced by a richer one.</summary>
    public event EventHandler<WizardDefinitionChangedEventArgs>? DefinitionChanged;

    /// <summary>Null until the first load; otherwise why the top-level probe failed.</summary>
    public string? LoadError { get; private set; }

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
            return _shared ??= new WizardCatalog(services.Paths);
        }
    }

    /// <summary>Phase one: the roster, badged from the summary hints. Cached per app run.</summary>
    public Task<IReadOnlyList<WizardDefinition>> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (InstanceGate)
        {
            return _load ??= LoadCoreAsync(cancellationToken);
        }
    }

    /// <summary>Drops every cached definition and re-probes from scratch.</summary>
    public Task<IReadOnlyList<WizardDefinition>> ReloadAsync(CancellationToken cancellationToken = default)
    {
        lock (InstanceGate)
        {
            _definitions.Clear();
            _detailLoads.Clear();
            _load = null;
        }

        return LoadAsync(cancellationToken);
    }

    public WizardDefinition? Find(string target) =>
        _definitions.TryGetValue(target, out var definition) ? definition : null;

    /// <summary>
    /// Phase two for one target, on demand: parses its <c>--help</c> (and, for a group, every
    /// subcommand's) and rebuilds its pages. Idempotent and safe to call from the hub's
    /// background warm-up and from the wizard launcher at the same time.
    /// </summary>
    public async Task<WizardDefinition> EnsureDetailAsync(string target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);

        await _detailLoads
            .GetOrAdd(target, key => LoadDetailAsync(key, cancellationToken))
            .ConfigureAwait(false);

        return Find(target) ?? Placeholder(target);
    }

    private async Task<IReadOnlyList<WizardDefinition>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _probe.HelpAsync(Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                LoadError = result.Error;
                return Array.Empty<WizardDefinition>();
            }

            LoadError = null;
            var help = SetupHelpParser.Parse(result.Text);

            foreach (var command in help.Commands)
            {
                if (HiddenTargets.Contains(command.Name))
                {
                    continue;
                }

                _definitions[command.Name] = Stub(command);
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

    private async Task LoadDetailAsync(string target, CancellationToken cancellationToken)
    {
        var existing = Find(target);
        var result = await _probe.HelpAsync(new[] { target }, cancellationToken).ConfigureAwait(false);

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

            Replace(failed);
            return;
        }

        var help = SetupHelpParser.Parse(result.Text);
        IReadOnlyList<WizardStep> steps;
        var curated = false;

        if (help.HasSubcommands)
        {
            var subcommands = await LoadSubcommandsAsync(target, help, cancellationToken).ConfigureAwait(false);
            steps = WizardStepFactory.BuildGroup(help, subcommands);
        }
        else
        {
            (steps, curated) = WizardStepFactory.Build(target, help);
        }

        // The per-target help is authoritative for certification; the summary hint from the
        // top-level screen was only ever a stand-in until this landed.
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
        });
    }

    private async Task<IReadOnlyDictionary<string, ParsedHelp>> LoadSubcommandsAsync(
        string target,
        ParsedHelp help,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentDictionary<string, ParsedHelp>(StringComparer.Ordinal);

        var probes = help.Commands.Select(async command =>
        {
            var probe = await _probe
                .HelpAsync(new[] { target, command.Name }, cancellationToken)
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

    private void Replace(WizardDefinition definition)
    {
        _definitions[definition.Target] = definition;
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
