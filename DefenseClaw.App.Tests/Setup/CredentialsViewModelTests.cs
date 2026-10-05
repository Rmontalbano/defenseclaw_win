using System.Diagnostics;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Credentials card's view-model and its terminal route. The runner is injected (no process starts) and so is the console launcher (no
/// window opens); both only ever see synthetic names. <c>keys set</c> and <c>fill-missing</c> are never run, only handed to the launcher.
/// </summary>
public sealed class CredentialsViewModelTests
{
    private const string Listing = """
        [
          {"env_name": "EXAMPLE_JUDGE_KEY", "canonical_env_name": "EXAMPLE_JUDGE_KEY", "feature": "LLM judge", "description": "d", "requirement": "required", "source": "unset", "set": false},
          {"env_name": "EXAMPLE_SCANNER_KEY", "canonical_env_name": "EXAMPLE_SCANNER_KEY", "feature": "Scanner", "description": "d", "requirement": "required", "source": "dotenv", "set": true},
          {"env_name": "EXAMPLE_OPTIONAL_KEY", "canonical_env_name": "EXAMPLE_OPTIONAL_KEY", "feature": "Telemetry", "description": "d", "requirement": "optional", "source": "unset", "set": false}
        ]
        """;

    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _temp = new();
        public List<string> Reads { get; } = new();
        public List<ProcessStartInfo> Launched { get; } = new();
        public string Output { get; set; } = Listing;
        public int ExitCode { get; set; }

        public Harness()
        {
            Services = TestServices.Create(_temp);
            var terminal = new CredentialTerminal(Services.Paths)
            {
                ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
                Launch = info =>
                {
                    Launched.Add(info);
                    return null;
                },
            };
            Credentials = new CredentialsViewModel(Services, terminal) { RunRead = RunAsync };
        }

        public AppServices Services { get; }

        public CredentialsViewModel Credentials { get; }

        private Task<CliInvocation> RunAsync(IReadOnlyList<string> argv)
        {
            Reads.Add(string.Join(' ', argv));
            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            foreach (var line in Output.Split('\n'))
            {
                InvocationFactory.Append(invocation, line.TrimEnd('\r'));
            }

            InvocationFactory.Finish(invocation, ExitCode);
            return Task.FromResult(invocation);
        }

        public void Dispose()
        {
            Services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public async Task A_read_fills_the_table_and_counts_missing_required_like_the_tui()
    {
        using var h = new Harness();
        var loaded = 0;
        h.Credentials.Loaded += (_, _) => loaded++;
        Assert.True(h.Credentials.ShowNotLoaded);

        await h.Credentials.RefreshAsync();

        Assert.Equal(new[] { "keys list --json" }, h.Reads);
        Assert.Equal(3, h.Credentials.Rows.Count);
        Assert.Equal(1, h.Credentials.MissingRequiredCount);
        Assert.Equal("1 required credential is missing.", h.Credentials.SummaryText);
        Assert.False(h.Credentials.ShowNotLoaded);
        Assert.False(h.Credentials.HasError);
        Assert.Equal(1, loaded);

        var missing = h.Credentials.Rows[0];
        Assert.Equal(("EXAMPLE_JUDGE_KEY", "required", "unset", "MISSING", "Bad"), (missing.EnvName, missing.Requirement, missing.Source, missing.SetText, missing.SetKey));
        var set = h.Credentials.Rows[1];
        Assert.Equal(("dotenv", "✓ set", "Ok"), (set.Source, set.SetText, set.SetKey));
        var optional = h.Credentials.Rows[2];
        Assert.Equal(("unset", "Neutral"), (optional.SetText, optional.SetKey));
    }

    [Fact]
    public async Task Noise_before_the_array_is_tolerated()
    {
        using var h = new Harness { Output = "[warn] something\n" + Listing };

        await h.Credentials.RefreshAsync();

        Assert.False(h.Credentials.HasError);
        Assert.Equal(3, h.Credentials.Rows.Count);
    }

    [Fact]
    public async Task A_failed_or_unreadable_read_shows_an_error_and_keeps_the_last_good_rows()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();

        h.Output = "Traceback: boom";
        h.ExitCode = 1;
        await h.Credentials.RefreshAsync();

        Assert.True(h.Credentials.HasError);
        Assert.Contains("exited 1", h.Credentials.Error, StringComparison.Ordinal);
        Assert.Equal(3, h.Credentials.Rows.Count); // a blank list would read as "nothing is missing"

        h.ExitCode = 0;
        h.Output = "this is not json";
        await h.Credentials.RefreshAsync();

        Assert.True(h.Credentials.HasError);
        Assert.Contains("not a credential list", h.Credentials.Error, StringComparison.Ordinal);
        Assert.Equal(3, h.Credentials.Rows.Count);
    }

