using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The TUI command catalogues (CUST-282): the checked-in, generated copy of each runtime's <c>tui/registry_data.py</c>
/// (<c>tools/gen-tui-registry.py</c>). The generator reads the source as data and writes the entries; these tests hold the result to what
/// the registries are (231 and 253 entries, 210 and 232 of them run on Windows) and to the safety rules the palette relies on: every entry
/// has a review tier, none is a bare <c>defenseclaw-gateway</c> (which would start a second daemon), no option outside a reviewed set can
/// ride on an argv, and only a reviewed set of read-only entries is ever allowed to run without a review. The command trees of both
/// runtimes (the Click introspection fixtures) prove that every argv names a command, and every option on it, that the runtime has.
/// </summary>
public sealed class TuiRegistryCatalogueTests
{
    private static readonly TuiRegistryCatalogue Baseline = TuiRegistryCatalogues.Baseline;
    private static readonly TuiRegistryCatalogue Extended = TuiRegistryCatalogues.Extended;

    private static readonly string[] Categories = { "daemon", "enforce", "info", "install", "other", "policy", "sandbox", "scan", "setup" };

    public static TheoryData<string> Runtimes => new() { "baseline", "extended" };

    private static TuiRegistryCatalogue Of(string runtime) => runtime == "baseline" ? Baseline : Extended;

    // ------------------------------------------------------------------ counts

    [Fact]
    public void The_catalogues_hold_every_entry_of_the_two_registries_and_say_how_many_run_on_Windows()
    {
        // 0.8.10's registry_data.py has 231 tuples; the pinned source's has 253, of which its own build_registry("windows") keeps 232
        // (it drops the 18 sandbox entries and the three connectors Windows cannot run: openclaw, openhands, zeptoclaw).
        Assert.Equal(231, Baseline.Entries.Count);
        Assert.Equal(231, Baseline.SourceEntries);
        Assert.Equal(253, Extended.Entries.Count);
        Assert.Equal(253, Extended.SourceEntries);

        Assert.Equal(232, Extended.OnWindows.Count);
        Assert.Equal(21, Extended.HiddenOnWindows.Count);

        // 0.8.10 has no such filter in its TUI, but its CLI refuses the same things on Windows: the sandbox group, and `setup <connector>` for a
        // connector its own platform table calls unsupported (openclaw, zeptoclaw, openhands, omnigent) or not certified (seven more).
        Assert.Equal(210, Baseline.OnWindows.Count);
        Assert.Equal(21, Baseline.HiddenOnWindows.Count);

        foreach (var catalogue in new[] { Baseline, Extended })
        {
            Assert.Equal(catalogue.Entries.Count, catalogue.OnWindows.Count + catalogue.HiddenOnWindows.Count);
        }
    }

