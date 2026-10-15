using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DefenseClaw.Core.IO;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>A downloaded, verified and unpacked update waiting in its staging folder.</summary>
internal sealed record StagedUpdate(AppVersion Version, string Directory, string NewApp, string NewHelper, string Sha256, long ZipBytes, IReadOnlyList<string> Notes);

/// <summary>What <see cref="SelfUpdateInstaller.StageAsync"/> produced: the staged update, or the reason nothing was installed.</summary>
internal sealed record StageResult(StagedUpdate? Staged, string? Refusal)
{
    public static StageResult Refuse(string reason) => new(null, reason);
}

/// <summary>What the marker file between the swap and the first healthy start says.</summary>
internal sealed record PendingUpdate
{
    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;

    public DateTimeOffset At { get; init; }

    /// <summary>The app folder the swap happened in, so the cleanup looks where the swap was.</summary>
    public string AppDirectory { get; init; } = string.Empty;
}

/// <summary>What <see cref="SelfUpdateInstaller.CompletePendingUpdate"/> found at start.</summary>
internal enum PostUpdateOutcome
{
    /// <summary>Nothing pending, nothing left over.</summary>
    None,

    /// <summary>Stale <c>.old</c> files without a marker were removed.</summary>
    CleanedLeftovers,

    /// <summary>The marker named this version: the update took. <c>.old</c> files and the marker are gone.</summary>
    Updated,

    /// <summary>A marker for another version: that update did not take (this is not the version it was installing). The marker is gone.</summary>
    NotApplied,
}

internal sealed record PostUpdateResult(PostUpdateOutcome Outcome, string? Version, IReadOnlyList<string> StillLocked);

/// <summary>
/// The disk side of updating this app: download, verify, unpack, swap the running files for the new ones, undo the swap, and tidy after.
/// <para>
/// <b>Verification.</b> The zip must hash to the value in checksums.txt <i>and</i> to GitHub's asset digest (when GitHub gave one); it must hold
/// exactly the app and the admin helper at its root; both unpacked files must report the release's version. Anything else is a refusal with
/// its reason and nothing on disk outside the staging folder has changed. There is no code signature to check (the exe is not
/// Authenticode-signed): the trust is HTTPS to github.com, the release's own checksums and GitHub's digest, and the sentence in
/// <see cref="TrustSentence"/> says so.
/// </para>
/// <para>
/// <b>Swap.</b> Windows lets a running exe be renamed. The app becomes <c>DefenseClaw.App.exe.old</c> and the helper
/// <c>DefenseClaw.GatewayAdmin.exe.old</c>; the new files move in; a marker (<c>pending-update.json</c>) records from/to. The next start
/// (<see cref="CompletePendingUpdate"/>) deletes the <c>.old</c> files and the marker when it is the new version; a failed start is undone by
/// <see cref="SwapTransaction.Rollback"/>, which the restart's hand-off calls when the successor never reports it started.
/// </para>
/// </summary>
internal sealed class SelfUpdateInstaller
{
    public const string OldSuffix = ".old";

    public const string MarkerName = "pending-update.json";

    /// <summary>The wording the review shows, and the docs repeat: no signature, so what the check rests on is spelled out.</summary>
    public const string TrustSentence =
        "This update is not Authenticode-signed. It is verified by its SHA-256 from the release's checksums.txt and by the digest GitHub publishes for the file, " +
        "downloaded over HTTPS from github.com/Rmontalbano/defenseclaw_win.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Func<HttpClient> _http;
    private readonly string _appExePath;
    private readonly string _dataRoot;
    private readonly Func<string, string?> _productVersion;
    private readonly Func<string?> _blockedReason;
    private readonly SelfUpdateLimits _limits;

    /// <param name="http">The client for downloads (made on first use). A test passes a fake handler; nothing here may reach github.com in a test.</param>
    /// <param name="appExePath">The running exe (captured at start: after the swap the process path may read as the renamed file).</param>
    /// <param name="dataRoot"><c>%LOCALAPPDATA%\DefenseClaw.App</c>: the staging folder and the marker live under it.</param>
    /// <param name="productVersion">The file's ProductVersion; <c>FileVersionInfo</c> when null.</param>
    /// <param name="blockedReason">Why an update may not go ahead now (a state-changing run is in flight), or null: <c>AppRestart.BlockedReason</c>.</param>
    public SelfUpdateInstaller(Func<HttpClient> http, string appExePath, string dataRoot, Func<string, string?>? productVersion = null, Func<string?>? blockedReason = null, SelfUpdateLimits? limits = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _appExePath = appExePath ?? throw new ArgumentNullException(nameof(appExePath));
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        _productVersion = productVersion ?? (static path => FileVersionInfo.GetVersionInfo(path).ProductVersion);
        _blockedReason = blockedReason ?? (static () => null);
        _limits = limits ?? SelfUpdateLimits.Default;
    }

