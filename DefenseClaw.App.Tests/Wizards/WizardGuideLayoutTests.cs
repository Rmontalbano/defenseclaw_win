using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The guided first step as the real window's content, offscreen (the window is never shown). A PNG of each state is written when
/// <c>DC_RENDER_DIR</c> names a folder, and never otherwise.
/// </summary>
[Collection(UiCollection.Name)]
public class WizardGuideLayoutTests
{
    private sealed class Probe : IDockerProbe
    {
        public Probe(DockerStatus status) => Status = status;

        public DockerStatus Status { get; }

        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Scene(WizardDefinition definition, IDockerProbe? probe)
        {
            _services = TestServices.Create(_temp);
            (ViewModel, Host) = UiThread.Run(() =>
            {
                var viewModel = new WizardViewModel(_services, definition, probe);
                var window = new WizardWindow(viewModel);
                var content = (Grid)window.Content;
                window.Content = null;
                foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
                {
                    content.Children.Remove(bar);
                }

                content.DataContext = viewModel;
                return (viewModel, new OffscreenHost(content, 900, 900));
            });
        }

        public WizardViewModel ViewModel { get; }

        public OffscreenHost Host { get; }

        public string[] VisibleTexts() =>
            VisualTree.Descendants<TextBlock>(Host.Content).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text).ToArray();

