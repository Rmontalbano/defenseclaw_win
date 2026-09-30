using System.Security;
using System.Windows.Controls;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The in-app secret route from the password box to the child's environment, and its lifetime. Every value here is
/// synthetic; nothing starts the real CLI (the isolated services cannot find one, and the one end-to-end test
/// uses cmd.exe).
/// </summary>
public class SecretEntryTests
{
    private const string Value = "synthetic-entry-token-4242";

    private static SecureString Secure(string text)
    {
        var secure = new SecureString();
        foreach (var c in text)
        {
            secure.AppendChar(c);
        }

        return secure;
    }

    private static WizardFieldViewModel Field(WizardViewModel vm, string flag) =>
        vm.Steps.SelectMany(s => s.Fields).Single(f => f.Field.Flag == flag && f.IsSecret);

    // -------------------------------------------------------------------- SecretEntry (the SecureString reader)

    [Theory]
    [InlineData("", 0, false)]
    [InlineData("   ", 0, false)]
    [InlineData("abc", 3, false)]
    [InlineData("  abc\t", 3, false)]
    [InlineData("a b", 3, false)]
    [InlineData("ab\ncd", 5, true)]
    [InlineData("ab\0cd", 5, true)]
    [InlineData("ab\tcd", 5, true)]
    [InlineData("\r\nabc\r\n", 3, false)]
    public void Inspect_reports_the_trimmed_length_and_inner_control_characters(string typed, int length, bool control)
    {
        using var secure = Secure(typed);

        var shape = SecretEntry.Inspect(secure);

        Assert.Equal(length, shape.TrimmedLength);
        Assert.Equal(control, shape.HasControlCharacter);
        Assert.Equal(length == 0, shape.IsEmpty);
    }

    [Fact]
    public void Inspect_treats_null_as_empty()
    {
        Assert.True(SecretEntry.Inspect(null).IsEmpty);
        Assert.Null(SecretEntry.ToSecret(null));
    }

