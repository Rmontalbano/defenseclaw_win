using System.Globalization;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Inventory;

/// <summary>Why an <see cref="InventoryScan"/> is the scan a caller was handed.</summary>
public enum InventoryScanBasis
{
    /// <summary>
    /// The newest scan recorded by a full inventory pass (<c>source</c> <c>scheduled</c> or
    /// <c>startup</c>) that finished cleanly (<c>result = 'ok'</c>).
    /// </summary>
    NewestFullScan,

    /// <summary>
    /// No such scan exists (a fresh install, or a build that names its sources differently), so
    /// this is simply the newest scan of any source and result.
    /// </summary>
    NewestAnyScan,
}

/// <summary>
/// One row of <c>ai_scans</c>: a single pass of the sidecar's AI discovery, which writes the
/// whole set of signals it currently believes in under its <see cref="ScanId"/>.
/// </summary>
/// <param name="ScanId">The <c>ai_scans.scan_id</c>; <c>ai_signals.scan_id</c> points at it.</param>
/// <param name="ScannedAtRaw">The stored text, e.g. <c>2026-09-29 03:21:50.7798903 +0000 UTC</c>.</param>
/// <param name="ScannedAt">
/// <paramref name="ScannedAtRaw"/> as a UTC instant, or <c>null</c> if it was not a shape
/// <see cref="InventoryTimestamps.TryParse"/> understands.
/// </param>
/// <param name="Source">
/// <c>scheduled</c>, <c>startup</c> (both full file walks) or <c>process</c> (the lightweight
/// per-minute pass) on the 0.8.x line.
/// </param>
/// <param name="Result"><c>ok</c> or <c>partial</c> on the 0.8.x line.</param>
/// <param name="Basis">Why this scan was picked; see <see cref="InventoryScanBasis"/>.</param>
public sealed record InventoryScan(
    string ScanId,
    string ScannedAtRaw,
    DateTimeOffset? ScannedAt,
    string Source,
    string Result,
    InventoryScanBasis Basis)
{
    public bool IsFullScan => Basis == InventoryScanBasis.NewestFullScan;
}

/// <summary>
/// Rows read for one chosen scan. <see cref="Scan"/> is <c>null</c> — with no rows — when the
/// database has the right layout but has not recorded a scan yet.
/// </summary>
public sealed record InventoryScanRows(InventoryScan? Scan, InventoryRows Rows);

/// <summary>
/// Reads the timestamps <c>inventory.db</c> stores. The sidecar writes Go's default
/// <c>time.Time</c> string form (<c>2026-07-28 21:33:39.2587908 +0000 UTC</c>: up to nine
/// fractional digits, a numeric offset, then a zone <em>name</em> that .NET's parsers reject), and
/// <see cref="TryParse"/> also accepts the RFC 3339 shape the rest of DefenseClaw uses.
/// </summary>
public static class InventoryTimestamps
{
    // date, time, optional fraction, optional Z or ±hh[:]mm, optional trailing zone name / monotonic suffix.
    private static readonly Regex Pattern = new(
        @"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})[ T](?<h>\d{2}):(?<mi>\d{2}):(?<s>\d{2})(?:\.(?<f>\d{1,9}))?\s*(?:Z|(?<sign>[+-])(?<oh>\d{2}):?(?<om>\d{2}))?(?:\s+[A-Za-z].*)?$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses <paramref name="raw"/> to a UTC instant. A value with no offset is taken as UTC
    /// (that is what the sidecar writes). Fractions beyond 100 ns are truncated.
    /// </summary>
    public static bool TryParse(string? raw, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out result);
        }

        try
        {
            var offset = TimeSpan.Zero;
            if (match.Groups["sign"].Success)
            {
                var minutes =
                    (Number(match, "oh") * 60) + Number(match, "om");
                offset = TimeSpan.FromMinutes(match.Groups["sign"].Value == "-" ? -minutes : minutes);
            }

            var local = new DateTime(
                Number(match, "y"), Number(match, "mo"), Number(match, "d"),
                Number(match, "h"), Number(match, "mi"), Number(match, "s"),
                DateTimeKind.Unspecified);

            var ticks = 0L;
            if (match.Groups["f"].Success)
            {
                // 100 ns ticks are the finest .NET resolution: keep the first seven digits.
                var digits = match.Groups["f"].Value.PadRight(7, '0').Substring(0, 7);
                ticks = long.Parse(digits, CultureInfo.InvariantCulture);
            }

            result = new DateTimeOffset(local.AddTicks(ticks), offset).ToUniversalTime();
            return true;
        }
        catch (ArgumentException)
        {
            // Month 13, an offset beyond ±14 h, and similar: shaped like a timestamp, not one.
            result = default;
            return false;
        }
    }

    private static int Number(Match match, string group) =>
        int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
}
