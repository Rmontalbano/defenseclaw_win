using System.Diagnostics;
using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Where the Python CLI runs, and what happens to arguments it would rewrite. Click on Windows expands every
/// argument (<c>%VAR%</c>, <c>$VAR</c>, <c>~</c>, wildcards) against the working directory, and the runner used to
/// start it in <c>~/.defenseclaw</c>, so <c>-- config.y?ml</c> reached the CLI as <c>config.yaml</c>.
/// </summary>
public sealed class CliRunnerWorkingDirectoryTests : IDisposable
{
    private readonly TempDirectory _data = new("dcw-data");
    private readonly TempDirectory _neutral = new("dcw-neutral");

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public void Dispose()
    {
        _data.Dispose();
        _neutral.Dispose();
    }

    private CliRunner Runner(string? neutral = null) =>
        new(
            new DefenseClawPaths(
                dataDirectory: _data.Path,
                binDirectory: Path.Combine(_data.Path, "no-such-bin"),
                searchPath: Array.Empty<string>()),
            neutralWorkingDirectory: neutral ?? _neutral.Path);

    /// <summary>A stand-in for the Python CLI: cmd.exe under the name the runner treats as Click-based.</summary>
    private string StandInForTheCli()
    {
        var path = _data.File("defenseclaw.exe");
        File.Copy(CmdPath, path, overwrite: true);
        return path;
    }

    private static string Cwd(CliInvocation invocation) =>
        Path.GetFullPath(invocation.OutputLines.First(l => l.Stream == CliStream.StandardOutput && l.Text.Length > 0).Text.Trim())
            .TrimEnd('\\');

    [Fact]
    public async Task The_python_cli_runs_in_the_neutral_directory_and_not_in_the_data_directory()
    {
        var invocation = await Runner().RunExecutableAsync(StandInForTheCli(), new[] { "/c", "cd" });

        Assert.Equal(0, invocation.ExitCode);
        Assert.Equal(Path.GetFullPath(_neutral.Path).TrimEnd('\\'), Cwd(invocation), ignoreCase: true);
    }

    [Fact]
    public async Task Every_other_child_keeps_the_data_directory_as_its_working_directory()
    {
        // The Go gateway and the installers do not expand their arguments, and running them elsewhere is unverified.
        var invocation = await Runner().RunExecutableAsync(CmdPath, new[] { "/c", "cd" });

        Assert.Equal(Path.GetFullPath(_data.Path).TrimEnd('\\'), Cwd(invocation), ignoreCase: true);
    }

    [Fact]
    public async Task The_neutral_directory_is_created_when_it_is_missing()
    {
        var neutral = Path.Combine(_neutral.Path, "not", "yet");

        var invocation = await Runner(neutral).RunExecutableAsync(StandInForTheCli(), new[] { "/c", "cd" });

        Assert.True(Directory.Exists(neutral));
        Assert.Equal(Path.GetFullPath(neutral).TrimEnd('\\'), Cwd(invocation), ignoreCase: true);
    }

