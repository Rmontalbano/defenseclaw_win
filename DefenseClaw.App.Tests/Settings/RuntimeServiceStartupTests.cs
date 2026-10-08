using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// Building the composition must not look for the CLI: a lookup walks PATH (one dead network entry costs ~40 s) and its "missing" answer
/// is remembered, which would hide a CLI installed a moment later. The runtime service only asks when it is started or refreshed.
/// </summary>
public sealed class RuntimeServiceStartupTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Composing_the_app_services_does_not_look_up_the_cli()
    {
        using var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"));

        Assert.False(services.Paths.TryGetKnownExecutable("defenseclaw", out _));
    }
}