        public void Dispose()
        {
            UiThread.Run(() =>
            {
                ViewModel.Dispose();
                Host.Dispose();
            });
            _services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void The_splunk_guide_shows_three_cards_and_the_local_one_says_why_it_is_off_when_docker_is_missing()
    {
        using var scene = new Scene(
            WizardSamples.Splunk(),
            new Probe(new DockerStatus(DockerState.NotInstalled, "Docker was not found on this machine's PATH.", Array.Empty<string>())));

        UiThread.WaitFor(() => !scene.ViewModel.Steps[0].Guide!.Cards.Single(c => c.RequiresDocker).IsChecking, "the Docker answer");
        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            var texts = scene.VisibleTexts();

            Assert.Contains("Splunk Observability Cloud", texts);
            Assert.Contains("Local Splunk (Docker)", texts);
            Assert.Contains("Splunk Enterprise (HEC)", texts);

            // Compact first: the reason it is off is never folded away, the detail is.
            Assert.DoesNotContain("What you need", texts);
            Assert.Contains(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(scene.Host.Content), b => b.IsOpen && b.Message.Contains("not found", StringComparison.Ordinal));
            RenderTo.Png(scene.Host, "splunk-guide-compact");

            scene.ViewModel.Steps[0].Guide!.ToggleAllCommand.Execute(null);
            scene.Host.Relayout();
            texts = scene.VisibleTexts();
            Assert.Equal(3, texts.Count(t => t == "What you need"));
            Thread.Sleep(800); // the Expander's open animation, so the picture shows where it ended
            scene.Host.Relayout();
            RenderTo.Png(scene.Host, "splunk-guide-expanded");
            Assert.Equal(3, texts.Count(t => t == "Where to get it"));
            Assert.Contains(texts, t => t.StartsWith("The CLI says: ", StringComparison.Ordinal));

            // The reason is in an InfoBar, and its link to Docker's install guide is on the card.
            Assert.Contains(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(scene.Host.Content), b => b.IsOpen && b.Message.Contains("not found", StringComparison.Ordinal));
            Assert.Contains(VisualTree.Descendants<Wpf.Ui.Controls.Button>(scene.Host.Content), b => b.IsVisible && Equals(b.Content, "Install Docker Desktop"));

            var switches = VisualTree.Descendants<Wpf.Ui.Controls.ToggleSwitch>(scene.Host.Content).Where(s => s.IsVisible).ToArray();
            Assert.Equal(3, switches.Length);
            Assert.Equal(new[] { true, false, true }, switches.Select(s => s.IsEnabled).ToArray());

            RenderTo.Png(scene.Host, "splunk-guide-no-docker");
        });
    }

    [Fact]
    public void The_splunk_guide_with_docker_running_lets_the_local_card_be_chosen_and_cautions_about_containers()
    {
        using var scene = new Scene(WizardSamples.Splunk(), new Probe(new DockerStatus(DockerState.Ready, "Docker is running.", Array.Empty<string>())));

        UiThread.WaitFor(() => scene.ViewModel.Steps[0].Guide!.Cards.Single(c => c.RequiresDocker).IsAvailable, "the Docker answer");
        UiThread.Run(() =>
        {
            scene.ViewModel.Steps[0].Guide!.Cards.Single(c => c.Card.FieldId == "logs").IsSelected = true;
            scene.ViewModel.Steps[0].Guide!.Cards.Single(c => c.Card.FieldId == "enterprise").IsSelected = true;
            scene.Host.Relayout();

            Assert.All(VisualTree.Descendants<Wpf.Ui.Controls.ToggleSwitch>(scene.Host.Content).Where(s => s.IsVisible), s => Assert.True(s.IsEnabled));
            Assert.Contains(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(scene.Host.Content), b => b.IsOpen && b.Message.Contains("starts containers", StringComparison.Ordinal));

            RenderTo.Png(scene.Host, "splunk-guide-docker-ready");
        });
    }

    [Fact]
    public void The_galileo_guide_is_an_information_page_with_no_switches()
    {
        using var scene = new Scene(WizardSamples.Galileo(), null);

        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            var texts = scene.VisibleTexts();

            Assert.Equal("About Galileo", scene.ViewModel.PageTitle);
            Assert.DoesNotContain("What you need", texts);
            scene.ViewModel.Steps[0].Guide!.ToggleAllCommand.Execute(null);
            scene.Host.Relayout();
            texts = scene.VisibleTexts();
            Assert.Contains("What you need", texts);
            Assert.Contains("What the command will do", texts);
            Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.ToggleSwitch>(scene.Host.Content), s => s.IsVisible);

            RenderTo.Png(scene.Host, "galileo-guide");
        });
    }

    [Fact]
    public void Each_card_is_compact_until_its_Details_toggle_opens_it_and_the_toggle_is_an_expand_collapse_control()
    {
        using var scene = new Scene(WizardSamples.Splunk(), new Probe(new DockerStatus(DockerState.Ready, "Docker is running.", Array.Empty<string>())));

        UiThread.WaitFor(() => scene.ViewModel.Steps[0].Guide!.Cards.Single(c => c.RequiresDocker).IsAvailable, "the Docker answer");
        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            var expanders = VisualTree.Descendants<Expander>(scene.Host.Content).Where(e => e.IsVisible && Equals(e.Header, "Details")).ToArray();
            Assert.Equal(3, expanders.Length);
            Assert.All(expanders, e => Assert.False(e.IsExpanded));
            Assert.DoesNotContain("What the command will do", scene.VisibleTexts());

            // Summary, flag chip and switch stay in view while compact.
            Assert.Contains(scene.VisibleTexts(), t => t.Contains("--", StringComparison.Ordinal));
            Assert.Equal(3, VisualTree.Descendants<Wpf.Ui.Controls.ToggleSwitch>(scene.Host.Content).Count(s => s.IsVisible));

            // The standard ExpandCollapse pattern, with a name that says which card.
            var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(expanders[0]);
            var pattern = (System.Windows.Automation.Provider.IExpandCollapseProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse)!;
            Assert.Equal(System.Windows.Automation.ExpandCollapseState.Collapsed, pattern.ExpandCollapseState);
            Assert.EndsWith("details", System.Windows.Automation.AutomationProperties.GetName(expanders[0]), StringComparison.Ordinal);

            pattern.Expand();
            scene.Host.Relayout();
            Assert.True(scene.ViewModel.Steps[0].Guide!.Cards[0].IsExpanded);
            Assert.Equal(1, scene.VisibleTexts().Count(t => t == "What the command will do"));

            pattern.Collapse();
            Assert.False(scene.ViewModel.Steps[0].Guide!.Cards[0].IsExpanded);
        });
    }

    [Fact]
    public void Expand_all_opens_every_card_and_then_reads_Collapse_all()
    {
        using var scene = new Scene(WizardSamples.Splunk(), null);

        UiThread.Run(() =>
        {
            var guide = scene.ViewModel.Steps[0].Guide!;
            Assert.Equal("Expand all", guide.ToggleAllText);
            Assert.Contains(VisualTree.Descendants<Wpf.Ui.Controls.Button>(scene.Host.Content), b => b.IsVisible && Equals(b.Content, "Expand all"));

            guide.ToggleAllCommand.Execute(null);
            Assert.All(guide.Cards, c => Assert.True(c.IsExpanded));
            Assert.Equal("Collapse all", guide.ToggleAllText);

            // One card closed by hand turns the link back into "Expand all".
            guide.Cards[0].IsExpanded = false;
            Assert.Equal("Expand all", guide.ToggleAllText);

            guide.ToggleAllCommand.Execute(null);
            guide.ToggleAllCommand.Execute(null);
            Assert.All(guide.Cards, c => Assert.False(c.IsExpanded));
        });
    }

    [Fact]
    public void Turning_a_card_on_opens_its_details_and_turning_it_off_leaves_them_as_they_were()
    {
        using var scene = new Scene(WizardSamples.Splunk(), new Probe(new DockerStatus(DockerState.Ready, "Docker is running.", Array.Empty<string>())));

        UiThread.WaitFor(() => scene.ViewModel.Steps[0].Guide!.Cards.Single(c => c.RequiresDocker).IsAvailable, "the Docker answer");
        UiThread.Run(() =>
        {
            var cards = scene.ViewModel.Steps[0].Guide!.Cards;
            var local = cards.Single(c => c.Card.FieldId == "logs");
            Assert.False(local.IsExpanded);

            local.IsSelected = true;
            Assert.True(local.IsExpanded);
            Assert.All(cards.Where(c => c != local), c => Assert.False(c.IsExpanded));

            local.IsSelected = false;
            Assert.True(local.IsExpanded);
        });
    }
}