    [Fact]
    public void ToSecret_trims_the_ends_and_keeps_the_middle()
    {
        using var secure = Secure("  " + Value + " \r\n");

        var secret = SecretEntry.ToSecret(secure);

        Assert.NotNull(secret);
        Assert.Equal(Value, secret!.Reveal());
        Assert.Equal(SecretValue.Redacted, secret.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("has\nnewline")]
    [InlineData("has\0nul")]
    public void ToSecret_returns_nothing_for_an_empty_or_unsendable_value(string typed)
    {
        using var secure = Secure(typed);

        Assert.Null(SecretEntry.ToSecret(secure));
    }

    // -------------------------------------------------------------------- the field view-model

    [Fact]
    public void A_splunk_token_field_offers_entry_and_names_the_variable()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");

            Assert.True(token.OffersInAppEntry);
            Assert.Equal("SPLUNK_ACCESS_TOKEN", token.InAppEnvName);
            Assert.Contains("SPLUNK_ACCESS_TOKEN", token.InAppExplanation, StringComparison.Ordinal);
            Assert.Contains("never on the command line", token.InAppExplanation, StringComparison.Ordinal);
            Assert.DoesNotContain("never takes the secret", token.CredentialExplanation, StringComparison.Ordinal);
            Assert.False(token.HasEntry);
        });
    }

    [Fact]
    public void A_terminal_only_secret_offers_no_entry_and_refuses_to_hold_one()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Llm());
            var key = Field(vm, "--api-key");

            Assert.False(key.OffersInAppEntry);
            Assert.Contains("never takes the secret", key.CredentialExplanation, StringComparison.Ordinal);

            key.SetEntry(Secure(Value));

            Assert.False(key.HasEntry);
            Assert.Null(key.MaterializeSecret());
            Assert.Null(vm.BuildRunOptions());
            Assert.Empty(vm.EnvironmentNotes);
        });
    }

    [Fact]
    public void A_typed_value_becomes_an_environment_overlay_and_only_names_are_shown()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");

            token.SetEntry(Secure("  " + Value + "  "));

            Assert.True(token.HasEntry);
            Assert.False(token.HasEntryProblem);

            var options = vm.BuildRunOptions();
            Assert.NotNull(options);
            var (name, secret) = Assert.Single(options!.EnvironmentOverlay);
            Assert.Equal("SPLUNK_ACCESS_TOKEN", name);
            Assert.Equal(Value, secret.Reveal()); // trimmed, and exactly what was typed

            // Nothing a screen can show carries the value.
            var note = Assert.Single(vm.EnvironmentNotes);
            Assert.True(vm.HasEnvironmentNotes);
            Assert.StartsWith("SPLUNK_ACCESS_TOKEN=•••", note, StringComparison.Ordinal);
            Assert.Contains("environment variable", note, StringComparison.Ordinal);
            Assert.All(
                new[] { note, token.EntryStatus, token.CredentialReviewNote, token.InAppExplanation, token.CredentialStatus, options.ToString() },
                text => Assert.DoesNotContain(Value, text, StringComparison.Ordinal));
            Assert.Contains("SPLUNK_ACCESS_TOKEN=•••", token.CredentialReviewNote, StringComparison.Ordinal);
            Assert.Contains("masked", token.CredentialReviewNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_value_never_lands_in_argv_the_command_text_or_the_answers()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");
            vm.Steps.SelectMany(s => s.Fields).Single(f => f.Field.Flag == "--o11y").IsOn = true;
            token.SetEntry(Secure(Value));

            while (!vm.IsReview)
            {
                vm.Next();
                Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
            }

            Assert.Contains("setup splunk", vm.CommandReview!.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain(Value, vm.CommandReview!.CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("--access-token", vm.CommandReview!.CommandText, StringComparison.Ordinal);
            Assert.Equal(string.Empty, token.Value);
            Assert.DoesNotContain(Value, string.Join('\n', vm.Definition.BuildArgv(new WizardValues())), StringComparison.Ordinal);

            // The review has what it needs to say how the secret travels.
            Assert.Single(vm.EnvironmentNotes);
            Assert.Equal(new[] { "SPLUNK_ACCESS_TOKEN" }, vm.BuildRunOptions()!.EnvironmentOverlay.Keys);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("     ")]
    public void An_empty_or_blank_entry_is_no_entry_and_the_run_is_an_ordinary_one(string typed)
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");

            token.SetEntry(Secure(typed));

            Assert.False(token.HasEntry);
            Assert.False(token.HasEntryProblem);
            Assert.Null(token.MaterializeSecret());
            Assert.Null(vm.BuildRunOptions()); // no overlay at all: the CLI falls back to what is stored
            Assert.Empty(vm.EnvironmentNotes);
            Assert.False(vm.HasEnvironmentNotes);
            Assert.Equal(string.Empty, token.Validate());
            Assert.Contains("Nothing entered", token.EntryStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Erasing_a_typed_value_drops_it()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");

            token.SetEntry(Secure(Value));
            Assert.True(token.HasEntry);

            token.SetEntry(Secure(string.Empty));

            Assert.False(token.HasEntry);
            Assert.Null(vm.BuildRunOptions());
            Assert.Empty(vm.EnvironmentNotes);
        });
    }

    [Fact]
    public void A_value_with_a_line_break_is_not_held_and_blocks_next_with_a_message()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");

            token.SetEntry(Secure("first-line\nsecond-line"));

            Assert.False(token.HasEntry);
            Assert.True(token.HasEntryProblem);
            Assert.Contains("control character", token.EntryProblem, StringComparison.Ordinal);
            Assert.Equal(token.EntryProblem, token.Validate());
            Assert.Null(vm.BuildRunOptions());
            Assert.DoesNotContain("first-line", token.EntryProblem, StringComparison.Ordinal);

            // Pasting a good value again clears the complaint.
            token.SetEntry(Secure(Value));
            Assert.False(token.HasEntryProblem);
            Assert.True(token.HasEntry);
        });
    }

    [Fact]
    public void Clearing_drops_the_value_tells_the_box_and_says_why()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");
            var cleared = 0;
            token.EntryCleared += (_, _) => cleared++;

            token.SetEntry(Secure(Value));
            token.ClearEntry("gone");

            Assert.False(token.HasEntry);
            Assert.Equal(1, cleared);
            Assert.True(token.HasEntryNotice);
            Assert.Equal("gone", token.EntryStatus);
            Assert.Null(vm.BuildRunOptions());

            // Clearing what is not held is a no-op: no event, no stale notice.
            token.ClearEntry("again");
            Assert.Equal(1, cleared);
        });
    }

    [Fact]
    public void A_typed_value_does_not_follow_the_operator_to_another_destination()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(
                services,
                SecretInAppRouteTests.Observability(SecretInAppRouteTests.ObservabilityAddHelpWithEnvVar));
            var preset = vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "add:arg1-preset");
            var token = Field(vm, "--token");

            preset.Value = "datadog";
            Assert.True(token.OffersInAppEntry);
            token.SetEntry(Secure(Value));
            Assert.True(token.HasEntry);

            // A different preset stores the token under a different name: the Datadog key must not be filed as a
            // Honeycomb one just because the box still had it.
            preset.Value = "honeycomb";

            Assert.False(token.HasEntry);
            Assert.Contains("destination changed", token.EntryStatus, StringComparison.Ordinal);
            Assert.Null(vm.BuildRunOptions());

            // Choosing a preset that has no token at all removes the route (and the box) too.
            token.SetEntry(Secure(Value));
            preset.Value = "some-new-vendor";
            Assert.False(token.OffersInAppEntry);
            Assert.False(token.HasEntry);
        });
    }

    [Fact]
    public void A_secret_on_a_page_the_answers_have_hidden_is_not_supplied()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(
                services,
                SecretInAppRouteTests.Observability(SecretInAppRouteTests.ObservabilityAddHelpWithEnvVar));
            var subcommand = vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "subcommand");
            var preset = vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "add:arg1-preset");
            var token = Field(vm, "--token");
            preset.Value = "datadog";
            token.SetEntry(Secure(Value));
            Assert.NotNull(vm.BuildRunOptions());

            // "list" does not take --token; the field is gated off, so its entry cannot ride along.
            subcommand.Value = "list";

            Assert.False(token.IsVisible);
            Assert.Null(vm.BuildRunOptions());
            Assert.Empty(vm.EnvironmentNotes);
        });
    }

    // -------------------------------------------------------------------- the run

    [Fact]
    public void A_run_clears_what_was_typed_however_it_ends()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");
            var boxCleared = 0;
            token.EntryCleared += (_, _) => boxCleared++;
            vm.Steps.SelectMany(s => s.Fields).Single(f => f.Field.Flag == "--o11y").IsOn = true;
            token.SetEntry(Secure(Value));
            while (!vm.IsReview)
            {
                vm.Next();
            }

            // The isolated services have no defenseclaw on PATH, so the run ends at "not found" before anything
            // starts — an outcome that is neither success nor a real failure, and the entry must go regardless.
            // The runner looks the executable up off the calling thread, so "not found" arrives when the command's
            // task completes, not from Execute itself: wait for it.
            vm.ExecuteCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.False(vm.IsRunning);
            Assert.True(vm.HasRun);
            Assert.False(token.HasEntry);
            Assert.Equal(1, boxCleared);
            Assert.Empty(vm.EnvironmentNotes);
            Assert.Null(vm.BuildRunOptions());
            Assert.Contains(WizardViewModel.SecretClearedSentence, vm.ResultMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(Value, vm.ResultMessage, StringComparison.Ordinal);
            Assert.Contains("cleared", token.EntryStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_run_with_nothing_typed_says_nothing_about_secrets()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            vm.Steps.SelectMany(s => s.Fields).Single(f => f.Field.Flag == "--o11y").IsOn = true;
            while (!vm.IsReview)
            {
                vm.Next();
            }

            vm.ExecuteCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.True(vm.HasRun);
            Assert.DoesNotContain("cleared", vm.ResultMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_preview_does_not_receive_the_value_and_does_not_consume_it()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(
                services,
                SecretInAppRouteTests.Observability(SecretInAppRouteTests.ObservabilityAddHelpWithEnvVar));
            vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "add:arg1-preset").Value = "datadog";
            var token = Field(vm, "--token");
            token.SetEntry(Secure(Value));
            while (!vm.IsReview)
            {
                vm.Next();
            }

            Assert.True(vm.CanPreview);
            vm.PreviewCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            // The preview ran (and, with no CLI on PATH, stopped at "not found"), but the secret is for the real
            // command only: still held, and the result line says a preview leaves it alone.
            Assert.True(vm.HasRun);
            Assert.True(token.HasEntry);
            Assert.Contains("not used by a preview", vm.ResultMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(Value, vm.ResultMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Closing_the_wizard_drops_the_value()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");
            token.SetEntry(Secure(Value));
            Assert.True(token.HasEntry);

            vm.Dispose();

            Assert.False(token.HasEntry);
            Assert.Null(token.MaterializeSecret());
        });
    }

    // -------------------------------------------------------------------- the password box

    [Fact]
    public void Typing_in_the_password_box_reaches_the_field_as_a_secure_entry_and_clearing_empties_the_box()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");
            var box = new PasswordBox { DataContext = token };
            SecretEntryBox.SetAttach(box, true);

            box.Password = Value;

            Assert.True(token.HasEntry);
            Assert.Equal(Value, token.MaterializeSecret()!.Reveal());
            Assert.Equal(string.Empty, token.Value); // the box never wrote to the string property

            // The app drops the value (a run ended): the box must not keep showing one.
            token.ClearEntry("cleared");

            Assert.Equal(string.Empty, box.Password);
            Assert.False(token.HasEntry);
            Assert.Equal("cleared", token.EntryStatus);

            // Detached: later clears no longer touch the box.
            SecretEntryBox.SetAttach(box, false);
            box.Password = "left-alone";
            token.SetEntry(Secure(Value));
            token.ClearEntry("again");
            Assert.Equal("left-alone", box.Password);
        });
    }

    // -------------------------------------------------------------------- end to end, with a real child

    [Fact]
    public async Task The_typed_value_reaches_a_real_child_only_through_its_environment()
    {
        using var temp = new TempDirectory();
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        // Built on an STA thread (the view-model owns a DispatcherTimer); the child then runs here.
        var (options, argv) = StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, SecretInAppRouteTests.Splunk());
            var token = Field(vm, "--access-token");
            token.SetEntry(Secure(Value));
            return (vm.BuildRunOptions()!, new[] { "/c", "set", "SPLUNK_ACCESS_TOKEN" });
        });

        using var services = TestServices.Create(temp);
        var invocation = await services.Cli.RunExecutableAsync(cmd, argv, options: options);

        Assert.Equal(0, invocation.ExitCode);
        Assert.Contains(invocation.OutputLines, l => l.Text == $"SPLUNK_ACCESS_TOKEN={SecretValue.Redacted}");
        Assert.Equal("env: SPLUNK_ACCESS_TOKEN=•••", invocation.EnvironmentDisplay);
        Assert.DoesNotContain(Value, invocation.CommandLine, StringComparison.Ordinal);
        Assert.All(invocation.OutputLines, l => Assert.DoesNotContain(Value, l.Text, StringComparison.Ordinal));
    }
}
