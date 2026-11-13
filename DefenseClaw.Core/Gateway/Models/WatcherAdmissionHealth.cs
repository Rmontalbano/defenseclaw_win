using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Gateway.Models;

/// <summary>
/// The watcher's report that admission scanning is off for some connector roots (DefenseClaw 1.0.9, <c>publishWatcherHealth</c> in internal/gateway/sidecar.go):
/// <c>/health</c> <c>watcher.details</c> gains <c>watch_roots_unavailable</c> (how many), <c>admission_scanning_off</c> (the connectors) and, when a root has an
/// ancestor folder other accounts can write, <c>admission_root_writable_folders</c>. The roots themselves are in the gateway log, not in <c>/health</c>.
/// Absent on older runtimes, so there is nothing for this to read there.
/// </summary>
/// <param name="RootsUnavailable">How many roots are not scanned before admission.</param>
/// <param name="Connectors">The connectors, as the gateway names them, sorted; at most <see cref="WatcherAdmissionHealth.MaxNames"/>.</param>
/// <param name="ConnectorsOmitted">How many more names the gateway sent than are kept.</param>
/// <param name="WritableFolders">Folders other accounts can write, as reported; at most <see cref="WatcherAdmissionHealth.MaxFolders"/>, each cut to <see cref="WatcherAdmissionHealth.MaxFolderLength"/>.</param>
/// <param name="FoldersOmitted">How many more folders the gateway sent than are kept.</param>
/// <param name="Reason">The watcher's own <c>last_error</c>, cleaned and cut; empty when it gave none.</param>
public sealed record WatcherAdmissionHealth(
    int RootsUnavailable,
    IReadOnlyList<string> Connectors,
    int ConnectorsOmitted,
    IReadOnlyList<string> WritableFolders,
    int FoldersOmitted,
    string Reason)
{
    /// <summary>The most connector names kept.</summary>
    public const int MaxNames = 12;

    /// <summary>The most writable folders kept.</summary>
    public const int MaxFolders = 6;

    /// <summary>The longest folder path shown.</summary>
    public const int MaxFolderLength = 260;

    /// <summary>The longest reason kept.</summary>
    public const int MaxReasonLength = 300;

    private static readonly Regex NamePattern = new(
        "^[a-z0-9][a-z0-9_.-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// The report, or null when the watcher says nothing of the kind: no block, no <c>watch_roots_unavailable</c>, or a count that is not a whole number above
    /// zero. Lenient: a wrong-typed list is read as empty, never an error.
    /// </summary>
    public static WatcherAdmissionHealth? From(ServiceState? watcher)
    {
        try
        {
            if (watcher?.Details is not { ValueKind: JsonValueKind.Object } details ||
                !details.TryGetProperty("watch_roots_unavailable", out var count) ||
                count.ValueKind != JsonValueKind.Number ||
                !count.TryGetInt32(out var roots) ||
                roots <= 0)
            {
                return null;
            }

            var names = new SortedSet<string>(StringComparer.Ordinal);
            var namesSeen = 0;
            foreach (var raw in Strings(details, "admission_scanning_off"))
            {
                namesSeen++;
                var name = raw.Trim().ToLowerInvariant();
                if (NamePattern.IsMatch(name))
                {
                    _ = names.Add(name);
                }
            }

            var folders = new List<string>();
            var foldersSeen = 0;
            foreach (var raw in Strings(details, "admission_root_writable_folders"))
            {
                var path = DisplayNames.Visible(raw.Trim());
                if (path.Length == 0)
                {
                    continue;
                }

                foldersSeen++;
                if (folders.Count < MaxFolders)
                {
                    folders.Add(Cut(path, MaxFolderLength));
                }
            }

            var kept = names.Take(MaxNames).ToList();
            return new WatcherAdmissionHealth(
                roots,
                kept,
                Math.Max(namesSeen - kept.Count, 0),
                folders,
                Math.Max(foldersSeen - folders.Count, 0),
                Cut(DisplayNames.Visible(watcher.LastError?.Trim()), MaxReasonLength));
        }
        catch (Exception ex) when (ex is InvalidOperationException or RegexMatchTimeoutException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"claudecode and codex", or "configured connectors" when the gateway named none (the CLI's own label).</summary>
    public string ConnectorLabel
    {
        get
        {
            if (Connectors.Count == 0)
            {
                return "configured connectors";
            }

            var list = Connectors.Count == 1 ? Connectors[0] : string.Join(", ", Connectors.Take(Connectors.Count - 1)) + " and " + Connectors[^1];
            return ConnectorsOmitted > 0 ? list + $" and {ConnectorsOmitted} more" : list;
        }
    }

    private static IEnumerable<string> Strings(JsonElement details, string key)
    {
        if (!details.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        // Bounded: the gateway sends a short list; a hostile or broken answer is not walked to the end.
        var walked = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (++walked > 256)
            {
                yield break;
            }

            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }

    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text[..max];
        return (char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut) + "…";
    }
}
