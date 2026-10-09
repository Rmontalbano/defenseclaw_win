using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// Setting a credential from the Credentials card without leaving the app (CUST-221): the masked box, the review with the value masked, the
/// run, and every way out - the installation guard first, the console window when the pseudo-console cannot be used, the CLI saying no. The
/// run is a fake (no pseudo-console starts, no <c>defenseclaw</c> is run, no window opens), and every name and value is synthetic. The point of
/// each test is as much what is <i>not</i> there as what is: the value must be in no string any surface of the card, the review or Activity
/// can show.
/// </summary>
public sealed class CredentialsInAppTests
{
    private const string Value = "synthetic-test-value";
    private const string SecondValue = "synthetic-second-value-0123";
    private const string Judge = "EXAMPLE_JUDGE_KEY";
    private const string Scanner = "EXAMPLE_SCANNER_KEY";
    private const string Telemetry = "EXAMPLE_TELEMETRY_KEY";
    private const string FromEnvironment = "EXAMPLE_ENV_KEY";
    private const string Reviewer = "EXAMPLE_REVIEWER_KEY";

    private sealed record Row(string Name, string Requirement, string Source, bool Set);

    private sealed class Harness : IDisposable
    {
        private readonly TempDirectory _temp = new();

        /// <param name="installation">Makes the installation the composition starts with from the scratch folder it treats as its data directory (null: the usual writable one).</param>
        public Harness(Func<string, InstallationContext>? installation = null, Func<bool>? pty = null, IEnumerable<Row>? rows = null)
        {
            Services = TestServices.Create(_temp, installation: installation?.Invoke(_temp.Path));
            Rows = rows?.ToList() ?? new List<Row>
            {
                new(Judge, "required", "unset", false),
                new(Scanner, "required", "dotenv", true),
                new(Telemetry, "optional", "unset", false),
                new(FromEnvironment, "required", "env", true),
                new(Reviewer, "required", "unset", false),
            };

            var terminal = new CredentialTerminal(Services.Paths)
            {
                ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
                Launch = info =>
                {
                    Launched.Add(info);
                    return null;
                },
            };
            Credentials = new CredentialsViewModel(Services, terminal)
            {
                RunRead = argv =>
                {
                    Reads.Add(string.Join(' ', argv));
                    return Task.FromResult(Finished(argv, Listing()));
                },
                RunSet = (name, value, _) =>
                {
                    Stored.Add((name, value.Reveal()));
                    return Task.FromResult(Respond(name));
                },
                PtyAvailable = pty ?? (() => true),
            };
        }

        public AppServices Services { get; }

        public CredentialsViewModel Credentials { get; }

        /// <summary>The scratch folder the composition treats as its data directory: what a managed installation is made at.</summary>
        public string DataPath => _temp.Path;

        public List<Row> Rows { get; }

        public List<string> Reads { get; } = new();

        public List<ProcessStartInfo> Launched { get; } = new();

        /// <summary>What the fake run was handed. Synthetic values, kept only so a test can say the run got them.</summary>
        public List<(string Name, string Value)> Stored { get; } = new();

        /// <summary>What the fake run answers for a name; the default is the CLI storing it.</summary>
        public Func<string, SecretPtyResult>? Answer { get; set; }

        public DiscoverActionReview Review => Credentials.Review;

        private string Listing() => JsonSerializer.Serialize(Rows.Select(r => new
        {
            env_name = r.Name,
            canonical_env_name = r.Name,
            feature = "Feature of " + r.Name,
            description = "What " + r.Name + " is for",
            requirement = r.Requirement,
            source = r.Source,
            set = r.Set,
        }));

        private SecretPtyResult Respond(string name)
        {
            if (Answer is { } answer)
            {
                return answer(name);
            }

            // The CLI stored it: the next read of the list shows it.
            var index = Rows.FindIndex(r => r.Name == name);
            if (index >= 0)
            {
                Rows[index] = Rows[index] with { Source = "dotenv", Set = true };
            }

            return Result(SecretPtyOutcome.Completed, name, exitCode: 0, typed: 1, lines: new[] { "  " + name + ":", "  OK Saved " + name + " = ***REDACTED*** to the .env" });
        }

        /// <summary>The operator's gesture: Set, type, Review.</summary>
        public Task OpenAndReview(CredentialRowViewModel row, string value = Value)
        {
            row.SetCommand.Execute(null);
            row.SetEntry(Secure(value));
            row.ReviewCommand.Execute(null);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Services.Dispose();
            _temp.Dispose();
        }
    }

    private static SecureString Secure(string text)
    {
        var secure = new SecureString();
        foreach (var c in text)
        {
            secure.AppendChar(c);
        }

        return secure;
    }

