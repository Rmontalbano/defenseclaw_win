using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-294: the ACP Guard wizard as the real window's content, offscreen (the window is never shown), at the main window's minimum (980 x 692) and at
/// 1468 x 875, light and dark: the page in the Mac's order (Client, Agent, Profile, Action mode, Managed enrollment and its two paths) and the review.
/// A PNG of each is written when <c>DC_RENDER_DIR</c> names a folder, and never otherwise. Fixture data only; nothing runs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AcpGuardWizardViewTests
{
    private const string RuntimeDir = @"C:\Users\operator\.defenseclaw";

    public static IEnumerable<object[]> Renders() =>
        from size in new[] { (980, 692), (1468, 875) }
        from mode in new[] { "Light", "Dark" }
        from managed in new[] { false, true }
        select new object[] { size.Item1, size.Item2, mode, managed };

    private static AcpGuardContext Context()
    {
        static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0.4", "acp", name));
        return AcpGuardDiscovery.From(
            new HelpProbeResult(Fixture("acp-setup-help.synthetic.txt"), null),
            AcpGuardReader.Catalog(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Fixture("acp-catalog.synthetic.json")))),
            AcpGuardReader.Profiles(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Fixture("acp-profiles.synthetic.json")))));
    }

    [Theory]
    [MemberData(nameof(Renders))]
    public void The_page_follows_the_Macs_order_and_the_review_shows_the_exact_command(int width, int height, string mode, bool managed)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, installation: managed ? TestInstallations.Managed() : null);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));
        var name = $"cust294-{(managed ? "managed" : "per-user")}-{width}x{height}-{mode}";

        var (viewModel, host) = UiThread.Run(() =>
        {
            var vm = new WizardViewModel(services, AcpGuardWizard.Build(Context(), services.Installation.Context.IsManaged));

            // Answered before the page is drawn, so a switch is drawn in its state rather than mid-animation.
            Set(vm, AcpGuardWizard.ClientField, "zed");
            Set(vm, AcpGuardWizard.AgentField, "kiro");
            Set(vm, AcpGuardWizard.ActivateField, ToggleValues.On);
            if (managed)
            {
                Set(vm, AcpGuardWizard.RuntimeDataDirField, RuntimeDir);
                Set(vm, AcpGuardWizard.TokenFileField, RuntimeDir + @"\acp\zed-kiro.token");
            }

            var window = new WizardWindow(vm);
            var content = (Grid)window.Content;
            window.Content = null;
            foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
            {
                content.Children.Remove(bar);
            }

            content.DataContext = vm;
            return (vm, new OffscreenHost(content, width, height));
        });

        try
        {
            UiThread.Run(() =>
            {
                host.Relayout();
                RenderTo.Png(host, name + "-page");

                // The header names the command this wizard runs, and the labels are in the Mac's order.
                var texts = VisualTree.Descendants<TextBlock>(host.Content).Where(t => t.IsVisible && t.Text.Length > 0).ToArray();
                Assert.Contains(texts, t => t.Text == "acp setup");
                var labels = new[] { "Client", "Agent", "Profile", "Action mode (block denied operations)", "Managed enrollment" };
                var tops = labels.Select(label => Top(texts.First(t => t.Text == label), host.Content)).ToArray();
                Assert.Equal(tops.Order(), tops);
                Assert.Equal(managed, texts.Any(t => t.Text == "Administrator-provisioned token file"));
                Assert.Equal(!managed, texts.Any(t => t.Text == AcpGuardArgv.ManagedNeedsManagedInstallation));

                // The footer's Next is on screen at the smallest size.
                var next = VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Next")!;
                Assert.True(Top(next, host.Content) + next.ActualHeight <= height, "Next is on screen");

                viewModel.Next();
                Assert.True(viewModel.IsReview, viewModel.ValidationSummary);
                host.Relayout();
                RenderTo.Png(host, name + "-review");

                var command = viewModel.CommandReview!.Steps.Single().CommandText;
                Assert.Contains("acp setup --client zed --agent kiro --profile default --activate", command, StringComparison.Ordinal);
                Assert.Equal(managed, !viewModel.CanExecute);
                var execute = VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Execute")!;
                Assert.Equal(!managed, execute.IsEnabled);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                viewModel.Dispose();
                host.Dispose();
            });
        }
    }

    private static double Top(FrameworkElement element, FrameworkElement root) => element.TranslatePoint(new Point(0, 0), root).Y;

    private static void Set(WizardViewModel vm, string id, string value) => vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id).Value = value;
}
