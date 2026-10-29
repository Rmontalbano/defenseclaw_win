using System.Globalization;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What a source's cached <c>index.json</c> says about the source itself (CUST-276): who publishes it, when it was fetched, and how its entries were
/// judged by the last sync. The TUI's <c>SourceIndex</c> header (<c>tui/services/registry_cache.py</c>) and the lines of its source detail
/// (<c>source_detail_info</c>: Fetched At, Publisher, Entries, Clean, Warnings, Blocked, Errors).
/// </summary>
/// <param name="Publisher">The manifest's <c>publisher</c>, one line; null when the index has none.</param>
/// <param name="FetchedAt">The raw <c>fetched_at</c> timestamp; null when the index has none.</param>
/// <param name="Entries">How many entries the source lists.</param>
/// <param name="Clean">Entries whose last scan found nothing.</param>
/// <param name="Warning">Entries whose last scan found something of a middle severity.</param>
/// <param name="Blocked">Entries whose last scan found something of a blocking severity.</param>
/// <param name="Error">Entries that could not be fetched or scanned.</param>
public sealed record RegistryIndexInfo(string? Publisher, string? FetchedAt, int Entries, int Clean, int Warning, int Blocked, int Error);

/// <summary>
/// One source's cache as read from disk: its entries, the facts of its header, and the sentence the operator should be told (nothing cached yet, the
/// index could not be read, it is too large, it was cut at the row limit). <see cref="IsProblem"/> separates "worth a warning" from "simply not synced yet".
/// </summary>
internal sealed record RegistryIndexRead(IReadOnlyList<RegistryEntryRow> Rows, string? Message, RegistryIndexInfo? Info, bool IsProblem);

/// <summary>
/// The facts of the selected source as its details card shows them: publisher, fetched at, and the entries / clean / warning / blocked / error counts,
/// each already worded (and toned) for the card. <see cref="None"/> is a source with no readable index - not synced yet, or not read yet - which shows
/// dashes and no counts rather than zeros (zero clean entries is a statement; no sync is not one).
/// </summary>
public sealed class RegistrySourceFacts
{
    public static RegistrySourceFacts None { get; } = new(null);

    public RegistrySourceFacts(RegistryIndexInfo? info)
    {
        Info = info;
    }

    public RegistryIndexInfo? Info { get; }

    /// <summary>True when the counts are known (an index was read).</summary>
    public bool HasCounts => Info is not null;

    /// <summary>The publisher, with any control or format character spelled out (it is a manifest field); a dash when the index names none.</summary>
    public string Publisher => Info?.Publisher is { Length: > 0 } publisher ? DisplayNames.Visible(publisher) : "—";

    /// <summary>When the manifest was fetched, in local time like Last sync; a dash when the index has no time.</summary>
    public string FetchedAt => RegistrySourceRow.FormatTimestamp(Info?.FetchedAt) is { } at ? DisplayNames.Visible(at) : "—";

    public string EntriesText => Info is null ? string.Empty : Info.Entries == 1 ? "1 entry" : Count(Info.Entries) + " entries";

    public string CleanText => Info is null ? string.Empty : Count(Info.Clean) + " clean";

    public string WarningText => Info is null ? string.Empty : Count(Info.Warning) + " warning";

    public string BlockedText => Info is null ? string.Empty : Count(Info.Blocked) + " blocked";

    public string ErrorText => Info is null ? string.Empty : Count(Info.Error) + " error";

    /// <summary>The tone key of each count's chip: a count above zero is toned by what it means, a zero stays quiet.</summary>
    public string CleanTone => Info is { Clean: > 0 } ? "Ok" : "Neutral";

    public string WarningTone => Info is { Warning: > 0 } ? "Warn" : "Neutral";

    public string BlockedTone => Info is { Blocked: > 0 } ? "Bad" : "Neutral";

    public string ErrorTone => Info is { Error: > 0 } ? "Bad" : "Neutral";

    /// <summary>
    /// All the counts in one line, for a screen reader and the tooltip: <c>5 entries: 1 clean, 1 warning, 1 blocked, 1 error</c>. Without an index it
    /// says there are none to show; why (never synced, unreadable, too large) is the sentence under "Cached entries".
    /// </summary>
    public string CountsText => Info is null
        ? "No counts available"
        : $"{EntriesText}: {CleanText}, {WarningText}, {BlockedText}, {ErrorText}";

    public override string ToString() => $"Publisher {Publisher}, fetched {FetchedAt}, {CountsText}";

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
