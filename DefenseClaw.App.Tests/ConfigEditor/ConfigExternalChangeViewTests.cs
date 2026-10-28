using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.App.Views.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-377 on screen: the review of a change made to config.yaml behind an open draft, and the card for a config.yaml that cannot be loaded. The window is built
/// and its content hosted offscreen (never shown), over a temp copy of config.yaml.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConfigExternalChangeViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static IEnumerable<string> VisibleTexts(OffscreenHost host) =>
        VisualTree.Descendants<TextBlock>(host.Content)
            .Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text);

    private static ConfigEditorWindowViewModel ViewModelOf(ConfigEditorWindow window) =>
        (ConfigEditorWindowViewModel)typeof(ConfigEditorWindow).GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static void Close(ConfigEditorWindow window)
    {
        typeof(ConfigEditorWindow).GetField("_closeApproved", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close();
    }

    [Fact]
    public async Task The_review_overlay_shows_the_disk_change_the_conflict_and_the_choices_without_a_secret()
    {
        var raw = LineEndings.Normalize(ConfigSamples.Raw);
        using var services = TestServices.Create(_temp, raw);
        services.ConfigWatcher.Dispose();

        await UiThread.Run(async () =>
        {
            var window = new ConfigEditorWindow(services);
            var vm = ViewModelOf(window);
            vm.FormSourceOverride = _ => Task.FromResult(LineEndings.Normalize(ConfigSamples.MaskedSource));
            await vm.LoadAsync();

            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = vm;
            using var host = new OffscreenHost(content, 1000, 720);
            try
            {
                vm.RawText = raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
                File.WriteAllText(
                    services.Paths.ConfigFilePath,
                    raw.Replace("model: gpt-4o", "model: o3", StringComparison.Ordinal)
                       .Replace("sample-not-a-real-token", "sample-changed-" + "secret-value", StringComparison.Ordinal));
                await vm.HandleDiskChangeAsync();
                host.Relayout();

                Assert.True(vm.ShowExternalChangeBanner);
                var review = VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Review the difference");
                Assert.NotNull(review);

                await vm.ReviewExternalChangeCommand.ExecuteAsync(null);
                host.Relayout();
                RenderTo.Png(host, "config-external-change");

                var texts = VisibleTexts(host).ToList();
                Assert.True(vm.IsReviewingExternalChange, string.Join(" | ", texts));
                Assert.True(texts.Any(t => t.Contains("llm.model", StringComparison.Ordinal) && t.Contains("gpt-4.1", StringComparison.Ordinal) && t.Contains("o3", StringComparison.Ordinal)), string.Join(" | ", texts));
                Assert.Contains(texts, t => t.Contains("llm.model", StringComparison.Ordinal) && t.Contains("gpt-4.1", StringComparison.Ordinal) && t.Contains("o3", StringComparison.Ordinal));
                Assert.True(texts.Any(t => t.Contains("token: " + ConfigDiffReviewBuilder.Hidden, StringComparison.Ordinal)), string.Join(" | ", texts));
                Assert.DoesNotContain(texts, t => t.Contains("sample-changed", StringComparison.Ordinal));
                Assert.DoesNotContain(texts, t => t.Contains("sample-not-a-real-token", StringComparison.Ordinal));

                var rebase = VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Rebase my edits");
                Assert.NotNull(rebase);
                Assert.True(rebase!.IsEnabled);
                Assert.NotNull(VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Discard my edits and reload"));
                Assert.NotNull(VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Back to editing"));

                await vm.RebaseDraftCommand.ExecuteAsync(null);
                host.Relayout();

                texts = VisibleTexts(host).ToList();
                Assert.False(vm.IsReviewingExternalChange);
                Assert.DoesNotContain(texts, t => t.Contains("Changed in both places", StringComparison.Ordinal));
                Assert.NotNull(VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Undo rebase"));
            }
            finally
            {
                Close(window);
            }
        });
    }

    [Fact]
    public async Task A_config_that_does_not_parse_shows_the_card_with_its_position_and_the_actions()
    {
        using var services = TestServices.Create(_temp, "llm:\n  model: x\n  provider: [unclosed\n");
        services.ConfigWatcher.Dispose();

        await UiThread.Run(async () =>
        {
            var window = new ConfigEditorWindow(services);
            var vm = ViewModelOf(window);
            vm.FormSourceOverride = _ => Task.FromResult(string.Empty);
            await vm.LoadAsync();

            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = vm;
            using var host = new OffscreenHost(content, 1000, 720);
            try
            {
                host.Relayout();
                RenderTo.Png(host, "config-unloadable");

                var texts = VisibleTexts(host).ToList();
                Assert.Contains("config.yaml is not valid YAML", texts);
                Assert.Contains(texts, t => t.StartsWith("Line ", StringComparison.Ordinal));
                foreach (var label in new[] { "Open in editor", "Show in Explorer", "Reload" })
                {
                    Assert.NotNull(VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == label));
                }

                // One explanation, not the card and the generic bar both.
                Assert.DoesNotContain("config.yaml does not parse as YAML", texts);
            }
            finally
            {
                Close(window);
            }
        });
    }
}
