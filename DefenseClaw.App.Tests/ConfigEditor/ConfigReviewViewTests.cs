using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.App.Views.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-220 on screen: the review overlay draws the masked diff over the editor, and the post-save bar offers the restart.
/// The window is built and its content hosted offscreen (never shown); the view-model is the window's own, reached by reflection,
/// loaded from a temp copy of config.yaml.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConfigReviewViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();


    private static IEnumerable<string> VisibleTexts(OffscreenHost host) =>
        VisualTree.Descendants<TextBlock>(host.Content)
            .Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text);

    [Fact]
    public async Task The_review_overlay_shows_the_masked_diff_and_the_restart_bar_follows_a_save()
    {
        var raw = LineEndings.Normalize(ConfigSamples.Raw);
        using var services = TestServices.Create(_temp, raw);
        services.ConfigWatcher.Dispose();

        await UiThread.Run(async () =>
        {
            var window = new ConfigEditorWindow(services);
            var vm = (ConfigEditorWindowViewModel)typeof(ConfigEditorWindow)
                .GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var restarts = 0;
            vm.FormSourceOverride = _ => Task.FromResult(LineEndings.Normalize(ConfigSamples.MaskedSource));
            vm.RestartGatewayRequest = () =>
            {
                restarts++;
                return Task.CompletedTask;
            };
            await vm.LoadAsync();

            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = vm;
            using var host = new OffscreenHost(content, 1000, 720);
            try
            {
                vm.RawText = raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal)
                    .Replace("sample-not-a-real-token", "sample-edited-" + "secret-value", StringComparison.Ordinal);

                await vm.ReviewAndSaveCommand.ExecuteAsync(null);
                host.Relayout();
                RenderTo.Png(host, "config-review");

                var texts = VisibleTexts(host).ToList();
                Assert.Contains("Review changes", texts);
                Assert.Contains(texts, t => t.Contains("mode: enforce", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("token: " + ConfigDiffReviewBuilder.Hidden, StringComparison.Ordinal));
                Assert.DoesNotContain(texts, t => t.Contains("sample-edited", StringComparison.Ordinal));
                Assert.DoesNotContain(texts, t => t.Contains("sample-not-a-real-token", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("unchanged line", StringComparison.Ordinal));

                await vm.ConfirmReviewedSaveCommand.ExecuteAsync(null);
                host.Relayout();
                RenderTo.Png(host, "config-restart-bar");

                texts = VisibleTexts(host).ToList();
                Assert.DoesNotContain("Review changes", texts);
                Assert.Contains(texts, t => t.Contains("restart the gateway to apply", StringComparison.Ordinal));

                var restart = VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Restart gateway…");
                Assert.NotNull(restart);
                Assert.Equal(0, restarts);
                restart!.Command.Execute(null);
                Assert.Equal(1, restarts);
            }
            finally
            {
                // A dirty editor would ask save / discard / cancel in a modal dialog when it closes; this one is being thrown away.
                typeof(ConfigEditorWindow).GetField("_closeApproved", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                window.Close();
            }
        });
    }
}
