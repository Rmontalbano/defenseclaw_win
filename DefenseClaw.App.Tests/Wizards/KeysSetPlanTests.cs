using System.Security;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// A key typed in a wizard whose CLI reads it from nowhere but a flag (CUST-328: the LLM wizard's API key, the gateway token): the card offers a masked box, the review
/// lists two steps - <c>keys set NAME</c>, then the wizard's command - and Execute runs them in that order through the same pseudo-console route as the Credentials
/// card. The value is on neither argv, in no string a surface shows and in no Activity entry; the console window is the automatic fallback. Everything is a fake: the
/// run of <c>keys set</c> is replaced, no pseudo-console starts and no <c>defenseclaw</c> is run (the isolated services have none on their PATH).
/// </summary>
public sealed class KeysSetPlanTests
{
    private const string Value = "synthetic-llm-key-0451";
    private const string Name = "EXAMPLE_LLM_KEY";

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
        vm.Steps.SelectMany(s => s.Fields).First(f => f.Field.Flag == flag);

    private static void ToReview(WizardViewModel vm)
    {
        for (var i = 0; i < 30 && !vm.IsReview; i++)
        {
            vm.Next();
            Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
        }

        Assert.True(vm.IsReview);
    }

    /// <summary>The main-model path of the LLM wizard, with the key variable named and a value typed for it.</summary>
    private static (WizardViewModel Vm, WizardFieldViewModel Key) Open(AppServices services, WizardDefinition definition, bool type = true, Func<bool>? pty = null)
    {
        var vm = new WizardViewModel(services, definition, new NeverDocker(), (_, _) => null)
        {
            PtyAvailable = pty ?? (() => true),
        };
        Assert.True(vm.SelectGoal("main"));
        Field(vm, "--provider").Value = "openai";
        Field(vm, "--model").Value = "example-model";
        Field(vm, "--api-key-env").Value = Name;
        var key = Field(vm, "--api-key");
        if (type)
        {
            key.SetEntry(Secure(Value));
        }

        return (vm, key);
    }

