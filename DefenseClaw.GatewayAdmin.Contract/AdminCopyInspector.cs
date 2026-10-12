namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>What the inspector found for the administrator copy of the gateway, or for the helper.</summary>
/// <param name="Exists">The file is there (every folder above it is too).</param>
/// <param name="Secure">Every folder from Program Files down and the file itself are admin-only and none is a reparse point.</param>
/// <param name="Problems">Every reason it is not secure or not usable, in words; empty when there is none.</param>
/// <param name="RecordSha256">The hash the helper recorded (admin-only record); null when there is no valid record.</param>
/// <param name="Sha256">The hash of the file as it is now; null when it was not read (not there, or not secure).</param>
public sealed record CopyInspection(bool Exists, bool Secure, IReadOnlyList<string> Problems, string? RecordSha256, string? Sha256, CopyRecord? Record = null)
{
    /// <summary>The file's bytes are the recorded bytes.</summary>
    public bool MatchesRecord => Sha256 is not null && RecordSha256 is not null &&
        string.Equals(Sha256, RecordSha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>It may be run elevated (by the helper) or have the gateway token sent to it (by the app): secure, recorded, and unchanged since.</summary>
    public bool Trusted => Exists && Secure && MatchesRecord;

    public static CopyInspection NotInstalled { get; } = new(false, false, Array.Empty<string>(), null, null);
}

/// <summary>
/// Looks at the administrator copy (or the helper) through <see cref="IAdminFileSystem"/> and says whether it is the file the helper installed:
/// the folders above it and the file are admin-only and not reparse points, and its SHA-256 equals the one in the admin-only record next to it.
/// Both the helper (before it runs the copy) and the app (its status, and the gateway peer check) call this, so they cannot disagree. The
/// hash is cached by file identity (path, length, last write) because the app asks once per authenticated request; the access lists are
/// read every time, because they are cheap and are what an attacker would change.
/// </summary>
public sealed class AdminCopyInspector
{
    private readonly IAdminFileSystem _fs;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Length, DateTime LastWrite, string Sha256)> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many files have been hashed (the cache's misses); a test reads it.</summary>
    public int HashesComputed { get; private set; }

    public AdminCopyInspector(IAdminFileSystem fs)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    }

    /// <summary>The administrator copy of the gateway.</summary>
    public CopyInspection InspectGatewayCopy(AdminLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return Inspect(
            layout.CopyChain,
            layout.GatewayCopyPath,
            layout.CopyRecordPath,
            static json => CopyRecord.TryParse(json) is { } r ? (r.Sha256, r) : (null, null));
    }

    /// <summary>The helper in Program Files.</summary>
    public CopyInspection InspectHelper(AdminLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return Inspect(
            layout.HelperChain,
            layout.HelperExe,
            layout.HelperRecordPath,
            static json => HelperRecord.TryParse(json) is { } r ? (r.Sha256, null) : (null, null));
    }

    /// <summary>The largest gateway the copy step accepts (the Mac helper's limit).</summary>
    public const long MaxGatewayBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The SHA-256 of the user's installed gateway, or why it cannot be had: it is missing, a link or junction is on the way to it (the folders
    /// are the user's, so they may be anything), it is empty or larger than <see cref="MaxGatewayBytes"/>, or it is not an executable.
    /// </summary>
    public (string? Sha256, string? Problem) HashInstalledGateway(AdminLayout layout)
    {
        var (node, problem) = CheckInstalledGatewayPath(layout);
        if (node is null)
        {
            return (null, problem);
        }

        var problems = new List<string>();
        var sha = Hash(layout.InstalledGatewayPath, node, problems);
        return (sha, problems.FirstOrDefault());
    }

    /// <summary>
    /// Looks at the installed gateway's path without reading the file: every folder on the way is a real folder and the file is a plain file of a
    /// plausible size. Returns the file's node, or why it may not be used.
    /// </summary>
    public (FsNode? Node, string? Problem) CheckInstalledGatewayPath(AdminLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var missing = "The installed gateway is not where the installer puts it (" + layout.InstalledGatewayPath + ").";
        foreach (var folder in layout.InstalledGatewayChain)
        {
            var folderNode = _fs.Stat(folder);
            if (!folderNode.Exists || !folderNode.IsDirectory)
            {
                return (null, missing);
            }

            if (folderNode.IsReparsePoint)
            {
                return (null, $"{folder} is a link or junction; the installed gateway is not read through one.");
            }
        }

        var node = _fs.Stat(layout.InstalledGatewayPath);
        if (!node.Exists || node.IsDirectory)
        {
            return (null, missing);
        }

        if (node.IsReparsePoint)
        {
            return (null, "The installed gateway is a link; it is not read through one.");
        }

        return node.Length is < 1 or > MaxGatewayBytes
            ? (null, "The installed gateway has an implausible size.")
            : (node, null);
    }

