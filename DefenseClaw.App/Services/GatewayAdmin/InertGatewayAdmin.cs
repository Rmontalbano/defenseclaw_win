using System.IO;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>
/// The "Run gateway as administrator" dependencies of a composition that must not reach the real machine (<see cref="AppServices.CreateIsolated"/>):
/// the feature reports itself unsupported, and every effect refuses. A test that exercises the feature supplies its own fakes.
/// </summary>
internal static class InertGatewayAdmin
{
    public static GatewayAdminDependencies Dependencies(AppServices services, IAdminLayoutSource layout, IAdminFileSystem fs) => new()
    {
        Supported = static () => new GateDecisionLike(false, "Not looked at in an isolated composition."),
        ModeOn = static () => false,
        SaveMode = static _ => { },
        Snapshot = () => services.Monitor.Current,
        InstalledVersion = static () => null,
        Layout = layout,
        Fs = fs,
        Account = new InertAccountProbe(),
        Elevation = new InertElevationProbe(),
        Elevator = new InertElevator(),
        Release = new InertReleaseSource(),
        Manifest = new InertManifestWriter(),
        Stage = new InertHelperStage(),
        EnvironmentNames = static () => Array.Empty<string>(),
    };

    private sealed class InertAccountProbe : IAdminAccountProbe
    {
        public AdminAccountKind Read() => AdminAccountKind.Unknown;
    }

    private sealed class InertElevationProbe : IProcessElevationProbe
    {
        public bool? IsElevated(int pid) => null;
    }

    private sealed class InertElevator : IAdminElevator
    {
        public Task<ElevationOutcome> RunAsync(ElevationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ElevationOutcome.Failed("elevation is not available in an isolated composition"));
    }

    private sealed class InertReleaseSource : IReleaseGatewaySource
    {
        public Task<(ReleaseGateway? Release, string? Problem)> GetAsync(string version, CancellationToken cancellationToken) =>
            Task.FromResult<(ReleaseGateway?, string?)>((null, "no release check in an isolated composition"));
    }

    private sealed class InertManifestWriter : IManifestWriter
    {
        public void Write(AdminLayout layout, ReleaseManifest manifest) => throw new IOException("no manifest is written in an isolated composition");
    }

    private sealed class InertHelperStage : IHelperStage
    {
        public StagedHelper? Locate(out string? problem)
        {
            problem = "no helper in an isolated composition";
            return null;
        }
    }
}
