using System.IO;
using System.Text.Json;
using DefenseClaw.Core.IO;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>What the app-update check remembers between runs.</summary>
internal sealed record SelfUpdateState
{
    /// <summary>When a check last got an answer from GitHub (200 or 304), Unix seconds; zero when never.</summary>
    public long LastCheckUnix { get; init; }

    /// <summary>The ETag of the last 200, sent back as <c>If-None-Match</c>: a 304 costs no rate limit.</summary>
    public string? ETag { get; init; }

    /// <summary>The body of the last 200, so a 304 and a throttled launch have a release to compare against.</summary>
    public string? ReleaseJson { get; init; }

    /// <summary>The version the operator chose to skip (Skip this version); a newer one is offered again.</summary>
    public string? SkippedVersion { get; init; }

    /// <summary>The newest version already announced with a toast, so a restart does not repeat it.</summary>
    public string? NotifiedVersion { get; init; }
}

/// <summary>
/// The file behind <see cref="SelfUpdateState"/>: <c>%LOCALAPPDATA%\DefenseClaw.App\self-update\state.json</c>. Small, read whole through
/// <see cref="SharedFile"/> (bounded), written by a temp file and a move so a crash leaves the old one. An unreadable file reads as empty:
/// the worst outcome is one extra check, never a refusal to start.
/// </summary>
internal sealed class SelfUpdateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private SelfUpdateState? _cached;

    public SelfUpdateStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    /// <summary>The app's own data folder, <c>%LOCALAPPDATA%\DefenseClaw.App</c>.</summary>
    public static string DefaultDataRoot() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DefenseClaw.App");

    public static string DefaultPath() => Path.Combine(DefaultDataRoot(), "self-update", "state.json");

    public SelfUpdateState Current
    {
        get
        {
            lock (_gate)
            {
                return _cached ??= Read();
            }
        }
    }

    /// <summary>Changes the state and saves it. A failed save keeps the change in memory for this run.</summary>
    public SelfUpdateState Update(Func<SelfUpdateState, SelfUpdateState> change)
    {
        lock (_gate)
        {
            _cached = change(_cached ??= Read());
            Write(_cached);
            return _cached;
        }
    }

    private SelfUpdateState Read()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<SelfUpdateState>(SharedFile.ReadAllText(_path), JsonOptions) ?? new SelfUpdateState() : new SelfUpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SelfUpdateState();
        }
    }

    private void Write(SelfUpdateState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Remembering is a convenience; the check works without it.
        }
    }
}
