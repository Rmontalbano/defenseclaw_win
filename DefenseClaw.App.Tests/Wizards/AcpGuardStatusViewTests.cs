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
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-401: the read-only "Guard status" card of the ACP Guard window on a 1.0.5 runtime. The card's lines for a healthy guard, a guard that needs attention,
/// a status that could not be read and a runtime that has no such command; the discovery that reads it beside the help and the gateway; the gate on the
/// version; and the real window's content rendered offscreen (never shown) at 980 x 692 and 1468 x 875, light and dark. A PNG is written when
/// <c>DC_RENDER_DIR</c> names a folder. Synthetic fixtures; nothing runs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AcpGuardStatusViewTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0.4", "acp", name));

    private static string StatusFixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0.5", "acp", name));

    private static AcpRead<AcpGuardStatus> Status(string name) => AcpGuardStatusReader.Parse(StatusFixture(name));

    private static AcpGuardContext Context(AcpRead<AcpGuardStatus>? status) => AcpGuardDiscovery.From(
        new HelpProbeResult(Fixture("acp-setup-help.synthetic.txt"), null),
        AcpGuardReader.Catalog(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Fixture("acp-catalog.synthetic.json")))),
        AcpGuardReader.Profiles(GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(Fixture("acp-profiles.synthetic.json")))),
        status);

    // ------------------------------------------------------------------ the card

    [Fact]
    public void A_healthy_guard_lists_each_pair_in_the_ok_tone_and_says_nothing_was_changed()
    {
        var block = AcpGuardStatusView.Block(Status("acp-status.healthy.synthetic.json"))!;

        Assert.Equal("Guard status", block.Title);
        Assert.Equal(new WizardInfoLine("ACP guard", "On, 2 guarded pairs"), block.Lines[0]);
        Assert.Equal(new WizardInfoLine("jetbrains / copilot", "Healthy · profile locked (action)", "Ok"), block.Lines[1]);
        Assert.Equal(new WizardInfoLine("zed / kiro", "Healthy · profile default (observe, inherited)", "Ok"), block.Lines[2]);
        Assert.Equal(3, block.Lines.Count);
        Assert.Equal(AcpGuardStatusView.HealthyNote, block.Note);
    }

    [Fact]
    public void A_pair_that_needs_attention_is_warned_with_the_CLIs_findings_beneath_it()
    {
        var block = AcpGuardStatusView.Block(Status("acp-status.attention.synthetic.json"))!;

        var pair = Assert.Single(block.Lines, l => l.Label == "zed / kiro");
        Assert.Equal("Warn", pair.ToneKey);
        Assert.StartsWith("Needs attention · profile default", pair.Value, StringComparison.Ordinal);

        var at = block.Lines.ToList().IndexOf(pair);
        Assert.Equal(new WizardInfoLine(string.Empty, "editor entry 'kiro-2' starts the guard with this pair's contract lock, which pins only the entries setup wrote, so the guard refuses to start", "Warn"), block.Lines[at + 1]);
        Assert.All(block.Lines.Skip(at + 1), l => Assert.Equal("Warn", l.ToneKey));
        Assert.Equal(AcpGuardStatusView.AttentionNote, block.Note);
        Assert.Equal("Ok", block.Lines.Single(l => l.Label == "jetbrains / copilot").ToneKey);
    }

    [Fact]
    public void A_guard_with_no_pairs_or_switched_off_says_so()
    {
        var none = AcpGuardStatusView.Block(AcpGuardStatusReader.Parse("""{"enabled":false,"bindings":{}}"""))!;

        Assert.Equal("Off (acp.enabled is false)", none.Lines[0].Value);
        Assert.Contains("None yet", none.Lines.Single(l => l.Label == "Pairs").Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_status_that_could_not_be_read_is_a_line_and_a_runtime_without_the_command_is_no_card()
    {
        var failed = AcpGuardStatusView.Block(AcpGuardStatusReader.FromRun(1, string.Empty, "Error: configuration is unavailable"))!;
        var line = Assert.Single(failed.Lines);
        Assert.Equal("Status", line.Label);
        Assert.Contains("Could not be read", line.Value, StringComparison.Ordinal);
        Assert.Contains("configuration is unavailable", line.Value, StringComparison.Ordinal);

        Assert.Null(AcpGuardStatusView.Block(AcpGuardStatusReader.FromRun(2, string.Empty, "No such command 'status'")));
        Assert.Null(AcpGuardStatusView.Block(null));
    }

    // ------------------------------------------------------------------ discovery and the definition

    [Fact]
    public async Task Discovery_reads_the_status_only_when_given_a_reader_and_the_definition_carries_the_card()
    {
        var help = new HelpProbeResult(Fixture("acp-setup-help.synthetic.txt"), null);
        Task<HelpProbeResult> ReadHelp(IReadOnlyList<string> _, CancellationToken __) => Task.FromResult(help);
        Task<GatewayResult<JsonDocument>> Fetch(string _, int __, CancellationToken ___) => Task.FromResult(GatewayResult<JsonDocument>.Error("gateway returned HTTP 404", 404));

        var older = await AcpGuardDiscovery.ReadAsync(ReadHelp, Fetch);
        Assert.Null(older.Status);
        Assert.Null(AcpGuardWizard.Build(older, managedInstallation: false).InfoBlock);

        var reads = 0;
        var current = await AcpGuardDiscovery.ReadAsync(ReadHelp, Fetch, default, _ =>
        {
            reads++;
            return Task.FromResult(Status("acp-status.healthy.synthetic.json"));
        });

        Assert.Equal(1, reads);
        Assert.True(current.Status!.IsOk);
        var definition = AcpGuardWizard.Build(current, managedInstallation: false);
        Assert.NotNull(definition.InfoBlock);

        // The card is information: the fields and the command are exactly what they were.
        Assert.Equal(AcpGuardWizard.Build(older, false).AllFields.Select(f => f.Id), definition.AllFields.Select(f => f.Id));
    }

    private static RuntimeProbeRunner Versioned(string version)
    {
        var pin = RuntimeFixtureRunner.For("95159fd");
        return (arguments, ct) => string.Join(' ', arguments) == "--version-json"
            ? Task.FromResult(RuntimeProbeOutput.Ok($"{{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"{version}\"}}"))
            : pin(arguments, ct);
    }

    [Theory]
    [InlineData("1.0.5", true)]
    [InlineData("1.0.4", false)]
    [InlineData("1.0.0", false)]
    public async Task The_status_is_read_only_on_a_runtime_that_can_vouch_for_it(string version, bool expected)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: Versioned(version));
        _ = await services.Runtime.RefreshAsync();

        Assert.Equal(expected, AcpGuardWizard.HasGuardStatus(services));
    }

    // ------------------------------------------------------------------ the window

    public static IEnumerable<object[]> Renders() =>
        from size in new[] { (980, 692), (1468, 875) }
        from mode in new[] { "Light", "Dark" }
        from state in new[] { "healthy", "attention" }
        select new object[] { size.Item1, size.Item2, mode, state };

    [Theory]
    [MemberData(nameof(Renders))]
    public void The_card_sits_above_the_form_and_the_form_is_still_in_the_Macs_order(int width, int height, string mode, string state)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));
        var name = $"cust401-acp-status-{state}-{width}x{height}-{mode}";

        var (viewModel, host) = UiThread.Run(() =>
        {
            var vm = new WizardViewModel(services, AcpGuardWizard.Build(Context(Status($"acp-status.{state}.synthetic.json")), services.Installation.Context.IsManaged));
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
                RenderTo.Png(host, name);

                var texts = VisualTree.Descendants<TextBlock>(host.Content).Where(t => t.IsVisible && t.Text.Length > 0).ToArray();
                var title = texts.First(t => t.Text == "Guard status");
                var client = texts.First(t => t.Text == "Client");
                Assert.True(Top(title, host.Content) < Top(client, host.Content), "the card is above the form");
                Assert.Contains(texts, t => t.Text == "zed / kiro");
                Assert.Equal(state == "attention", texts.Any(t => t.Text.Contains("editor entry 'kiro-2' starts the guard", StringComparison.Ordinal)));
                Assert.Contains(texts, t => t.Text == (state == "attention" ? AcpGuardStatusView.AttentionNote : AcpGuardStatusView.HealthyNote));

                // The Codex-home bar is for setup codex alone.
                Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(host.Content), b => b.IsOpen && b.Title == "Codex home is a link");
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

    [Fact]
    public void Without_a_status_the_window_has_no_card()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        var (viewModel, host) = UiThread.Run(() =>
        {
            var vm = new WizardViewModel(services, AcpGuardWizard.Build(Context(null), services.Installation.Context.IsManaged));
            var window = new WizardWindow(vm);
            var content = (Grid)window.Content;
            window.Content = null;
            foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
            {
                content.Children.Remove(bar);
            }

            content.DataContext = vm;
            return (vm, new OffscreenHost(content, 980, 692));
        });

        try
        {
            UiThread.Run(() =>
            {
                host.Relayout();
                Assert.False(viewModel.HasInfoBlock);
                Assert.DoesNotContain(VisualTree.Descendants<TextBlock>(host.Content), t => t.IsVisible && t.Text == "Guard status");
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
}
