using System.Security.Cryptography;
using DefenseClaw.App.Services;
using DefenseClaw.GatewayAdmin.Contract;
using DefenseClaw.GatewayAdmin.Tests;

namespace DefenseClaw.App.Tests.GatewayAdmin;

internal sealed class FixedLayout(AdminLayout? layout) : IAdminLayoutSource
{
    public AdminLayout? Layout { get; set; } = layout;

    public AdminLayout? Get() => Layout;
}

internal sealed class FixedAccount(AdminAccountKind kind) : IAdminAccountProbe
{
    public AdminAccountKind Kind { get; set; } = kind;

    public AdminAccountKind Read() => Kind;
}

internal sealed class FixedElevationProbe : IProcessElevationProbe
{
    public bool? Answer { get; set; }

    public bool? IsElevated(int pid) => Answer;
}

/// <summary>Records every request and answers as told. The only way a test could "elevate" is through this, and it never does.</summary>
internal sealed class FakeElevator : IAdminElevator
{
    public List<ElevationRequest> Requests { get; } = new();

    public List<string> Events { get; set; } = new();

    public Func<ElevationRequest, ElevationOutcome> Behaviour { get; set; } = _ => ElevationOutcome.Completed(0);

    /// <summary>Runs after the request is recorded: a test uses it to leave a result file as the helper would.</summary>
    public Action<ElevationRequest>? OnRun { get; set; }

    public Task<ElevationOutcome> RunAsync(ElevationRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Events.Add("elevate:" + AdminVerbs.Name(request.Verb));
        OnRun?.Invoke(request);
        return Task.FromResult(Behaviour(request));
    }
}

internal sealed class FakeRelease : IReleaseGatewaySource
{
    public List<string> Asked { get; } = new();

    public ReleaseGateway? Release { get; set; }

    public string? Problem { get; set; }

    public Task<(ReleaseGateway? Release, string? Problem)> GetAsync(string version, CancellationToken cancellationToken)
    {
        Asked.Add(version);
        return Task.FromResult((Release, Release is null ? Problem ?? "no release" : null));
    }
}

internal sealed class FakeManifest : IManifestWriter
{
    public List<ReleaseManifest> Written { get; } = new();

    public List<string> Events { get; set; } = new();

    public void Write(AdminLayout layout, ReleaseManifest manifest)
    {
        Events.Add("manifest");
        Written.Add(manifest);
    }
}

internal sealed class FakeStage : IHelperStage
{
    public StagedHelper? Helper { get; set; }

    public string? Problem { get; set; } = "not there";

    public StagedHelper? Locate(out string? problem)
    {
        problem = Helper is null ? Problem : null;
        return Helper;
    }
}

/// <summary>A synthetic machine for <see cref="GatewayAdminService"/>: the helper and the copy installed or not, a release, a stage, an account.</summary>
internal sealed class AdminRig
{
    public const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string ProgramFiles = @"C:\TestPF";
    public const string Profile = @"C:\TestUsers\tester";
    public const string Version = "1.0.0";

    /// <summary>This app's Windows session in the rig: only the copy run in it is the helper's start (CUST-392).</summary>
    public const int AppSession = 1;

