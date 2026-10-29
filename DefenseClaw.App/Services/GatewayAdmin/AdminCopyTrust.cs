using System.Globalization;
using System.IO;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.App.Services;

/// <summary>
/// The peer check's second trusted place: the administrator copy of the gateway that "Run gateway as administrator" keeps in
/// <c>%ProgramFiles%\DefenseClaw Companion\AdminGateway\&lt;SID&gt;</c>. Fails closed: anything unreadable is not trusted.
/// <para>
/// <b>Why Program Files makes the copy worth the token.</b> A program of the same account can write anywhere the user can, so a file in the
/// profile says nothing about who put it there. Under Program Files only administrators can write, so a file there that the helper recorded
/// was put there through a UAC prompt the user approved. That is <i>checked, not assumed</i>: <see cref="AdminCopyInspector.InspectGatewayCopy"/>
/// reads the owner and access list of Program Files, the product folder, <c>AdminGateway</c>, the SID folder, the copy and <c>copy.json</c> on
/// every call (<see cref="AdminAcl.Problems"/>: owner an administrator, no other principal with a write-like right), refuses a link anywhere on
/// the way, and requires the copy's SHA-256 to equal the one in <c>copy.json</c>. The SID folder is the signed-in account's own, from its token
/// (<see cref="WindowsAdminLayoutSource"/>), so another account's copy is never this account's gateway.
/// </para>
/// <para>
/// <b>Two ways to recognise it.</b> By its file (<see cref="IsTrustedImage"/>): the process's image path is exactly this account's copy and the
/// copy passes those checks. By the helper's records (<see cref="IsStartedCopy"/>, CUST-362), for an elevated gateway whose image path Windows
/// would not give this unelevated app: the copy passes the same checks; the helper's own <c>last-result.json</c>, in that admin-only folder and
/// checked the same way, says its last run was a successful start or restart; the gateway's <c>gateway.pid</c> names the very pid on the port,
/// this account's copy and this account's data folder; and that file was written during that start. gateway.pid is in the user's folder, so
/// on its own it is only a claim: it counts because no other account can write there, and the admin-only record pins it to one start. A
/// process whose path is unreadable and that the records do not name is refused, whatever it is called.
/// </para>
/// </summary>
internal sealed class AdminCopyTrust : IAdminCopyTrust
{
    /// <summary>How long before the helper's recorded finish the gateway may have written gateway.pid: the helper's 180 s start budget, and slack.</summary>
    internal static readonly TimeSpan PidRecordLead = TimeSpan.FromMinutes(4);

    /// <summary>How long after it: the helper records its result once the start has returned, so this is only slack for file-time rounding.</summary>
    internal static readonly TimeSpan PidRecordLag = TimeSpan.FromSeconds(10);

    private const int MaxResultBytes = 256 * 1024;

    private readonly IAdminLayoutSource _layout;
    private readonly IAdminFileSystem _fs;
    private readonly AdminCopyInspector _inspector;

