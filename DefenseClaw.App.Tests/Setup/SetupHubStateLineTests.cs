using System.Windows.Controls;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Setup hub's per-card state line (CUST-271): the card for a subject says what config.yaml holds for it today, in the TUI's words; the
/// tile stays 233 x 92; the lines follow the file when it is read again. The wording itself is <c>SetupStateLinesTests</c>'s; this is which card
/// gets which line, and that the tile draws it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupHubStateLineTests
{
    private const string Yaml =
        "guardrail:\n  enabled: true\n  mode: observe\n  connectors:\n    codex:\n      mode: observe\n";

    private static WizardDefinition Def(string target, string group, PlatformStatus status = PlatformStatus.NotApplicable, string description = "") => new()
    {
        Target = target,
        Title = target,
        Group = group,
        PlatformStatus = status,
        Description = description,
    };

    private static ConfigDocument Doc(string yaml) => ConfigStore.Parse(yaml);

    [Theory]
    [InlineData("llm", WizardGroups.Credentials, true)]
    [InlineData("guardrail", WizardGroups.GuardrailAndPolicy, true)]
    [InlineData("remove", WizardGroups.Connectors, true)]
    [InlineData("notifications", WizardGroups.Other, true)]
    [InlineData("notifications-set", WizardGroups.Other, true)]
    [InlineData("codex", WizardGroups.Connectors, true)]
    [InlineData("claude-code", WizardGroups.Connectors, true)]
    [InlineData("openclaw", WizardGroups.Connectors, false)]
    [InlineData("zeptoclaw", WizardGroups.Connectors, false)]
    [InlineData("webhook", WizardGroups.Observability, false)]
    [InlineData("skill-scanner", WizardGroups.Scanners, false)]
    public void Only_the_targets_that_have_a_subject_get_a_line(string target, string group, bool has) =>
        Assert.Equal(has, WizardCardStates.For(target, group, Doc(Yaml)) is not null);

    [Fact]
    public void Each_target_gets_its_own_subjects_line()
    {
        var doc = Doc(Yaml);

        Assert.Equal(SetupStateLines.Guardrail(doc), WizardCardStates.For("guardrail", WizardGroups.GuardrailAndPolicy, doc));
        Assert.Equal(SetupStateLines.Roster(doc), WizardCardStates.For("remove", WizardGroups.Connectors, doc));
        Assert.Equal(SetupStateLines.Connector(doc, "claude-code"), WizardCardStates.For("claude-code", WizardGroups.Connectors, doc));
        Assert.Equal(SetupStateLines.NotificationCategories(doc), WizardCardStates.For("notifications-set", WizardGroups.Other, doc));
        Assert.Equal("On · observe · regex_only", WizardCardStates.For("guardrail", WizardGroups.GuardrailAndPolicy, doc)!.Text);
        Assert.StartsWith("Configured · observe", WizardCardStates.For("codex", WizardGroups.Connectors, doc)!.Text, StringComparison.Ordinal);
        Assert.Equal("Not configured", WizardCardStates.For("claude-code", WizardGroups.Connectors, doc)!.Text);
    }

    [Fact]
    public void A_card_without_a_line_is_unchanged_and_one_with_a_line_adds_it_to_the_tooltip_and_the_screen_reader()
    {
        var plain = new WizardCardViewModel(Def("webhook", WizardGroups.Observability, description: "Add a webhook."));
        var card = new WizardCardViewModel(Def("guardrail", WizardGroups.GuardrailAndPolicy, description: "Configure the guardrail."));
        var before = card.TileToolTip;

        Assert.False(plain.HasStateLine);
        Assert.Equal(string.Empty, plain.StateLine);

        Assert.True(card.ApplyState(WizardCardStates.For("guardrail", card.Group, Doc(Yaml))));

        Assert.True(card.HasStateLine);
        Assert.Equal("On · observe · regex_only", card.StateLine);
        Assert.Equal(card.CommandHint + Environment.NewLine + card.StateDetail + before[card.CommandHint.Length..], card.TileToolTip);
        Assert.Contains("Guardrail: on", card.AutomationName, StringComparison.Ordinal);
        Assert.False(card.ApplyState(WizardCardStates.For("guardrail", card.Group, Doc(Yaml)))); // the same line is no change
    }

    [Fact]
    public void A_tile_that_cannot_be_opened_shows_its_reason_and_no_state_line()
    {
        var card = new WizardCardViewModel(Def("openclaw", WizardGroups.Connectors, PlatformStatus.Unsupported, "OpenClaw."));
        _ = card.ApplyState(SetupStateLines.Connector(Doc(Yaml), "openclaw"));

        Assert.False(card.IsAvailable);
        Assert.False(card.HasStateLine);
        Assert.Equal(card.UnavailableReason, card.TileBlurb);
    }

    [Fact]
    public void The_hub_draws_a_state_line_on_the_tiles_that_have_one_keeps_them_233_by_92_and_follows_the_file()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, configYaml: Yaml);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1500, 900);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.ShowCards(new[]
                {
                    Def("codex", WizardGroups.Connectors, PlatformStatus.Certified, "Configure DefenseClaw for Codex via the hook bus."),
                    Def("guardrail", WizardGroups.GuardrailAndPolicy, description: "Configure the LLM guardrail (routes LLM traffic and more)."),
                    Def("notifications-set", WizardGroups.Other, description: "Toggle a single notifications category or source."),
                    Def("webhook", WizardGroups.Observability, description: "Configure Slack/PagerDuty/Webex/generic chat notifiers."),
                });
                shell.Host.Relayout();

                var lines = VisualTree.Descendants<TextBlock>(shell.Page!).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Assert.Contains("On · observe · regex_only", lines);
                Assert.Contains(lines, l => l.StartsWith("Configured · observe", StringComparison.Ordinal));
                Assert.Contains(lines, l => l.EndsWith(" of 6 on", StringComparison.Ordinal));

                var tiles = VisualTree.Descendants<Button>(shell.Page!).Where(b => b.Width == 233 && b.IsVisible && b.Height == 92).ToArray();
                Assert.True(tiles.Length >= 4);
                Assert.All(tiles, t => Assert.Equal(92, t.ActualHeight, 0.5));

                // The file is read again with a different guardrail: the line follows.
                File.WriteAllText(temp.File("config.yaml"), "guardrail:\n  enabled: false\n");
                services.ReloadConfig();
                shell.Host.Relayout();
                Assert.Equal("Off · observe · regex_only", vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "guardrail").StateLine);
                VisualTree.Descendants<ScrollViewer>(shell.Page!).FirstOrDefault(sv => sv.ScrollableHeight > 0)?.ScrollToEnd();
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, "cust271-hub-state-lines");
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
