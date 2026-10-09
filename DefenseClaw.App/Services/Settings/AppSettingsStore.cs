using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DefenseClaw.App.Services.Settings;

/// <summary>The file operations of <see cref="AppSettingsStore"/> that wait for a file another process holds for a moment.</summary>
internal enum StoreFileOperation
{
    /// <summary>Reading the settings file.</summary>
    Read,

    /// <summary>Moving the temp file over the settings file.</summary>
    Move,

    /// <summary>Removing the temp file after a save (or a failed one).</summary>
    RemoveTemp,
}

/// <summary>
/// The app's settings file, <c>%LOCALAPPDATA%\DefenseClaw.App\settings.json</c> — the same folder as its update cache and
/// crash log, and never anything under <c>~\.defenseclaw</c>, which belongs to the CLI. One store per file, shared by the
/// whole process: <see cref="ForPath"/> hands every caller the same instance, which is what makes its lock the only writer.
/// <para>
/// <b>Shape.</b> <c>{ "schemaVersion": 1, "appearance": { … }, "monitoring": { … }, "notifications": { … }, "startup": { … },
/// "connection": { … }, "updates": { … } }</c>; see <see cref="AppSettings"/> and <see cref="AppSettingsCodec"/>. A section
/// the file lacks is that section's defaults, so a file written by an older build (appearance only, or empty) simply reads as
/// "everything else at its default" — adding a section needs no migration and no version bump. The version is bumped only if a
/// section's meaning ever changes incompatibly.
/// </para>
/// <para>
/// <b>Use.</b> <see cref="Current"/> is the settings (cached; cheap to read on any thread). <see cref="Update"/> changes them:
/// it applies a function to the current settings, keeps the result in memory, writes it, and raises <see cref="Changed"/> when
/// something actually differs. Reads and writes are serialized; <see cref="Update"/> is atomic with respect to other updates, so
/// "add one to a counter" from two threads adds two.
/// </para>
/// <para>
/// <b>Writes.</b> Only the sections that changed are written, into the file as it is on disk right then: members this build
/// does not know (a newer build's, or a hand edit's), whole unknown sections, and a <c>schemaVersion</c> higher than this
/// build's are all kept exactly as found. The text goes to a temp file beside the target, is flushed, and is moved over it, so
/// a crash mid-write leaves the previous file whole. A write that fails (a locked file, a full disk) is traced and reported by
/// <see cref="Update"/> returning false; the change stays in memory, is remembered as unsaved, and goes out with the next write.
/// A file that is missing or damaged is replaced by one holding every section that differs from its defaults.
/// </para>
/// <para>
/// <b>A file held for a moment is waited for, not failed.</b> A virus scanner, an indexer or a backup agent opens a file it has just seen
/// change, and until it lets go the read of that file, the move over it and the removal of the temp file fail with a sharing violation
/// (or, for the move and the removal, "access denied"). Each of those three is tried up to <see cref="Attempts"/> times, with 20, 40 and 60 ms
/// between the tries: 120 ms at most for one operation, so at most 360 ms for an <see cref="Update"/> in which all three meet a hold, and
/// it is the caller's thread that waits. A read waits only for a sharing or lock violation - one that fails for good (no permission, a
/// path too long) must fail at once, since <see cref="Current"/> asks again until it works; the move and the removal wait for any I/O error or
/// access denial, as the move always did. Without this a hold of a few milliseconds turned a setting change into "could not be saved" on the
/// Settings page (CUST-323: <c>Update</c> returned false in a run of the concurrent-update test on this machine).
/// </para>
/// <para>
/// <b>Tolerance.</b> A missing, empty, truncated, non-JSON or wrongly shaped file yields the defaults; a bad field falls back
/// for that field alone. Nothing is thrown at the caller for anything the file contains or the disk does.
/// </para>
/// </summary>
internal sealed class AppSettingsStore
{
    /// <summary>The version this build writes. A file that already says more keeps its number.</summary>
    public const int SchemaVersion = 1;

    /// <summary>A settings file bigger than this is not one (they are a few hundred bytes); it reads as damaged rather than being loaded whole.</summary>
    private const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>How many times a file operation is tried when something else has the file open for a moment: the first try and three more.</summary>
    internal const int Attempts = 4;