    public string AppDirectory => Path.GetDirectoryName(_appExePath) ?? string.Empty;

    public string MarkerPath => Path.Combine(_dataRoot, MarkerName);

    public string StagingRoot => Path.Combine(_dataRoot, "self-update");

    /// <summary>Why an update cannot start now, or null: a state-changing run in flight, an exe with an unexpected name, a folder this user cannot write.</summary>
    public string? PreconditionRefusal()
    {
        if (_blockedReason() is { } busy)
        {
            return busy;
        }

        if (!string.Equals(Path.GetFileName(_appExePath), SelfUpdatePackage.AppExeName, StringComparison.OrdinalIgnoreCase))
        {
            return $"This copy runs as {Path.GetFileName(_appExePath)}, not {SelfUpdatePackage.AppExeName}, so an update cannot replace it.";
        }

        return CanWrite(AppDirectory)
            ? null
            : $"The folder this app runs from ({AppDirectory}) is not writable by this user, so it cannot be updated in place. Install the new version over it with the installer instead.";
    }

    /// <summary>True when the app runs from a build output folder (<c>publish</c>, <c>bin</c>): updating works, but replaces a developer's build.</summary>
    public bool IsDevelopmentFolder => AppDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(static part => part.Equals("publish", StringComparison.OrdinalIgnoreCase) || part.Equals("bin", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The expected SHA-256 of the release's zip from checksums.txt, fetched on its own (a few hundred bytes) so the review can show it before
    /// anything large is downloaded. Null hash with the reason on failure.
    /// </summary>
    public async Task<(string? Sha256, string? Problem)> FetchExpectedHashAsync(SelfUpdateRelease release, CancellationToken cancellationToken)
    {
        if (release.NotInstallableReason is { } missing)
        {
            return (null, missing);
        }

        var directory = Path.Combine(StagingRoot, release.Version.ToString());
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SelfUpdateRelease.ChecksumsName);
        var download = await DownloadAsync(release.Checksums!.Url, path, _limits.ChecksumsBytes, release.Checksums.Size, null, cancellationToken).ConfigureAwait(false);
        if (download.Problem is not null)
        {
            return (null, download.Problem);
        }

        var hash = SelfUpdatePackage.FindChecksum(SharedFile.ReadAllText(path), release.Zip!.Name, out var problem);
        if (hash is null)
        {
            return (null, problem);
        }

        if (release.Zip.Digest is { } digest)
        {
            var api = SelfUpdatePackage.ParseDigest(digest);
            if (api is null)
            {
                return (null, "GitHub's digest for the zip is not a SHA-256, so it cannot be compared.");
            }

            if (api != hash)
            {
                return (null, $"checksums.txt ({Short(hash)}) and GitHub's digest ({Short(api)}) disagree about the zip. Nothing was installed.");
            }
        }

        return (hash, null);
    }

    /// <summary>Downloads the zip, verifies it and unpacks it into a staging folder. Changes nothing outside <see cref="StagingRoot"/>.</summary>
    public async Task<StageResult> StageAsync(SelfUpdateRelease release, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (PreconditionRefusal() is { } blocked)
        {
            return StageResult.Refuse(blocked);
        }

        if (release.NotInstallableReason is { } missing)
        {
            return StageResult.Refuse(missing);
        }

        var zipAsset = release.Zip!;
        var notes = new List<string>();
        if (IsDevelopmentFolder)
        {
            notes.Add("This app runs from a build folder (publish or bin); the update replaces that build.");
        }

        var directory = Path.Combine(StagingRoot, release.Version.ToString());
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            _ = Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return StageResult.Refuse($"The staging folder {directory} could not be prepared: {ex.Message}");
        }

        progress?.Report("Reading the release's checksums…");
        var (expected, hashProblem) = await FetchExpectedHashAsync(release, cancellationToken).ConfigureAwait(false);
        if (expected is null)
        {
            return StageResult.Refuse(hashProblem ?? "The release's checksums could not be read.");
        }

        if (release.Zip!.Digest is null)
        {
            notes.Add("GitHub published no digest for the zip; it was checked against checksums.txt only.");
        }

        var zipPath = Path.Combine(directory, zipAsset.Name);
        progress?.Report($"Downloading {zipAsset.Name}…");
        var download = await DownloadAsync(zipAsset.Url, zipPath, _limits.ZipBytes, zipAsset.Size, progress, cancellationToken).ConfigureAwait(false);
        if (download.Problem is not null)
        {
            return StageResult.Refuse(download.Problem);
        }

        progress?.Report("Checking the download…");
        if (download.Sha256 != expected)
        {
            return StageResult.Refuse($"The download's SHA-256 ({Short(download.Sha256!)}) does not match checksums.txt ({Short(expected)}). It was discarded.");
        }

        if (SelfUpdatePackage.ValidateEntries(zipPath, _limits) is { } badEntries)
        {
            return StageResult.Refuse(badEntries);
        }

        var unpacked = Path.Combine(directory, "files");
        _ = Directory.CreateDirectory(unpacked);
        if (SelfUpdatePackage.Extract(zipPath, unpacked, _limits) is { } badExtract)
        {
            return StageResult.Refuse(badExtract);
        }

        var newApp = Path.Combine(unpacked, SelfUpdatePackage.AppExeName);
        var newHelper = Path.Combine(unpacked, SelfUpdatePackage.HelperExeName);
        foreach (var file in new[] { newApp, newHelper })
        {
            if (SelfUpdatePackage.CheckProductVersion(Path.GetFileName(file), _productVersion(file), release.Version) is { } wrongVersion)
            {
                return StageResult.Refuse(wrongVersion);
            }
        }

        return new StageResult(new StagedUpdate(release.Version, directory, newApp, newHelper, expected, download.Length, notes), null);
    }

