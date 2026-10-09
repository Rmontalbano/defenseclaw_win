using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// A stored value a reader chose not to load because it is larger than the limit that reader works to
/// (<see cref="AuditReader.DefaultPayloadLimitBytes"/>, <see cref="MutationReader.PayloadLimit"/>,
/// <see cref="EventStreamReader.PayloadByteLimit"/>, <see cref="NetworkEgressReader.PayloadLimit"/>).
/// <para>
/// The row is still returned: its small columns are intact, the big one is empty, and this record says which column, how big it
/// is and what the limit was, so a screen can write "too large to display" and the reason instead of a truncated document (cut
/// JSON reads as a different, wrong document) or nothing at all. The size is decided in SQL with <c>octet_length</c>, which reads
/// the record header and not the value, so an oversized payload is never copied out of the database - and a page of rows is not
/// slowed down by one: measured on 100 rows of which 12 held a 2 MB value, 0.2 ms against 26 ms for
/// <c>length(CAST(x AS BLOB))</c> and 28 ms for <c>substr(x, 1, n)</c>, which both read the whole value.
/// </para>
/// <para>
/// This is what keeps "unavailable" apart from "empty": a page whose rows are all oversized is a page of rows with a reason each,
/// never a page with nothing in it (the Mac's <c>lastQuerySucceeded = !rows.isEmpty</c> for an oversized first row).
/// </para>
/// </summary>
/// <param name="Column">The column, as the database names it (<c>details</c>, <c>structured_json</c>, <c>payload_json</c>, <c>before_json</c>, ...).</param>
/// <param name="Bytes">How many bytes the stored value is.</param>
/// <param name="LimitBytes">The limit it is over.</param>
public sealed record OversizedValue(string Column, long Bytes, long LimitBytes)
{
    /// <summary>One sentence: <c>structured_json is 3.2 MB, over the 256 KB limit</c>.</summary>
    public string Reason => string.Create(
        CultureInfo.InvariantCulture,
        $"{Column} is {FormatSize(Bytes)}, over the {FormatSize(LimitBytes)} limit");

    /// <summary>
    /// What stands in a code box where the value would be - <c>(not shown: structured_json is 3.2 MB, over the 256 KB limit)</c> - broken
    /// at spaces into lines of at most <paramref name="width"/> characters. A code box scrolls sideways instead of wrapping, so one long
    /// line is cut off at the edge of the box with the scroll bar drawn over it; short lines are all seen. A size is never split from
    /// its unit (<c>270,018 bytes</c>), a word longer than <paramref name="width"/> stays whole on a line of its own, and
    /// <see cref="int.MaxValue"/> keeps everything on one line (for a field that wraps by itself).
    /// </summary>
    public IReadOnlyList<string> PlaceholderLines(int width)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        var words = new List<string>();
        foreach (var word in $"(not shown: {Reason})".Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (words.Count > 0 && char.IsAsciiDigit(words[^1][^1]) && StartsWithUnit(word))
            {
                words[^1] = $"{words[^1]} {word}";
            }
            else
            {
                words.Add(word);
            }
        }

        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && (long)line.Length + 1 + word.Length > width)
            {
                lines.Add(line.ToString());
                _ = line.Clear();
            }

            _ = (line.Length > 0 ? line.Append(' ') : line).Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary><see cref="PlaceholderLines"/> as one string, the lines separated by <c>\n</c>.</summary>
    public string Placeholder(int width) => string.Join('\n', PlaceholderLines(width));

    private static readonly string[] SizeUnits = ["bytes", "KB", "MB", "GB"];

    private static bool StartsWithUnit(string word) =>
        SizeUnits.Any(unit => word.StartsWith(unit, StringComparison.Ordinal) && (word.Length == unit.Length || !char.IsLetterOrDigit(word[unit.Length])));

    /// <summary>
    /// A size in words. Under a megabyte it is exact (<c>262,145 bytes</c>, <c>256 KB</c> when it is a whole number of kilobytes), so
    /// "262,145 bytes, over the 256 KB limit" never reads as "256 KB, over 256 KB"; above that it is rounded (<c>3.2 MB</c>).
    /// </summary>
    public static string FormatSize(long bytes)
    {
        const long Kilobyte = 1024;
        const long Megabyte = 1024 * Kilobyte;
        const long Gigabyte = 1024 * Megabyte;

        return bytes switch
        {
            < Kilobyte => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
            < Megabyte when bytes % Kilobyte == 0 => string.Create(CultureInfo.InvariantCulture, $"{bytes / Kilobyte} KB"),
            < Megabyte => string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} bytes"),
            < Gigabyte => string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / Megabyte:0.#} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{(double)bytes / Gigabyte:0.#} GB"),
        };
    }

    /// <summary>The reasons of <paramref name="values"/> in one line, separated by semicolons; empty for none.</summary>
    public static string Describe(IEnumerable<OversizedValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join("; ", values.Select(value => value.Reason));
    }
}

/// <summary>
/// The SQL half of <see cref="OversizedValue"/>: the two expressions a reader puts in its select list for a capped column, and
/// the reading of the second. Limits are embedded as integer literals (a number the reader owns, never input), the way the readers
/// already embed their other limits.
/// </summary>
internal static class PayloadCap
{
    /// <summary>"No limit": the column is selected as it is and no size is computed, so the statement is the one it was before there were limits.</summary>
    internal const long Unlimited = long.MaxValue;

    /// <summary>
    /// The column when it is <paramref name="limit"/> bytes or fewer, NULL when it is bigger (and when it is NULL). The value of an
    /// oversized column is never produced, so none of its bytes are read.
    /// </summary>
    internal static string Within(string column, long limit) =>
        limit == Unlimited
            ? column
            : string.Create(CultureInfo.InvariantCulture, $"CASE WHEN octet_length({column}) <= {limit} THEN {column} END");

    /// <summary>The size in bytes of the column when it is bigger than <paramref name="limit"/>, otherwise NULL: the second select-list entry of a capped column.</summary>
    internal static string SizeWhenOver(string column, long limit) =>
        limit == Unlimited
            ? "NULL"
            : string.Create(CultureInfo.InvariantCulture, $"CASE WHEN octet_length({column}) > {limit} THEN octet_length({column}) END");

    /// <summary>
    /// The oversized values of the current row: for each <c>(column, ordinal)</c>, the ordinal holds the size written by
    /// <see cref="SizeWhenOver"/>, non-NULL only for a value over the limit. No allocation for the usual row, which has none.
    /// </summary>
    internal static IReadOnlyList<OversizedValue> Read(SqliteDataReader reader, long limit, params ReadOnlySpan<(string Column, int Ordinal)> columns)
    {
        List<OversizedValue>? found = null;
        foreach (var (column, ordinal) in columns)
        {
            if (!reader.IsDBNull(ordinal))
            {
                (found ??= new List<OversizedValue>(columns.Length)).Add(new OversizedValue(column, reader.GetInt64(ordinal), limit));
            }
        }

        return found ?? (IReadOnlyList<OversizedValue>)Array.Empty<OversizedValue>();
    }
}