    /// <summary>The wait before the next try is this many milliseconds times the number of the try that just failed: 20, 40, 60.</summary>
    internal const int PauseStepMilliseconds = 20;

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private static readonly ConcurrentDictionary<string, AppSettingsStore> Stores = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,

        // A path or a version string should read as typed in a file a person may open: no & for an ampersand.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _gate = new();
    private AppSettings? _current;

    /// <summary>Sections changed in memory that are not on disk yet (a write failed); written with the next one.</summary>
    private AppSettingsSections _unsaved;

    private AppSettingsStore(string path)
    {
        FilePath = path;
    }

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DefenseClaw.App",
        "settings.json");

    /// <summary>Where this store reads and writes.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Called before every try of every file operation, with which operation it is and the number of the try (1 for the first). A test
    /// throws from it to stand in for a scanner holding the file for a moment; null in the app.
    /// </summary>
    internal Action<StoreFileOperation, int>? BeforeFileOperation { get; set; }

    /// <summary>How the store waits between two tries: a real sleep in the app, a recorder in a test (which then sees the bound without waiting it out).</summary>
    internal Action<TimeSpan> Pause { get; set; } = Thread.Sleep;

    /// <summary>
    /// Raised after an <see cref="Update"/> (or a <see cref="Load"/> that found the file changed) leaves the settings different
    /// from what they were, carrying both and which sections differ. Raised on the thread that made the change, after the
    /// store's lock is released: marshal to the UI thread yourself if you touch controls. Two updates on different threads may
    /// raise in either order; <see cref="AppSettingsChangedEventArgs.Current"/> is what that update produced, and
    /// <see cref="Current"/> is always the latest. A handler that throws is traced and skipped; the others still run.
    /// </summary>
    public event EventHandler<AppSettingsChangedEventArgs>? Changed;

    /// <summary>
    /// The store for <paramref name="path"/> (the app's own file when null): the same instance for the same file, however the
    /// path is spelled, so every part of the app that reads or changes settings goes through one lock and one cache.
    /// </summary>
    public static AppSettingsStore ForPath(string? path = null)
    {
        var resolved = path ?? DefaultPath;
        try
        {
            resolved = System.IO.Path.GetFullPath(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that cannot be normalized is used as given; every read and write will fail and be tolerated.
        }

        return Stores.GetOrAdd(resolved, static full => new AppSettingsStore(full));
    }

    /// <summary>
    /// A store over <paramref name="path"/> that shares nothing — no cache, no lock — with the one <see cref="ForPath"/> hands out:
    /// what a test uses to stand in for "the app was restarted" (a new process reading the file the old one wrote). Never use it
    /// beside <see cref="ForPath"/> on the same file in the app itself: two writers is exactly what <see cref="ForPath"/> prevents.
    /// </summary>
    internal static AppSettingsStore OpenFresh(string path) => new(path);

    /// <summary>
    /// The settings now. Read from the file the first time and kept; changed by <see cref="Update"/>, re-read by
    /// <see cref="Load"/>. The defaults when the file cannot be read at all (locked) — not remembered, so the next call tries again.
    /// </summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (_current is null)
                {
                    var read = ReadDisk();
                    if (read.State == DiskState.Unreadable)
                    {
                        return AppSettings.Defaults;
                    }

                    _current = read.Settings;
                }

                return _current;
            }
        }
    }

    /// <summary>
    /// Re-reads the file and returns what a new process would see: its settings, or the defaults when it is missing, damaged or
    /// cannot be read. When the file was read, the cache follows it (an edit made by hand while the app runs is picked up) and
    /// <see cref="Changed"/> is raised if that differs from what the store held; a file that is damaged or locked leaves the
    /// cache alone, so a glitch on disk does not reset the running app's settings. Never throws.
    /// </summary>
    public AppSettings Load()
    {
        AppSettingsChangedEventArgs? args = null;
        AppSettings result;

        lock (_gate)
        {
            var read = ReadDisk();
            result = read.Settings;

            switch (read.State)
            {
                case DiskState.Ok:
                case DiskState.Missing:
                    if (_current is not null && _current != read.Settings)
                    {
                        args = new AppSettingsChangedEventArgs(_current, read.Settings);
                    }

                    _current = read.Settings;
                    _unsaved = AppSettingsSections.None;
                    break;

                case DiskState.Damaged:
                    _current ??= read.Settings;
                    break;
            }
        }

        Raise(args);
        return result;
    }

    /// <summary>
    /// Changes the settings: <paramref name="change"/> receives the current ones and returns the new ones (use <c>with</c>); the
    /// result is kept, written to the file, and announced through <see cref="Changed"/> if it differs. The function runs under
    /// the store's lock, so keep it to building a value — no I/O, and no call back into this store.
    /// <para>
    /// Returns true when the file now holds the change (or there was nothing to change), false when it could not be saved
    /// (traced; the change is still in memory and will be written with the next one) or when the file could not be read at all
    /// and so the settings could not be changed without guessing at the rest. Never throws for anything on disk.
    /// </para>
    /// </summary>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppSettingsChangedEventArgs? args = null;
        bool saved;

        lock (_gate)
        {
            if (_current is null)
            {
                var read = ReadDisk();
                if (read.State == DiskState.Unreadable)
                {
                    return false;
                }

                _current = read.Settings;
            }

            var previous = _current;
            var next = change(previous) ?? throw new InvalidOperationException("The change function returned null settings.");

            var changed = AppSettingsChangedEventArgs.Diff(previous, next);
            if (changed != AppSettingsSections.None)
            {
                _current = next;
                _unsaved |= changed;
                args = new AppSettingsChangedEventArgs(previous, next);
            }

            saved = Persist();
        }

        Raise(args);
        return saved;
    }

    // ------------------------------------------------------------------ disk

    private enum DiskState
    {
        /// <summary>The file was read and is a JSON object.</summary>
        Ok,

        /// <summary>There is no file: a fresh install.</summary>
        Missing,

        /// <summary>The file is there and is not a JSON object (empty, truncated, junk, an array, too big).</summary>
        Damaged,

        /// <summary>The file could not be read (locked, no permission); what is in it is unknown.</summary>
        Unreadable,
    }

    private readonly record struct DiskRead(DiskState State, JsonObject Root, AppSettings Settings);

    /// <summary>Reads and parses the file. Never throws. Callers hold <c>_gate</c>.</summary>
    private DiskRead ReadDisk()
    {
        string text;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return new DiskRead(DiskState.Missing, new JsonObject(), AppSettings.Defaults);
            }

            if (info.Length > MaxFileBytes)
            {
                return new DiskRead(DiskState.Damaged, new JsonObject(), AppSettings.Defaults);
            }

            // Waited for when the file is held for a moment (see Attempts); anything else that goes wrong here is "unreadable" at once.
            text = Retrying(StoreFileOperation.Read, () => File.ReadAllText(FilePath), static ex => ex is IOException io && IsSharingOrLockViolation(io));
        }