    /// <summary>
    /// Replaces the running app (and the helper beside it) with the staged files and writes the marker. On any failure the swap is undone and
    /// the reason returned; on success the returned transaction can still <see cref="SwapTransaction.Rollback"/>.
    /// </summary>
    public (SwapTransaction? Swap, string? Refusal) Swap(StagedUpdate staged, AppVersion from)
    {
        if (PreconditionRefusal() is { } blocked)
        {
            return (null, blocked);
        }

        var transaction = new SwapTransaction(_appExePath, Path.Combine(AppDirectory, SelfUpdatePackage.HelperExeName), MarkerPath);
        var problem = transaction.Apply(staged, new PendingUpdate { From = from.ToString(), To = staged.Version.ToString(), At = DateTimeOffset.UtcNow, AppDirectory = AppDirectory });
        return problem is null ? (transaction, null) : (null, problem);
    }

    /// <summary>
    /// Run at every start. A marker for <paramref name="running"/> means the update took: the <c>.old</c> files, the marker and the staging
    /// folder go. A marker for another version means that update was not applied: the marker goes. With no marker, <c>.old</c> files are
    /// leftovers and go. A file still locked (the previous process is slow to exit) is reported in <see cref="PostUpdateResult.StillLocked"/>
    /// for the caller to retry.
    /// </summary>
    public PostUpdateResult CompletePendingUpdate(AppVersion running)
    {
        var locked = new List<string>();
        var pending = ReadMarker();
        var appDirectory = pending is { AppDirectory.Length: > 0 } ? pending.AppDirectory : AppDirectory;

        var removedOld = false;
        foreach (var name in new[] { SelfUpdatePackage.AppExeName, SelfUpdatePackage.HelperExeName })
        {
            var old = Path.Combine(appDirectory, name + OldSuffix);
            if (File.Exists(old))
            {
                removedOld = true;
                if (!TryDelete(old))
                {
                    locked.Add(old);
                }
            }
        }

        if (pending is null)
        {
            return new PostUpdateResult(removedOld && locked.Count == 0 ? PostUpdateOutcome.CleanedLeftovers : PostUpdateOutcome.None, null, locked);
        }

        var took = AppVersion.TryParse(pending.To, out var to) && to.CompareTo(running) == 0;
        if (locked.Count == 0)
        {
            TryDelete(MarkerPath);
            if (took)
            {
                TryDeleteDirectory(Path.Combine(StagingRoot, to.ToString()));
            }
        }

        return new PostUpdateResult(took ? PostUpdateOutcome.Updated : PostUpdateOutcome.NotApplied, pending.To, locked);
    }

