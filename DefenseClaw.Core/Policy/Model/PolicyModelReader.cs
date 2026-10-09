using DefenseClaw.Core.Cli;

namespace DefenseClaw.Core.Policy.Model;

/// <summary>What one read of the catalog produced.</summary>
/// <param name="Catalog">The catalog: every part that was read, and for a part that was not, what was there before (if anything) with the part's error set.</param>
/// <param name="Problems">One sentence for each part that could not be read; empty for a complete read. A read with problems is a partial read: nothing may be changed on the strength of it.</param>
/// <param name="ReadAnything">True when at least one part was read now.</param>
public sealed record PolicyCatalogRead(PolicyCatalog Catalog, IReadOnlyList<string> Problems, bool ReadAnything)
{
    /// <summary>True when every part was read.</summary>
    public bool IsComplete => Problems.Count == 0;
}

/// <summary>
/// Reads the catalog of the pinned runtime's Policies model with the read-only commands it has for it (no Python bridge; see
/// <c>docs/RUNTIME-COMPAT-95159fd.md</c>): <c>policy list --json</c>, <c>guardrail list-packs --json</c>, <c>guardrail protection list
/// --json</c> and <c>config show --section guardrail --format json</c>, then the runtime's own data files for the three things no command
/// prints (<see cref="IPolicyDataFiles"/>). Each part has its own error, as the runtime's catalog has: one part failing empties its own view
/// and leaves the rest, and the read is then reported as partial. Nothing here asks about sandboxes: the model has no view for them.
/// <para>
/// <b>Runs only reads.</b> Every argv is checked against <see cref="PolicyActionGuard.IsAllowedRead"/> before it reaches the runner, so a bug in
/// the composition cannot make this class start anything else. <b>The runner is called on the caller's context</b>, at most
/// <see cref="MaxParallelReads"/> at a time, because the app's runner raises its Activity events on the thread that calls it.
/// </para>
/// </summary>
public sealed class PolicyModelReader
{
    /// <summary>How many reads run at once (each is a Python start-up).</summary>
    public const int MaxParallelReads = 3;

    /// <summary>Runs one read-only <c>defenseclaw</c> command.</summary>
    public delegate Task<CliInvocation> Runner(IReadOnlyList<string> argv, CancellationToken cancellationToken);

    private readonly Runner _run;
    private readonly IPolicyDataFiles _files;

    public PolicyModelReader(Runner runner, IPolicyDataFiles? files = null)
    {
        _run = runner ?? throw new ArgumentNullException(nameof(runner));
        _files = files ?? FileSystemPolicyData.Instance;
    }

    private sealed record Part(bool Ok, string Stdout, string Problem);

    /// <summary>Reads the whole catalog. <paramref name="previous"/> supplies the rows of a part that fails, so an old row is never mistaken for a new one (the caller shows the problem).</summary>
    /// <exception cref="CliNotFoundException">The <c>defenseclaw</c> CLI is not installed.</exception>
    public async Task<PolicyCatalogRead> ReadAsync(PolicyCatalog? previous = null, CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(MaxParallelReads, MaxParallelReads);
        var policiesTask = RunAsync(gate, PolicyActionGuard.CatalogReads[0], cancellationToken);
        var packsTask = RunAsync(gate, PolicyActionGuard.CatalogReads[1], cancellationToken);
        var protectionTask = RunAsync(gate, PolicyActionGuard.CatalogReads[2], cancellationToken);
        var configTask = RunAsync(gate, PolicyActionGuard.CatalogReads[3], cancellationToken);
        await Task.WhenAll(policiesTask, packsTask, protectionTask, configTask).ConfigureAwait(true);

        var problems = new List<string>();
        var read = false;

        // ---- named policies ----
        var policies = previous?.Policies ?? Array.Empty<NamedPolicy>();
        var policiesError = string.Empty;
        var policiesPart = policiesTask.Result;
        IReadOnlyList<NamedPolicy> parsedPolicies = Array.Empty<NamedPolicy>();
        var policiesParseError = string.Empty;
        if (policiesPart.Ok && PolicyCatalogJson.TryParsePolicies(policiesPart.Stdout, out parsedPolicies, out policiesParseError))
        {
            policies = parsedPolicies;
            read = true;
        }
        else
        {
            policiesError = policiesPart.Ok ? policiesParseError : policiesPart.Problem;
            problems.Add(policiesError);
        }

        // ---- rule packs ----
        ListPacksDocument? packsDoc = null;
        var packError = string.Empty;
        var packsPart = packsTask.Result;
        var parsedPacks = new ListPacksDocument(null, Array.Empty<ScopePack>(), Array.Empty<RulePackEntry>());
        var packsParseError = string.Empty;
        if (packsPart.Ok && PolicyCatalogJson.TryParseListPacks(packsPart.Stdout, out parsedPacks, out packsParseError))
        {
            packsDoc = parsedPacks;
            read = true;
        }
        else
        {
            packError = packsPart.Ok ? packsParseError : packsPart.Problem;
            problems.Add(packError);
        }

        // ---- scope postures, opt-in packs ----
        ProtectionDocument? protectionDoc = null;
        GuardrailSettings? settings = null;
        var postureError = string.Empty;
        var protectionPart = protectionTask.Result;
        var parsedProtection = new ProtectionDocument(Array.Empty<ProtectionPack>(), Array.Empty<ProtectionScope>());
        var protectionParseError = string.Empty;
        if (protectionPart.Ok && PolicyCatalogJson.TryParseProtection(protectionPart.Stdout, out parsedProtection, out protectionParseError))
        {
            protectionDoc = parsedProtection;
        }
        else
        {
            postureError = protectionPart.Ok ? protectionParseError : protectionPart.Problem;
        }

        var configPart = configTask.Result;
        var parsedSettings = new GuardrailSettings("observe", false, "HIGH", string.Empty, string.Empty, new Dictionary<string, ConnectorGuardrailSettings>(StringComparer.Ordinal));
        var configParseError = string.Empty;
        if (configPart.Ok && PolicyCatalogJson.TryParseGuardrailSettings(configPart.Stdout, out parsedSettings, out configParseError))
        {
            settings = parsedSettings;
        }
        else if (postureError.Length == 0)
        {
            postureError = configPart.Ok ? configParseError : configPart.Problem;
        }

        IReadOnlyList<ScopePosture> postures = previous?.Postures ?? Array.Empty<ScopePosture>();
        IReadOnlyList<ProtectionPack> protection = previous?.Protection ?? Array.Empty<ProtectionPack>();
        if (packsDoc is not null && protectionDoc is not null && settings is not null)
        {
            postures = PolicyPostureComposer.Compose(packsDoc, protectionDoc, settings, out var composeIssues);
            protection = protectionDoc.Packs;
            read = true;
            if (composeIssues.Count > 0)
            {
                postureError = string.Join(" ", composeIssues);
            }
        }
        else if (postureError.Length == 0)
        {
            postureError = "The scopes cannot be built without the rule pack list.";
        }

        if (postureError.Length > 0)
        {
            problems.Add(postureError);
        }

        // ---- the runtime's own data files: chains, rule families, composed packs' bases ----
        var folders = postures.Select(p => p.PackFolder).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        var data = await Task.Run(
            () =>
            {
                var chains = _files.ReadToolChains(policies);
                var families = new Dictionary<string, IReadOnlyList<RuleFamily>>(StringComparer.Ordinal);
                var bases = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var folder in folders)
                {
                    families[folder] = _files.ReadRuleFamilies(folder, policies);
                    if (_files.ReadPackBase(folder) is { } baseName)
                    {
                        bases[folder] = baseName;
                    }
                }

                return (chains, families, bases);
            },
            cancellationToken).ConfigureAwait(true);