    [Fact]
    public void The_entries_Windows_does_not_run_are_exactly_the_ones_each_runtimes_own_platform_table_refuses()
    {
        var sandboxes = new[]
        {
            "sandbox init", "sandbox setup", "sandbox start", "sandbox stop", "sandbox restart", "sandbox status", "sandbox exec", "sandbox shell",
            "sandbox policy diff", "sandbox",
        };
        var connectors = new[]
        {
            "setup openclaw", "setup zeptoclaw", "setup hermes", "setup cursor", "setup windsurf", "setup geminicli", "setup copilot",
            "setup openhands", "setup antigravity", "setup opencode", "setup omnigent",
        };

        Assert.Equal(
            sandboxes.Concat(connectors).Order(StringComparer.Ordinal),
            Baseline.HiddenOnWindows.Select(e => e.Name).Order(StringComparer.Ordinal));

        var pinnedSandboxes = new[]
        {
            "sandbox status", "sandbox list", "sandbox doctor", "sandbox setup", "sandbox approvals", "sandbox activity", "sandbox policy explain",
            "sandbox pack list", "sandbox image list", "sandbox image build", "sandbox image rm", "sandbox review", "sandbox stop", "sandbox start",
            "sandbox delete", "sandbox unblock", "sandbox enable", "sandbox disable",
        };
        Assert.Equal(
            pinnedSandboxes.Concat(new[] { "setup openclaw", "setup zeptoclaw", "setup openhands" }).Order(StringComparer.Ordinal),
            Extended.HiddenOnWindows.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Each_hidden_entry_carries_the_runtimes_own_reason_and_nothing_else_does()
    {
        foreach (var catalogue in new[] { Baseline, Extended })
        {
            Assert.All(catalogue.HiddenOnWindows, e => Assert.False(string.IsNullOrWhiteSpace(e.WindowsUnavailable), e.Name));
            Assert.All(catalogue.OnWindows, e => Assert.Null(e.WindowsUnavailable));
        }

        string ReasonOf(TuiRegistryCatalogue c, string name) => c.Find(name)!.WindowsUnavailable!;

        Assert.Equal("Sandboxes run on Linux and macOS only.", ReasonOf(Baseline, "sandbox start"));
        Assert.Equal("Sandboxes run on Linux and macOS only.", ReasonOf(Extended, "sandbox delete"));
        Assert.Contains("guardrail proxy", ReasonOf(Baseline, "setup openclaw"), StringComparison.Ordinal);
        Assert.Contains("requires WSL", ReasonOf(Extended, "setup openhands"), StringComparison.Ordinal);
        Assert.Contains("certification", ReasonOf(Baseline, "setup cursor"), StringComparison.Ordinal);

        // The same connector the two runtimes disagree about: OmniGent runs on the pinned one, in degraded mode, and not on 0.8.10.
        Assert.NotNull(Baseline.Find("setup omnigent")!.WindowsUnavailable);
        Assert.Null(Extended.Find("setup omnigent")!.WindowsUnavailable);
        Assert.Null(Extended.Find("setup cursor")!.WindowsUnavailable);

        // What the runtimes' own platform tables call supported stays: Codex and Claude Code on both, the local stack controller on both.
        foreach (var catalogue in new[] { Baseline, Extended })
        {
            Assert.All(new[] { "setup codex", "setup claude-code", "setup local-observability up" }, n => Assert.Null(catalogue.Find(n)!.WindowsUnavailable));
        }
    }

    [Fact]
    public void The_hidden_entries_can_be_summed_up_by_reason_for_the_palettes_note()
    {
        var reasons = Extended.HiddenReasons;

        Assert.Equal(21, reasons.Sum(r => r.Count));
        Assert.Equal(("Sandboxes run on Linux and macOS only.", 18), reasons[0]);
        Assert.Equal(reasons.Select(r => r.Reason).Distinct(StringComparer.Ordinal).Count(), reasons.Count);
        Assert.Equal(21, Baseline.HiddenReasons.Sum(r => r.Count));
    }

    // ------------------------------------------------------------------ shape

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Every_entry_has_the_registry_shape(string runtime)
    {
        var catalogue = Of(runtime);

        Assert.Equal(catalogue.Entries.Count, catalogue.Entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalogue.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name), e.Name);
            Assert.False(string.IsNullOrWhiteSpace(e.Description), e.Name);
            Assert.Contains(e.Category, Categories);
            Assert.NotEmpty(e.Argv);
            Assert.All(e.Argv, token => Assert.False(string.IsNullOrEmpty(token), e.Name));

            // The TUI asks for more text exactly when it has a hint for it.
            Assert.Equal(e.NeedsArgument, e.ArgumentHint.Length > 0);
            Assert.Equal(e.Binary == TuiBinary.Gateway ? "defenseclaw-gateway" : "defenseclaw", e.Executable);
        });
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Registry_names_that_share_one_command_are_all_kept(string runtime)
    {
        // The TUI keeps its aliases so that the names operators know still work; the palette offers every one.
        var catalogue = Of(runtime);
        var doctor = catalogue.Entries.Where(e => e.Binary == TuiBinary.Cli && e.Argv.SequenceEqual(new[] { "doctor" })).Select(e => e.Name);

        Assert.Equal(new[] { "doctor", "doctor run", "readiness" }, doctor);
        Assert.Equal(new[] { "list skills", "skill list", "skills" }, catalogue.Entries.Where(e => e.Argv.SequenceEqual(new[] { "skill", "list" })).Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void No_entry_is_a_bare_gateway_and_none_starts_with_an_option(string runtime)
    {
        // `defenseclaw-gateway` with no arguments starts a second sidecar daemon. Nothing a catalogue holds may be that.
        var catalogue = Of(runtime);
        var gateway = catalogue.Entries.Where(e => e.Binary == TuiBinary.Gateway).ToArray();

        Assert.NotEmpty(gateway);
        Assert.All(gateway, e =>
        {
            Assert.NotEmpty(e.Argv);
            Assert.False(e.Argv[0].StartsWith('-'), e.Name);
            Assert.Matches("^[a-z][a-z-]*$", e.Argv[0]);
        });

        // The CLI's only argv that opens with an option is the TUI's `help` entry (`defenseclaw --help`).
        Assert.Equal(new[] { "help" }, catalogue.Entries.Where(e => e.Argv[0].StartsWith('-')).Select(e => e.Name));
        Assert.Equal(TuiBinary.Cli, catalogue.Find("help")!.Binary);
    }

    [Fact]
    public void The_gateway_entries_are_the_ones_the_TUI_runs_on_defenseclaw_gateway()
    {
        var visible = new[]
        {
            "scan code", "policy evaluate", "policy evaluate-firewall", "policy reload", "policy domains", "start", "stop", "restart", "gateway status",
            "watchdog start", "watchdog stop", "watchdog status", "connector verify", "connector teardown", "connector list-backups",
            "gateway audit export", "gateway provenance show", "restart gateway",
        };

        foreach (var catalogue in new[] { Baseline, Extended })
        {
            Assert.Equal(
                visible.Order(StringComparer.Ordinal),
                catalogue.OnWindows.Where(e => e.Binary == TuiBinary.Gateway).Select(e => e.Name).Order(StringComparer.Ordinal));
        }

        Assert.Equal(7, Baseline.HiddenOnWindows.Count(e => e.Binary == TuiBinary.Gateway));
        Assert.Equal(0, Extended.HiddenOnWindows.Count(e => e.Binary == TuiBinary.Gateway));
    }

    // ------------------------------------------------------------------ what may ride on an argv

    private static readonly string[] SecretFragments = { "key", "token", "secret", "password", "credential", "value" };

    [Fact]
    public void The_reviewed_options_name_no_credential_the_way_the_TUIs_own_test_would()
    {
        // The TUI treats a flag as secret-bearing when its name contains one of these. None of the reviewed options does, so none can
        // carry one, and a regenerated registry that adds such a flag cannot get past the check below.
        Assert.All(TuiRegistryCatalogues.ReviewedFlags, flag =>
        {
            Assert.StartsWith("--", flag, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretFragments, fragment => flag.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        });
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Every_argv_passes_the_hygiene_check_and_carries_only_reviewed_options(string runtime)
    {
        var catalogue = Of(runtime);

        Assert.All(catalogue.Entries, e =>
        {
            Assert.Null(TuiRegistryCatalogues.ArgvProblem(e.Argv));
            Assert.DoesNotContain("--", e.Argv);
            Assert.All(e.Argv.Where(a => a.StartsWith('-')), flag => Assert.Contains(flag, TuiRegistryCatalogues.ReviewedFlags));
        });

        // The catalogues use all of the reviewed options between them, and no more than that: the set is not wider than what is needed.
        var used = Baseline.Entries.Concat(Extended.Entries).SelectMany(e => e.Argv).Where(a => a.StartsWith('-')).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(TuiRegistryCatalogues.ReviewedFlags.Order(StringComparer.Ordinal), used.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("--token")]
    [InlineData("--api-key")]
    [InlineData("--api-key=abc")]
    [InlineData("--value")]
    [InlineData("--client-secret")]
    [InlineData("--password")]
    [InlineData("--show-credentials")]
    [InlineData("--reveal")]
    [InlineData("--frobnicate")]
    [InlineData("-x")]
    public void An_option_nobody_reviewed_is_refused_wherever_it_stands(string option)
    {
        Assert.NotNull(TuiRegistryCatalogues.ArgvProblem(new[] { "setup", "llm", option }));
        Assert.NotNull(TuiRegistryCatalogues.ArgvProblem(new[] { option }));
    }

    [Theory]
    [InlineData("doctor; calc")]
    [InlineData("a|b")]
    [InlineData("a b")]
    [InlineData("$env:X")]
    [InlineData("%PATH%")]
    [InlineData("`x`")]
    [InlineData("a&b")]
    [InlineData("a*")]
    [InlineData("~")]
    [InlineData("")]
    public void A_word_with_shell_syntax_or_a_space_in_it_is_refused(string token)
    {
        Assert.NotNull(TuiRegistryCatalogues.ArgvProblem(new[] { "skill", token }));
    }

    [Fact]
    public void Nothing_to_run_is_refused_and_the_reviewed_shapes_pass()
    {
        Assert.NotNull(TuiRegistryCatalogues.ArgvProblem(Array.Empty<string>()));
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(new[] { "doctor" }));
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(new[] { "doctor", "--fix", "--yes" }));
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(new[] { "config", "show", "--effective", "--section", "observability" }));
        Assert.Null(TuiRegistryCatalogues.ArgvProblem(new[] { "--help" }));
    }

    // ------------------------------------------------------------------ the tier of every entry

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Every_entry_has_a_tier_and_what_the_classifier_does_not_know_is_a_change(string runtime)
    {
        var catalogue = Of(runtime);

        Assert.All(catalogue.Entries, e => Assert.True(Enum.IsDefined(CommandTiers.Classify(e.Argv)), e.Name));

        // Unknown is StateChanging: a bare group (`skill`), a noun nobody listed (`alerts`, `config path`) and a verb the vocabulary lacks.
        Assert.All(new[] { "skill", "alerts", "config path", "audit", "setup" }, name =>
            Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(catalogue.Find(name)!.Argv)));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(new[] { "frobnicate" }));

        // The verbs that end something are destructive whatever else is on the line.
        Assert.All(new[] { "quarantine skill", "remove plugin", "uninstall --all --yes", "reset --yes", "connector teardown", "alerts dismiss", "policy delete" }, name =>
            Assert.Equal(CommandTier.Destructive, CommandTiers.Classify(catalogue.Find(name)!.Argv)));
    }

