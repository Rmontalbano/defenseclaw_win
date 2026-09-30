using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The galileo wizard's API-key page and review page as the real window's content, offscreen. The window itself is never
/// shown: its content is lifted out and hosted at a fixed size (the title bar, which needs a real window, is left behind).
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class GalileoCardLayoutTests
{
    private const string Key = "synthetic-galileo-key-7788";

    [Fact]
    public void The_key_page_has_a_hidden_entry_box_the_save_choice_and_says_where_the_key_comes_from()
    {
        using var scene = Scene.Open();

        UiThread.Run(() =>
        {
            scene.GoToKeyPage();

            var box = scene.KeyBox;
            Assert.True(box.IsVisible);
            Assert.InRange(box.ActualWidth, 300, 460);
            Assert.Contains("hidden entry", System.Windows.Automation.AutomationProperties.GetName(box), StringComparison.Ordinal);

            // The chip says it comes from the environment, and that there is no flag for it.
            Assert.Contains("env GALILEO_API_KEY", scene.VisibleTexts(), StringComparer.Ordinal);
            Assert.DoesNotContain("(positional)", scene.VisibleTexts(), StringComparer.Ordinal);

            // The choice that maps to --persist-api-key: worded plainly, off to start with.
            var save = scene.SaveSwitch;
            Assert.True(save.IsVisible);
            Assert.False(save.IsChecked);
            Assert.Contains("--persist-api-key", scene.VisibleTexts(), StringComparer.Ordinal);

            // The card's own words: what is stored, and what a preview does.
            Assert.Contains(scene.VisibleTexts(), t => t.Contains("checks before it previews", StringComparison.Ordinal));

            scene.Render("galileo-key-page");
        });
    }

    [Fact]
    public void Typing_a_key_turns_saving_on_and_the_card_says_so()
    {
        using var scene = Scene.Open();

        UiThread.Run(() =>
        {
            scene.GoToKeyPage();
            scene.KeyBox.Password = Key;
        });

        // The switch's knob slides by animation; give its clock a moment so the picture shows where it ended up.
        Thread.Sleep(600);

        UiThread.Run(() =>
        {
            scene.Host.Relayout();

            Assert.True(scene.SaveSwitch.IsChecked);
            Assert.Contains(scene.VisibleTexts(), t => t.Contains("will save it to ~/.defenseclaw/.env", StringComparison.Ordinal));
            Assert.DoesNotContain(scene.VisibleTexts(), t => t.Contains(Key, StringComparison.Ordinal));

            scene.Render("galileo-key-page-typed");
        });
    }

    [Fact]
    public void A_stored_key_is_said_on_the_card_and_the_box_can_stay_empty()
    {
        using var scene = Scene.Open(storedKey: true);

        UiThread.Run(() =>
        {
            scene.GoToKeyPage();

            Assert.Contains(scene.VisibleTexts(), t => t.Contains("GALILEO_API_KEY has a value", StringComparison.Ordinal));
            Assert.Contains(scene.VisibleTexts(), t => t.Contains("already stored", StringComparison.Ordinal) && t.Contains("leave this blank", StringComparison.Ordinal));
            Assert.Equal(string.Empty, scene.KeyBox.Password);

            scene.Render("galileo-key-page-stored");
        });
    }

    [Fact]
    public void The_review_shows_the_environment_variable_masked_and_never_the_value()
    {
        using var scene = Scene.Open();

        UiThread.Run(() =>
        {
            scene.GoToKeyPage();
            scene.KeyBox.Password = Key;
            scene.GoToReview();

            var texts = scene.VisibleTexts();
            Assert.Contains(texts, t => t.StartsWith("GALILEO_API_KEY=•••", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, t => t.Contains(Key, StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("--persist-api-key", StringComparison.Ordinal) && t.Contains("setup galileo", StringComparison.Ordinal));

            scene.Render("galileo-review");
        });
    }

    // ------------------------------------------------------------------ the scene

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;
        private readonly WizardViewModel _viewModel;

        private Scene(bool storedKey)
        {
            if (storedKey)
            {
                _ = _temp.WriteFile(".env", "GALILEO_API_KEY=synthetic-stored-key-0001\n");
            }

            _services = TestServices.Create(_temp);
            (_viewModel, Host) = UiThread.Run(() =>
            {
                var viewModel = new WizardViewModel(_services, WizardSamples.Galileo());

                // The window's content, lifted out: the window is never shown (and so never touches the desktop).
                var window = new WizardWindow(viewModel);
                var content = (Grid)window.Content;
                window.Content = null;
                foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
                {
                    content.Children.Remove(bar);
                }

                content.DataContext = viewModel;
                return (viewModel, new OffscreenHost(content, 900, 760));
            });
        }

        public OffscreenHost Host { get; }

        public static Scene Open(bool storedKey = false) => new(storedKey);

        public void GoToKeyPage()
        {
            while (_viewModel.CurrentStep?.Title != "API key")
            {
                _viewModel.Next();
                Assert.False(_viewModel.HasValidationSummary, _viewModel.ValidationSummary);
            }

            Host.Relayout();
        }

        public void GoToReview()
        {
            while (!_viewModel.IsReview)
            {
                _viewModel.Next();
            }

            Host.Relayout();
        }

        public PasswordBox KeyBox =>
            VisualTree.Find<PasswordBox>(Host.Content) ?? throw new InvalidOperationException("The key page has no password box.");

        public Wpf.Ui.Controls.ToggleSwitch SaveSwitch =>
            VisualTree.Find<Wpf.Ui.Controls.ToggleSwitch>(Host.Content, s => s.IsVisible)
            ?? throw new InvalidOperationException("The key page has no save switch.");

        /// <summary>The text of every visible text block and text box on the page.</summary>
        public string[] VisibleTexts() =>
            VisualTree.Descendants<TextBlock>(Host.Content).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text)
                .Concat(VisualTree.Descendants<TextBox>(Host.Content).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text))
                .ToArray();

        public void Render(string fileName) => RenderTo.Png(Host, fileName);

        public void Dispose()
        {
            UiThread.Run(() =>
            {
                _viewModel.Dispose();
                Host.Dispose();
            });
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