    [Fact]
    public void The_default_neutral_directory_is_the_apps_own_folder_under_local_app_data()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "DefenseClaw.App", "cwd"), CliWorkingDirectory.DefaultPath);
    }

    // ------------------------------------------------------------------ refusing a target the CLI would rewrite

    private static readonly CliRunOptions Refusing = new() { RefuseExpandingTargets = true };

    [Fact]
    public async Task A_target_the_cli_would_expand_is_refused_before_anything_is_recorded_or_started()
    {
        _neutral.Write("skill-a", "x");
        _neutral.Write("skill-b", "x");
        var runner = Runner();

        var ex = await Assert.ThrowsAsync<ArgumentExpansionException>(() => runner.RunExecutableAsync(
            StandInForTheCli(), new[] { "skill", "block", "--", "skill-*" }, options: Refusing));

        Assert.Equal(3, Assert.Single(ex.Hazards).Index);
        Assert.Equal(new[] { "skill-a", "skill-b" }, ex.Hazards[0].Expansion.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Contains("“skill-*” would arrive as", ex.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task A_target_with_an_environment_variable_is_refused_too()
    {
        const string Variable = "DCW_TEST_TARGET_X1";
        Environment.SetEnvironmentVariable(Variable, "somewhere-else");
        try
        {
            await Assert.ThrowsAsync<ArgumentExpansionException>(() => Runner().RunExecutableAsync(
                StandInForTheCli(), new[] { "skill", "block", "--", "%" + Variable + "%" }, options: Refusing));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }

    [Fact]
    public async Task Nothing_is_refused_when_nothing_would_change()
    {
        _neutral.Write("unrelated", "x");
        var runner = Runner();

        // A wildcard nothing matches, a question mark in prose, a percent sign: the CLI leaves all of them alone.
        var invocation = await runner.RunExecutableAsync(
            StandInForTheCli(), new[] { "/c", "echo", "--", "no-match-*", "why?", "50%" }, options: Refusing);

        Assert.Equal(0, invocation.ExitCode);
    }

    [Fact]
    public async Task Only_targets_after_the_terminator_are_refused_not_operator_typed_option_values()
    {
        _neutral.Write("skill-a", "x");

        var invocation = await Runner().RunExecutableAsync(
            StandInForTheCli(), new[] { "/c", "echo", "--reason", "skill-*", "--", "plain" }, options: Refusing);

        Assert.Equal(0, invocation.ExitCode);
    }

    [Fact]
    public async Task The_refusal_is_opt_in_and_only_for_the_python_cli()
    {
        _neutral.Write("skill-a", "x");
        var runner = Runner();
        var argv = new[] { "/c", "echo", "--", "skill-*" };

        // Without the option: runs (the review already warned).
        Assert.Equal(0, (await runner.RunExecutableAsync(StandInForTheCli(), argv)).ExitCode);

        // Another executable does not expand its arguments: nothing to refuse.
        Assert.Equal(0, (await runner.RunExecutableAsync(CmdPath, argv, options: Refusing)).ExitCode);
    }

    // ------------------------------------------------------------------ resolution off the caller's thread

    [Fact]
    public async Task Resolving_the_executable_never_blocks_the_caller()
    {
        // A PATH entry that never answers (a dead UNC share) used to hold the caller - a UI handler - for as long as
        // the scan took, because the scan ran before RunNamedAsync returned its Task.
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var paths = new DefenseClawPaths(
            dataDirectory: _data.Path,
            binDirectory: Path.Combine(_data.Path, "no-such-bin"),
            searchPath: new[] { @"\\192.0.2.1\tools" },
            fileExists: candidate =>
            {
                entered.Set();
                _ = release.Wait(TimeSpan.FromSeconds(30));
                return false;
            });
        var runner = new CliRunner(paths, neutralWorkingDirectory: _neutral.Path);

        // Safety net: if the scan were synchronous the call below would never return, so let it go after a while.
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ => release.Set(), TaskScheduler.Default);

        var timer = Stopwatch.StartNew();
        var task = runner.RunAsync(new[] { "status" });
        var returnedAfter = timer.Elapsed;

        Assert.True(returnedAfter < TimeSpan.FromSeconds(2), $"RunAsync held the caller for {returnedAfter}");
        Assert.False(task.IsCompleted);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the scan never started");

        release.Set();
        var ex = await Assert.ThrowsAsync<CliNotFoundException>(() => task);
        Assert.Equal("defenseclaw", ex.ExecutableName);
    }

    // ------------------------------------------------------------------ against the real CLI

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task A_wildcard_target_reaches_the_real_cli_unchanged_even_when_the_data_directory_holds_a_match()
    {
        // The reproduction of D3-01: `tool status --json -- config.y?ml` from a directory holding config.yaml answers about
        // "config.yaml". The data directory below plays ~/.defenseclaw; the child must not run there.
        _ = _data.Write("config.yaml", "x");
        var runner = new CliRunner(new DefenseClawPaths(dataDirectory: _data.Path), neutralWorkingDirectory: _neutral.Path);

        var invocation = await runner.RunAsync(new[] { "tool", "status", "--json", "--", "config.y?ml" });

        Assert.Equal("config.y?ml", ReportedName(invocation));
    }

    [LiveFact]
    [Trait("Category", "Live")]
    public async Task The_expansion_this_app_predicts_is_the_one_the_real_cli_performs()
    {
        // %USERNAME% is expanded by Click whatever the working directory is - which is why the review says so.
        var runner = new CliRunner(new DefenseClawPaths(dataDirectory: _data.Path), neutralWorkingDirectory: _neutral.Path);
        var argv = new[] { "tool", "status", "--json", "--", "%USERNAME%" };

        var predicted = Assert.Single(ArgvHazards.Find(argv, _neutral.Path));
        var invocation = await runner.RunAsync(argv);

        Assert.True(predicted.Changes);
        Assert.Equal(predicted.Expansion[0], ReportedName(invocation));
    }

    /// <summary>The <c>name</c> <c>defenseclaw tool status --json</c> reports: the target as the CLI received it.</summary>
    private static string ReportedName(CliInvocation invocation)
    {
        Assert.Equal(0, invocation.ExitCode);
        var stdout = string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));
        var json = stdout[stdout.IndexOf('{')..];
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("name").GetString()!;
    }
}