    public AdminCopyTrust(IAdminLayoutSource layout, IAdminFileSystem fs)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _inspector = new AdminCopyInspector(fs);
    }

    public bool IsTrustedImage(string imagePath)
    {
        if (_layout.Get() is not { } layout || !layout.IsGatewayCopy(imagePath))
        {
            return false;
        }

        try
        {
            return _inspector.InspectGatewayCopy(layout).Trusted;
        }
#pragma warning disable CA1031 // A check that throws is a check that failed: not trusted.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public bool IsStartedCopy(PortOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Only for a process whose path is unknown: a path Windows did give is the kernel's answer, and the records never overrule it.
        if (!string.IsNullOrWhiteSpace(owner.ImagePath) ||
            !string.Equals(owner.ProcessName, GatewayPeerVerifier.GatewayProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return RecordProblem(owner) is null;
        }
#pragma warning disable CA1031 // A check that throws is a check that failed: not trusted.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    public string? ExplainRefusal(PortOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        try
        {
            return Explain(owner);
        }
#pragma warning disable CA1031 // Words for a banner: a look that fails leaves the verifier's own sentence.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private string? Explain(PortOwner owner)
    {
        if (string.IsNullOrWhiteSpace(owner.ImagePath))
        {
            var problem = RecordProblem(owner);
            return problem is null
                ? null
                : "could not be verified: Windows does not show this app which file it runs from (it may be running as administrator or as " +
                  "another account), and it is not the administrator gateway this app started: " + problem;
        }

        if (_layout.Get() is not { } layout)
        {
            return null;
        }

        if (layout.IsGatewayCopy(owner.ImagePath))
        {
            var copy = _inspector.InspectGatewayCopy(layout);
            return copy.Trusted
                ? null
                : "runs from this account's administrator copy, but that copy did not pass its checks: " + FirstProblem(copy);
        }

        return FolderUnder(layout.AdminGatewayRoot, owner.ImagePath) switch
        {
            null => null,
            { Length: > 0 } sid when AdminLayout.IsValidSid(sid) && !string.Equals(sid, layout.UserSid, StringComparison.OrdinalIgnoreCase) =>
                "runs from another account's administrator copy, not this account's, so it is not sent this account's token",
            _ => "runs from this app's administrator folder, but not from this account's administrator copy",
        };
    }

    /// <summary>Null when the helper's records name <paramref name="owner"/> as the copy it started; otherwise the first reason they do not.</summary>
    private string? RecordProblem(PortOwner owner)
    {
        if (_layout.Get() is not { } layout)
        {
            return "this account has no administrator setup to match it against";
        }

        // 1. The copy, judged exactly as for a readable path (its folders and itself admin-only, no link, equal to copy.json).
        var copy = _inspector.InspectGatewayCopy(layout);
        if (!copy.Exists)
        {
            return "there is no administrator copy of the gateway";
        }

        if (!copy.Trusted)
        {
            return "the administrator copy did not pass its checks: " + FirstProblem(copy);
        }

        // 2. The helper's own note of its last run. It is in the copy's admin-only folder, which step 1 checked; the file is checked like copy.json,
        //    because whoever can write it decides what the helper "did".
        var resultNode = _fs.Stat(layout.ResultPath);
        if (!resultNode.Exists)
        {
            return "the administrator helper has no record of starting a gateway";
        }

        if (AdminOnlyProblem(resultNode, layout.ResultPath) is { } unsafeResult)
        {
            return "the administrator helper's record is not admin-only: " + unsafeResult;
        }

        var result = AdminRunResult.TryParse(_fs.ReadText(layout.ResultPath, MaxResultBytes));
        if (result is null ||
            !DateTimeOffset.TryParse(result.FinishedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var finishedAt))
        {
            return "the administrator helper's record cannot be read";
        }

        var started = result.Verb == AdminVerbs.Name(AdminVerb.Start) || result.Verb == AdminVerbs.Name(AdminVerb.Restart);
        if (!started || result.ExitCode != AdminExit.Ok)
        {
            return $"the administrator helper's last run was '{result.Verb}' with exit code {result.ExitCode}, not a successful start";
        }

        // 3. The gateway's own record of the process it started, in the data folder the helper pointed it at (DEFENSECLAW_HOME). Read without
        //    following a link; a bare number (no executable) is not enough.
        var pidPath = Path.Combine(layout.DataDirectory, GatewayPidRecord.FileName);
        var pidNode = _fs.Stat(pidPath);
        if (!pidNode.Exists)
        {
            return "there is no gateway.pid naming it";
        }

        if (pidNode.IsReparsePoint || pidNode.IsDirectory)
        {
            return "gateway.pid is a link or a folder, which is not read";
        }

        if (GatewayPidRecord.TryParse(_fs.ReadText(pidPath, GatewayPidRecord.MaxBytes)) is not { } record)
        {
            return "gateway.pid does not name a process and the file it runs";
        }

        // The listening-port owner (from the kernel's TCP table) must be the very process the record names.
        if (record.Pid != owner.Pid)
        {
            return $"gateway.pid names pid {record.Pid}, a different process";
        }

        if (!layout.IsGatewayCopy(record.Executable))
        {
            return $"gateway.pid names {record.Executable}, not this account's administrator copy";
        }

        if (record.DataDirectory is { } dataDirectory && !AdminLayout.SamePath(dataDirectory, layout.DataDirectory))
        {
            return $"gateway.pid is for the data folder {dataDirectory}, not {layout.DataDirectory}";
        }

        // 4. The record was written during that start (the gateway writes it once, as it starts): an old gateway.pid whose pid Windows has since
        //    given to another process does not match a later start.
        var finished = finishedAt.UtcDateTime;
        var written = pidNode.LastWriteUtc.Kind == DateTimeKind.Local ? pidNode.LastWriteUtc.ToUniversalTime() : pidNode.LastWriteUtc;
        if (written < finished - PidRecordLead || written > finished + PidRecordLag)
        {
            return "gateway.pid was not written during the administrator helper's last start";
        }

        return null;
    }

    private static string? AdminOnlyProblem(FsNode node, string path)
    {
        if (node.IsReparsePoint)
        {
            return $"{path} is a link";
        }

        if (node.IsDirectory)
        {
            return $"{path} is a folder";
        }

        if (node.Security is null)
        {
            return $"the access list of {path} could not be read";
        }

        return AdminAcl.Problems(node.Security, path).FirstOrDefault()?.TrimEnd('.');
    }

    private static string FirstProblem(CopyInspection copy) =>
        copy.Problems.FirstOrDefault()?.TrimEnd('.') ?? "it is not the file the helper recorded";

    /// <summary>The name of the folder directly below <paramref name="root"/> that holds <paramref name="path"/>; null when it is not below it.</summary>
    private static string? FolderUnder(string root, string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!AdminLayout.IsInside(root, full) || AdminLayout.SamePath(root, full))
            {
                return null;
            }

            var rest = full[(Path.TrimEndingDirectorySeparator(root).Length + 1)..];
            var separator = rest.IndexOf(Path.DirectorySeparatorChar);
            return separator < 0 ? string.Empty : rest[..separator];
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