    public static readonly byte[] Gateway = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 % 251)).ToArray();
    public static readonly byte[] Helper = Enumerable.Range(0, 2048).Select(i => (byte)(i * 13 % 239)).ToArray();

    public FakeFs Fs { get; } = new();
    public AdminLayout Layout { get; } = AdminLayout.Create(ProgramFiles, UserSid, Profile);
    public FixedLayout LayoutSource { get; }
    public FixedAccount Account { get; } = new(AdminAccountKind.SplitTokenAdministrator);
    public FixedElevationProbe Elevation { get; } = new();
    public FakeElevator Elevator { get; } = new();
    public FakeRelease Release { get; } = new();
    public FakeManifest Manifest { get; } = new();
    public FakeStage Stage { get; } = new();
    public List<string> Events { get; } = new();
    public bool Supported { get; set; } = true;
    public bool ModeOn { get; set; }
    public List<bool> Saved { get; } = new();
    public string? InstalledVersion { get; set; } = Version;
    public GatewaySnapshot Snapshot { get; set; } = new() { State = AppGatewayState.GatewayStopped, Install = DefenseClaw.Core.Install.InstallState.GatewayStopped };
    public IReadOnlyList<string> EnvironmentNames { get; set; } = new[] { "PATH", "TEMP" };
    public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
    public GatewayAdminService Service { get; }

    /// <summary>The same fakes, for a test that builds a whole <see cref="AppServices"/> around them.</summary>
    public GatewayAdminDependencies Dependencies { get; }

    public AdminRig(bool helperInstalled = false, bool copyInstalled = false, bool installedGateway = true)
    {
        LayoutSource = new FixedLayout(Layout);
        Elevator.Events = Events;
        Manifest.Events = Events;

        Fs.AddDir(ProgramFiles, AdminAcl.TrustedInstaller);
        Fs.AddDir(Profile, UserSid, UserAcl());
        Fs.AddDir(Path.Combine(Profile, ".local"), UserSid, UserAcl());
        Fs.AddDir(Path.Combine(Profile, ".local", "bin"), UserSid, UserAcl());
        Fs.AddDir(Layout.DataDirectory, UserSid, UserAcl());
        if (installedGateway)
        {
            Fs.AddFile(Layout.InstalledGatewayPath, Gateway, UserSid, UserAcl());
        }

        if (helperInstalled)
        {
            InstallHelper();
        }

        if (copyInstalled)
        {
            InstallCopy(Gateway);
        }

        Release.Release = new ReleaseGateway(Version, Sha(Gateway), new string('a', 64), "checksums.txt signature verified (cosign)", "Verified");
        Stage.Helper = new StagedHelper(@"C:\TestApp\DefenseClaw.GatewayAdmin.exe", Sha(Helper));

        Dependencies = new GatewayAdminDependencies
        {
            Supported = () => new GateDecisionLike(Supported, Supported ? string.Empty : "Needs DefenseClaw 1.0."),
            ModeOn = () => ModeOn,
            SaveMode = on =>
            {
                ModeOn = on;
                Saved.Add(on);
            },
            Snapshot = () => Snapshot,
            InAppSession = static owner => owner.SessionId == AppSession,
            InstalledVersion = () => InstalledVersion,
            Layout = LayoutSource,
            Fs = Fs,
            Account = Account,
            Elevation = Elevation,
            Elevator = Elevator,
            Release = Release,
            Manifest = Manifest,
            Stage = Stage,
            EnvironmentNames = () => EnvironmentNames,
            Clock = new FixedTimeProvider(() => Now),
        };
        Service = new GatewayAdminService(Dependencies);
        Fs.Changes.Clear();
    }

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static List<AclEntry> UserAcl() => new() { new AclEntry(UserSid, true, 0x1F01FF), new AclEntry(AdminAcl.Administrators, true, 0x1F01FF) };

    public void InstallHelper()
    {
        Fs.AddDir(Layout.ProductRoot);
        Fs.AddDir(Layout.HelperDirectory);
        Fs.AddFile(Layout.HelperExe, Helper);
        Fs.AddFile(Layout.HelperRecordPath, System.Text.Encoding.UTF8.GetBytes(new HelperRecord(1, Sha(Helper), "2026-10-01T00:00:00Z").ToJson()));
    }

    public void InstallCopy(byte[] bytes)
    {
        Fs.AddDir(Layout.ProductRoot);
        Fs.AddDir(Layout.AdminGatewayRoot);
        Fs.AddDir(Layout.UserDirectory);
        Fs.AddFile(Layout.GatewayCopyPath, bytes);
        Fs.AddFile(
            Layout.CopyRecordPath,
            System.Text.Encoding.UTF8.GetBytes(CopyRecord.Create(Sha(bytes), Version, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Sha(bytes), "Verified").ToJson()));
    }

    /// <summary>Leaves the result note the helper would, for the step that just ran.</summary>
    public void LeaveResult(AdminVerb verb, int exitCode, params string[] lines) =>
        Fs.AddFile(
            Layout.ResultPath,
            System.Text.Encoding.UTF8.GetBytes(new AdminRunResult(1, AdminVerbs.Name(verb), exitCode, Now.UtcDateTime.AddSeconds(1).ToString("O"), lines).ToJson()));

    private sealed class FixedTimeProvider(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }
}