    private static CliInvocation Finished(IReadOnlyList<string> argv, string output, int exitCode = 0)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        foreach (var line in output.Split('\n'))
        {
            InvocationFactory.Append(invocation, line.TrimEnd('\r'));
        }

        InvocationFactory.Finish(invocation, exitCode);
        return invocation;
    }

    private static SecretPtyResult Result(SecretPtyOutcome outcome, string name, int? exitCode, int typed, IEnumerable<string>? lines = null, string? failure = null, string message = "")
    {
        var invocation = InvocationFactory.Create(false, "keys", "set", name);
        InvocationFactory.UsePromptSecret(invocation);
        foreach (var line in lines ?? Array.Empty<string>())
        {
            InvocationFactory.Append(invocation, line);
        }

        if (failure is not null)
        {
            InvocationFactory.Fail(invocation, failure);
        }
        else
        {
            InvocationFactory.Finish(invocation, exitCode ?? 0);
        }

        return new SecretPtyResult(outcome, invocation, typed, 1, message.Length > 0 ? message : (failure ?? "The command finished."));
    }

    /// <summary>Every string the card, its rows, the review and Activity can show: the value must be in none of them.</summary>
    private static void AssertNothingHolds(Harness h, params string[] hidden)
    {
        var shown = new List<string?>
        {
            h.Credentials.Note,
            h.Credentials.Error,
            h.Credentials.SummaryText,
            h.Credentials.FooterText,
            h.Credentials.FillMissingButtonToolTip,
            h.Credentials.FillReviewText,
            h.Credentials.FillReviewToolTip,
            h.Review.ResultText,
            h.Review.ResultOutput,
            h.Review.AcknowledgementText,
        };

        foreach (var row in h.Credentials.Rows)
        {
            shown.AddRange(new[]
            {
                row.EnvName, row.Feature, row.Description, row.Source, row.SetText, row.SetToolTip, row.SetButtonToolTip, row.SetButtonAutomationName,
                row.SetCommandText, row.EntryStatus, row.EntryProblem, row.EntryAutomationName, row.ReviewToolTip, row.ReviewAutomationName,
                row.RowAutomationName, row.ToString(),
            });
        }

        if (h.Review.CommandReview is { } review)
        {
            shown.AddRange(new[] { review.Title, review.Summary, review.CommandText, review.ClipboardText, review.AutomationName, review.AutomationHelp, review.ConfirmLabel });
            foreach (var step in review.Steps)
            {
                shown.AddRange(new[] { step.CommandText, step.ClipboardText, step.Purpose, step.StatusText, step.AutomationName, step.ToString() });
                shown.AddRange(step.Argv);
            }

            foreach (var warning in review.Warnings)
            {
                shown.AddRange(new[] { warning.Title, warning.Message });
            }
        }

        foreach (var entry in h.Services.Cli.Activity)
        {
            shown.AddRange(new[] { entry.CommandLine, entry.PowerShellCommandLine, entry.FailureReason, entry.EnvironmentDisplay });
            shown.AddRange(entry.Argv);
            shown.AddRange(entry.OutputLines.Select(l => l.Text));
        }

        foreach (var secret in hidden)
        {
            Assert.DoesNotContain(shown, text => text is not null && text.Contains(secret, StringComparison.Ordinal));
        }
    }

    // ----------------------------------------------------------------------------------------------------------- Set

    [Fact]
    public async Task Set_opens_a_masked_box_and_nothing_is_stored_until_the_review_is_confirmed()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        Assert.Equal(Judge, row.EnvName);
        h.Reads.Clear();

        Assert.True(row.OffersInApp);
        Assert.True(h.Credentials.InAppAvailable);
        Assert.True(row.ShowTerminalButton);
        Assert.False(row.ShowEditor);
        Assert.Equal("Set EXAMPLE_JUDGE_KEY", row.SetButtonAutomationName);

        row.SetCommand.Execute(null);
        Assert.True(row.IsEditing);
        Assert.True(row.ShowEditor);
        Assert.False(row.HasEntry);
        Assert.False(row.ReviewCommand.CanExecute(null));
        Assert.Empty(h.Launched);

        row.SetEntry(Secure(Value));
        Assert.True(row.HasEntry);
        Assert.Equal(Value.Length, row.EntryLength);
        Assert.Contains("20 characters", row.EntryStatus, StringComparison.Ordinal);
        Assert.True(row.ReviewCommand.CanExecute(null));

        // Nothing has run: not a read, not a console, not a store.
        Assert.Empty(h.Stored);
        Assert.Empty(h.Reads);
        Assert.Empty(h.Launched);
        Assert.Empty(h.Services.Cli.Activity);
        Assert.False(h.Review.IsOpen);

        row.ReviewCommand.Execute(null);

        Assert.True(h.Review.IsOpen);
        var review = h.Review.CommandReview!;
        Assert.Equal("Store a value for EXAMPLE_JUDGE_KEY?", review.Title);
        Assert.Equal("Store value", review.ConfirmLabel);
        var step = Assert.Single(review.Steps);
        Assert.Equal(new[] { "keys", "set", Judge }, step.Argv);
        Assert.Equal(CommandTier.StateChanging, step.Tier);
        Assert.Equal(CommandTier.StateChanging, review.Tier);
        Assert.Equal("defenseclaw keys set EXAMPLE_JUDGE_KEY", review.CommandText);
        Assert.False(review.IsBlocked);

        // The value is a mask and a count, in the review's own words.
        Assert.Contains(CredentialsViewModel.MaskedValue, review.Summary, StringComparison.Ordinal);
        Assert.Contains("20 characters, hidden", review.Summary, StringComparison.Ordinal);
        Assert.Contains("not on the command line", review.Summary, StringComparison.Ordinal);

        // Still nothing has run: the review is the last step before it.
        Assert.Empty(h.Stored);
        Assert.Empty(h.Reads);
        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task Confirming_the_review_stores_the_value_drops_it_and_reads_the_list_again()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        await h.OpenAndReview(row);
        h.Reads.Clear();

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        // The run got the name and the value, once.
        Assert.Equal(new[] { (Judge, Value) }, h.Stored);
        Assert.True(h.Review.IsFinished);
        Assert.Equal("Ok", h.Review.ResultKey);
        Assert.Equal("Succeeded (exit 0)", h.Review.CommandReview!.Steps[0].StatusText);

        // The box is empty and closed; nothing is held.
        Assert.False(row.HasEntry);
        Assert.False(row.IsEditing);
        Assert.Equal(0, row.EntryLength);

        // The list was read again, so the row now says the CLI sees the value.
        Assert.Equal(new[] { "keys list --json" }, h.Reads);
        var refreshed = Assert.Single(h.Credentials.Rows, r => r.EnvName == Judge);
        Assert.Equal("✓ set", refreshed.SetText);
        Assert.Equal("dotenv", refreshed.Source);
        Assert.Equal("Ok", h.Credentials.NoteKey);
        Assert.Equal("Stored EXAMPLE_JUDGE_KEY. The value is shown nowhere; the row now reads set.", h.Credentials.Note);
        Assert.Empty(h.Launched);

        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task Cancelling_the_review_keeps_the_box_and_nothing_runs_and_cancelling_the_box_forgets_the_value()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        await h.OpenAndReview(row);

        h.Review.DismissCommand.Execute(null);

        Assert.False(h.Review.IsOpen);
        Assert.True(row.IsEditing);
        Assert.True(row.HasEntry); // still being edited: the operator may review again
        Assert.Empty(h.Stored);

        row.CancelEntryCommand.Execute(null);

        Assert.False(row.IsEditing);
        Assert.False(row.HasEntry);
        Assert.False(row.ReviewCommand.CanExecute(null));
        Assert.Empty(h.Stored);
        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task The_password_box_is_told_to_empty_whenever_the_row_drops_a_value()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        var emptied = 0;
        row.EntryCleared += (_, _) => emptied++;
        row.SetCommand.Execute(null);

        row.SetEntry(Secure(Value));
        row.CancelEntryCommand.Execute(null);
        Assert.Equal(1, emptied);

        // Nothing held: nothing to empty.
        row.CancelEntryCommand.Execute(null);
        Assert.Equal(1, emptied);
    }

    [Theory]
    [InlineData("first line\nsecond line", true)]
    [InlineData("tab\tseparated", true)]
    [InlineData("", false)]
    [InlineData("    ", false)]
    public async Task A_value_the_cli_would_refuse_or_nothing_is_not_held(string typed, bool problem)
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        row.SetCommand.Execute(null);

        row.SetEntry(Secure(typed));

        Assert.False(row.HasEntry);
        Assert.Equal(problem, row.HasEntryProblem);
        Assert.False(row.ReviewCommand.CanExecute(null));
        if (typed.Trim().Length > 0)
        {
            Assert.DoesNotContain(typed.Trim(), row.EntryStatus, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_value_longer_than_any_credential_is_not_held_and_the_padding_of_a_paste_is_not_counted()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        row.SetCommand.Execute(null);

        row.SetEntry(Secure(new string('x', CredentialRowViewModel.MaxValueLength + 1)));
        Assert.False(row.HasEntry);
        Assert.Contains("longer than " + CredentialRowViewModel.MaxValueLength.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), row.EntryProblem, StringComparison.Ordinal);

        row.SetEntry(Secure("  " + Value + "  "));
        Assert.True(row.HasEntry);
        Assert.Equal(Value.Length, row.EntryLength);
        Assert.Equal(string.Empty, row.EntryProblem);

        // The longest value is accepted.
        row.SetEntry(Secure(new string('x', CredentialRowViewModel.MaxValueLength)));
        Assert.True(row.HasEntry);
    }

    [Fact]
    public async Task Reading_the_list_again_closes_every_box_and_drops_what_was_typed()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        row.SetCommand.Execute(null);
        row.SetEntry(Secure(Value));
        Assert.True(row.HasEntry);

        await h.Credentials.RefreshAsync();

        Assert.False(row.HasEntry);
        Assert.False(row.IsEditing);
        Assert.NotSame(row, h.Credentials.Rows[0]);
        Assert.False(h.Credentials.Rows[0].IsEditing);
    }

    [Fact]
    public async Task Leaving_the_page_drops_what_was_typed()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        row.SetCommand.Execute(null);
        row.SetEntry(Secure(Value));
        await h.Credentials.BeginFillAsync();
        Assert.True(h.Credentials.IsFilling);

        h.Credentials.CancelEntries();

        Assert.False(row.HasEntry);
        Assert.False(row.IsEditing);
        Assert.False(h.Credentials.IsFilling);
        Assert.Equal(0, h.Credentials.FillEntryCount);
    }

    [Fact]
    public async Task The_review_says_when_it_replaces_a_stored_value_and_when_the_environment_would_still_win()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();

        var stored = h.Credentials.Rows.Single(r => r.EnvName == Scanner);
        await h.OpenAndReview(stored);
        var replaces = Assert.Single(h.Review.CommandReview!.Warnings);
        Assert.Equal("Replaces a stored value", replaces.Title);
        Assert.Contains("EXAMPLE_SCANNER_KEY already has a value in ~/.defenseclaw/.env", replaces.Message, StringComparison.Ordinal);
        h.Review.DismissCommand.Execute(null);

        var environment = h.Credentials.Rows.Single(r => r.EnvName == FromEnvironment);
        await h.OpenAndReview(environment);
        var wins = Assert.Single(h.Review.CommandReview!.Warnings);
        Assert.Equal("The environment wins", wins.Title);
        Assert.Contains("set in this app's environment", wins.Message, StringComparison.Ordinal);
        h.Review.DismissCommand.Execute(null);

        var fresh = h.Credentials.Rows.Single(r => r.EnvName == Judge);
        await h.OpenAndReview(fresh);
        Assert.Empty(h.Review.CommandReview!.Warnings);
    }

    // ----------------------------------------------------------------------------------------------------------- the guard

    [Fact]
    public async Task On_a_read_only_installation_nothing_is_offered_and_nothing_can_be_started()
    {
        using var h = new Harness(installation: TestInstallations.ManagedAt);
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];

        Assert.False(row.CanSet);
        Assert.False(row.SetCommand.CanExecute(null));
        Assert.Equal(TestInstallations.ManagedReason, row.SetButtonToolTip);

        // Pressing it anyway does nothing; the box does not open.
        row.SetCommand.Execute(null);
        Assert.False(row.IsEditing);

        // A value that is somehow handed to the row cannot be reviewed, and a review that is asked for anyway says why.
        row.SetEntry(Secure(Value));
        Assert.False(row.ReviewCommand.CanExecute(null));
        Assert.Equal(TestInstallations.ManagedReason, row.ReviewToolTip);
        h.Credentials.ReviewSet(row);
        Assert.False(h.Review.IsOpen);

        await h.Credentials.BeginFillAsync();
        Assert.False(h.Credentials.IsFilling);
        Assert.Equal(TestInstallations.ManagedReason, h.Credentials.Note);
        Assert.Equal("Warn", h.Credentials.NoteKey);

        Assert.Empty(h.Stored);
        Assert.Empty(h.Launched);
        Assert.Empty(h.Services.Cli.Activity);
    }

    [Fact]
    public async Task A_box_that_is_open_when_the_installation_turns_read_only_closes_and_forgets()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        row.SetCommand.Execute(null);
        row.SetEntry(Secure(Value));
        await h.Credentials.BeginFillAsync();

        h.Services.Installation.Replace(TestInstallations.ManagedAt(h.DataPath));
        h.Credentials.RefreshInstallation();

        Assert.False(row.IsEditing);
        Assert.False(row.HasEntry);
        Assert.False(h.Credentials.IsFilling);
        Assert.False(row.CanSet);
        Assert.Equal(TestInstallations.ManagedReason, row.ReviewToolTip);
        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task A_review_confirmed_after_the_installation_turned_read_only_runs_nothing_and_records_one_refusal()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        await h.OpenAndReview(row);
        Assert.False(h.Review.CommandReview!.IsBlocked);

        // config.yaml was edited to managed while the review was open.
        h.Services.Installation.Replace(TestInstallations.ManagedAt(h.DataPath));
        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(h.Stored);
        Assert.Empty(h.Launched);
        Assert.True(h.Review.IsFinished);
        Assert.Equal("Bad", h.Review.ResultKey);
        Assert.StartsWith("Not run.", h.Review.ResultText, StringComparison.Ordinal);
        var refusal = Assert.Single(h.Services.Cli.Activity);
        Assert.Equal(new[] { "keys", "set", Judge }, refusal.Argv);
        Assert.StartsWith(CliRunner.RefusedPrefix, refusal.FailureReason, StringComparison.Ordinal);
        Assert.False(refusal.UsedPromptSecret);

        // The page learns of the flip, and the value that never ran is dropped with the box.
        h.Credentials.RefreshInstallation();
        Assert.False(row.HasEntry);
        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task The_card_asks_the_live_installation_before_it_opens_a_review_and_says_why_when_it_will_not()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        row.SetCommand.Execute(null);
        row.SetEntry(Secure(Value));

        // The box was opened while the installation was writable; the flip has not been noticed by the card yet.
        h.Services.Installation.Replace(TestInstallations.ManagedAt(h.DataPath));
        h.Credentials.ReviewSet(row);

        // The card asks the live answer before it opens anything.
        Assert.False(h.Review.IsOpen);
        Assert.Equal(TestInstallations.ManagedReason, h.Credentials.Note);
    }

    // ----------------------------------------------------------------------------------------------------------- the fallback

    [Fact]
    public async Task Where_the_app_cannot_type_a_value_Set_opens_the_console_window_as_before()
    {
        using var h = new Harness(pty: () => false);
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];

        Assert.False(h.Credentials.InAppAvailable);
        Assert.False(row.OffersInApp);
        Assert.False(row.ShowTerminalButton);
        Assert.False(h.Credentials.ShowFillTerminalButton);
        Assert.Equal("Set EXAMPLE_JUDGE_KEY in a terminal", row.SetButtonAutomationName);
        Assert.StartsWith("Opens a console window running", row.SetButtonToolTip, StringComparison.Ordinal);
        Assert.Equal("Fill missing credentials in a terminal", h.Credentials.FillMissingAutomationName);
        Assert.Contains("console window", h.Credentials.FooterText, StringComparison.Ordinal);

        row.SetCommand.Execute(null);
        UiWait(() => h.Launched.Count == 1 && h.Services.Cli.Activity.Count == 1);

        Assert.False(row.IsEditing);
        var info = Assert.Single(h.Launched);
        Assert.Equal("/d /s /c \"\"C:\\Tools\\defenseclaw.exe\" keys set EXAMPLE_JUDGE_KEY & echo. & pause\"", info.Arguments);
        Assert.Empty(h.Stored);
        Assert.Single(h.Services.Cli.Activity); // the hand-off entry, as before
    }

    [Fact]
    public async Task Where_the_app_cannot_type_a_value_Fill_missing_hands_the_tuis_argv_to_the_console()
    {
        using var h = new Harness(pty: () => false);
        await h.Credentials.RefreshAsync();

        await h.Credentials.BeginFillCommand.ExecuteAsync(null);

        Assert.False(h.Credentials.IsFilling);
        var info = Assert.Single(h.Launched);
        Assert.Equal("/d /s /c \"\"C:\\Tools\\defenseclaw.exe\" keys fill-missing --yes & echo. & pause\"", info.Arguments);
        Assert.All(h.Credentials.Rows, r => Assert.False(r.IsEditing));
    }

    [Fact]
    public async Task A_run_that_could_not_use_the_pseudo_console_with_nothing_typed_opens_the_console_automatically()
    {
        using var h = new Harness();
        h.Answer = name => Result(SecretPtyOutcome.PromptNotSeen, name, exitCode: null, typed: 0, failure: "the command did not show its hidden prompt within 30 s - process tree killed", message: "the command did not show its hidden prompt within 30 s - process tree killed");
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        await h.OpenAndReview(row);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        // The route failed and nothing was typed: the same command is handed to a console window, and the card remembers.
        var info = Assert.Single(h.Launched);
        Assert.Equal("/d /s /c \"\"C:\\Tools\\defenseclaw.exe\" keys set EXAMPLE_JUDGE_KEY & echo. & pause\"", info.Arguments);
        Assert.False(h.Credentials.InAppAvailable);
        Assert.False(row.OffersInApp);
        Assert.False(row.ShowEditor);
        Assert.Contains("console window was opened instead", h.Credentials.Note, StringComparison.Ordinal);
        Assert.Contains("did not show its hidden prompt", h.Credentials.Note, StringComparison.Ordinal);
        Assert.Equal("Warn", h.Credentials.NoteKey);
        Assert.False(row.HasEntry);

        // Nothing was stored by the app, and the next Set goes straight to the console.
        Assert.Equal(new[] { (Judge, Value) }, h.Stored);
        Assert.Equal("Fill missing credentials in a terminal", h.Credentials.FillMissingAutomationName);
        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task A_run_that_never_started_is_reported_in_the_review_and_recorded_and_falls_back_too()
    {
        using var h = new Harness();
        h.Answer = name => new SecretPtyResult(SecretPtyOutcome.Unavailable, null, 0, 1, "This version of Windows has no pseudo-console.");
        await h.Credentials.RefreshAsync();
        await h.OpenAndReview(h.Credentials.Rows[0]);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Bad", h.Review.ResultKey);
        var entry = Assert.Single(h.Services.Cli.Activity, a => a.FailureReason is not null);
        Assert.StartsWith(CliRunner.RefusedPrefix, entry.FailureReason, StringComparison.Ordinal);
        Assert.Contains("no pseudo-console", entry.FailureReason, StringComparison.Ordinal);
        Assert.Single(h.Launched);
        Assert.False(h.Credentials.InAppAvailable);
    }

    [Fact]
    public async Task A_cli_that_says_no_after_the_value_was_typed_is_its_answer_and_not_a_reason_for_a_console()
    {
        using var h = new Harness();
        h.Answer = name => Result(SecretPtyOutcome.Completed, name, exitCode: 1, typed: 1, lines: new[] { "  " + name + ":", "  error: could not write the file" }, message: "The command exited with code 1.");
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        await h.OpenAndReview(row);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(h.Launched);
        Assert.True(h.Credentials.InAppAvailable);
        Assert.Equal("Bad", h.Review.ResultKey);
        Assert.Equal("Failed (exit 1)", h.Review.CommandReview!.Steps[0].StatusText);
        Assert.Equal("Bad", h.Credentials.NoteKey);
        Assert.Contains("EXAMPLE_JUDGE_KEY was not stored: The command exited with code 1.", h.Credentials.Note, StringComparison.Ordinal);
        Assert.Contains("Activity", h.Credentials.Note, StringComparison.Ordinal);
        Assert.False(row.HasEntry); // dropped when the run started, kept by nobody
        Assert.Contains("error: could not write the file", h.Review.ResultOutput, StringComparison.Ordinal);
        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task A_cli_that_is_not_installed_is_reported_by_the_review_and_nothing_is_stored_or_opened()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        h.Credentials.RunSet = (_, _, _) => throw new CliNotFoundException("defenseclaw", new[] { @"C:\Tools\defenseclaw.exe" });
        var row = h.Credentials.Rows[0];
        await h.OpenAndReview(row);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.True(h.Review.IsFinished);
        Assert.Equal("Bad", h.Review.ResultKey);
        Assert.Equal("Could not start", h.Review.CommandReview!.Steps[0].StatusText);
        Assert.Equal("Bad", h.Credentials.NoteKey);
        Assert.StartsWith("Nothing was stored", h.Credentials.Note, StringComparison.Ordinal);
        Assert.Empty(h.Launched);
        Assert.True(h.Credentials.InAppAvailable);
        Assert.False(row.HasEntry);
        AssertNothingHolds(h, Value);
    }

    [Fact]
    public async Task A_command_that_ended_without_asking_for_the_value_is_not_reported_as_stored()
    {
        using var h = new Harness();
        h.Answer = name => Result(SecretPtyOutcome.Completed, name, exitCode: 0, typed: 0, lines: new[] { "nothing to ask" }, message: "The command ended (exit 0) without asking for the value, so nothing was typed.");
        await h.Credentials.RefreshAsync();
        await h.OpenAndReview(h.Credentials.Rows[0]);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Bad", h.Review.ResultKey);
        Assert.Equal("Reported a problem (exit 0)", h.Review.CommandReview!.Steps[0].StatusText);
        Assert.Equal("Bad", h.Credentials.NoteKey);
        Assert.DoesNotContain("Stored", h.Credentials.Note, StringComparison.Ordinal);
        Assert.Empty(h.Launched);
    }

    [Fact]
    public async Task A_cli_that_said_yes_but_whose_list_still_shows_the_variable_unset_is_not_trusted_blindly()
    {
        using var h = new Harness();
        h.Answer = name => Result(SecretPtyOutcome.Completed, name, exitCode: 0, typed: 1, lines: new[] { "  OK Saved" }); // and the list is not changed
        await h.Credentials.RefreshAsync();
        await h.OpenAndReview(h.Credentials.Rows[0]);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Warn", h.Credentials.NoteKey);
        Assert.Contains("the list still shows EXAMPLE_JUDGE_KEY as unset", h.Credentials.Note, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------------- Fill missing

    [Fact]
    public async Task Fill_missing_opens_a_box_on_every_required_credential_that_is_unset_and_no_other()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();

        await h.Credentials.BeginFillCommand.ExecuteAsync(null);

        Assert.True(h.Credentials.IsFilling);
        Assert.Equal(
            new[] { Judge, Reviewer },
            h.Credentials.Rows.Where(r => r.IsEditing).Select(r => r.EnvName).ToArray());
        Assert.All(h.Credentials.Rows.Where(r => r.IsEditing), r => Assert.True(r.IsMissingRequired && r.ShowEditor));
        Assert.Equal(0, h.Credentials.FillEntryCount);
        Assert.False(h.Credentials.CanReviewFill);
        Assert.Equal("Review 0 values…", h.Credentials.FillReviewText);
        Assert.Empty(h.Launched);
        Assert.Empty(h.Stored);
    }

    [Fact]
    public async Task Fill_missing_reviews_one_command_per_value_typed_and_runs_them_in_order_once_confirmed()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        await h.Credentials.BeginFillCommand.ExecuteAsync(null);
        var judge = h.Credentials.Rows.Single(r => r.EnvName == Judge);
        var reviewer = h.Credentials.Rows.Single(r => r.EnvName == Reviewer);

        judge.SetEntry(Secure(Value));
        Assert.Equal(1, h.Credentials.FillEntryCount);
        Assert.Equal("Review 1 value…", h.Credentials.FillReviewText);
        reviewer.SetEntry(Secure(SecondValue));
        Assert.Equal(2, h.Credentials.FillEntryCount);
        Assert.True(h.Credentials.CanReviewFill);
        Assert.Equal("Review 2 values…", h.Credentials.FillReviewText);

        h.Credentials.ReviewFillCommand.Execute(null);

        var review = h.Review.CommandReview!;
        Assert.Equal("Store 2 values?", review.Title);
        Assert.Equal("Store 2 values", review.ConfirmLabel);
        Assert.Equal(
            new[] { new[] { "keys", "set", Judge }, new[] { "keys", "set", Reviewer } },
            review.Steps.Select(s => s.Argv.ToArray()).ToArray());
        Assert.All(review.Steps, s => Assert.Equal(CommandTier.StateChanging, s.Tier));
        Assert.Contains("EXAMPLE_JUDGE_KEY  " + CredentialsViewModel.MaskedValue + "  (20 characters)", review.Summary, StringComparison.Ordinal);
        Assert.Contains("EXAMPLE_REVIEWER_KEY  " + CredentialsViewModel.MaskedValue + "  (27 characters)", review.Summary, StringComparison.Ordinal);
        Assert.Empty(h.Stored);
        AssertNothingHolds(h, Value, SecondValue);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (Judge, Value), (Reviewer, SecondValue) }, h.Stored);
        Assert.Equal("Ok", h.Review.ResultKey);
        Assert.False(h.Credentials.IsFilling);
        Assert.All(h.Credentials.Rows, r => Assert.False(r.IsEditing || r.HasEntry));
        Assert.Equal(0, h.Credentials.MissingRequiredCount);
        Assert.Equal("Stored 2 values: EXAMPLE_JUDGE_KEY, EXAMPLE_REVIEWER_KEY. They are shown nowhere; the rows now read set.", h.Credentials.Note);
        Assert.Equal("Ok", h.Credentials.NoteKey);
        AssertNothingHolds(h, Value, SecondValue);
    }

    [Fact]
    public async Task A_box_left_empty_in_Fill_missing_is_skipped()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        await h.Credentials.BeginFillCommand.ExecuteAsync(null);
        h.Credentials.Rows.Single(r => r.EnvName == Reviewer).SetEntry(Secure(SecondValue));

        h.Credentials.ReviewFillCommand.Execute(null);
        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (Reviewer, SecondValue) }, h.Stored);
        Assert.Equal(1, h.Credentials.MissingRequiredCount); // the judge's key is still unset: it was not asked for
        Assert.Equal("Stored EXAMPLE_REVIEWER_KEY. The value is shown nowhere; the row now reads set.", h.Credentials.Note);
    }

    [Fact]
    public async Task When_the_first_value_is_refused_the_rest_are_not_tried_and_nothing_typed_is_kept()
    {
        using var h = new Harness();
        h.Answer = name => Result(SecretPtyOutcome.Completed, name, exitCode: 1, typed: 1, message: "The command exited with code 1.");
        await h.Credentials.RefreshAsync();
        await h.Credentials.BeginFillCommand.ExecuteAsync(null);
        h.Credentials.Rows.Single(r => r.EnvName == Judge).SetEntry(Secure(Value));
        h.Credentials.Rows.Single(r => r.EnvName == Reviewer).SetEntry(Secure(SecondValue));
        h.Credentials.ReviewFillCommand.Execute(null);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (Judge, Value) }, h.Stored);
        Assert.Equal("Skipped: an earlier step did not succeed", h.Review.CommandReview!.Steps[1].StatusText);
        Assert.Contains("EXAMPLE_JUDGE_KEY was not stored", h.Credentials.Note, StringComparison.Ordinal);
        Assert.Contains("1 more was not tried", h.Credentials.Note, StringComparison.Ordinal);
        Assert.All(h.Credentials.Rows, r => Assert.False(r.HasEntry));
        Assert.False(h.Credentials.IsFilling);
        AssertNothingHolds(h, Value, SecondValue);
    }

    [Fact]
    public async Task Cancelling_Fill_missing_closes_the_boxes_and_forgets_what_was_typed()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        await h.Credentials.BeginFillCommand.ExecuteAsync(null);
        h.Credentials.Rows.Single(r => r.EnvName == Judge).SetEntry(Secure(Value));

        h.Credentials.CancelFillCommand.Execute(null);

        Assert.False(h.Credentials.IsFilling);
        Assert.All(h.Credentials.Rows, r => Assert.False(r.IsEditing || r.HasEntry));
        Assert.Equal(0, h.Credentials.FillEntryCount);
        Assert.False(h.Credentials.ReviewFillCommand.CanExecute(null));
        Assert.Empty(h.Stored);
    }

    [Fact]
    public async Task Fill_missing_with_nothing_missing_says_so()
    {
        using var h = new Harness(rows: new[] { new Row(Scanner, "required", "dotenv", true) });
        await h.Credentials.RefreshAsync();

        await h.Credentials.BeginFillCommand.ExecuteAsync(null);

        Assert.False(h.Credentials.IsFilling);
        Assert.Equal("No required credential is missing.", h.Credentials.Note);
        Assert.Equal("Ok", h.Credentials.NoteKey);
    }

    [Fact]
    public async Task The_console_buttons_are_still_there_beside_the_in_app_ones()
    {
        using var h = new Harness();
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];

        Assert.True(row.ShowTerminalButton);
        Assert.True(h.Credentials.ShowFillTerminalButton);
        row.SetInTerminalCommand.Execute(null);
        UiWait(() => h.Launched.Count == 1);
        await h.Credentials.OpenFillMissingInTerminalAsync();

        Assert.Equal(2, h.Launched.Count);
        Assert.EndsWith("keys fill-missing --yes & echo. & pause\"", h.Launched[1].Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cards_change_notifications_follow_the_route()
    {
        using var h = new Harness();
        h.Answer = name => Result(SecretPtyOutcome.Unavailable, name, exitCode: null, typed: 0, failure: "could not be started in a pseudo-console: nope", message: "could not be started in a pseudo-console: nope");
        await h.Credentials.RefreshAsync();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)h.Credentials).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var rowRaised = new List<string?>();
        ((INotifyPropertyChanged)h.Credentials.Rows[0]).PropertyChanged += (_, e) => rowRaised.Add(e.PropertyName);
        await h.OpenAndReview(h.Credentials.Rows[0]);

        await h.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(nameof(CredentialsViewModel.InAppAvailable), raised);
        Assert.Contains(nameof(CredentialsViewModel.FooterText), raised);
        Assert.Contains(nameof(CredentialsViewModel.FillMissingAutomationName), raised);
        Assert.Contains(nameof(CredentialRowViewModel.OffersInApp), rowRaised);
        Assert.Contains(nameof(CredentialRowViewModel.SetButtonAutomationName), rowRaised);
    }

    [Fact]
    public void The_argv_that_stores_a_value_is_a_command_of_the_cli_and_carries_no_value()
    {
        var argv = SecretPtyRunner.SetKeyArgv(Judge);

        Assert.Equal(new[] { "keys", "set", Judge }, argv);
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(argv));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(CredentialsViewModel.FillMissingArgv));
        Assert.DoesNotContain("--value", argv); // the CLI's own flag for it would put the value on the command line
    }

    private static void UiWait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TestTimeouts.Ceiling)
        {
            Thread.Sleep(10);
        }

        Assert.True(condition(), "the condition was not reached");
    }
}
