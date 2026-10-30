using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.FirstRun;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.FirstRun;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.FirstRun;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The surfaces CUST-397 changed, drawn offscreen on a synthetic per-user 1.0.5 at the app's two review sizes in Cisco Light and Default Dark: the Overview's
/// "What needs attention" card (an observing connector with a saved <c>closed</c>, and a saved <c>open</c> over a strict pack with its
/// <c>guardrail fail-mode closed</c> hint), and the first-run policy card with the Action profile's three fail-mode choices. A PNG is written when
/// <c>DC_RENDER_DIR</c> names a folder, and never otherwise. Nothing runs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class Runtime105FailModeRenderTests : IDisposable
{
    private const string Config =
        "guardrail:\n  enabled: true\n  connector: claudecode\n  connectors:\n" +
        "    claudecode:\n      mode: observe\n      hook_fail_mode: closed\n" +
        "    codex:\n      mode: action\n      rule_pack: strict\n      hook_fail_mode: open\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (width, height) in new[] { (980, 692), (1468, 875) })
        {
            foreach (var (style, mode) in new[] { ("Cisco", "Light"), ("Default", "Dark") })
            {
                yield return new object[] { style, mode, width, height };
            }
        }
    }

    private static GatewaySnapshot Running() => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "Gateway answering on 127.0.0.1:39871",
        ActiveConnectors = new[] { "claudecode", "codex" },
        PolledAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_attention_card_says_a_saved_mode_decides(string style, string mode, int width, int height)
    {
        using var services = TestServices.Create(_temp, Config, FirstRunFailModeTests.Versioned("1.0.5"));
        _ = await services.Runtime.RefreshAsync();
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));
        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                var snapshot = Running();
                _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(services.Monitor, new object[] { snapshot });
                shell = new PanelShell(services, width, height);
                var panel = shell.Show<OverviewPanel>();
                var vm = (OverviewPanelViewModel)shell.ViewModel;
                vm.Apply(snapshot);
                shell.Host.Relayout();

                Assert.True(vm.SavedFailModeWins);
                Assert.DoesNotContain(vm.Attention, r => r.Title.Contains("is not applied", StringComparison.Ordinal));
                var list = VisualTree.Find<ItemsControl>(panel, l => AutomationProperties.GetName(l) == "What needs attention")!;
                var texts = VisualTree.Descendants<TextBlock>(list).Select(t => t.Text).ToList();
                Assert.Contains("claudecode: observe mode with a fail-closed hook", texts);
                Assert.Contains("codex: a saved hook_fail_mode: open overrides the strict rule pack", texts);

                RenderTo.Png(shell.Host, $"cust397-overview-attention-{style.ToLowerInvariant()}-{mode.ToLowerInvariant()}-{width}x{height}");
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_first_run_policy_card_offers_save_nothing_first(string style, string mode, int width, int height)
    {
        using var services = TestServices.Create(_temp, runtimeProbeRunner: FirstRunFailModeTests.Versioned("1.0.5"));
        _ = await services.Runtime.RefreshAsync();
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));
        FirstRunViewModel? vm = null;
        OffscreenHost? host = null;
        try
        {
            UiThread.Run(() =>
            {
                vm = new FirstRunViewModel(services, _ => Task.FromResult<IReadOnlyList<WizardDefinition>>(Array.Empty<WizardDefinition>()), new GatewaySnapshot
                {
                    State = AppGatewayState.NotInitialized,
                    Install = InstallState.InstalledNotInitialized,
                    CliPath = @"C:\synthetic\defenseclaw.exe",
                });
                vm.Profile = "action";

                var window = new FirstRunWindow(vm);
                var content = (Grid)window.Content;
                window.Content = null;
                content.DataContext = vm;
                content.Children.Remove(content.Children.OfType<Wpf.Ui.Controls.TitleBar>().Single());
                content.RowDefinitions[0].Height = new GridLength(32);
                host = new OffscreenHost(content, width, height);

                var scroll = VisualTree.Descendants<ScrollViewer>(content).First(s => s.Name == "PageScroll");
                var header = VisualTree.Descendants<DcCardHeader>(content).Single(h => Equals(h.Content, "Policy and enforcement"));
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + header.TranslatePoint(new Point(0, 0), scroll).Y - 12);
                host.Relayout();

                RenderTo.Png(host, $"cust397-firstrun-failmode-{style.ToLowerInvariant()}-{mode.ToLowerInvariant()}-{width}x{height}");

                var box = VisualTree.Descendants<ComboBox>(content).Single(c => AutomationProperties.GetName(c) == "Hook failure mode");
                Assert.True(box.IsVisible);
                Assert.Equal(FirstRunFailMode.Policy, box.SelectedValue);
                Assert.Equal(3, box.Items.Count);
                Assert.Contains(VisualTree.Descendants<TextBlock>(content), t => t.IsVisible && t.Text == FirstRunFailMode.SavedOrPolicyHint);
                Assert.Contains(VisualTree.Descendants<TextBlock>(content), t => t.IsVisible && t.Text == vm.ProfileCaption && t.Text.Contains("its own files", StringComparison.Ordinal));
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "the page needs no horizontal scrolling");
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                host?.Dispose();
                vm?.Dispose();
            });
        }
    }
}
