using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.ViewModels;

/// <summary>What a diff line did: the view tints <see cref="Added"/> and <see cref="Removed"/> in the tone tints, <see cref="Context"/> is plain.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>One line of a unified diff.</summary>
/// <param name="Kind">Added, removed or context.</param>
/// <param name="Text">The line, with its <c>+</c> / <c>-</c> marker.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text)
{
    /// <summary>The row template's trigger value (a string, so the XAML needs no enum import).</summary>
    public string KindKey => Kind.ToString();
}

/// <summary>Which diff the inspector shows for a change.</summary>
public enum MutationDiffMode
{
    /// <summary>The row recorded nothing beyond who / what / when.</summary>
    None,

    /// <summary>Two columns: the value before (red) and after (green), the Mac's <c>DiffView</c>.</summary>
    BeforeAfter,

    /// <summary>Only <c>diff_json</c> was recorded: a unified diff.</summary>
    Unified,

    /// <summary>An audit row's own structured record, no diff.</summary>
    Recorded,
}

/// <summary>Turns what a change recorded into what the inspector draws. Pure text work, no WPF.</summary>
public static class MutationDiffBuilder
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Which of the diffs <paramref name="item"/> can show: before / after first, then a recorded diff, then the audit row's own record.</summary>
    public static MutationDiffMode ModeOf(MutationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.HasBeforeAfter ? MutationDiffMode.BeforeAfter
            : item.HasDiff ? MutationDiffMode.Unified
            : item.StructuredJson.Length > 0 ? MutationDiffMode.Recorded
            : MutationDiffMode.None;
    }

    /// <summary>JSON re-indented so a change reads line by line; anything that is not JSON comes back as it was.</summary>
    public static string Pretty(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, Indented);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>
    /// The unified diff of a recorded <c>diff_json</c>: an array of <c>{path, op, before, after}</c> entries (the gateway's shape)
    /// becomes a <c>@@ path (op)</c> header with a removed and an added line; text that already is a unified diff keeps its
    /// <c>+</c> / <c>-</c> lines; any other JSON is shown indented, untinted.
    /// </summary>
    public static IReadOnlyList<DiffLine> Unified(string diffJson)
    {
        if (string.IsNullOrWhiteSpace(diffJson))
        {
            return Array.Empty<DiffLine>();
        }

        if (TryEntries(diffJson, out var entries))
        {
            return entries;
        }

        var lines = new List<DiffLine>();
        var text = IsJson(diffJson) ? Pretty(diffJson) : diffJson;
        var looksUnified = !IsJson(diffJson);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var kind = looksUnified && trimmed.StartsWith('+') && !trimmed.StartsWith("+++", StringComparison.Ordinal) ? DiffLineKind.Added
                : looksUnified && trimmed.StartsWith('-') && !trimmed.StartsWith("---", StringComparison.Ordinal) ? DiffLineKind.Removed
                : DiffLineKind.Context;
            lines.Add(new DiffLine(kind, trimmed));
        }

        return lines;
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind is JsonValueKind.Array or JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryEntries(string diffJson, out IReadOnlyList<DiffLine> lines)
    {
        lines = Array.Empty<DiffLine>();
        try
        {
            using var document = JsonDocument.Parse(diffJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return false;
            }

            var result = new List<DiffLine>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !HasAny(entry, "path", "before", "after"))
                {
                    return false;
                }

                var path = Member(entry, "path");
                var op = Member(entry, "op");
                result.Add(new DiffLine(DiffLineKind.Context, $"@@ {(path.Length > 0 ? path : "(root)")}{(op.Length > 0 ? $" ({op})" : string.Empty)}"));
                if (entry.TryGetProperty("before", out var before) && before.ValueKind != JsonValueKind.Null)
                {
                    result.Add(new DiffLine(DiffLineKind.Removed, "- " + Inline(before)));
                }

                if (entry.TryGetProperty("after", out var after) && after.ValueKind != JsonValueKind.Null)
                {
                    result.Add(new DiffLine(DiffLineKind.Added, "+ " + Inline(after)));
                }
            }

            lines = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasAny(JsonElement element, params string[] names) => names.Any(name => element.TryGetProperty(name, out _));

    private static string Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string Inline(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}