        var catalog = new PolicyCatalog
        {
            Policies = policies,
            PoliciesError = policiesError,
            GlobalPack = packsDoc?.Global ?? previous?.GlobalPack,
            ConnectorPacks = packsDoc?.Connectors ?? previous?.ConnectorPacks ?? Array.Empty<ScopePack>(),
            Packs = packsDoc?.Packs ?? previous?.Packs ?? Array.Empty<RulePackEntry>(),
            PackError = packError,
            Postures = postures,
            Protection = protection,
            Families = data.families,
            Chains = data.chains,
            PackBases = data.bases,
            PostureError = postureError,
            MultiConnector = settings is not null ? settings.Connectors.Count > 0 : previous?.MultiConnector ?? false,
            PolicyFolder = PolicyFolderOf(packsDoc) ?? previous?.PolicyFolder ?? string.Empty,
        };

        return new PolicyCatalogRead(catalog, problems, read);
    }

    /// <summary>The policy folder (where composed packs go): the parent of the <c>guardrail</c> folder the preset packs live in.</summary>
    private static string? PolicyFolderOf(ListPacksDocument? packs)
    {
        if (packs is null)
        {
            return null;
        }

        foreach (var folder in packs.Packs.Where(p => p.IsPreset).Select(p => p.Folder).Concat(packs.Global is { } g ? new[] { g.Folder } : Array.Empty<string>()))
        {
            if (!Path.IsPathFullyQualified(folder))
            {
                continue;
            }

            var guardrail = Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (guardrail is not null && string.Equals(Path.GetFileName(guardrail), "guardrail", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(guardrail);
            }
        }

        return null;
    }

    private async Task<Part> RunAsync(SemaphoreSlim? gate, IReadOnlyList<string> argv, CancellationToken cancellationToken)
    {
        if (!PolicyActionGuard.IsAllowedRead(argv))
        {
            throw new InvalidOperationException($"'{string.Join(' ', argv)}' is not a read this reader may run.");
        }

        if (gate is not null)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        }

        try
        {
            var command = "defenseclaw " + string.Join(' ', argv);
            var invocation = await _run(argv, cancellationToken).ConfigureAwait(true);
            var stdout = string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));
            var stderr = string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardError).Select(l => l.Text));

            if (invocation.FailureReason is { Length: > 0 } failure)
            {
                return new Part(false, stdout, $"'{command}' did not complete: {failure}");
            }

            if (invocation.ExitCode != 0)
            {
                return new Part(false, stdout, $"'{command}' failed: {Summarize(invocation, stderr, stdout)}");
            }

            if (invocation.IsOutputTruncated)
            {
                return new Part(false, stdout, $"'{command}' printed more than this app keeps for one command.");
            }

            return new Part(true, stdout, string.Empty);
        }
        finally
        {
            _ = gate?.Release();
        }
    }

    private static string Summarize(CliInvocation invocation, string stderr, string stdout)
    {
        var text = LastLine(stderr);
        if (text.Length == 0)
        {
            text = LastLine(stdout);
        }

        return text.Length > 0 ? text : $"Exit code {invocation.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}.";
    }

    private static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? string.Empty;
}
