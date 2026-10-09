using System.ComponentModel;
using System.Diagnostics;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// The Setup hub on a read-only installation (CUST-308): the wizard tiles, the wizard's own Execute, the guardrail controls on the page and the
/// credential rows that open a console. Every wizard ends in a change, so none of them is offered; the preview (<c>--dry-run</c>), which writes
/// nothing, and every read stay. The installations are synthetic (<see cref="TestInstallations"/>); no window opens and no process starts.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationSetupTests : IDisposable
{
    private const string CredentialListing = """
        [
          {"env_name": "EXAMPLE_JUDGE_KEY", "canonical_env_name": "EXAMPLE_JUDGE_KEY", "feature": "LLM judge", "description": "d", "requirement": "required", "source": "unset", "set": false},
          {"env_name": "EXAMPLE_SCANNER_KEY", "canonical_env_name": "EXAMPLE_SCANNER_KEY", "feature": "Scanner", "description": "d", "requirement": "required", "source": "dotenv", "set": true}
        ]
        """;

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _services.Add(services);
        return services;
    }

    private static WizardDefinition Definition(string target, string title) => new()
    {
        Target = target,
        Title = title,
        Group = WizardGroups.Credentials,
        PlatformStatus = PlatformStatus.NotApplicable,
        Description = "Configure " + title + ".",
    };

    // ------------------------------------------------------------------ the tiles

    [Fact]
    public void A_tile_opens_its_wizard_on_a_writable_installation()
    {
        var card = new WizardCardViewModel(Definition("llm", "LLM providers"));
        var guarded = new WizardCardViewModel(Definition("llm", "LLM providers"), () => null);

        Assert.True(card.CanLaunch);
        Assert.Null(card.InstallationBlockedReason);
        Assert.True(guarded.CanLaunch);
        Assert.DoesNotContain(Environment.NewLine, guarded.TileToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tile_on_a_read_only_installation_stays_in_its_group_is_off_and_says_why_in_its_tooltip()
    {
        var card = new WizardCardViewModel(Definition("llm", "LLM providers"), () => TestInstallations.ManagedReason);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(card.IsAvailable);
        Assert.False(card.CanLaunch);
        Assert.Equal(TestInstallations.ManagedReason, card.InstallationBlockedReason);
        Assert.EndsWith(TestInstallations.ManagedReason, card.TileToolTip, StringComparison.Ordinal);
        Assert.Contains(card.CommandHint, card.TileToolTip, StringComparison.Ordinal);

        card.RefreshInstallation();
        Assert.Contains(nameof(WizardCardViewModel.CanLaunch), raised);
        Assert.Contains(nameof(WizardCardViewModel.TileToolTip), raised);
    }

    [Fact]
    public async Task The_hub_launches_nothing_from_a_tile_on_a_read_only_installation()
    {
        var services = Create(TestInstallations.Managed(_temp.Path));
        var (vm, card) = UiThread.Run(() =>
        {
            var panel = new SetupPanelViewModel(services);
            panel.ShowCards(new[] { Definition("llm", "LLM providers") });
            return (panel, panel.Groups.SelectMany(g => g.Cards).Single());
        });

        Assert.False(card.CanLaunch);
        Assert.Equal(TestInstallations.ManagedReason, card.InstallationBlockedReason);

        // Pressing it anyway ends at once: no window opens (a wizard window would need the UI thread's main window), and the card is not "opening".
        await UiThread.Run(() => vm.LaunchCommand.ExecuteAsync(card));
        Assert.False(card.IsOpening);
        Assert.Empty(services.Cli.Activity);
    }

    [Fact]
    public void The_hubs_tiles_and_banner_are_drawn_again_when_the_installation_changes_while_the_page_is_open()
    {
        var services = Create();
        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(new[] { Definition("llm", "LLM providers") });
            var card = vm.Groups.SelectMany(g => g.Cards).Single();
            var changed = new List<string?>();
            ((INotifyPropertyChanged)card).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.True(card.CanLaunch);
            Assert.False(vm.HasInstallationBlock);

            vm.SetActive(true);
            try
            {
                services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

                Assert.Contains(nameof(WizardCardViewModel.CanLaunch), changed);
                Assert.False(card.CanLaunch);
                Assert.True(vm.HasInstallationBlock);
                Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);

                services.Installation.Replace(TestInstallations.UserDefault());

                Assert.True(card.CanLaunch);
                Assert.False(vm.HasInstallationBlock);
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    [Fact]
    public void The_guardrail_buttons_on_the_hub_are_off_on_a_read_only_installation_and_on_otherwise()
    {
        var writable = UiThread.Run(() => new SetupPanelViewModel(Create()));
        var managed = UiThread.Run(() => new SetupPanelViewModel(Create(TestInstallations.ManagedAt(_temp.Path))));

        Assert.True(writable.CanUseGuardrailControls);
        Assert.False(writable.HasInstallationBlock);
        Assert.Null(writable.InstallationBlockedReason);

        Assert.False(managed.CanUseGuardrailControls);
        Assert.True(managed.HasInstallationBlock);
        Assert.Equal(TestInstallations.ManagedReason, managed.InstallationBlockedReason);
    }

    // ------------------------------------------------------------------ the wizard's own page

    private static WizardViewModel ReviewOf(AppServices services)
    {
        var vm = new WizardViewModel(services, WizardSamples.Galileo());
        for (var i = 0; i < 12 && !vm.IsReview; i++)
        {
            vm.Next();
        }

        Assert.True(vm.IsReview, vm.ValidationSummary);
        return vm;
    }

    [Fact]
    public void A_wizard_review_on_a_writable_installation_can_be_executed()
    {
        var services = Create();

        UiThread.Run(() =>
        {
            using var vm = ReviewOf(services);

            Assert.Null(vm.InstallationBlockedReason);
            Assert.True(vm.CanExecute);
            Assert.False(vm.CommandReview!.IsBlocked);
        });
    }

    [Fact]
    public void A_wizard_review_on_a_read_only_installation_cannot_be_executed_but_can_still_be_previewed()
    {
        var services = Create(TestInstallations.ManagedAt(_temp.Path));

        UiThread.Run(() =>
        {
            using var vm = ReviewOf(services);

            Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
            Assert.False(vm.CanExecute);

            // The review shows the reason as a bar and keeps its own button off.
            var review = vm.CommandReview!;
            Assert.True(review.IsBlocked);
            Assert.Equal(TestInstallations.ManagedReason, review.BlockedReason);
            Assert.Contains(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle && w.Message == TestInstallations.ManagedReason);

            // The dry run writes nothing, and the runner lets it through.
            Assert.True(vm.SupportsPreview);
            Assert.True(vm.CanPreview);
        });
    }

    [Fact]
    public void An_open_wizard_follows_the_installation_when_it_turns_read_only_and_back()
    {
        var services = Create();

        UiThread.Run(() =>
        {
            using var vm = ReviewOf(services);
            var changed = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.True(vm.CanExecute);
            Assert.False(vm.CommandReview!.IsBlocked);

            services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));

            Assert.Contains(nameof(WizardViewModel.CanExecute), changed);
            Assert.Contains(nameof(WizardViewModel.InstallationBlockedReason), changed);
            Assert.False(vm.CanExecute);
            Assert.True(vm.CommandReview!.IsBlocked);
            Assert.Equal(TestInstallations.ManagedReason, vm.CommandReview.BlockedReason);

            services.Installation.Replace(TestInstallations.UserDefault());

            Assert.True(vm.CanExecute);
            Assert.False(vm.CommandReview!.IsBlocked);
        });
    }

    [Fact]
    public void Executing_a_wizard_on_a_read_only_installation_by_any_route_runs_nothing()
    {
        var services = Create(TestInstallations.ManagedAt(_temp.Path));

        UiThread.Run(() =>
        {
            using var vm = ReviewOf(services);

            vm.ExecuteCommand.Execute(null);

            Assert.False(vm.IsRunning);
            Assert.False(vm.HasRun);
            Assert.Empty(services.Cli.Activity);
        });
    }

    // ------------------------------------------------------------------ the Credentials card

    private sealed class CredentialHarness
    {
        public required List<ProcessStartInfo> Launched { get; init; }

        public required CredentialsViewModel Credentials { get; init; }
    }

    private CredentialHarness Credentials(AppServices services)
    {
        var launched = new List<ProcessStartInfo>();
        var terminal = new CredentialTerminal(services.Paths)
        {
            ResolveExecutable = () => Task.FromResult<string?>(@"C:\Tools\defenseclaw.exe"),
            Launch = info =>
            {
                launched.Add(info);
                return null;
            },
        };
        var vm = new CredentialsViewModel(services, terminal)
        {
            RunRead = argv =>
            {
                var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
                foreach (var line in CredentialListing.Split('\n'))
                {
                    InvocationFactory.Append(invocation, line.TrimEnd('\r'));
                }

                InvocationFactory.Finish(invocation, 0);
                return Task.FromResult(invocation);
            },
        };
        return new CredentialHarness { Credentials = vm, Launched = launched };
    }

    [Fact]
    public async Task On_a_writable_installation_a_missing_credential_can_be_set_in_a_terminal()
    {
        var h = Credentials(Create());
        await h.Credentials.RefreshAsync();

        var missing = h.Credentials.Rows[0];
        Assert.True(h.Credentials.CanChange);
        Assert.Null(h.Credentials.ChangesBlockedReason);
        Assert.True(missing.CanSet);
        Assert.StartsWith("Opens a console window running", missing.SetToolTip, StringComparison.Ordinal);
        Assert.True(missing.SetInTerminalCommand.CanExecute(null));
    }

    [Fact]
    public async Task On_a_read_only_installation_the_credentials_are_still_listed_but_no_console_opens_to_change_one()
    {
        var services = Create(TestInstallations.ManagedAt(_temp.Path));
        var h = Credentials(services);

        await h.Credentials.RefreshAsync();

        // The names and states are a read.
        Assert.Equal(2, h.Credentials.Rows.Count);
        Assert.Equal(1, h.Credentials.MissingRequiredCount);

        // Every control that would open a console for keys set or fill-missing is off, with the sentence as its tooltip.
        Assert.False(h.Credentials.CanChange);
        Assert.Equal(TestInstallations.ManagedReason, h.Credentials.ChangesBlockedReason);
        Assert.Equal(TestInstallations.ManagedReason, h.Credentials.FillMissingToolTip);
        Assert.All(h.Credentials.Rows, row =>
        {
            Assert.False(row.CanSet);
            Assert.Equal(TestInstallations.ManagedReason, row.SetToolTip);
            Assert.False(row.SetInTerminalCommand.CanExecute(null));
        });

        // A hand-off that is started anyway is refused before the launcher is reached: the console is a run the runner never sees.
        await h.Credentials.OpenFillMissingInTerminalAsync();
        h.Credentials.Rows[0].SetInTerminalCommand.Execute(null);

        Assert.Empty(h.Launched);
        Assert.Equal(TestInstallations.ManagedReason, h.Credentials.Note);
        Assert.Equal("Warn", h.Credentials.NoteKey);
    }

    [Fact]
    public async Task The_credential_rows_are_drawn_again_when_the_installation_changes()
    {
        var services = Create();
        var h = Credentials(services);
        await h.Credentials.RefreshAsync();
        var row = h.Credentials.Rows[0];
        var changed = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(row.CanSet);

        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        h.Credentials.RefreshInstallation();

        Assert.False(row.CanSet);
        Assert.Contains(nameof(CredentialRowViewModel.CanSet), changed);
        Assert.Contains(nameof(CredentialRowViewModel.SetToolTip), changed);

        services.Installation.Replace(TestInstallations.UserDefault());
        h.Credentials.RefreshInstallation();

        Assert.True(row.CanSet);
    }
}
