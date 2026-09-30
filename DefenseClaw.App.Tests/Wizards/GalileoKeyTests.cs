using System.Security;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// <c>setup galileo</c>'s API key. The CLI (0.8.10, <c>cmd_setup_galileo.py</c>) reads <c>GALILEO_API_KEY</c> from the
/// environment and has no flag for it, keeps it only when <c>--persist-api-key</c> is on, and wants it in every mode —
/// a <c>--dry-run</c> included. The wizard therefore carries a flagless secret field routed to that variable, and a
/// visible "save it" choice. Every value here is synthetic; nothing starts the real CLI.
/// </summary>
public class GalileoKeyTests
{
    private const string Key = "synthetic-galileo-key-7788";

    private static SecureString Secure(string text)
    {
        var secure = new SecureString();
        foreach (var c in text)
        {
            secure.AppendChar(c);
        }

        return secure;
    }

    private static WizardField KeyField(WizardDefinition definition) =>
        definition.AllFields.Single(f => f.Id == WizardSyntheticSecrets.GalileoKeyFieldId);

    private static WizardField PersistField(WizardDefinition definition) =>
        definition.AllFields.Single(f => f.Flag == WizardSyntheticSecrets.PersistFlag);

    private static WizardFieldViewModel VmField(WizardViewModel vm, string id) =>
        vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id);

    private static void ToReview(WizardViewModel vm)
    {
        while (!vm.IsReview)
        {
            vm.Next();
            Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
        }
    }

    // -------------------------------------------------------------------- the definition

    [Fact]
    public void The_guided_flow_gets_a_key_page_with_a_secret_that_has_no_flag()
    {
        var definition = WizardSamples.Galileo();
        var key = KeyField(definition);

        Assert.Equal(WizardFieldKind.Secret, key.Kind);
        Assert.Equal("Galileo API key", key.Label);
        Assert.Null(key.Flag); // nothing about it can become a flag
        Assert.False(key.IsPositional);
        Assert.Equal("env GALILEO_API_KEY", key.FlagDisplay); // the chip says where it comes from, not "(positional)"

        // Only the guided path (no subcommand) needs the key; `enable`, `status`, ... do not.
        Assert.Equal("subcommand", key.VisibleWhenFieldId);
        Assert.Equal(new[] { string.Empty }, key.VisibleWhenValues);

        var page = definition.Steps.Single(s => s.Fields.Contains(key));
        Assert.Equal("API key", page.Title);
        Assert.Equal(new[] { string.Empty }, page.VisibleWhenValues);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_key_page_is_built_from_the_real_help_under_either_line_ending(string eol)
    {
        // The real CLI pipes CRLF on Windows; the persist option's help wraps across two lines either way.
        var definition = WizardSamples.Galileo(LineEndings.With(WizardSamples.GalileoHelp, eol));

        Assert.Equal("Galileo API key", KeyField(definition).Label);
        Assert.Equal("Save the key to DefenseClaw's .env", PersistField(definition).Label);
        Assert.Equal("--persist-api-key", PersistField(definition).Flag);
    }

    [Fact]
    public void The_key_is_routed_to_GALILEO_API_KEY_for_delivery_and_for_the_stored_check()
    {
        var definition = WizardSamples.Galileo();
        var route = KeyField(definition).Credential!;
        var values = WizardSamples.StartingValues(definition);

        Assert.Equal("GALILEO_API_KEY", route.InAppVariable(values));
        Assert.Equal("GALILEO_API_KEY", route.EnvName(values));
        Assert.Equal("Galileo API key for trace export", route.Purpose);
        Assert.Equal("--persist-api-key", route.PersistFlag);
    }

    [Fact]
    public void The_persist_flag_becomes_a_clearly_worded_choice_on_the_same_page_and_starts_off()
    {
        var definition = WizardSamples.Galileo();
        var persist = PersistField(definition);

        Assert.Equal("Save the key to DefenseClaw's .env", persist.Label);
        Assert.Equal(WizardFieldKind.Switch, persist.Kind);
        Assert.Equal("--persist-api-key", persist.FlagDisplay);
        Assert.Equal(ToggleValues.Off, persist.DefaultValue);
        Assert.Contains("nothing keeps it", persist.Help, StringComparison.Ordinal);

        Assert.Same(
            definition.Steps.Single(s => s.Fields.Contains(KeyField(definition))),
            definition.Steps.Single(s => s.Fields.Contains(persist)));

        // It moved, it was not copied: exactly one field carries the flag, and no page was left empty.
        Assert.Single(definition.AllFields, f => f.Flag == "--persist-api-key");
        Assert.All(definition.Steps, s => Assert.True(s.Fields.Count > 0 || s.Id.EndsWith("empty", StringComparison.Ordinal)));
    }

    [Fact]
    public void No_card_is_added_when_the_installed_cli_no_longer_advertises_the_variable()
    {
        // A CLI that renamed the variable would ignore GALILEO_API_KEY, so offering to supply it would be a lie.
        var help = WizardSamples.GalileoHelp.Replace("Copy GALILEO_API_KEY from the environment", "Copy the key from the environment", StringComparison.Ordinal);

        var definition = WizardSamples.Galileo(help);

        Assert.DoesNotContain(definition.AllFields, f => f.Kind == WizardFieldKind.Secret);
        Assert.Equal("Persist API key", PersistField(definition).Label); // untouched, as generated
    }

    [Fact]
    public void Other_targets_are_left_alone()
    {
        var steps = WizardSamples.Galileo().Steps;

        Assert.Same(steps, WizardSyntheticSecrets.Add("splunk", steps));
        Assert.Same(steps, WizardSyntheticSecrets.Add("datadog", steps));
    }

    [Fact]
    public void The_card_says_what_the_source_says_about_saving_and_about_previews()
    {
        var route = KeyField(WizardSamples.Galileo()).Credential!;

        Assert.Contains("--dry-run", route.IfMissing, StringComparison.Ordinal);
        Assert.Contains("changes nothing", route.IfMissing, StringComparison.Ordinal);
        Assert.Contains("only when \"Save the key to DefenseClaw's .env\" is on", route.InAppStorage, StringComparison.Ordinal);
        Assert.Contains("preview passes only if the key is already stored", route.PreviewNote, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- argv

    [Fact]
    public void A_value_put_in_the_answers_still_never_reaches_argv()
    {
        var definition = WizardSamples.Galileo();
        var values = WizardSamples.StartingValues(definition);
        values[WizardSyntheticSecrets.GalileoKeyFieldId] = Key;

        var argv = definition.BuildArgv(values);

        Assert.All(argv, a => Assert.DoesNotContain(Key, a, StringComparison.Ordinal));
        Assert.DoesNotContain(argv, a => a.Contains("api-key", StringComparison.OrdinalIgnoreCase) && a != "--persist-api-key");
        Assert.DoesNotContain("--persist-api-key", argv); // and the choice is off until it is made
    }

    [Fact]
    public void Turning_the_save_choice_on_is_the_only_thing_that_adds_the_flag()
    {
        var definition = WizardSamples.Galileo();
        var values = WizardSamples.StartingValues(definition);
        var persist = PersistField(definition);

        Assert.DoesNotContain("--persist-api-key", definition.BuildArgv(values));

        values[persist.Id] = ToggleValues.On;
        var on = definition.BuildArgv(values);
        Assert.Single(on, a => a == "--persist-api-key");
        Assert.Equal("setup", on[0]);
        Assert.Equal("galileo", on[1]);

        values[persist.Id] = ToggleValues.Off;
        Assert.DoesNotContain("--persist-api-key", definition.BuildArgv(values));

        // The key page is a guided-path page: another subcommand carries neither the flag nor a secret.
        values[persist.Id] = ToggleValues.On;
        values["subcommand"] = "status";
        Assert.DoesNotContain("--persist-api-key", definition.BuildArgv(values));
        Assert.DoesNotContain(definition.VisibleCredentials(values), f => f.Id == WizardSyntheticSecrets.GalileoKeyFieldId);
    }

    // -------------------------------------------------------------------- the wizard

    [Fact]
    public void A_typed_key_is_supplied_as_GALILEO_API_KEY_and_the_review_shows_it_masked()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);

            Assert.True(key.OffersInAppEntry);
            Assert.Equal("GALILEO_API_KEY", key.InAppEnvName);
            Assert.Contains("GALILEO_API_KEY", key.InAppExplanation, StringComparison.Ordinal);
            Assert.DoesNotContain("the CLI stores it in ~/.defenseclaw/.env.", key.InAppExplanation, StringComparison.Ordinal); // it does not, by default
            Assert.Contains("only when \"Save the key to DefenseClaw's .env\" is on", key.InAppExplanation, StringComparison.Ordinal);

            key.SetEntry(Secure("  " + Key + " "));
            ToReview(vm);

            var options = vm.BuildRunOptions();
            Assert.NotNull(options);
            var (name, secret) = Assert.Single(options!.EnvironmentOverlay);
            Assert.Equal("GALILEO_API_KEY", name);
            Assert.Equal(Key, secret.Reveal());

            // The review's environment note carries the name and the mask, never the value.
            var note = Assert.Single(vm.EnvironmentNotes);
            Assert.StartsWith("GALILEO_API_KEY=•••", note, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, note, StringComparison.Ordinal);

            // The credential list on the review page has the key too.
            Assert.True(vm.HasReviewCredentials);
            Assert.Contains(vm.ReviewCredentials, f => f.Id == WizardSyntheticSecrets.GalileoKeyFieldId);
            Assert.Contains("GALILEO_API_KEY=•••", key.CredentialReviewNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_key_never_lands_in_the_command_the_answers_or_any_status_text()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);
            key.SetEntry(Secure(Key));
            ToReview(vm);

            var command = vm.CommandReview!.CommandText;
            Assert.Contains("setup galileo", command, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, command, StringComparison.Ordinal);
            Assert.DoesNotContain("GALILEO_API_KEY", command, StringComparison.Ordinal); // not even the name: it is not argv
            Assert.Equal(string.Empty, key.Value);

            Assert.All(
                new[] { key.EntryStatus, key.CredentialReviewNote, key.InAppExplanation, key.CredentialStatus, key.PersistSentence, vm.EnvironmentNotes.Single() },
                text => Assert.DoesNotContain(Key, text, StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("      ")]
    public void A_blank_key_is_no_overlay_no_note_and_no_flag(string typed)
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);

            key.SetEntry(Secure(typed));
            ToReview(vm);

            Assert.False(key.HasEntry);
            Assert.Null(key.MaterializeSecret());
            Assert.Null(vm.BuildRunOptions()); // an ordinary run: the CLI uses what is stored
            Assert.Empty(vm.EnvironmentNotes);
            Assert.False(vm.HasEnvironmentNotes);
            Assert.Equal(string.Empty, key.Validate()); // leaving it blank is allowed
            Assert.DoesNotContain("--persist-api-key", vm.CommandReview!.CommandText, StringComparison.Ordinal);
            Assert.False(VmField(vm, PersistField(vm.Definition).Id).IsOn);
        });
    }

    [Fact]
    public void A_key_with_a_line_break_is_refused_here_rather_than_by_the_cli()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);

            key.SetEntry(Secure("first\nsecond"));

            Assert.False(key.HasEntry);
            Assert.True(key.HasEntryProblem);
            Assert.Null(vm.BuildRunOptions());
        });
    }

    // -------------------------------------------------------------------- saving the key

    [Fact]
    public void Typing_a_key_switches_saving_on_because_otherwise_the_key_is_lost()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);
            var save = VmField(vm, PersistField(vm.Definition).Id);
            Assert.False(save.IsOn);

            key.SetEntry(Secure(Key));

            Assert.True(save.IsOn);
            Assert.Equal(ToggleValues.On, save.Value);
            Assert.True(save.IsChanged); // shows up in the review's "changed" list

            ToReview(vm);
            Assert.Contains("--persist-api-key", vm.CommandReview!.CommandText, StringComparison.Ordinal);
            Assert.Contains(vm.ReviewChanges, c => c.StartsWith("Save the key to DefenseClaw's .env:", StringComparison.Ordinal));

            var note = Assert.Single(vm.EnvironmentNotes);
            Assert.Contains("will save it to ~/.defenseclaw/.env", note, StringComparison.Ordinal);
            Assert.Contains("will save it", key.EntryStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_operator_can_turn_saving_off_and_typing_again_does_not_undo_that()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);
            var save = VmField(vm, PersistField(vm.Definition).Id);

            key.SetEntry(Secure(Key));
            Assert.True(save.IsOn);

            save.IsOn = false; // use it once, keep nothing

            var note = Assert.Single(vm.EnvironmentNotes);
            Assert.Contains("will not be saved (--persist-api-key is off)", note, StringComparison.Ordinal);
            Assert.Contains("will not be saved", key.EntryStatus, StringComparison.Ordinal);

            key.ClearEntry();
            key.SetEntry(Secure(Key));

            Assert.False(save.IsOn);
            ToReview(vm);
            Assert.DoesNotContain("--persist-api-key", vm.CommandReview!.CommandText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_choice_made_before_typing_is_respected()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);
            var save = VmField(vm, PersistField(vm.Definition).Id);

            save.IsOn = true;
            save.IsOn = false;
            key.SetEntry(Secure(Key));

            Assert.False(save.IsOn);
        });
    }

    // -------------------------------------------------------------------- a stored key

    [Fact]
    public void A_stored_key_is_reported_and_leaving_the_box_blank_is_a_normal_run()
    {
        using var temp = new TempDirectory();
        _ = temp.WriteFile(".env", "GALILEO_API_KEY=synthetic-stored-key-0001\n");
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);

            Assert.Equal("Ok", key.CredentialStatusKey);
            Assert.Contains("GALILEO_API_KEY has a value", key.CredentialStatus, StringComparison.Ordinal);
            Assert.Contains("already stored", key.EntryStatus, StringComparison.Ordinal);
            Assert.Contains("leave this blank", key.EntryStatus, StringComparison.Ordinal);
            Assert.All(new[] { key.CredentialStatus, key.EntryStatus, key.CredentialReviewNote }, t => Assert.DoesNotContain("synthetic-stored", t, StringComparison.Ordinal));

            ToReview(vm);

            Assert.Null(vm.BuildRunOptions());
            Assert.Empty(vm.EnvironmentNotes);
            Assert.True(vm.CanExecute);
        });
    }

    // -------------------------------------------------------------------- previews and the run

    [Fact]
    public void A_preview_is_never_given_the_key_and_says_what_that_means()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);
            key.SetEntry(Secure(Key));
            ToReview(vm);

            // The one decision point: the real run gets the variable, a preview gets nothing.
            var real = vm.RunOptionsFor(preview: false);
            Assert.NotNull(real);
            Assert.Equal(new[] { "GALILEO_API_KEY" }, real!.EnvironmentOverlay.Keys);
            Assert.Null(vm.RunOptionsFor(preview: true));

            Assert.True(vm.CanPreview);
            vm.PreviewCommand.Execute(null);

            // The preview ran (and, with no CLI on PATH, stopped at "not found"); the key stayed with the box for Execute.
            Assert.True(vm.HasRun);
            Assert.True(key.HasEntry);
            Assert.Contains("not used by a preview", vm.ResultMessage, StringComparison.Ordinal);
            Assert.Contains("preview passes only if the key is already stored", vm.ResultMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, vm.ResultMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_real_run_clears_the_key_however_it_ends()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);
            key.SetEntry(Secure(Key));
            ToReview(vm);

            // No defenseclaw on the isolated PATH: the run ends at "not found" before anything starts.
            vm.ExecuteCommand.Execute(null);

            Assert.True(vm.HasRun);
            Assert.False(key.HasEntry);
            Assert.Null(vm.BuildRunOptions());
            Assert.Empty(vm.EnvironmentNotes);
            Assert.Contains(WizardViewModel.SecretClearedSentence, vm.ResultMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(Key, vm.ResultMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_key_of_a_subcommand_that_does_not_need_it_is_not_supplied()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var key = VmField(vm, WizardSyntheticSecrets.GalileoKeyFieldId);
            key.SetEntry(Secure(Key));
            Assert.NotNull(vm.BuildRunOptions());

            VmField(vm, "subcommand").Value = "status";

            Assert.False(key.IsVisible);
            Assert.Null(vm.BuildRunOptions());
            Assert.Empty(vm.EnvironmentNotes);
        });
    }
}