    private sealed class NeverDocker : IDockerProbe
    {
        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DockerStatus(DockerState.Unknown, "Docker was not looked at.", Array.Empty<string>()));
    }

    private static SecretPtyResult Stored(string name, SecretPtyOutcome outcome = SecretPtyOutcome.Completed, int exitCode = 0, int typed = 1, string message = "Stored.")
    {
        var invocation = InvocationFactory.Create(false, "keys", "set", name);
        InvocationFactory.UsePromptSecret(invocation);
        InvocationFactory.Append(invocation, "  " + name + ":");
        InvocationFactory.Finish(invocation, exitCode);
        return new SecretPtyResult(outcome, invocation, typed, 1, message);
    }

    private static string[] EverythingShown(WizardViewModel vm, WizardFieldViewModel key)
    {
        var shown = new List<string?>
        {
            vm.ResultMessage, vm.OutputText, vm.ExitBadgeText, vm.ChangeSummary, vm.ReviewProblem, vm.PromptWarning,
            key.EntryStatus, key.EntryProblem, key.EntryNotice, key.InAppExplanation, key.CredentialReviewNote, key.CredentialStatus, key.CredentialCommand,
            key.CredentialExplanation, key.CredentialMessage, key.EntryToolTip, key.InAppAutomationName,
        };
        shown.AddRange(vm.Output.Select(o => o.DisplayLine));
        shown.AddRange(vm.EnvironmentNotes);
        shown.AddRange(vm.ReviewChanges);
        if (vm.CommandReview is { } review)
        {
            shown.AddRange(new[] { review.Title, review.Summary, review.CommandText, review.ClipboardText, review.AutomationName, review.AutomationHelp });
            foreach (var step in review.Steps)
            {
                shown.AddRange(new[] { step.CommandText, step.ClipboardText, step.Purpose, step.StatusText, step.AutomationName });
                shown.AddRange(step.Argv);
            }

            shown.AddRange(review.Warnings.SelectMany(w => new[] { w.Title, w.Message }));
        }

        return shown.Where(t => t is not null).Select(t => t!).ToArray();
    }

    // -------------------------------------------------------------------- the card

    [Fact]
    public async Task The_llm_key_card_offers_a_masked_box_where_the_app_can_type_and_the_console_stays_beside_it()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition, type: false);
            using var scope = vm;

            Assert.True(key.IsSecret);
            Assert.False(key.OffersInAppEntry); // the CLI reads --api-key from the flag alone: no environment route
            Assert.True(key.OffersKeysSetEntry);
            Assert.True(key.ShowsEntryBox);
            Assert.Equal(Name, key.CredentialEnvName);
            Assert.True(key.CanOpenTerminal); // the console button is still there
            Assert.Contains("keys set " + Name, key.InAppExplanation, StringComparison.Ordinal);
            Assert.Contains("Or store it once from a real console", key.CredentialExplanation, StringComparison.Ordinal);

            key.SetEntry(Secure(Value));
            Assert.True(key.HasEntry);
            Assert.Contains("stores it as " + Name, key.EntryStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Without_a_pseudo_console_or_on_a_read_only_installation_the_card_is_the_terminal_card_it_was()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition, type: false, pty: () => false);
            using var scope = vm;

            Assert.False(key.OffersKeysSetEntry);
            Assert.False(key.ShowsEntryBox);
            Assert.Contains("This app never takes the secret itself", key.CredentialExplanation, StringComparison.Ordinal);
            key.SetEntry(Secure(Value));
            Assert.False(key.HasEntry); // refused rather than kept unused

            vm.PtyAvailable = () => true;
            Assert.True(key.OffersKeysSetEntry);
        });

        using var managedTemp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(managedTemp, installation: TestInstallations.ManagedAt(managedTemp.Path));
            var (vm, key) = Open(services, definition, type: false);
            using var scope = vm;

            Assert.False(key.OffersKeysSetEntry);
        });
    }

    [Fact]
    public void The_gateway_token_card_gets_the_same_box()
    {
        var eol = WizardSamples.LlmHelp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var help = SetupHelpParser.Parse(string.Join(eol, new[]
        {
            "Usage: defenseclaw setup gateway [OPTIONS]",
            "",
            "  Configure the OpenClaw gateway.",
            "",
            "Options:",
            "  --host TEXT     Gateway host.",
            "  --token TEXT    Gateway auth token.",
            "  --help          Show this message and exit.",
        }));
        var (steps, curated) = WizardStepFactory.Build("gateway", help);
        steps = SecretRoutes.Annotate("gateway", WizardWindowsPolicy.Filter("gateway", steps));
        var definition = new WizardDefinition
        {
            Target = "gateway",
            Title = "Gateway",
            Group = WizardGroups.Credentials,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsCurated = curated,
            IsDetailLoaded = true,
        };

        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, definition, new NeverDocker(), (_, _) => null) { PtyAvailable = () => true };
            var token = Field(vm, "--token");

            Assert.False(token.OffersInAppEntry);
            Assert.True(token.OffersKeysSetEntry);
            Assert.Equal("OPENCLAW_GATEWAY_TOKEN", token.CredentialEnvName);
        });
    }

    // -------------------------------------------------------------------- the plan

    [Fact]
    public async Task A_typed_key_makes_a_two_step_review_and_the_command_carries_only_the_name()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition);
            using var scope = vm;
            ToReview(vm);

            var review = vm.CommandReview!;
            Assert.Equal(2, review.Steps.Count);
            Assert.Equal(new[] { "keys", "set", Name }, review.Steps[0].Argv);
            Assert.Equal(1, review.Steps[0].Number);
            Assert.Equal(2, review.Steps[1].Number);
            Assert.Equal(CommandTier.StateChanging, review.Steps[0].Tier);
            Assert.Equal("setup", review.Steps[1].Argv[0]);
            Assert.Equal("llm", review.Steps[1].Argv[1]);
            Assert.Contains("--api-key-env " + Name, review.Steps[1].CommandText, StringComparison.Ordinal);
            Assert.DoesNotContain("--api-key", review.Steps[1].Argv.Where(a => a != "--api-key-env"));
            Assert.Contains("in order", review.Summary, StringComparison.Ordinal);

            Assert.All(EverythingShown(vm, key), text => Assert.DoesNotContain(Value, text, StringComparison.Ordinal));
            Assert.Contains("Step 1 stores", key.CredentialReviewNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task With_nothing_typed_the_review_is_the_single_command_it_was()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, _) = Open(services, definition, type: false);
            using var scope = vm;
            ToReview(vm);

            var step = Assert.Single(vm.CommandReview!.Steps);
            Assert.Equal("llm", step.Argv[1]);
            Assert.Equal("Command", vm.CommandReview.Title);
        });
    }

    [Fact]
    public async Task Execute_stores_the_key_first_then_runs_the_command_and_the_value_is_nowhere_afterwards()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition);
            using var scope = vm;
            var stored = new List<(string Name, string Value)>();
            vm.RunSet = (name, value, _) =>
            {
                stored.Add((name, value.Reveal()));
                Assert.True(vm.IsRunning);
                Assert.False(key.HasEntry); // the box was emptied before the CLI was asked
                return Task.FromResult(Stored(name));
            };
            var cleared = 0;
            key.EntryCleared += (_, _) => cleared++;
            ToReview(vm);
            Assert.True(vm.CanExecute);

            vm.ExecuteCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal((Name, Value), Assert.Single(stored));
            Assert.True(cleared >= 1);
            Assert.False(key.HasEntry);
            Assert.True(vm.HasRun);

            // The second step was reached (it ended at "no CLI here", which is not a store failure).
            Assert.DoesNotContain("did not store", vm.ResultMessage, StringComparison.Ordinal);
            Assert.Contains(Name + ":", vm.OutputText, StringComparison.Ordinal); // the first step's transcript is in the console
            Assert.All(EverythingShown(vm, key), text => Assert.DoesNotContain(Value, text, StringComparison.Ordinal));
            Assert.All(services.Cli.Activity, entry =>
            {
                Assert.DoesNotContain(Value, entry.CommandLine, StringComparison.Ordinal);
                Assert.All(entry.Argv, a => Assert.DoesNotContain(Value, a, StringComparison.Ordinal));
            });

            // The key is used up: the review is back to the one command, and a retry needs the value again.
            Assert.Single(vm.CommandReview!.Steps);
        });
    }

    [Fact]
    public async Task A_store_the_cli_refuses_stops_the_plan_before_the_command_and_says_so()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition);
            using var scope = vm;
            vm.RunSet = (name, _, _) => Task.FromResult(Stored(name, exitCode: 1, message: "The CLI exited 1 after the value was typed."));
            ToReview(vm);

            vm.ExecuteCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal("exit 1", vm.ExitBadgeText);
            Assert.Equal("Bad", vm.ExitBadgeKey);
            Assert.Contains("Step 1 of 2 did not store " + Name, vm.ResultMessage, StringComparison.Ordinal);
            Assert.Contains("wizard's command was not run", vm.ResultMessage, StringComparison.Ordinal);
            Assert.False(vm.LastRunSucceeded);
            Assert.False(key.HasEntry);
            Assert.All(EverythingShown(vm, key), text => Assert.DoesNotContain(Value, text, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task A_route_that_failed_with_nothing_typed_opens_the_console_and_does_not_run_the_command()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition);
            using var scope = vm;
            var consoles = new List<string>();
            vm.RunSet = (name, _, _) => Task.FromResult(new SecretPtyResult(SecretPtyOutcome.PromptNotSeen, null, 0, 1, "the CLI never showed its prompt"));
            vm.OpenConsole = name =>
            {
                consoles.Add(name);
                return null;
            };
            ToReview(vm);

            vm.ExecuteCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(new[] { Name }, consoles);
            Assert.Contains("A console window opened for " + Name, vm.ResultMessage, StringComparison.Ordinal);
            Assert.Contains("wizard's command was not run", vm.ResultMessage, StringComparison.Ordinal);
            Assert.Equal("not run", vm.ExitBadgeText);

            // Remembered: the card is the terminal card for the rest of this wizard.
            Assert.False(key.OffersKeysSetEntry);
            Assert.False(vm.KeysSetAvailable);
            Assert.All(EverythingShown(vm, key), text => Assert.DoesNotContain(Value, text, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_preview_stores_nothing_and_keeps_the_value_for_execute()
    {
        // The installed llm help has no --dry-run, so the preview is tried on the sample help with one added.
        var eol = WizardSamples.LlmHelp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var help = SetupHelpParser.Parse(WizardSamples.LlmHelp.Replace(
            "  --non-interactive     Never prompt.",
            "  --dry-run             Preview only." + eol + "  --non-interactive     Never prompt.",
            StringComparison.Ordinal));
        var (steps, curated) = WizardStepFactory.Build("llm", help);
        steps = SecretRoutes.Annotate("llm", WizardWindowsPolicy.Filter("llm", steps));
        var definition = new WizardDefinition
        {
            Target = "llm",
            Title = "LLM providers",
            Group = WizardGroups.Credentials,
            Steps = steps,
            PlatformStatus = help.PlatformStatus,
            IsCurated = curated,
            IsDetailLoaded = true,
        };

        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new WizardViewModel(services, definition, new NeverDocker(), (_, _) => null) { PtyAvailable = () => true };
            var stored = 0;
            vm.RunSet = (name, _, _) =>
            {
                stored++;
                return Task.FromResult(Stored(name));
            };
            Field(vm, "--provider").Value = "openai";
            Field(vm, "--model").Value = "example-model";
            Field(vm, "--api-key-env").Value = Name;
            var key = Field(vm, "--api-key");
            key.SetEntry(Secure(Value));
            ToReview(vm);
            Assert.True(vm.CanPreview);

            vm.PreviewCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Assert.Equal(0, stored);
            Assert.True(key.HasEntry);
            Assert.Contains("does not store the key", vm.ResultMessage, StringComparison.Ordinal);
            Assert.All(EverythingShown(vm, key), text => Assert.DoesNotContain(Value, text, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Naming_another_variable_drops_the_value_typed_for_the_first()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition);
            using var scope = vm;
            Assert.True(key.HasEntry);

            Field(vm, "--api-key-env").Value = "EXAMPLE_OTHER_KEY";

            Assert.False(key.HasEntry);
            Assert.Equal("EXAMPLE_OTHER_KEY", key.CredentialEnvName);
            Assert.Contains("destination changed", key.EntryStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_installation_turning_read_only_drops_the_value_and_the_box()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition);
            using var scope = vm;
            Assert.True(key.HasEntry);

            services.Installation.Replace(TestInstallations.ManagedAt(temp.Path));

            // The wizard follows the guard on the dispatcher; with no application in this body it runs at once.
            Assert.False(key.OffersKeysSetEntry);
            Assert.False(key.HasEntry);
        });
    }

    [Fact]
    public async Task Closing_the_wizard_drops_a_typed_key()
    {
        var definition = await CatalogHelp.RealAsync("llm");
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var (vm, key) = Open(services, definition);
            Assert.True(key.HasEntry);

            vm.Dispose();

            Assert.False(key.HasEntry);
            Assert.Null(key.MaterializeSecret());
        });
    }
}
