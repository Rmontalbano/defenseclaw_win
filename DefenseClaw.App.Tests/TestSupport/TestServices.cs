using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
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
    /// <summary>
    /// The read limits of a test composition: the suite's ceiling for everything that happens on its own (<see cref="TestTimeouts.Ceiling"/>), for
    /// every kind of <c>audit.db</c> read. The app's own are 10 s, 8 s and 5 s, and a read that outlasts one is reported as "could not be read",
    /// which a panel shows as an empty list and a test sees as "there were no findings" - on a machine busy enough, the wait for a thread alone
    /// can pass that (CUST-323). A test that is about the limit itself builds its own composition with <see cref="ReaderTimeouts.Production"/> or a tiny one.
    /// </summary>
    public static ReaderTimeouts ReaderTimeouts { get; } = ReaderTimeouts.Uniform(TestTimeouts.Ceiling);

    /// <param name="dataDirectory">The scratch folder the composition treats as the data directory.</param>
    /// <param name="installation">
    /// The installation the composition starts with (CUST-308): null is the permissive, user-owned one every test has always had; a managed or invalid
    /// one (<see cref="TestInstallations"/>) makes the composition read-only from the start.
    /// </param>
    public static DefenseClawPaths IsolatedPaths(string dataDirectory, InstallationContext? installation = null) =>
        new(
            dataDirectory: dataDirectory,
            binDirectory: System.IO.Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>(),
            installation: installation);

    /// <param name="runtimeProbeRunner">
    /// What answers the runtime probes (<c>--version-json</c>, the help screens). Null: nothing does, so the runtime reads as unknown and every
    /// capability newer than 0.8.10 is absent - the default for the panels that do not care.
    /// </param>
    /// <param name="installation">The installation the composition starts with; null is the usual writable one. See <see cref="IsolatedPaths"/>.</param>
    public static AppServices Create(
        TempDirectory temp,
        string? configYaml = null,
        RuntimeProbeRunner? runtimeProbeRunner = null,
        InstallationContext? installation = null)
    {
        if (configYaml is not null)
        {
            _ = temp.WriteFile("config.yaml", configYaml);
        }

        return AppServices.CreateIsolated(
            IsolatedPaths(temp.Path, installation),
            claudeSettingsPath: temp.File("claude-settings.json"),
            runtimeProbeRunner: runtimeProbeRunner,
            readerTimeouts: ReaderTimeouts);
    }
}