    /// <summary>
    /// Every entry the classifier calls read-only, by name. Each one's description was read: it lists, shows, checks, validates or previews. A
    /// regenerated registry that adds an entry the classifier would call a read must be looked at before it is added here.
    /// </summary>
    private static readonly string[] ReadOnlyBoth =
    {
        "agent discovery status", "agent signatures list", "config show", "config show effective observability", "config validate",
        "connector list-backups", "connector verify", "doctor",
        "doctor run", "gateway provenance show", "gateway status", "guardrail status", "help", "info plugin", "info skill", "keys check",
        "keys list", "keys list --json", "list mcps", "list plugins", "list skills", "list tools", "mcp list", "mcps", "observability plan",
        "plugin info", "plugin list", "plugins", "policy domains", "policy list", "policy show", "policy validate", "readiness", "setup local-observability logs",
        "setup local-observability status", "setup local-observability url", "skill info", "skill list",
        "skill search", "skills", "status", "tool list", "tool status", "tools", "uninstall dry-run", "version", "watchdog status",
    };

    [Fact]
    public void The_entries_the_classifier_calls_read_only_are_exactly_the_reviewed_ones()
    {
        // 0.8.10's `sandbox status` (a gateway verb) is also a read, and hidden on Windows.
        Assert.Equal(
            ReadOnlyBoth.Append("sandbox status").Order(StringComparer.Ordinal),
            ReadOnlyNames(Baseline));

        // The pinned source's reads: the same, and the guardrail pack list and four sandbox reads, which Windows does not run.
        Assert.Equal(
            ReadOnlyBoth.Concat(new[] { "guardrail protection list", "sandbox doctor", "sandbox image list", "sandbox list", "sandbox pack list", "sandbox status" }).Order(StringComparer.Ordinal),
            ReadOnlyNames(Extended));
    }