/// <summary>One row of the Mutations table, and everything its inspector shows.</summary>
public sealed class MutationRow
{
    public MutationRow(MutationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;

        var local = item.Timestamp.ToLocalTime();
        TimestampText = item.Timestamp == DateTimeOffset.MinValue
            ? item.RawTimestamp
            : local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : local.ToString("MMM d HH:mm", CultureInfo.CurrentCulture);
        FullTimestampText = item.Timestamp == DateTimeOffset.MinValue
            ? item.RawTimestamp
            : local.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        Actor = item.Actor.Length > 0 ? item.Actor : "—";
        TargetText = item.TargetType.Length > 0 && item.Target.Length > 0 ? $"{item.TargetType}/{item.Target}"
            : item.Target.Length > 0 ? item.Target
            : item.TargetType.Length > 0 ? item.TargetType
            : "—";
        VersionText = item.VersionFrom.Length == 0 && item.VersionTo.Length == 0 ? "—" : $"{item.VersionFrom} → {item.VersionTo}";
        ConnectorText = item.Connector ?? "—";
        SourceText = item.Source == MutationSource.ActivityEvent ? "Activity" : item.Bucket;
        ReasonText = item.Reason.Length > 0 ? item.Reason.ReplaceLineEndings(" ") : "—";

        Mode = MutationDiffBuilder.ModeOf(item);
        BeforeText = item.BeforeJson.Length > 0 ? MutationDiffBuilder.Pretty(item.BeforeJson) : "(empty)";
        AfterText = item.AfterJson.Length > 0 ? MutationDiffBuilder.Pretty(item.AfterJson) : "(empty)";
        UnifiedLines = Mode == MutationDiffMode.Unified ? MutationDiffBuilder.Unified(item.DiffJson) : Array.Empty<DiffLine>();
        RecordedText = Mode == MutationDiffMode.Recorded ? MutationDiffBuilder.Pretty(item.StructuredJson) : string.Empty;
    }

    public MutationItem Item { get; }

    public string Id => Item.Id;

    public DateTimeOffset Timestamp => Item.Timestamp;

    public string TimestampText { get; }

    public string FullTimestampText { get; }

    public string Actor { get; }

    public string Action => Item.Action;

    public string TargetText { get; }

    public string VersionText { get; }

    public string ReasonText { get; }

    public string ConnectorText { get; }

    public string SourceText { get; }

    public MutationDiffMode Mode { get; }

    public bool ShowBeforeAfter => Mode == MutationDiffMode.BeforeAfter;

    public bool ShowUnified => Mode == MutationDiffMode.Unified;

    public bool ShowRecorded => Mode == MutationDiffMode.Recorded;

    public bool ShowNothing => Mode == MutationDiffMode.None;

    /// <summary>The value before the change, indented; "(empty)" when only an after value was recorded.</summary>
    public string BeforeText { get; }

    public string AfterText { get; }

    public IReadOnlyList<DiffLine> UnifiedLines { get; }

    /// <summary>An audit row's structured record, indented.</summary>
    public string RecordedText { get; }

    /// <summary>The inspector's key / value rows; empty values are left out.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Fields
    {
        get
        {
            var fields = new List<KeyValuePair<string, string>>();
            Add("Actor", Item.Actor);
            Add("Target", TargetText == "—" ? string.Empty : TargetText);
            Add("When", FullTimestampText);
            Add("Reason", Item.Reason);
            Add("Version", VersionText == "—" ? string.Empty : VersionText);
            Add("Connector", Item.Connector ?? string.Empty);
            Add("Source", SourceText);
            return fields;

            void Add(string name, string value)
            {
                if (value.Length > 0)
                {
                    fields.Add(new KeyValuePair<string, string>(name, value));
                }
            }
        }
    }

    /// <summary>The row, as plain text for the clipboard.</summary>
    public string CopyText
    {
        get
        {
            var text = new StringBuilder();
            _ = text.Append(FullTimestampText).Append(' ').Append(Action).Append(' ').Append(TargetText).Append(" by ").Append(Actor);
            if (Item.Reason.Length > 0)
            {
                _ = text.Append(": ").Append(ReasonText);
            }

            return text.ToString();
        }
    }

    /// <summary>True when <paramref name="search"/> (already trimmed; empty matches all) is in the actor, action, target, reason or source.</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Item.Actor.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Item.Action.Contains(search, StringComparison.OrdinalIgnoreCase)
        || TargetText.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Item.Reason.Contains(search, StringComparison.OrdinalIgnoreCase)
        || SourceText.Contains(search, StringComparison.OrdinalIgnoreCase);
}
