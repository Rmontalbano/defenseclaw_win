using System.Windows.Automation;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// CUST-270: the Setup hub's Observability, Webhook and Trusted binary paths tiles open their list-first window instead of the wizard (the window's
/// Add opens the wizard on add). Without the shell's wiring they open the wizard as they always did; on a read-only installation they stay open, since
/// the window only reads there, while every other wizard tile is off.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupHubEditorTilesTests : IDisposable
{
    private readonly SetupEditorHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static WizardDefinition Def(string target, string title) => new()
    {
        Target = target,
        Title = title,
        Group = WizardGroups.Observability,
        PlatformStatus = PlatformStatus.NotApplicable,
        Description = "Configure " + title + ".",
    };

    private static readonly WizardDefinition[] Cards =
    {
        Def("observability", "Observability"),
        Def("webhook", "Webhook"),
        Def("trusted-paths", "Trusted binary paths"),
        Def("llm", "LLM providers"),
    };

    [Fact]
    public void The_shell_wires_the_hub_to_the_editors()
    {
        var services = _harness.Services();
        var shell = UiThread.Run(() => new PanelShell(services, 1500, 900));
        try
        {
            UiThread.Run(() => shell.Show<SetupPanel>());
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);

            Assert.NotNull(vm.OpenResourceEditor);
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public async Task Pressing_one_of_the_three_tiles_opens_that_editor_and_not_the_wizard()
    {
        var services = _harness.Services();
        var opened = new List<SetupResource>();
        var (vm, tiles) = UiThread.Run(() =>
        {
            var panel = new SetupPanelViewModel(services) { OpenResourceEditor = opened.Add };
            panel.ShowCards(Cards);
            return (panel, panel.Groups.SelectMany(g => g.Cards).ToDictionary(c => c.Target));
        });

        foreach (var target in new[] { "observability", "webhook", "trusted-paths" })
        {
            Assert.True(tiles[target].CanLaunch);
            await UiThread.Run(() => vm.LaunchCommand.ExecuteAsync(tiles[target]));

            // Ends at once: no wizard was asked for, so the card never showed "opening".
            Assert.False(tiles[target].IsOpening);
        }

        Assert.Equal(new[] { SetupResource.Observability, SetupResource.Webhooks, SetupResource.TrustedPaths }, opened);
        Assert.Empty(services.Cli.Activity);
    }

    [Fact]
    public void Setting_the_opener_after_the_cards_exist_updates_them_and_taking_it_away_puts_them_back()
    {
        var services = _harness.Services(TestInstallations.ManagedAt(_harness.DataDirectory));
        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(Cards);
            var webhook = vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "webhook");
            Assert.False(webhook.OpensEditor);
            Assert.False(webhook.CanLaunch); // read-only installation, no editor wired: as it was

            vm.OpenResourceEditor = _ => { };
            Assert.True(webhook.OpensEditor);
            Assert.True(webhook.CanLaunch);

            vm.OpenResourceEditor = null;
            Assert.False(webhook.OpensEditor);
            Assert.False(webhook.CanLaunch);
        });
    }

    [Fact]
    public void On_a_read_only_installation_the_editor_tiles_stay_open_say_that_changes_are_off_and_every_other_tile_is_off()
    {
        var services = _harness.Services(TestInstallations.ManagedAt(_harness.DataDirectory));
        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services) { OpenResourceEditor = _ => { } };
            vm.ShowCards(Cards);
            var cards = vm.Groups.SelectMany(g => g.Cards).ToDictionary(c => c.Target);

            foreach (var target in new[] { "observability", "webhook", "trusted-paths" })
            {
                Assert.True(cards[target].CanLaunch, target);
                Assert.Contains("Opens the list", cards[target].TileToolTip, StringComparison.Ordinal);
                Assert.EndsWith("Changes are off: " + TestInstallations.ManagedReason, cards[target].TileToolTip, StringComparison.Ordinal);
            }

            Assert.False(cards["llm"].CanLaunch);
            Assert.EndsWith(TestInstallations.ManagedReason, cards["llm"].TileToolTip, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void On_a_writable_installation_the_editor_tile_says_what_it_opens()
    {
        var services = _harness.Services();
        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services) { OpenResourceEditor = _ => { } };
            vm.ShowCards(Cards);
            var observability = vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "observability");

            Assert.Equal("defenseclaw setup observability" + Environment.NewLine + "Opens the list; Add opens the wizard.", observability.TileToolTip);
            Assert.Equal("Configure Observability", observability.LaunchAutomationName);
        });
    }

    [Fact]
    public void The_hub_redraws_the_editor_tiles_when_the_installation_turns_read_only()
    {
        var services = _harness.Services();
        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services) { OpenResourceEditor = _ => { } };
            vm.ShowCards(Cards);
            var card = vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "trusted-paths");
            var changed = new List<string?>();
            ((System.ComponentModel.INotifyPropertyChanged)card).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.SetActive(true);
            try
            {
                services.Installation.Replace(TestInstallations.ManagedAt(_harness.DataDirectory));

                Assert.Contains(nameof(WizardCardViewModel.TileToolTip), changed);
                Assert.True(card.CanLaunch);
                Assert.EndsWith("Changes are off: " + TestInstallations.ManagedReason, card.TileToolTip, StringComparison.Ordinal);
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    [Fact]
    public void The_real_tiles_of_the_hub_carry_the_same_names_whether_or_not_the_editors_are_wired()
    {
        var services = _harness.Services();
        var shell = UiThread.Run(() => new PanelShell(services, 1500, 900));
        try
        {
            UiThread.Run(() => shell.Show<SetupPanel>());
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.ShowCards(Cards);
                shell.Host.Relayout();

                var tiles = VisualTree.Descendants<System.Windows.Controls.Button>(shell.Page!)
                    .Where(b => b.IsVisible && b.DataContext is WizardCardViewModel)
                    .Select(b => AutomationProperties.GetName(b))
                    .Order(StringComparer.Ordinal)
                    .ToArray();

                Assert.Equal(new[] { "Configure LLM providers", "Configure Observability", "Configure Trusted binary paths", "Configure Webhook" }, tiles);
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
