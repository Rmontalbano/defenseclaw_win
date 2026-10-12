using System.Security.Cryptography;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin.Tests;

internal sealed class FakeIdentity : ICallerIdentity
{
    public CallerIdentity Value { get; set; } = null!;

    public Exception? Throws { get; set; }

    public CallerIdentity Read() => Throws is null ? Value : throw Throws;
}

internal sealed class FakeConsent : IConsent
{
    public bool Answer { get; set; } = true;

    public List<(string Title, string Text)> Asked { get; } = new();

    public bool Confirm(string title, string text)
    {
        Asked.Add((title, text));
        return Answer;
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public Func<ProcessRequest, ProcessOutcome> Behaviour { get; set; } = _ => new ProcessOutcome(true, 0, false, new[] { "ok" }, null);

    public List<ProcessRequest> Requests { get; } = new();

    public List<string> Events { get; set; } = new();

    public ProcessOutcome Run(ProcessRequest request)
    {
        Requests.Add(request);
        Events.Add("run:" + string.Join(' ', request.Arguments));
        return Behaviour(request);
    }
}

internal sealed class FakeTokenOwner : ITokenOwner
{
    public List<string> Events { get; set; } = new();

    public List<string> Owners { get; } = new();

    public IDisposable Use(string ownerSid)
    {
        Owners.Add(ownerSid);
        Events.Add("owner:set");
        return new Scope(Events);
    }

    private sealed class Scope(List<string> events) : IDisposable
    {
        public void Dispose() => events.Add("owner:restored");
    }
}

internal sealed class FakeRunning : IRunningGateway
{
    public Func<bool> Value { get; set; } = () => false;

    public bool IsRunning(AdminLayout layout) => Value();
}

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
}

/// <summary>A tree on a real temp folder with owners and links decided by the test (no privilege is needed, and nothing is changed on disk).</summary>
internal sealed class FakeOwners : IOwnerFileSystem
{
    public Dictionary<string, string> Owner { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, string Sid)> Set { get; } = new();

    public string DefaultOwner { get; set; } = AdminAcl.Administrators;

    public OwnerNode? Probe(string path)
    {
        var dir = Directory.Exists(path);
        if (!dir && !File.Exists(path))
        {
            return null;
        }

        return Links.Contains(path)
            ? new OwnerNode(dir, true, null)
            : new OwnerNode(dir, false, Owner.TryGetValue(path, out var o) ? o : DefaultOwner);
    }

    public IReadOnlyList<string> Children(string directory) => Directory.EnumerateFileSystemEntries(directory).ToArray();

    public void SetOwner(string path, string ownerSid)
    {
        if (Links.Contains(path))
        {
            throw new InvalidOperationException("a link must never be changed");
        }

        if (Failing.Contains(path))
        {
            throw new IOException("access denied");
        }

        Set.Add((path, ownerSid));
        Owner[path] = ownerSid;
    }
}

/// <summary>A complete, synthetic machine for the helper: a user, a Program Files, an installed gateway, a manifest.</summary>
internal sealed class Rig
{
    public const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    public const string OtherSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    public const string ProgramFiles = @"C:\TestPF";
    public const string Profile = @"C:\TestUsers\tester";