    /// <summary>The status report: the helper, the copy, and whether the copy is the installed gateway.</summary>
    public AdminStatusReport Report(AdminLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var helper = InspectHelper(layout);
        var copy = InspectGatewayCopy(layout);
        var (installed, installedProblem) = HashInstalledGateway(layout);

        bool? matches = copy.Sha256 is not null && installed is not null
            ? string.Equals(copy.Sha256, installed, StringComparison.OrdinalIgnoreCase)
            : null;

        var problems = new List<string>();
        problems.AddRange(helper.Problems);
        problems.AddRange(copy.Problems);
        if (installedProblem is not null)
        {
            problems.Add(installedProblem);
        }

        return new AdminStatusReport(
            helper.Exists,
            helper.Trusted,
            copy.Exists,
            copy.Trusted,
            copy.Sha256,
            installed,
            matches,
            copy.Record?.Version,
            problems);
    }

    private CopyInspection Inspect(
        IReadOnlyList<string> chain,
        string file,
        string recordPath,
        Func<string?, (string? Sha256, CopyRecord? Record)> parseRecord)
    {
        var problems = new List<string>();

        foreach (var folder in chain)
        {
            var node = _fs.Stat(folder);
            if (!node.Exists)
            {
                // Nothing installed yet: not a fault.
                return problems.Count == 0 ? CopyInspection.NotInstalled : new CopyInspection(false, false, problems, null, null);
            }

            Check(node, folder, problems, mustBeDirectory: true);
        }

        var fileNode = _fs.Stat(file);
        if (!fileNode.Exists)
        {
            return problems.Count == 0 ? CopyInspection.NotInstalled : new CopyInspection(false, false, problems, null, null);
        }

        Check(fileNode, file, problems, mustBeDirectory: false);

        // The record sits in the same folder as the helper's or the copy's; it is checked like the file, because whoever can write it decides what is "recorded".
        var recordNode = _fs.Stat(recordPath);
        string? recordSha = null;
        CopyRecord? record = null;
        if (!recordNode.Exists)
        {
            problems.Add("There is no record of what the helper installed.");
        }
        else
        {
            Check(recordNode, recordPath, problems, mustBeDirectory: false);
            (recordSha, record) = parseRecord(_fs.ReadText(recordPath, 8 * 1024));
            if (recordSha is null)
            {
                problems.Add("The record of what the helper installed cannot be read.");
            }
        }

        var secure = problems.Count == 0;
        string? sha = null;

        // Hashed even when the only problem is a loose access list or a missing record, so a status can say what is there; never through a
        // reparse point, and never a folder.
        if (!fileNode.IsDirectory && !problems.Any(static p => p.Contains("reparse point", StringComparison.OrdinalIgnoreCase)))
        {
            sha = Hash(file, fileNode, problems);
            secure = problems.Count == 0;
        }

        return new CopyInspection(true, secure, problems, recordSha, sha, record);
    }

    private string? Hash(string file, FsNode node, List<string> problems)
    {
        lock (_gate)
        {
            if (_hashes.TryGetValue(file, out var cached) && cached.Length == node.Length && cached.LastWrite == node.LastWriteUtc)
            {
                return cached.Sha256;
            }
        }

        try
        {
            using var stream = _fs.OpenReadExclusive(file);
            var sha = Hex.HashOf(stream);
            lock (_gate)
            {
                _hashes[file] = (node.Length, node.LastWriteUtc, sha);
                HashesComputed++;
            }

            return sha;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The file could not be read: {ex.Message}");
            return null;
        }
    }

    private static void Check(FsNode node, string label, List<string> problems, bool mustBeDirectory)
    {
        if (node.IsReparsePoint)
        {
            problems.Add($"{label} is a reparse point (a link or junction), which is never followed.");
            return;
        }

        if (node.IsDirectory != mustBeDirectory)
        {
            problems.Add(mustBeDirectory ? $"{label} is not a folder." : $"{label} is a folder, not a file.");
            return;
        }

        if (node.Security is null)
        {
            problems.Add($"The access list of {label} could not be read.");
            return;
        }

        problems.AddRange(AdminAcl.Problems(node.Security, label));
    }
}