    public PendingUpdate? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath) ? JsonSerializer.Deserialize<PendingUpdate>(SharedFile.ReadAllText(MarkerPath), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string Short(string hash) => hash.Length > 12 ? hash[..12] + "…" : hash;

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".dcwin-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    private async Task<(string? Sha256, long Length, string? Problem)> DownloadAsync(string url, string destination, long maxBytes, long declaredSize, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(destination);
        if (declaredSize > maxBytes)
        {
            return (null, 0, $"{name} is {ReadLimits.Describe(declaredSize)} according to GitHub, over the {ReadLimits.Describe(maxBytes)} limit for an update.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, 0, $"Downloading {name} failed: HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is { } advertised && advertised > maxBytes)
            {
                return (null, 0, $"{name} is {ReadLimits.Describe(advertised)}, over the {ReadLimits.Describe(maxBytes)} limit for an update.");
            }

            using var hash = SHA256.Create();
            long total = 0;
            var lastReport = Stopwatch.StartNew();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        return (null, total, $"{name} is larger than the {ReadLimits.Describe(maxBytes)} limit for an update; the download was stopped.");
                    }

                    hash.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (progress is not null && lastReport.ElapsedMilliseconds > 250)
                    {
                        lastReport.Restart();
                        progress.Report($"Downloading {name}: {total / (1024 * 1024)} MiB…");
                    }
                }
            }

            _ = hash.TransformFinalBlock([], 0, 0);
            if (declaredSize > 0 && total != declaredSize)
            {
                return (null, total, $"{name} arrived as {total} bytes but GitHub lists {declaredSize}. It was discarded.");
            }

            return (Convert.ToHexStringLower(hash.Hash!), total, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return (null, 0, $"Downloading {name} failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, 0, $"Downloading {name} timed out.");
        }
    }
}

/// <summary>
/// One swap of the app's files and the way back. <see cref="Apply"/> renames the current files to <c>.old</c>, moves the new ones in and writes the
/// marker, undoing itself on any failure; <see cref="Rollback"/> puts the old files back (a new file that cannot be deleted because the failed
/// successor holds it is moved aside instead) and removes the marker.
/// </summary>
internal sealed class SwapTransaction
{
    private readonly string _app;
    private readonly string _helper;
    private readonly string _marker;
    private bool _appMoved;
    private bool _helperMoved;
    private bool _hadHelper;
    private bool _newAppIn;
    private bool _newHelperIn;
    private bool _markerWritten;

    public SwapTransaction(string app, string helper, string marker)
    {
        _app = app;
        _helper = helper;
        _marker = marker;
    }

    /// <summary>Null on success, otherwise why not (the swap is then already undone).</summary>
    public string? Apply(StagedUpdate staged, PendingUpdate marker)
    {
        try
        {
            // An .old from an earlier update that is still held (a previous process that has not exited) cannot be overwritten: say so.
            foreach (var old in new[] { _app + SelfUpdateInstaller.OldSuffix, _helper + SelfUpdateInstaller.OldSuffix })
            {
                if (File.Exists(old) && !SelfUpdateInstaller.TryDelete(old))
                {
                    return $"{Path.GetFileName(old)} from an earlier update is still in use. Close the other copy of the app and try again.";
                }
            }

            File.Move(_app, _app + SelfUpdateInstaller.OldSuffix);
            _appMoved = true;
            _hadHelper = File.Exists(_helper);
            if (_hadHelper)
            {
                File.Move(_helper, _helper + SelfUpdateInstaller.OldSuffix);
                _helperMoved = true;
            }

            File.Move(staged.NewApp, _app);
            _newAppIn = true;
            File.Move(staged.NewHelper, _helper);
            _newHelperIn = true;

            _ = Directory.CreateDirectory(Path.GetDirectoryName(_marker)!);
            File.WriteAllText(_marker, JsonSerializer.Serialize(marker, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            _markerWritten = true;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var undo = Rollback();
            return $"Replacing the program files failed ({ex.Message}). " + (undo.Count == 0 ? "The previous version was left as it was." : "Some files could not be put back: " + string.Join("; ", undo));
        }
    }

    /// <summary>Puts the previous files back. Returns what could not be restored (empty when all of it was).</summary>
    public IReadOnlyList<string> Rollback()
    {
        var problems = new List<string>();
        if (_newAppIn)
        {
            RemoveOrMoveAside(_app, problems);
            _newAppIn = false;
        }

        if (_newHelperIn)
        {
            RemoveOrMoveAside(_helper, problems);
            _newHelperIn = false;
        }

        Restore(_app, ref _appMoved, problems);
        Restore(_helper, ref _helperMoved, problems);

        if (_markerWritten && SelfUpdateInstaller.TryDelete(_marker))
        {
            _markerWritten = false;
        }

        return problems;
    }

    private static void Restore(string path, ref bool moved, List<string> problems)
    {
        if (!moved)
        {
            return;
        }

        try
        {
            File.Move(path + SelfUpdateInstaller.OldSuffix, path, overwrite: true);
            moved = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static void RemoveOrMoveAside(string path, List<string> problems)
    {
        if (!File.Exists(path) || SelfUpdateInstaller.TryDelete(path))
        {
            return;
        }

        // The failed successor may still hold it open; a running file can be renamed even when it cannot be deleted.
        try
        {
            File.Move(path, path + $".failed-{Guid.NewGuid():N}", overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }
    }
}