#pragma warning disable CA1031 // Unreadable settings mean the defaults, whatever the reason.
        catch (Exception ex)
        {
            Trace.TraceWarning($"App settings could not be read from {FilePath}: {ex.Message}");
            return new DiskRead(DiskState.Unreadable, new JsonObject(), AppSettings.Defaults);
        }
#pragma warning restore CA1031

        // The parse is lazy: a JsonObject builds its member table the first time it is touched, and that is when a duplicate member
        // name throws (ArgumentException) - inside Read, not inside Parse. So both are guarded, and both mean "not a settings file".
        try
        {
            return JsonNode.Parse(text, nodeOptions: null, ReadOptions) is JsonObject root
                ? new DiskRead(DiskState.Ok, root, AppSettingsCodec.Read(root))
                : new DiskRead(DiskState.Damaged, new JsonObject(), AppSettings.Defaults);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new DiskRead(DiskState.Damaged, new JsonObject(), AppSettings.Defaults);
        }
    }

    /// <summary>
    /// Writes whatever is unsaved. Re-reads the file first so members and sections this build does not own survive, and so a
    /// file that went missing or damaged since it was loaded is rebuilt with every non-default section, not just the changed one.
    /// Returns true when nothing is left unsaved. Callers hold <c>_gate</c>.
    /// </summary>
    private bool Persist()
    {
        if (_unsaved == AppSettingsSections.None || _current is null)
        {
            return true;
        }

        var read = ReadDisk();
        if (read.State == DiskState.Unreadable)
        {
            return false;
        }

        var sections = _unsaved;
        if (read.State is DiskState.Missing or DiskState.Damaged)
        {
            sections |= AppSettingsChangedEventArgs.Diff(AppSettings.Defaults, _current);
        }

        var root = read.Root;
        try
        {
            AppSettingsCodec.Write(root, _current, sections);
            StampVersion(root);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Read already walked every member this touches, so this is not expected; but nothing on disk may throw at the caller.
            Trace.TraceWarning($"App settings could not be merged into {FilePath}: {ex.Message}");
            return false;
        }

        if (!WriteAtomically(root))
        {
            return false;
        }

        _unsaved = AppSettingsSections.None;
        return true;
    }

    /// <summary>Makes <c>schemaVersion</c> this build's, or keeps a higher one as found; a file without one gets it as its first member.</summary>
    private static void StampVersion(JsonObject root)
    {
        var version = Math.Max(AppSettingsCodec.VersionOf(root), SchemaVersion);
        if (root.ContainsKey(AppSettingsCodec.VersionKey))
        {
            root[AppSettingsCodec.VersionKey] = version;
            return;
        }

        var members = root.ToList();
        root.Clear();
        root[AppSettingsCodec.VersionKey] = version;
        foreach (var (name, value) in members)
        {
            root[name] = value;
        }
    }

    /// <summary>Temp file beside the target, flushed to disk, then moved over it. False (and traced) on any failure; the previous file is never left half-written.</summary>
    private bool WriteAtomically(JsonObject root)
    {
        string? temp = null;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(root.ToJsonString(WriteOptions) + Environment.NewLine);

            temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            MoveOver(temp, FilePath);
            temp = null;
            return true;
        }
