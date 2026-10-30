using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The status strip with the command chip showing (CUST-393), drawn as the window draws it beside the gateway's own "Running" pill: the chip reads
/// "1 command running", never a bare "Running". A PNG is written when <c>DC_RENDER_DIR</c> names a folder (and never otherwise). Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class StatusStripRunningChipViewTests
{
    private readonly ITestOutputHelper _output;

    public StatusStripRunningChipViewTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(false, 980)]
    [InlineData(true, 980)]
    [InlineData(false, 1468)]
    [InlineData(true, 1468)]
    public void The_command_chip_is_drawn_as_a_count_of_commands_not_as_a_state(bool dark, int width)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, dark ? AppearanceMode.Dark : AppearanceMode.Light));
        using var scene = new StripScene();
        scene.Strip.Apply(StripScene.Snapshot(new[] { "codex" }, version: "1.0.4"));
        if (width >= 1400)
        {
            // The narrow window has no room for the redaction chip as well; leaving it out shows the command chip at 980.
            scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");
        }

        scene.Commands.OnStarted(null, StripScene.Running("doctor"));

        var row = UiThread.Run(() => new StripRow(scene.Strip, width));
        try
        {
            UiThread.WaitFor(() => row.Visible.Count > 0, "the chips to be laid out");
            UiThread.Run(() =>
            {
                _output.WriteLine($"{(dark ? "Dark" : "Light")} {width}: visible [{string.Join(", ", row.Visible)}] hidden [{string.Join(", ", row.Hidden)}]");
                var chip = scene.Chip(StripChipKey.Running);
                Assert.Equal("1 command running", chip.Text);
                Assert.Equal(chip.ToolTip, chip.AutomationName);
                Assert.Contains(StripChipKey.Running, row.Visible);
                RenderTo.Png(row.Host, $"cust393-strip-command-chip-{(dark ? "Dark" : "Light")}-{width}");
            });
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }
}
