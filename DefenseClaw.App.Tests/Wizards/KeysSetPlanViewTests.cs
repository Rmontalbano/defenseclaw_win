using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The LLM wizard's in-app key step as the real window's content, offscreen (CUST-328): the "Key and endpoint" page with the masked box and the console button beside
/// it, a value typed into the box itself, and the review page with its two steps. A PNG of each is written when <c>DC_RENDER_DIR</c> names a folder, and never otherwise.
/// The value is synthetic and nothing is run.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class KeysSetPlanViewTests
{
    private const string Value = "synthetic-llm-key-0451";
    private const string Name = "EXAMPLE_LLM_KEY";

    private const string Config = """
        llm:
          provider: openai
          model: example-model
          api_key_env: EXAMPLE_LLM_KEY
        """;

    private sealed class NeverDocker : IDockerProbe
    {
        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DockerStatus(DockerState.Unknown, "Docker was not looked at.", Array.Empty<string>()));
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        public Scene(WizardDefinition definition)
        {
            _services = TestServices.Create(_temp, LineEndings.Normalize(Config));
            (ViewModel, Host) = UiThread.Run(() =>
            {
                var viewModel = new WizardViewModel(_services, definition, new NeverDocker(), (_, _) => null) { PtyAvailable = () => true };
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

        public void GoTo(string title)
        {
            for (var i = 0; i < 20 && ViewModel.PageTitle != title; i++)
            {
                Assert.False(ViewModel.IsReview, "ran past " + title);
                ViewModel.Next();
                Assert.False(ViewModel.HasValidationSummary, ViewModel.ValidationSummary);
            }

            Assert.Equal(title, ViewModel.PageTitle);
            Host.Relayout();
        }

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
    public async Task The_key_page_offers_a_masked_box_and_the_review_lists_the_two_steps()
    {
        using var scene = new Scene(await CatalogHelp.RealAsync("llm"));

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.SelectGoal("main"));
            scene.GoTo("Key and endpoint");

            var boxes = VisualTree.Descendants<PasswordBox>(scene.Host.Content).Where(b => b.IsVisible).ToArray();
            var box = Assert.Single(boxes);
            Assert.Contains("hidden entry", System.Windows.Automation.AutomationProperties.GetName(box), StringComparison.Ordinal);
            var texts = scene.VisibleTexts();
            Assert.Contains(texts, t => t.Contains("defenseclaw keys set " + Name, StringComparison.Ordinal));
            Assert.Contains("Open a terminal to store it", VisualTree.Descendants<Wpf.Ui.Controls.Button>(scene.Host.Content).Where(b => b.IsVisible).Select(b => b.Content as string));

            // Typing in the box itself reaches the field, and the card says what Execute will do.
            box.Password = Value;
            scene.Host.Relayout();
            Assert.Contains(scene.VisibleTexts(), t => t.StartsWith("A value is entered (hidden). Execute stores it as " + Name, StringComparison.Ordinal));
            Assert.DoesNotContain(scene.VisibleTexts(), t => t.Contains(Value, StringComparison.Ordinal));
            RenderTo.Png(scene.Host, "llm-key-step");

            while (!scene.ViewModel.IsReview)
            {
                scene.ViewModel.Next();
            }

            scene.Host.Relayout();
            var review = scene.VisibleTexts()
                .Concat(VisualTree.Descendants<System.Windows.Controls.TextBox>(scene.Host.Content).Where(b => b.IsVisible).Select(b => b.Text))
                .ToArray();
            Assert.Contains(review, t => t.Contains("defenseclaw keys set " + Name, StringComparison.Ordinal));
            Assert.Contains(review, t => t.Contains("defenseclaw setup llm", StringComparison.Ordinal));
            Assert.DoesNotContain(review, t => t.Contains(Value, StringComparison.Ordinal));
            RenderTo.Png(scene.Host, "llm-key-review");
        });
    }
}