#pragma warning disable CA1031 // A setting that cannot be saved must not take the app down.
        catch (Exception ex)
        {
            Trace.TraceWarning($"App settings could not be saved to {FilePath}: {ex.Message}");
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            if (temp is not null)
            {
                try
                {
                    // The same scanner that held the target may have the temp file open, and a temp file left behind is never cleaned up.
                    _ = Retrying(StoreFileOperation.RemoveTemp, () => { File.Delete(temp); return true; }, IsTransientForReplace);
                }
#pragma warning disable CA1031 // Best-effort cleanup of a temp file.
                catch (Exception)
                {
                }
#pragma warning restore CA1031
            }
        }
    }

    /// <summary>
    /// <c>File.Move(overwrite)</c>, tried a few times: a virus scanner or an indexer that has the old file open for a moment
    /// turns the replace into a sharing violation, and losing a setting to that would be silly. Bounded (see <see cref="Attempts"/>), so a
    /// real failure still ends.
    /// </summary>
    private void MoveOver(string temp, string target)
    {
        _ = Retrying(StoreFileOperation.Move, () => { File.Move(temp, target, overwrite: true); return true; }, IsTransientForReplace);
    }

    /// <summary>
    /// Runs <paramref name="attempt"/>, and again (at most <see cref="Attempts"/> times in all, pausing 20, 40, 60 ms between the tries) while it
    /// fails in a way <paramref name="isTransient"/> calls a hold that will pass. The last failure, or any other, is the caller's.
    /// </summary>
    private T Retrying<T>(StoreFileOperation operation, Func<T> attempt, Func<Exception, bool> isTransient)
    {
        for (var number = 1; ; number++)
        {
            try
            {
                BeforeFileOperation?.Invoke(operation, number);
                return attempt();
            }
            catch (Exception ex) when (number < Attempts && isTransient(ex))
            {
                Pause(TimeSpan.FromMilliseconds(PauseStepMilliseconds * number));
            }
        }
    }

    /// <summary>
    /// A replace or a delete meets a scanner's hold as a sharing violation, as "access denied" (a file pending deletion) or as another I/O error
    /// (a section mapped by the scanner): all waited for, as the move always was. A read is pickier (<see cref="IsSharingOrLockViolation"/>), because
    /// a read that fails for good (no permission) is asked for again by every <see cref="Current"/> until it works, and must fail at once.
    /// </summary>
    private static bool IsTransientForReplace(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION: another process has the file open in a way that excludes this one, for now.</summary>
    private static bool IsSharingOrLockViolation(IOException ex) => (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private void Raise(AppSettingsChangedEventArgs? args)
    {
        if (args is null)
        {
            return;
        }

        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<AppSettingsChangedEventArgs>)handler)(this, args);
            }
#pragma warning disable CA1031 // One misbehaving subscriber must not silence the others or fail the update that raised it.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"App settings: a Changed subscriber threw: {ex}");
            }
        }
    }
}