    private static string[] ReadOnlyNames(TuiRegistryCatalogue catalogue) =>
        catalogue.Entries.Where(e => CommandTiers.Classify(e.Argv) == CommandTier.ReadOnly).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();

    private static bool OnTheAllowList(TuiRegistryEntry e) =>
        e.Binary == TuiBinary.Gateway ? CommandTiers.IsUnreviewedGatewayRead(e.Argv) : CommandTiers.IsUnreviewedRead(e.Argv);

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Only_a_listed_read_may_run_without_a_review_and_the_list_is_the_reviewed_one(string runtime)
    {
        var catalogue = Of(runtime);
        var listed = catalogue.Entries.Where(OnTheAllowList).ToArray();

        // Nothing that changes anything is listed, however its name reads.
        Assert.All(listed, e => Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(e.Argv)));

        // And the listed ones are these: bare reads on the allow-list (the gateway's are `status`, `provenance show` and the four the palette
        // adds, each read from the 0.8.10 gateway's help: `watchdog status`, `connector verify`, `connector list-backups`, `policy domains`).
        // An entry with an option on it (`keys list --json`) is a read that is still reviewed.
        var expected = new[]
        {
            "agent discovery status", "agent signatures list", "config show", "config validate", "connector list-backups", "connector verify",
            "doctor", "doctor run", "gateway provenance show",
            "gateway status", "guardrail status", "info plugin", "info skill", "keys check", "keys list", "list mcps", "list plugins", "list skills",
            "list tools", "mcp list", "mcps", "observability plan", "plugin info", "plugin list", "plugins", "policy domains", "policy list", "policy show",
            "policy validate", "readiness", "setup local-observability logs", "setup local-observability status", "setup local-observability url",
            "skill info", "skill list", "skill search", "skills", "status", "tool list", "tool status", "tools", "version", "watchdog status",
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal), listed.Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Reads_that_take_a_target_are_listed_bare_and_reviewed_once_the_target_is_on_them(string runtime)
    {
        // `skill info <name>` etc. take a target and so are reviewed even though the bare verb is on the list: the palette adds the target
        // after `--`, and an argv with a target on it is never on the list. Each of these entries' bare argv IS listed, and needs an argument.
        var catalogue = Of(runtime);
        var needTarget = catalogue.Entries.Where(e => e.NeedsArgument && OnTheAllowList(e)).Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "info plugin", "info skill", "plugin info", "policy show", "skill info", "skill search", "tool status" }, needTarget);
        Assert.All(needTarget, name => Assert.False(CommandTiers.IsUnreviewedRead(catalogue.Find(name)!.Argv.Append("--").Append("x").ToArray())));
    }

    // ------------------------------------------------------------------ the runtime's own command tree

    private sealed record TreeOption(string[] Names, bool Flag);

    private sealed record TreeCommand(string Path, bool Group, TreeOption[] Options)
    {
        /// <summary>
        /// True when the command has the option. A Click on/off switch (<c>--tui/--no-tui</c>) has two spellings, and the command-tree fixture
        /// of the newer source lists only the first of this one (the source says <c>--tui/--no-tui</c>), so a <c>--no-x</c> counts when
        /// <c>--x</c> is a switch of the command.
        /// </summary>
        public bool HasOption(string name) =>
            Options.Any(o => o.Names.Contains(name, StringComparer.Ordinal)) ||
            (name.StartsWith("--no-", StringComparison.Ordinal) && Options.Any(o => o.Flag && o.Names.Contains("--" + name[5..], StringComparer.Ordinal)));
    }

    private static Dictionary<string, TreeCommand> Tree(string fixture)
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText(fixture));
        return document.RootElement.GetProperty("commands").EnumerateArray()
            .Select(c => new TreeCommand(
                c.GetProperty("path").GetString()!,
                c.GetProperty("group").GetBoolean(),
                c.GetProperty("options").EnumerateArray()
                    .Select(o => new TreeOption(o.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray(), o.GetProperty("flag").GetBoolean()))
                    .ToArray()))
            .ToDictionary(c => c.Path, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("baseline", "cli-tree-0.8.10.json")]
    [InlineData("extended", "runtime-95159fd/cli/cli-tree.json")]
    public void Every_CLI_entry_names_a_command_of_its_runtime_and_only_options_that_command_has(string runtime, string fixture)
    {
        var tree = Tree(fixture);
        var problems = new List<string>();

        foreach (var entry in Of(runtime).Entries.Where(e => e.Binary == TuiBinary.Cli))
        {
            var nouns = entry.Argv.TakeWhile(a => !a.StartsWith('-')).ToArray();
            var options = entry.Argv.Where(a => a.StartsWith('-')).ToArray();

            // The root command: `defenseclaw --help`.
            if (nouns.Length == 0)
            {
                continue;
            }

            // The longest run of leading words that is a command; words after it must be positionals of a leaf (`config reference observability`).
            var length = nouns.Length;
            while (length > 0 && !tree.ContainsKey(string.Join(' ', nouns.Take(length))))
            {
                length--;
            }

            if (length == 0)
            {
                problems.Add($"{entry.Name}: no command '{nouns[0]}'");
                continue;
            }

            var command = tree[string.Join(' ', nouns.Take(length))];
            if (length < nouns.Length && command.Group)
            {
                problems.Add($"{entry.Name}: '{string.Join(' ', nouns)}' is not a command under the group '{command.Path}'");
                continue;
            }

            foreach (var option in options.Where(o => !command.HasOption(o)))
            {
                problems.Add($"{entry.Name}: '{command.Path}' has no option {option}");
            }
        }

        Assert.Empty(problems);
    }

    // ------------------------------------------------------------------ choosing the catalogue

    private static RuntimeCapabilities Capabilities(string set) =>
        RuntimeProbe.Evaluate(RuntimeCapabilityTests.Screens(set), "defenseclaw.exe", "fp", DateTimeOffset.UnixEpoch).Capabilities;

    [Fact]
    public void A_runtime_with_the_larger_registry_gets_it_and_every_other_runtime_gets_the_one_0_8_10_has()
    {
        Assert.Same(Extended, TuiRegistryCatalogues.For(Capabilities("95159fd")));

        // 0.8.10: the installed baseline. Nothing probed yet, a runtime that did not answer: also the baseline, because what 0.8.10 has is never gated.
        Assert.Same(Baseline, TuiRegistryCatalogues.For(Capabilities("0.8.10")));
        Assert.Same(Baseline, TuiRegistryCatalogues.For(RuntimeCapabilities.Unknown));
        Assert.Same(Baseline, TuiRegistryCatalogues.For(null));
    }

    [Fact]
    public void The_generated_header_records_where_each_registry_came_from()
    {
        Assert.Contains("0.8.10", Baseline.Runtime, StringComparison.Ordinal);
        Assert.Contains("95159fd", Extended.Runtime, StringComparison.Ordinal);
        Assert.Equal("defenseclaw/tui/registry_data.py", Baseline.Source);
        Assert.Equal("cli/defenseclaw/tui/registry_data.py", Extended.Source);
        Assert.Matches("^[0-9a-f]{64}$", Baseline.SourceSha256);
        Assert.Matches("^[0-9a-f]{64}$", Extended.SourceSha256);
        Assert.NotEqual(Baseline.SourceSha256, Extended.SourceSha256);
        Assert.Contains(RuntimeCapabilityCatalog.VerifiedCommit, Extended.Runtime, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_file_says_it_is_generated_and_holds_no_local_path()
    {
        var path = Path.Combine(FindRepositoryRoot(), "DefenseClaw.Core", "Cli", "TuiRegistryData.Generated.cs");
        var text = File.ReadAllText(path);

        Assert.StartsWith("// <auto-generated>", text, StringComparison.Ordinal);
        Assert.Contains("tools/gen-tui-registry.py", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@":\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(FindRepositoryRoot(), "tools", "gen-tui-registry.py")));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DefenseClaw.Win.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"DefenseClaw.Win.sln was not found above {AppContext.BaseDirectory}.");
    }
}
