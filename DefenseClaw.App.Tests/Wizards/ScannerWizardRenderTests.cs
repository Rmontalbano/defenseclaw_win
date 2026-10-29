using System.Windows.Controls;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The scanner wizards opened on a synthetic v9 config.yaml (CUST-390), as the real window's content offscreen at the app's two review sizes in
/// Cisco Light and Default Dark: the skill scanner's Analyzers and Policy pages and the MCP scanner's Analyzers page, each showing the configured
/// values rather than "(leave unchanged)". A PNG of each is written when <c>DC_RENDER_DIR</c> names a folder, and never otherwise. No Docker is
/// looked at and nothing runs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ScannerWizardRenderTests
{
    private sealed class NeverDocker : IDockerProbe
    {
        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DockerStatus(DockerState.Unknown, "Docker was not looked at.", Array.Empty<string>()));
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (width, height) in new[] { (980, 692), (1468, 875) })
        {
            foreach (var (style, mode) in new[] { ("Cisco", "Light"), ("Default", "Dark") })
            {
                yield return new object[] { "skill-scanner", "Analyzers", style, mode, width, height };
                yield return new object[] { "skill-scanner", "Policy and apply", style, mode, width, height };
                yield return new object[] { "mcp-scanner", "Analyzers and scope", style, mode, width, height };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_scanner_page_opens_on_the_configured_values(string target, string page, string style, string mode, int width, int height)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, ScannerWizardSamples.ConfigV9);
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));
        var definition = ScannerWizardSamples.Build(target, target == "skill-scanner" ? ScannerWizardSamples.SkillHelp104 : ScannerWizardSamples.McpHelp104);

        WizardViewModel? vm = null;
        OffscreenHost? host = null;
        try
        {
            UiThread.Run(() =>
            {
                vm = new WizardViewModel(services, definition, new NeverDocker(), (_, _) => null);
                var window = new WizardWindow(vm);
                var content = (Grid)window.Content;
                window.Content = null;
                foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
                {
                    content.Children.Remove(bar);
                }

                content.DataContext = vm;
                host = new OffscreenHost(content, width, height);

                for (var i = 0; i < 10 && vm.PageTitle != page; i++)
                {
                    Assert.False(vm.IsReview, "ran past " + page);
                    vm.Next();
                }

                Assert.Equal(page, vm.PageTitle);
                host.Relayout();
                RenderTo.Png(host, $"scanner-wizard-{target}-{page.Replace(' ', '-')}-{style}-{mode}-{width}x{height}".ToLowerInvariant());

                Assert.Contains("scanners.", vm.Definition.BaselineNote, StringComparison.Ordinal);
                var texts = VisualTree.Descendants<TextBlock>(host.Content).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text).ToArray();
                switch (page)
                {
                    case "Policy and apply":
                        Assert.Contains("strict", texts);
                        Assert.Contains("CRITICAL", texts);
                        Assert.Contains("LOW", texts);
                        break;
                    case "Analyzers and scope":
                        Assert.Contains(VisualTree.Descendants<TextBox>(host.Content), t => t.IsVisible && t.Text == "yara,llm");
                        break;
                }

                // An untouched page sends no setting.
                Assert.Equal(new[] { "setup", target, "--non-interactive" }, vm.Definition.BuildArgv(WizardSamples.StartingValues(vm.Definition)));
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                vm?.Dispose();
                host?.Dispose();
            });
        }
    }
}
