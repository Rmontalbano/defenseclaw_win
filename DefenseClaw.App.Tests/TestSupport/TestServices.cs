using DefenseClaw.App.Services;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Builds an <see cref="AppServices"/> over a scratch data directory. The runner's PATH is empty and
/// its bin directory does not exist, so every <c>RunAsync</c> ends in <c>CliNotFoundException</c>
/// before any process could start: nothing here can reach the real DefenseClaw install.
/// </summary>
internal static class TestServices
{
    public static DefenseClawPaths IsolatedPaths(string dataDirectory) =>
        new(
            dataDirectory: dataDirectory,
            binDirectory: System.IO.Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>());

    /// <param name="runtimeProbeRunner">
    /// What answers the runtime probes (<c>--version-json</c>, the help screens). Null: nothing does, so the runtime reads as unknown and every
    /// capability newer than 0.8.10 is absent - the default for the panels that do not care.
    /// </param>
    public static AppServices Create(TempDirectory temp, string? configYaml = null, RuntimeProbeRunner? runtimeProbeRunner = null)
    {
        if (configYaml is not null)
        {
            _ = temp.WriteFile("config.yaml", configYaml);
        }

        return AppServices.CreateIsolated(
            IsolatedPaths(temp.Path),
            claudeSettingsPath: temp.File("claude-settings.json"),
            runtimeProbeRunner: runtimeProbeRunner);
    }
}