    public static readonly byte[] GatewayBytes = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7 % 251)).ToArray();
    public static readonly byte[] HelperBytes = Enumerable.Range(0, 2048).Select(i => (byte)(i * 13 % 239)).ToArray();

    public FakeFs Fs { get; } = new();
    public FakeIdentity Identity { get; } = new();
    public FakeConsent Consent { get; } = new();
    public FakeRunner Runner { get; } = new();
    public FakeTokenOwner Token { get; } = new();
    public FakeRunning Running { get; } = new();
    public FakeOwners Owners { get; } = new();
    public FakeClock Clock { get; } = new();
    public AdminLayout Layout { get; } = AdminLayout.Create(ProgramFiles, UserSid, Profile);
    public AdminHelper Helper { get; }
    public List<string> Events { get; } = new();

    /// <param name="atHome">The helper runs from its Program Files path (installed, recorded); false: from the folder the app was unpacked to.</param>
    public Rig(bool atHome = true, bool withCopy = false)
    {
        Runner.Events = Events;
        Token.Events = Events;

        // Program Files and the user's profile folders.
        Fs.AddDir(ProgramFiles, AdminAcl.TrustedInstaller);
        Fs.AddDir(Profile, UserSid, UserAcl());
        Fs.AddDir(Path.Combine(Profile, ".local"), UserSid, UserAcl());
        Fs.AddDir(Path.Combine(Profile, ".local", "bin"), UserSid, UserAcl());
        Fs.AddFile(Layout.InstalledGatewayPath, GatewayBytes, UserSid, UserAcl());
        Fs.AddDir(Layout.DataDirectory, UserSid, UserAcl());

        if (atHome)
        {
            InstallHelperFiles();
        }

        if (withCopy)
        {
            InstallCopyFiles(GatewayBytes);
        }

        WriteManifest();

        Identity.Value = Caller();
        Helper = new AdminHelper(Identity, Fs, Consent, Runner, Token, Running, Owners, Clock);
        Fs.Changes.Clear();
    }

    public static List<AclEntry> UserAcl() => new() { new AclEntry(UserSid, true, 0x1F01FF), new AclEntry(AdminAcl.Administrators, true, 0x1F01FF) };

    public CallerIdentity Caller(
        bool elevated = true,
        bool split = true,
        string? parent = UserSid,
        string? image = null,
        IReadOnlyList<string>? environment = null) =>
        new(UserSid, "tester", Profile, elevated, split, parent, image ?? Layout.HelperExe, @"C:\TestWin", ProgramFiles, environment ?? new[] { "PATH", "TEMP", "GITHUB_TOKEN" });

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void InstallHelperFiles()
    {
        Fs.AddDir(Layout.ProductRoot);
        Fs.AddDir(Layout.HelperDirectory);
        Fs.AddFile(Layout.HelperExe, HelperBytes);
        Fs.AddFile(Layout.HelperRecordPath, System.Text.Encoding.UTF8.GetBytes(new HelperRecord(1, Sha(HelperBytes), "2026-10-01T00:00:00.0000000Z").ToJson()));
    }

    public void InstallCopyFiles(byte[] bytes, string? recordSha = null)
    {
        Fs.AddDir(Layout.ProductRoot);
        Fs.AddDir(Layout.AdminGatewayRoot);
        Fs.AddDir(Layout.UserDirectory);
        Fs.AddFile(Layout.GatewayCopyPath, bytes);
        var record = CopyRecord.Create(recordSha ?? Sha(bytes), "1.0.0", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), Sha(bytes), "Verified");
        Fs.AddFile(Layout.CopyRecordPath, System.Text.Encoding.UTF8.GetBytes(record.ToJson()));
    }

    public void WriteManifest(byte[]? gateway = null, DateTime? created = null, string? sid = null)
    {
        var manifest = new ReleaseManifest(
            1,
            sid ?? UserSid,
            "1.0.0",
            Sha(gateway ?? GatewayBytes),
            new string('a', 64),
            "Verified",
            (created ?? Clock.UtcNow).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Fs.AddDir(Layout.ManifestDirectory, UserSid, UserAcl());
        Fs.AddFile(Layout.ManifestPath, System.Text.Encoding.UTF8.GetBytes(manifest.ToJson()), UserSid, UserAcl());
    }

    /// <summary>Changes other than the result note the helper leaves for the app after a run (its own folder, its own file).</summary>
    public IEnumerable<string> MeaningfulChanges => Fs.Changes.Where(c =>
        !c.EndsWith(AdminLayout.ResultFileName, StringComparison.Ordinal) && c != "ensure:" + Layout.UserDirectory);

    public HelperResult Run(AdminVerb verb) => Helper.Run(verb);

    public string AllLines(HelperResult result) => string.Join('\n', result.Lines);
}
