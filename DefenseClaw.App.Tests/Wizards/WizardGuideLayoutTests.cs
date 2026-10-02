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
            Assert.Equal(3, texts.Count(t => t == "What you need"));
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
            Assert.Contains("What you need", texts);
            Assert.Contains("What the command will do", texts);
            Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.ToggleSwitch>(scene.Host.Content), s => s.IsVisible);

            RenderTo.Png(scene.Host, "galileo-guide");
        });
    }
}
