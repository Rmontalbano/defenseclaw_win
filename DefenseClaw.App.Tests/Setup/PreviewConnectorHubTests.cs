using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Tests.Wizards;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// A connector the 1.0.6 runtime calls <c>preview</c> (OpenCode on Windows) in the Setup hub and its wizard (CUST-425): badged Preview in the caution
/// tone, launchable, not counted as certified, and hidden by "Hide uncertified connectors" like the other connectors that are not certified. The
/// connector tile and the wizard header are rendered offscreen at the app's two review sizes in light and dark; a PNG of each is written when
/// <c>DC_RENDER_DIR</c> names a folder, and never otherwise.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PreviewConnectorHubTests
{
    private static WizardDefinition Def(string target, string title, PlatformStatus status, string note = "") => new()
    {
        Target = target,
        Title = title,
        Group = WizardGroups.Connectors,
        PlatformStatus = status,
        PlatformNote = note,
        Description = "Configure DefenseClaw for " + title + ".",
        IsDetailLoaded = true,
    };

    private static readonly WizardDefinition[] Catalog =
    {
        Def("claude-code", "Claude Code", PlatformStatus.Certified),
        Def("codex", "Codex", PlatformStatus.Certified),
        Def("cursor", "Cursor", PlatformStatus.NotCertified),
        Def("opencode", "OpenCode", PlatformStatus.Preview, PreviewStatusTests.PreviewNote),
        Def("remove", "Remove a connector", PlatformStatus.NotApplicable),
    };

    [Theory]
    [InlineData(PlatformStatus.Certified, true)]
    [InlineData(PlatformStatus.NotApplicable, true)]
    [InlineData(PlatformStatus.NotCertified, false)]
    [InlineData(PlatformStatus.Preview, false)]
    [InlineData(PlatformStatus.Unsupported, false)]
    [InlineData(PlatformStatus.Unknown, false)]
    public void The_certification_filter_keeps_only_certified_connectors_and_flows_with_nothing_to_certify(PlatformStatus status, bool kept)
    {
        Assert.Equal(kept, SetupPanelViewModel.PassesCertificationFilter(status));
    }

    [Fact]
    public void A_preview_connector_is_badged_launchable_and_hidden_only_by_the_certification_filter()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(Catalog);

            var card = vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "opencode");
            Assert.Equal(PlatformStatus.Preview, card.PlatformStatus);
            Assert.Equal("Preview on Windows", card.Badge);
            Assert.Equal("Warn", card.BadgeKey);
            Assert.True(card.IsAvailable);
            Assert.Contains(vm.ConnectorChoices, c => c.Target == "opencode");

            // Not counted as certified: Claude Code and Codex only.
            Assert.Contains("2 certified connector(s)", vm.StatusNote, StringComparison.Ordinal);

            vm.CertifiedOnly = true;
            Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "opencode");
            Assert.DoesNotContain(vm.ConnectorChoices, c => c.Target == "opencode");
            Assert.DoesNotContain(vm.Groups.SelectMany(g => g.Cards), c => c.Target == "cursor");
            Assert.Contains(vm.ConnectorChoices, c => c.Target == "codex");

            vm.CertifiedOnly = false;
            Assert.Contains(vm.ConnectorChoices, c => c.Target == "opencode");
        });
    }

    // ------------------------------------------------------------------ renders

    /// <summary>The app's two review sizes in Cisco Light and Default Dark (the wizard header's badge is drawn the same in every style).</summary>
    public static IEnumerable<object[]> Renders()
    {
        foreach (var (width, height) in new[] { (980, 692), (1468, 875) })
        {
            foreach (var (style, mode) in new[] { ("Cisco", "Light"), ("Default", "Dark") })
            {
                yield return new object[] { width, height, style, mode };
            }
        }
    }

    /// <summary>The same sizes in Linear Light, which draws the tile's certification glyph (Cisco and Default draw none, only the tooltip), and Default Dark.</summary>
    public static IEnumerable<object[]> ChooserRenders()
    {
        foreach (var (width, height) in new[] { (980, 692), (1468, 875) })
        {
            foreach (var (style, mode) in new[] { ("Linear", "Light"), ("Default", "Dark") })
            {
                yield return new object[] { width, height, style, mode };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ChooserRenders))]
    public void The_connector_chooser_shows_the_preview_badge_beside_the_others(int width, int height, string style, string mode)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, ConfigSamples.Raw);
        services.ConfigWatcher.Dispose();
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));

        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, width, height);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");

            UiThread.Run(() =>
            {
                vm.ShowCards(Catalog);
                vm.HasLoadError = false;
                vm.LoadError = string.Empty;
                vm.IsConnectorSetupOpen = true;
                shell.Host.Relayout();
                UiThread.Settle();
                shell.Host.Relayout();
                RenderTo.Png(shell.Host, $"cust425-connector-chooser-{width}x{height}-{style.ToLowerInvariant()}-{mode.ToLowerInvariant()}");

                var tile = VisualTree.Descendants<Button>(shell.Page!)
                    .Single(b => b.IsVisible && b.DataContext is WizardCardViewModel { Target: "opencode" });
                var card = (WizardCardViewModel)tile.DataContext;
                Assert.Contains("Preview on Windows", card.AutomationName, StringComparison.Ordinal);
                Assert.Contains("Preview on Windows", card.TileToolTip, StringComparison.Ordinal);

                // The Linear style draws the certification as a glyph beside the title (a shape as well as a colour); Default draws none.
                var glyph = VisualTree.Descendants<DcStatusGlyph>(tile).Single();
                Assert.Equal("Warn", glyph.Tone);
                Assert.Equal(style == "Linear", glyph.ActualWidth > 0);
                Assert.Same(vm.LaunchCommand, tile.Command);
                Assert.Equal("Configure OpenCode", AutomationProperties.GetName(tile));
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Theory]
    [MemberData(nameof(Renders))]
    public void The_wizard_header_names_the_preview_and_carries_the_CLIs_note(int width, int height, string style, string mode)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));

        var help = SetupHelpParser.Parse(PreviewStatusTests.OpenCodeHelp);
        var (steps, curated) = WizardStepFactory.Build("opencode", help);
        var definition = new WizardDefinition
        {
            Target = "opencode",
            Title = "OpenCode",
            Group = WizardGroups.Connectors,
            Description = help.Summary,
            Steps = WizardWindowsPolicy.Filter("opencode", steps),
            PlatformStatus = help.PlatformStatus,
            PlatformNote = help.PlatformNote,
            IsCurated = curated,
            IsDetailLoaded = true,
            HelpText = PreviewStatusTests.OpenCodeHelp,
        };

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
                host.Relayout();
                RenderTo.Png(host, $"cust425-wizard-header-{width}x{height}-{style.ToLowerInvariant()}-{mode.ToLowerInvariant()}");

                Assert.Equal("Preview on Windows", vm.CertificationBadge);
                Assert.Equal("Warn", vm.CertificationKey);
                Assert.Contains(PreviewStatusTests.PreviewNote, vm.CertificationWarning, StringComparison.Ordinal);
                Assert.DoesNotContain("could not be read", vm.CertificationWarning, StringComparison.Ordinal);

                var texts = VisualTree.Descendants<TextBlock>(host.Content).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Contains("Preview on Windows", texts);
                Assert.Contains(texts, t => t.Contains(PreviewStatusTests.PreviewNote, StringComparison.Ordinal));
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

    private sealed class NeverDocker : IDockerProbe
    {
        public Task<DockerStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DockerStatus(DockerState.Unknown, "Docker was not looked at.", Array.Empty<string>()));
    }
}