    [Fact]
    public async Task An_empty_list_is_its_own_state()
    {
        using var h = new Harness { Output = "[]" };

        await h.Credentials.RefreshAsync();

        Assert.True(h.Credentials.ShowEmpty);
        Assert.False(h.Credentials.ShowNotLoaded);
        Assert.Equal(0, h.Credentials.MissingRequiredCount);
    }

    [Fact]
    public async Task Without_a_cli_the_card_says_so_instead_of_throwing()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var credentials = new CredentialsViewModel(services); // the real runner, with an empty PATH: it cannot start anything

        await credentials.RefreshAsync();

        Assert.True(credentials.HasError);
        Assert.Contains("not found", credentials.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(credentials.IsLoading);
    }

    [Fact]
    public async Task Check_runs_the_read_only_keys_check_shows_its_verdict_and_re_reads_the_list()
    {
        using var h = new Harness { Output = "  1/2 required credentials set.\n  x EXAMPLE_JUDGE_KEY" , ExitCode = 1 };

        await h.Credentials.CheckCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "keys check", "keys list --json" }, h.Reads);
        Assert.Equal("Warn", h.Credentials.NoteKey);
        Assert.Contains("1/2 required credentials set.", h.Credentials.Note, StringComparison.Ordinal);
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(CredentialsViewModel.CheckArgv));
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(CredentialsViewModel.ListArgv));
    }

    [Fact]
    public void The_reads_never_carry_a_flag_that_prints_values()
    {
        Assert.DoesNotContain("--show-values", CredentialsViewModel.ListArgv);
        Assert.False(CommandTiers.PrintsSecrets(CredentialsViewModel.ListArgv));
    }

    [Fact]
    public async Task A_row_Set_opens_a_console_with_the_exact_command_and_never_runs_it()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        h.Reads.Clear();

        var row = h.Credentials.Rows[0];
        Assert.Equal("defenseclaw keys set EXAMPLE_JUDGE_KEY", row.SetCommandText);
        row.SetInTerminalCommand.Execute(null);
        UiWait(() => h.Launched.Count == 1);

        var info = Assert.Single(h.Launched);
        Assert.Equal(DefenseClaw.App.Services.Wizards.WizardCredentials.CommandInterpreterPath(), info.FileName, ignoreCase: true);
        Assert.Equal("/d /s /c \"\"C:\\Tools\\defenseclaw.exe\" keys set EXAMPLE_JUDGE_KEY & echo. & pause\"", info.Arguments);
        Assert.False(info.UseShellExecute);
        Assert.False(info.CreateNoWindow); // a visible console window

        // Nothing was run through the runner: the hand-off is only recorded in Activity.
        Assert.Empty(h.Reads);
        var entry = Assert.Single(h.Services.Cli.Activity);
        Assert.Equal(new[] { "keys", "set", "EXAMPLE_JUDGE_KEY" }, entry.Argv);
        Assert.Null(entry.ExitCode);
        Assert.Contains("hidden prompt", Assert.Single(entry.OutputLines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fill_missing_hands_the_tuis_argv_to_the_console_and_returning_re_reads_once()
    {
        using var h = new Harness();

        await h.Credentials.OpenFillMissingInTerminalAsync();

        var info = Assert.Single(h.Launched);
        Assert.Equal("/d /s /c \"\"C:\\Tools\\defenseclaw.exe\" keys fill-missing --yes & echo. & pause\"", info.Arguments);
        Assert.Equal("defenseclaw keys fill-missing --yes", CredentialsViewModel.FillMissingCommandText);
        Assert.Empty(h.Reads);

        h.Credentials.NotifyReturned();
        UiWait(() => h.Reads.Count == 1);
        Assert.Equal("keys list --json", h.Reads[0]);

        // Only a console that was opened arms the re-read: a second activation does nothing.
        h.Credentials.NotifyReturned();
        Assert.Single(h.Reads);
    }

    [Fact]
    public async Task Returning_without_having_opened_a_console_reads_nothing()
    {
        using var h = new Harness();

        h.Credentials.NotifyReturned();
        await Task.Yield();

        Assert.Empty(h.Reads);
    }

    [Fact]
    public async Task A_missing_cli_or_a_failed_launch_is_reported_and_records_nothing()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var notFound = new CredentialsViewModel(services); // the real resolver over an empty PATH

        await notFound.OpenFillMissingInTerminalAsync();

        Assert.Equal("Bad", notFound.NoteKey);
        Assert.Contains("not on PATH", notFound.Note, StringComparison.Ordinal);

        var failing = new CredentialsViewModel(services, new CredentialTerminal(services.Paths)
        {
            ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
            Launch = _ => "The console could not be started: nope.",
        });
        await failing.OpenFillMissingInTerminalAsync();

        Assert.Contains("nope", failing.Note, StringComparison.Ordinal);
        Assert.Empty(services.Cli.Activity);
    }

    [Theory]
    [InlineData("EXAMPLE_KEY", true)]
    [InlineData("bad name", false)]
    [InlineData("A&calc", false)]
    [InlineData("A\"B", false)]
    [InlineData("", false)]
    public void Only_an_environment_variable_name_can_be_set_from_a_row(string name, bool expected)
    {
        var row = new CredentialRowViewModel(new CredentialRow(name, name, "F", "required", "unset", false, string.Empty), null);

        Assert.Equal(expected, row.CanSet);
        Assert.Equal(expected, row.SetInTerminalCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("keys", "set", "NAME&calc")]
    [InlineData("keys", "set", "NAME|more")]
    [InlineData("keys", "set", "%PATH%")]
    [InlineData("keys", "set", "A B")]
    [InlineData("keys", "set", "")]
    public async Task A_token_that_could_change_the_console_command_line_is_refused(string verb, string subcommand, string name)
    {
        var argv = new[] { verb, subcommand, name };
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var launched = 0;
        var terminal = new CredentialTerminal(services.Paths)
        {
            ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
            Launch = _ =>
            {
                launched++;
                return null;
            },
        };

        var result = await terminal.OpenAsync(argv);

        Assert.False(result.Started);
        Assert.Equal(0, launched);
    }

    [Fact]
    public async Task An_executable_path_with_a_quote_or_a_percent_is_refused()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        foreach (var path in new[] { "C:\\a\"b\\defenseclaw.exe", @"C:\%TEMP%\defenseclaw.exe" })
        {
            var launched = 0;
            var terminal = new CredentialTerminal(services.Paths)
            {
                ResolveExecutable = () => Task.FromResult<string?>(path),
                Launch = _ =>
                {
                    launched++;
                    return null;
                },
            };

            var result = await terminal.OpenAsync(new[] { "keys", "set", "EXAMPLE_KEY" });

            Assert.False(result.Started);
            Assert.Equal(0, launched);
        }
    }

    private static void UiWait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), "the condition was not reached");
    }
}
