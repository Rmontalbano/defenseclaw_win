using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The one gate (<see cref="InstallationGate"/>, enforced by <see cref="CliRunner"/>): while the installation is not writable a run is allowed
/// exactly when its tier is read-only, held against the whole DefenseClaw 0.8.10 command tree and a table of the shapes that matter; a refusal
/// is a finished Activity entry with a fixed exit code and a sentence, nothing is started, and the installation's identity reaches every
/// DefenseClaw child last. Children here are copies of <c>cmd.exe</c> (the real CLI is never run); every value is synthetic.
/// </summary>
public sealed class InstallationGateTests
{
    private const string Reason = "This installation is administrator managed. Use enterprise deployment tooling to change it.";

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static InstallationContext Managed() => new InstallationMachine().WithSecureClientLayout().Resolve();

    private static InstallationContext Invalid() => new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", "relative").Resolve();

    private static InstallationContext Writable() => InstallationContext.Unmanaged(@"C:\data\.defenseclaw");

    private static CliRunner Runner(TempDirectory temp, Func<InstallationContext> installation, RuntimeSelection? runtime = null)
    {
        var paths = new DefenseClawPaths(
            dataDirectory: temp.Path,
            binDirectory: Path.Combine(temp.Path, "no-such-bin"),
            searchPath: Array.Empty<string>(),
            runtime: runtime);
        return new CliRunner(paths, neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"), installation: installation);
    }

    // ------------------------------------------------------------------------------------------------------------ the tier table

    private sealed record Leaf(string Path, string[] Tokens);

    private static readonly Leaf[] Leaves = LoadLeaves();

    private static Leaf[] LoadLeaves()
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText("cli-tree-0.8.10.json"));
        return document.RootElement.GetProperty("commands").EnumerateArray()
            .Where(c => !c.GetProperty("group").GetBoolean())
            .Select(c => new Leaf(c.GetProperty("path").GetString()!, c.GetProperty("path").GetString()!.Split(' ')))
            .ToArray();
    }

    [Fact]
    public void Every_leaf_of_the_whole_command_tree_is_refused_when_read_only_unless_the_classifier_calls_it_read_only()
    {
        Assert.True(Leaves.Length >= 170, $"only {Leaves.Length} leaves - is the fixture truncated?");
        var invalid = Invalid();
        var managed = Managed();

        foreach (var leaf in Leaves)
        {
            var tier = CommandTiers.Classify(leaf.Tokens);
            var allowed = tier == CommandTier.ReadOnly;

            foreach (var context in new[] { managed, invalid })
            {
                var refusal = InstallationGate.RefusalFor(context, "defenseclaw", leaf.Tokens);
                if (allowed)
                {
                    Assert.True(refusal is null, $"'{leaf.Path}' is read-only and must run on a read-only installation");
                }
                else
                {
                    Assert.True(refusal == context.BlockedReason, $"'{leaf.Path}' is {tier} and must be refused with the installation's reason");
                }
            }

            // A writable installation refuses nothing, whatever the tier.
            Assert.Null(InstallationGate.RefusalFor(Writable(), "defenseclaw", leaf.Tokens));
        }
    }

    [Fact]
    public void The_tree_has_every_tier_so_the_table_above_is_not_vacuous()
    {
        var tiers = Leaves.Select(l => CommandTiers.Classify(l.Tokens)).ToHashSet();

        Assert.Contains(CommandTier.ReadOnly, tiers);
        Assert.Contains(CommandTier.StateChanging, tiers);
        Assert.Contains(CommandTier.Destructive, tiers);
    }

    public static TheoryData<string, string, CommandTier> Shapes => new()
    {
        // reads
        { "defenseclaw", "status --json", CommandTier.ReadOnly },
        { "defenseclaw", "skill list --json", CommandTier.ReadOnly },
        { "defenseclaw", "mcp list --json --connector claudecode", CommandTier.ReadOnly },
        { "defenseclaw", "doctor", CommandTier.ReadOnly },
        { "defenseclaw", "config show --section guardrail --format json", CommandTier.ReadOnly },
        { "defenseclaw", "--version-json", CommandTier.ReadOnly },
        { "defenseclaw", "setup splunk --help", CommandTier.ReadOnly },

        // previews
        { "defenseclaw", "alerts acknowledge --dry-run", CommandTier.ReadOnly },
        { "defenseclaw", "alerts dismiss --dry-run", CommandTier.ReadOnly },
        { "defenseclaw", "setup redaction defaults set --profile strict --json --dry-run", CommandTier.ReadOnly },

        // the gateway
        { "defenseclaw-gateway", "status", CommandTier.ReadOnly },
        { "defenseclaw-gateway", "provenance show", CommandTier.ReadOnly },
        { "defenseclaw-gateway.exe", "start", CommandTier.StateChanging },
        { "defenseclaw-gateway", "stop", CommandTier.StateChanging },
        { "defenseclaw-gateway", "restart", CommandTier.StateChanging },
        { @"C:\managed\bin\defenseclaw-gateway.exe", "restart", CommandTier.StateChanging },

        // changes
        { "defenseclaw", "skill block -- pdf-tools", CommandTier.StateChanging },
        { "defenseclaw", "setup splunk --non-interactive", CommandTier.StateChanging },
        { "defenseclaw", "guardrail mode action", CommandTier.StateChanging },
        { "defenseclaw", "agent discover", CommandTier.StateChanging },
        { "defenseclaw", "doctor --fix", CommandTier.StateChanging },
        { "defenseclaw", "config show --reveal", CommandTier.StateChanging },
        { "defenseclaw", "init --json-summary", CommandTier.StateChanging },
        { "defenseclaw", "", CommandTier.StateChanging },

        // a target that spells a read does not make a change one
        { "defenseclaw", "skill quarantine -- list", CommandTier.Destructive },
        { "defenseclaw", "skill block -- --help", CommandTier.StateChanging },
        { "defenseclaw", "registry sync list", CommandTier.StateChanging },

        // destructive
        { "defenseclaw", "skill remove -- pdf-tools", CommandTier.Destructive },
        { "defenseclaw", "alerts dismiss", CommandTier.Destructive },
        { "defenseclaw", "setup splunk dashboards destroy", CommandTier.Destructive },

        // any other program is a change: an installer, a script, a tool nobody has heard of
        { "DefenseClawSetup-x64.exe", "/quiet /norestart", CommandTier.StateChanging },
        { @"C:\Windows\System32\cmd.exe", "/c echo hi", CommandTier.StateChanging },
        { "cosign", "verify-blob --help", CommandTier.StateChanging },
        { "docker", "exec dc-next-1 defenseclaw status --json", CommandTier.StateChanging },
        { "skill-scanner", "scan .", CommandTier.StateChanging },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_gate_judges_a_run_by_its_tier(string executable, string commandLine, CommandTier expected)
    {
        var argv = commandLine.Length == 0 ? Array.Empty<string>() : commandLine.Split(' ');

        Assert.Equal(expected, InstallationGate.TierOf(executable, argv));

        foreach (var context in new[] { Managed(), Invalid() })
        {
            var refusal = InstallationGate.RefusalFor(context, executable, argv);
            Assert.Equal(expected == CommandTier.ReadOnly, refusal is null);
            if (refusal is not null)
            {
                Assert.Equal(context.BlockedReason, refusal);
            }
        }

        Assert.Null(InstallationGate.RefusalFor(Writable(), executable, argv));
    }

    [Fact]
    public void The_reads_the_classifier_cannot_prove_are_allowed_by_the_module_that_builds_them_and_only_in_that_exact_shape()
    {
        var context = Managed();

        // Each of these is a read the app makes, and each starts with a verb the first-verb classifier cannot place or reads as a change.
        string[][] reads =
        [
            ["agent", "discovery", "runtime", "permissions", "--json"],
            ["guardrail", "list-packs", "--json"],
            ["guardrail", "protection", "list", "--json"],
            ["guardrail", "validate-pack", @"C:\packs\strict", "--json"],
            ["setup", "redaction", "status", "--json"],
            ["setup", "redaction", "profile", "list", "--json"],
        ];
        foreach (var read in reads)
        {
            Assert.Null(InstallationGate.RefusalFor(context, "defenseclaw", read));
        }

        // The same words with one more thing on them, or one thing off, are not the read.
        string[][] notReads =
        [
            ["agent", "discovery", "runtime", "permissions", "--json", "--grant"],
            ["agent", "discovery", "runtime", "permissions", "--grant"],
            ["agent", "discovery", "runtime", "scan"],
            ["guardrail", "list-packs", "--json", "--yes"],
            ["guardrail", "validate-pack", "relative\\strict", "--json"],
            ["guardrail", "use-pack", "strict"],
            ["setup", "redaction", "status"],
            ["setup", "redaction", "status", "--json", "--yes"],
        ];
        foreach (var notRead in notReads)
        {
            Assert.Equal(Reason, InstallationGate.RefusalFor(context, "defenseclaw", notRead));
        }

        // And only the CLI itself earns that: the same argv on another program is a change.
        Assert.Equal(Reason, InstallationGate.RefusalFor(context, "cmd", ["setup", "redaction", "status", "--json"]));
    }

    [Fact]
    public void The_refusal_reason_says_that_reads_still_run_and_then_the_installations_own_sentence()
    {
        var reason = InstallationGate.RefusalReason(Managed());

        Assert.StartsWith("DefenseClaw for Windows runs only read-only commands against this installation. ", reason, StringComparison.Ordinal);
        Assert.EndsWith(Reason, reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run the gate refused is the Activity entry <see cref="CliRunner.RecordRefusal"/> makes for every refusal (the stale-list one among them):
    /// born finished, no exit code, "refused - &lt;reason&gt;" as its failure, the reason as its one output line, and nothing started.
    /// </summary>
    private static void AssertRefused(CliInvocation invocation, InstallationContext context)
    {
        var reason = InstallationGate.RefusalReason(context);

        Assert.False(invocation.IsRunning);
        Assert.False(invocation.Succeeded);
        Assert.Null(invocation.ExitCode);
        Assert.Equal(CliRunner.RefusedPrefix + " — " + reason, invocation.FailureReason);
        var line = Assert.Single(invocation.OutputLines);
        Assert.Equal(CliStream.Notice, line.Stream);
        Assert.StartsWith("Refused before it started: nothing was run and nothing was changed. ", line.Text, StringComparison.Ordinal);
        Assert.EndsWith(reason, line.Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------ through the runner

    [Fact]
    public async Task A_refused_run_is_the_one_refused_activity_entry_and_nothing_started()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, Managed);
        var started = new List<CliInvocation>();
        var completed = new List<CliInvocation>();
        runner.InvocationStarted += (_, i) => started.Add(i);
        runner.InvocationCompleted += (_, i) => completed.Add(i);

        // There is no defenseclaw.exe on this "PC": a refusal must not need one.
        var invocation = await runner.RunAsync(["skill", "block", "--", "pdf-tools"]);

        AssertRefused(invocation, Managed());
        Assert.Equal("defenseclaw", invocation.Executable);
        Assert.Equal(new[] { "skill", "block", "--", "pdf-tools" }, invocation.Argv);
        Assert.Same(invocation, Assert.Single(runner.Activity));
        Assert.Same(invocation, Assert.Single(started));
        Assert.Same(invocation, Assert.Single(completed));
    }

    [Fact]
    public async Task The_gateway_and_a_path_to_an_installer_are_refused_the_same_way()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, Invalid);

        var gateway = await runner.RunGatewayAsync(["restart"]);
        var installer = await runner.RunExecutableAsync(Path.Combine(temp.Path, "DefenseClawSetup-x64.exe"), ["/quiet", "/norestart"], options: CliRunOptions.Installer);
        var named = await runner.RunNamedAsync("defenseclaw", ["setup", "splunk"]);

        Assert.All(new[] { gateway, installer, named }, i =>
        {
            AssertRefused(i, Invalid());
            Assert.Contains(Invalid().BlockedReason!, i.OutputLines.Single().Text, StringComparison.Ordinal);
        });
        Assert.Equal(3, runner.Activity.Count);
    }

    [Fact]
    public async Task A_read_is_not_refused_it_goes_on_to_look_for_the_cli()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, Managed);

        // CliNotFoundException is "the gate let it through and the lookup failed", which on this PC with no CLI is the proof it ran.
        string[][] reads =
        [
            ["status", "--json"],
            ["skill", "list", "--json"],
            ["alerts", "acknowledge", "--dry-run"],
            ["--version-json"],
            ["guardrail", "list-packs", "--json"],
        ];
        foreach (var read in reads)
        {
            _ = await Assert.ThrowsAsync<CliNotFoundException>(() => runner.RunAsync(read));
        }

        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task A_container_run_is_judged_on_the_command_asked_for_not_on_docker_exec()
    {
        using var temp = new TempDirectory();
        var container = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", temp.Path);
        var runner = Runner(temp, Managed, container);

        var refused = await runner.RunAsync(["skill", "block", "--", "x"]);

        AssertRefused(refused, Managed());
        Assert.Equal("defenseclaw", refused.Executable);

        // The read goes on to the container path, which wants docker; there is none on this PC.
        var missing = await Assert.ThrowsAsync<CliNotFoundException>(() => runner.RunAsync(["status", "--json"]));
        Assert.Equal("docker", missing.ExecutableName);
    }

    [Fact]
    public async Task A_writable_installation_refuses_nothing()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, Writable);

        _ = await Assert.ThrowsAsync<CliNotFoundException>(() => runner.RunAsync(["skill", "remove", "--", "x"]));

        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task The_installation_is_read_for_every_run_so_a_file_fixed_while_the_app_runs_lifts_the_refusal()
    {
        using var temp = new TempDirectory();
        var current = Managed();
        var runner = Runner(temp, () => current);

        var before = await runner.RunAsync(["skill", "block", "--", "x"]);
        current = Writable();
        _ = await Assert.ThrowsAsync<CliNotFoundException>(() => runner.RunAsync(["skill", "block", "--", "x"]));
        current = Managed();
        var after = await runner.RunAsync(["skill", "block", "--", "x"]);

        AssertRefused(before, Managed());
        AssertRefused(after, Managed());
        Assert.Equal(2, runner.Activity.Count);
    }

    [Fact]
    public async Task The_paths_own_installation_is_the_default_so_a_runner_built_on_managed_paths_refuses_with_no_extra_wiring()
    {
        using var temp = new TempDirectory();
        var paths = new DefenseClawPaths(dataDirectory: temp.Path, binDirectory: Path.Combine(temp.Path, "no-such-bin"), searchPath: Array.Empty<string>(), installation: Managed());
        var runner = new CliRunner(paths, neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"));

        var refused = await runner.RunAsync(["policy", "activate", "--", "strict"]);

        AssertRefused(refused, Managed());
    }

    [Fact]
    public async Task A_refused_run_still_refuses_a_secret_on_argv_and_a_bad_overlay_instead_of_recording_them()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, Managed);
        var secret = new SecretValue("synthetic-secret-value-0001");
        runner.RegisterSecret(secret);

        _ = await Assert.ThrowsAsync<SecretInArgumentException>(() => runner.RunAsync(["keys", "set", "synthetic-secret-value-0001"]));
        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(["skill", "block"], options: new CliRunOptions { EnvironmentOverlay = new Dictionary<string, SecretValue> { ["BAD NAME"] = secret } }));

        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task A_refused_run_does_not_start_the_program_even_when_the_program_exists()
    {
        using var temp = new TempDirectory();
        var marker = temp.File("ran.txt");
        var runner = Runner(temp, Managed);

        // cmd.exe would write the marker if it were started. It is not "defenseclaw", so it is a change, so it is refused.
        var invocation = await runner.RunExecutableAsync(CmdPath, ["/c", "echo", "x", ">", marker]);

        AssertRefused(invocation, Managed());
        Assert.False(File.Exists(marker));
    }

    // ------------------------------------------------------------------------------------------------------------ the identity, last

    private static string FakeCli(TempDirectory temp)
    {
        var fake = Path.Combine(temp.Path, "defenseclaw.exe");
        File.Copy(CmdPath, fake);
        return fake;
    }

    /// <summary>What the child saw for <paramref name="variable"/>: <c>echo %VAR% --dry-run</c> (a standalone --dry-run makes it a preview, which runs everywhere).</summary>
    private static async Task<string> SeenAsync(CliRunner runner, string executable, string variable, CliRunOptions? options = null)
    {
        var invocation = await runner.RunExecutableAsync(executable, ["/c", "echo", $"%{variable}%", "--dry-run"], options: options);
        Assert.Equal(0, invocation.ExitCode);
        var line = Assert.Single(invocation.OutputLines, l => l.Stream == CliStream.StandardOutput).Text;
        Assert.EndsWith(" --dry-run", line, StringComparison.Ordinal);
        return line[..^" --dry-run".Length];
    }

    [Fact]
    public async Task A_managed_installation_gives_every_defenseclaw_child_its_home_config_and_mode()
    {
        using var temp = new TempDirectory();
        var managed = Managed();
        var runner = Runner(temp, () => managed);
        var fake = FakeCli(temp);

        Assert.Equal(managed.PinnedHome, await SeenAsync(runner, fake, "DEFENSECLAW_HOME"));
        Assert.Equal(managed.PinnedConfig, await SeenAsync(runner, fake, "DEFENSECLAW_CONFIG"));
        Assert.Equal("managed_enterprise", await SeenAsync(runner, fake, "DEFENSECLAW_DEPLOYMENT_MODE"));
    }

    [Fact]
    public async Task A_calls_own_environment_cannot_move_a_child_to_another_installation()
    {
        using var temp = new TempDirectory();
        var managed = Managed();
        var runner = Runner(temp, () => managed);
        var fake = FakeCli(temp);
        var hostile = CliRunOptions.Default
            .WithEnvironment("DEFENSECLAW_HOME", new SecretValue(@"D:\somewhere\else"))
            .WithEnvironment("DEFENSECLAW_CONFIG", new SecretValue(@"D:\somewhere\else\config.yaml"))
            .WithEnvironment("DEFENSECLAW_DEPLOYMENT_MODE", new SecretValue("unmanaged_byod"));

        // The overlay's values would print as the redaction mask if the child had seen them; it sees the pinned ones instead.
        Assert.Equal(managed.PinnedHome, await SeenAsync(runner, fake, "DEFENSECLAW_HOME", hostile));
        Assert.Equal(managed.PinnedConfig, await SeenAsync(runner, fake, "DEFENSECLAW_CONFIG", hostile));
        Assert.Equal("managed_enterprise", await SeenAsync(runner, fake, "DEFENSECLAW_DEPLOYMENT_MODE", hostile));
    }

    [Fact]
    public async Task A_variable_the_selection_does_not_pin_keeps_what_the_app_itself_would_pass_on_whatever_the_overlay_says()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, Writable);
        var fake = FakeCli(temp);
        var hostile = CliRunOptions.Default.WithEnvironment("DEFENSECLAW_HOME", new SecretValue(@"D:\somewhere\else"));
        var inherited = Environment.GetEnvironmentVariable("DEFENSECLAW_HOME");

        var seen = await SeenAsync(runner, fake, "DEFENSECLAW_HOME", hostile);

        Assert.Equal(inherited ?? "%DEFENSECLAW_HOME%", seen);
        Assert.DoesNotContain(SecretValue.Redacted, seen, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_with_no_overlay_and_nothing_pinned_leaves_the_environment_exactly_as_the_app_has_it()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp, Writable);
        var fake = FakeCli(temp);
        var inherited = Environment.GetEnvironmentVariable("DEFENSECLAW_CONFIG");

        Assert.Equal(inherited ?? "%DEFENSECLAW_CONFIG%", await SeenAsync(runner, fake, "DEFENSECLAW_CONFIG"));
    }

    [Fact]
    public async Task Only_defenseclaw_programs_get_the_identity()
    {
        using var temp = new TempDirectory();
        var pinned = new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", @"D:\dc\home").Resolve();
        var runner = Runner(temp, () => pinned);
        var fake = FakeCli(temp);
        var inherited = Environment.GetEnvironmentVariable("DEFENSECLAW_HOME");

        Assert.Equal(@"D:\dc\home", pinned.PinnedHome);
        Assert.Equal(@"D:\dc\home", await SeenAsync(runner, fake, "DEFENSECLAW_HOME"));

        // cmd.exe under its own name is another program: it gets the app's environment and nothing of the selection.
        Assert.Equal(inherited ?? "%DEFENSECLAW_HOME%", await SeenAsync(runner, CmdPath, "DEFENSECLAW_HOME"));
    }

    [Fact]
    public async Task A_side_by_side_runtimes_home_and_a_pinned_home_agree_so_the_developer_selector_is_unchanged()
    {
        using var temp = new TempDirectory();
        var fake = FakeCli(temp);
        var home = Path.Combine(temp.Path, "home");
        Directory.CreateDirectory(home);
        var selection = RuntimeSelection.ForCli(fake, home, null);
        var machine = new InstallationMachine { Runtime = selection };
        var context = machine.Resolve();
        var runner = Runner(temp, () => context, selection);

        Assert.Equal(InstallationSource.AppOverride, context.Source);
        Assert.Equal(home, await SeenAsync(runner, fake, "DEFENSECLAW_HOME"));
    }

    [Fact]
    public void Config_paths_follow_the_selected_installation_when_it_is_not_the_default_location()
    {
        using var temp = new TempDirectory();
        var elsewhere = temp.Write("elsewhere.yaml", "gateway:\n  api_port: 18971\n");
        var context = new InstallationMachine()
            .WithEnvironment("DEFENSECLAW_CONFIG", elsewhere)
            .WithFile(elsewhere, "gateway:\n  api_port: 18971\n")
            .Resolve();
        var paths = new DefenseClawPaths(dataDirectory: temp.Path, installation: context);

        Assert.Equal(elsewhere, paths.ConfigFilePath);
        Assert.Equal(Path.Combine(temp.Path, ".env"), paths.EnvFilePath);
        Assert.Equal(18971, new ConfigStore(paths).Load().Config.Gateway.ApiPort);
    }

    [Fact]
    public void Paths_without_an_installation_are_user_owned_and_keep_the_config_where_it_always_was()
    {
        var paths = new DefenseClawPaths(dataDirectory: @"C:\data\.defenseclaw");

        Assert.True(paths.Installation.IsMutable);
        Assert.Equal(@"C:\data\.defenseclaw\config.yaml", paths.ConfigFilePath);
        Assert.Equal(@"C:\data\.defenseclaw", paths.Installation.HomeRoot);

        // A context whose config is the default one (a resolution from the user default) keeps the very same string.
        var resolved = new InstallationMachine().Resolve();
        var viaContext = new DefenseClawPaths(dataDirectory: InstallationMachine.DefaultHome, installation: resolved);
        Assert.Equal(InstallationMachine.DefaultHome + @"\config.yaml", viaContext.ConfigFilePath);
    }

    [Fact]
    public void A_managed_layout_moves_the_data_directory_and_pins_its_own_cli_when_that_file_is_there()
    {
        var managed = Managed();

        var paths = RuntimeEnvironment.CreatePaths(RuntimeSelection.Installed, managed);

        Assert.Equal(managed.HomeRoot, paths.DataDirectory);
        Assert.Equal(DataDirectorySource.Installation, paths.DataDirectoryOrigin.Source);
        Assert.Equal(managed.ConfigPath, paths.ConfigFilePath);
        Assert.Equal(managed.Layout!.CliPath, paths.CliPathOverride);
        Assert.Same(managed, paths.Installation);
        Assert.Contains("Settings", paths.DataDirectoryOrigin.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Creating_paths_for_the_default_installation_is_the_call_it_always_was()
    {
        var resolved = new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", @"D:\dc\home").Resolve();

        var withContext = RuntimeEnvironment.CreatePaths(RuntimeSelection.Installed, resolved);
        var plain = RuntimeEnvironment.CreatePaths(RuntimeSelection.Installed);

        Assert.Equal(plain.BinDirectory, withContext.BinDirectory);
        Assert.Equal(plain.CliPathOverride, withContext.CliPathOverride);
        Assert.True(withContext.Runtime.IsDefault);
        Assert.True(plain.Installation.IsMutable);
        Assert.Same(resolved, withContext.Installation);
    }
}
