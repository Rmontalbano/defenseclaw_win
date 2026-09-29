using DefenseClaw.App.Services;
using DefenseClaw.Core.Paths;

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

    public static AppServices Create(TempDirectory temp, string? configYaml = null)
    {
        if (configYaml is not null)
        {
            _ = temp.WriteFile("config.yaml", configYaml);
        }

        return AppServices.CreateIsolated(
            IsolatedPaths(temp.Path),
            claudeSettingsPath: temp.File("claude-settings.json"));
    }
}
