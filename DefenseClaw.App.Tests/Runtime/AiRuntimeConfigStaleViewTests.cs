using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// What the operator sees on the Runtime panel when <c>config.yaml</c> or <c>.env</c> changed after the snapshot was read (CUST-312): the same STALE
/// banner, kept rows and off buttons a failed read gives (CUST-309), with the config as the reason. The view is hosted offscreen over a view-model
/// whose gateway and CLI are scripts; the data is synthetic. A PNG is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiRuntimeConfigStaleViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static CliInvocation Done(IReadOnlyList<string> argv, string output)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Append(invocation, output);
        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    [Fact]
    public async Task A_config_change_after_the_read_puts_the_stale_banner_over_the_kept_snapshot_with_the_config_as_the_reason()
    {
        _services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _ = await _services.Runtime.RefreshAsync();
        var files = new ConfigEdits(_services);

        var (vm, init) = UiThread.Run(() =>
        {
            var model = new AiRuntimePanelViewModel(_services)
            {
                ReadSnapshot = _ => Task.FromResult(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Fixture("rest/ai-usage-runtime.populated.synthetic.json")))),
                RunPermissionsRead = (argv, _) => Task.FromResult(Done(argv, Fixture("cli/agent-discovery-runtime-permissions.windows.synthetic.json"))),
            };
            model.SetActive(true); // on screen: it listens for the reload
            return (model, model.InitializeAsync());
        });
        UiThread.WaitFor(() => init.IsCompleted, "the first read");
        var (view, host) = UiThread.Run(() =>
        {
            var panel = new AiRuntimePanel { DataContext = vm };
            return (panel, new OffscreenHost(panel, 1100, 820));
        });

        try
        {
            UiThread.Run(() =>
            {
                host.Relayout();
                Assert.False(vm.IsStale);
                Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(view), b => b.IsOpen && b.Title == "Stale");
            });

            // The edit, and the watcher's notification of it (raised from this thread, delivered on the UI thread).
            files.EditConfig();
            files.Reload();

            UiThread.Run(() =>
            {
                host.Relayout();
                var banner = VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(view).First(b => b.IsOpen && b.Title == "Stale");
                Assert.StartsWith("STALE", banner.Message, StringComparison.Ordinal);
                Assert.Contains(AiRuntimePanelViewModel.ConfigMovedReason, banner.Message, StringComparison.Ordinal);

                // Kept: the findings are still listed, and the planes say their reading is of the last poll.
                Assert.Equal(6, Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view)).Items.Count);

                var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).ToArray();
                Assert.False(buttons.Single(b => AutomationProperties.GetName(b) == "Poll now").IsEnabled);
                Assert.True(buttons.Single(b => AutomationProperties.GetName(b) == "Refresh").IsEnabled);

                RenderTo.Png(host, "config-stale-runtime");
            });

            // A fresh read takes the banner away and gives the buttons back.
            var refresh = UiThread.Run(() => vm.RefreshCommand.ExecuteAsync(null));
            UiThread.WaitFor(() => refresh.IsCompleted, "the fresh read");
            UiThread.Run(() =>
            {
                host.Relayout();
                Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(view), b => b.IsOpen && b.Title == "Stale");
                Assert.True(VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Single(b => AutomationProperties.GetName(b) == "Poll now").IsEnabled);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                vm.SetActive(false);
                host.Dispose();
            });
        }
    }
}
