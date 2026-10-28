using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>A key changed both in the draft and in the file on disk, to different values. Both values are shown masked: a secret is <c>[hidden]</c>, never its text.</summary>
/// <param name="Path">The dotted key.</param>
/// <param name="Yours">The draft's value as a masked line, or <c>(removed)</c>.</param>
/// <param name="OnDisk">The file's value as a masked line, or <c>(removed)</c>.</param>
public sealed record RebaseConflict(string Path, string Yours, string OnDisk)
{
    /// <summary>The conflict as one line for the review: <c>llm.model  yours: gpt-4.1  file: o3</c>.</summary>
    public string Line => $"{Path}  yours: {Yours}  file: {OnDisk}";
}

/// <summary>
/// What re-applying a draft over a config.yaml that changed on disk would do (<see cref="ConfigDraftRebase.Plan"/>).
/// </summary>
/// <param name="Success">The draft could be combined with the file. False: <see cref="Failure"/> says why and <see cref="Text"/> is empty; nothing may be applied.</param>
/// <param name="Text">The combined text: the file as it is now with the draft's changes on it. A conflicting key keeps the file's value.</param>
/// <param name="YourKeys">The keys the draft changed against what the editor last read, in path order.</param>
/// <param name="DiskKeys">The keys that changed on disk since the editor last read it, in path order.</param>
/// <param name="Conflicts">The keys changed in both places to different values (the file's value is in <see cref="Text"/>; the draft's is kept by Undo).</param>
/// <param name="Failure">Why the draft cannot be combined, or null.</param>
public sealed record RebasePlan(
    bool Success,
    string Text,
    IReadOnlyList<string> YourKeys,
    IReadOnlyList<string> DiskKeys,
    IReadOnlyList<RebaseConflict> Conflicts,
    string? Failure)
{
    /// <summary>One sentence for the review: what moved where.</summary>
    public string Summary
    {
        get
        {
            var disk = DiskKeys.Count == 0 ? "No setting changed on disk" : $"{DiskKeys.Count:N0} setting{(DiskKeys.Count == 1 ? string.Empty : "s")} changed on disk";
            var yours = YourKeys.Count == 0 ? "no setting edited here" : $"{YourKeys.Count:N0} edited here";
            var both = Conflicts.Count == 0 ? string.Empty : $", {Conflicts.Count:N0} changed in both places";
            return $"{disk}; {yours}{both}.";
        }
    }
}

/// <summary>
/// Re-applies an unsaved draft over a config.yaml that changed on disk behind the editor (CUST-377; the Mac app's draft survives an external change the same way,
/// defenseclaw_mac v1.1.31-v1.1.34). A three-way merge of what the editor last read (<c>baseText</c>), the draft (<c>mineText</c>) and the file now
/// (<c>theirsText</c>), decided by <i>keys</i>: the keys the draft changed are put on the new file, the keys changed in both places to different values are
/// reported as conflicts and keep the file's value.
/// <para>
/// The text is merged by lines, so comments, order and layout of both sides survive, and the result is then <b>checked by keys</b>: every key the draft changed
/// has the draft's value (or the file's, for a conflict), every key only the disk changed has the disk's, and no other key moved. A merge that does not pass
/// (neighbouring lines edited on both sides in a way a line merge cannot place) is refused whole with a reason - never a half-applied draft. Pure; no I/O.
/// </para>
/// </summary>
public static class ConfigDraftRebase
{
    /// <summary>The value a removed key shows.</summary>
    public const string Removed = "(removed)";

    public static RebasePlan Plan(string baseText, string mineText, string theirsText)
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(mineText);
        ArgumentNullException.ThrowIfNull(theirsText);

        if (ConfigYamlFlatten.TryFlatten(mineText) is not { } mine)
        {
            return Failed("Your draft is not valid YAML yet (or is too large to compare). Fix it in the RAW tab first, or discard it.");
        }

        if (ConfigYamlFlatten.TryFlatten(theirsText) is not { } theirs)
        {
            return Failed("config.yaml on disk does not load as YAML (or is too large to compare), so there is nothing to combine your edits with. Fix the file, or discard your edits.");
        }

        if (ConfigYamlFlatten.TryFlatten(baseText) is not { } baseKeys)
        {
            // What the editor last read did not parse (the operator was fixing it): every key of the draft is "theirs to keep" against an empty base.
            baseKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var yourKeys = Changed(baseKeys, mine);
        var diskKeys = Changed(baseKeys, theirs);
        var conflictPaths = yourKeys.Intersect(diskKeys, StringComparer.Ordinal).Where(p => !SameValue(mine, theirs, p)).ToList();

        var merged = MergeText(baseText, mineText, theirsText);
        if (merged is null || ConfigYamlFlatten.TryFlatten(merged) is not { } result)
        {
            return Failed("Your edits and the changes on disk touch neighbouring lines and could not be combined safely. Review the difference, then redo your edits on the new file or discard them.");
        }

        // The merge is checked by keys, not trusted.
        var conflictSet = new HashSet<string>(conflictPaths, StringComparer.Ordinal);
        var all = new HashSet<string>(baseKeys.Keys, StringComparer.Ordinal);
        all.UnionWith(mine.Keys);
        all.UnionWith(theirs.Keys);
        all.UnionWith(result.Keys);
        foreach (var path in all)
        {
            IReadOnlyDictionary<string, string> expected =
                conflictSet.Contains(path) ? theirs
                : yourKeys.Contains(path) ? mine
                : diskKeys.Contains(path) ? theirs
                : baseKeys;
            if (!SameValue(result, expected, path))
            {
                return Failed("Your edits and the changes on disk could not be combined safely. Review the difference, then redo your edits on the new file or discard them.");
            }
        }

        var conflicts = conflictPaths
            .OrderBy(static p => p, StringComparer.Ordinal)
            .Select(p => new RebaseConflict(p, MaskedValue(p, mine), MaskedValue(p, theirs)))
            .ToList();
        return new RebasePlan(true, merged, yourKeys.OrderBy(static p => p, StringComparer.Ordinal).ToList(), diskKeys.OrderBy(static p => p, StringComparer.Ordinal).ToList(), conflicts, null);
    }

    private static RebasePlan Failed(string reason) => new(false, string.Empty, [], [], [], reason);

    /// <summary>The keys whose value differs between the two maps (added, removed or changed).</summary>
    private static HashSet<string> Changed(IReadOnlyDictionary<string, string> from, IReadOnlyDictionary<string, string> to)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, value) in to)
        {
            if (!from.TryGetValue(path, out var previous) || !string.Equals(previous, value, StringComparison.Ordinal))
            {
                changed.Add(path);
            }
        }

        foreach (var path in from.Keys)
        {
            if (!to.ContainsKey(path))
            {
                changed.Add(path);
            }
        }

        return changed;
    }

    private static bool SameValue(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b, string path)
    {
        var inA = a.TryGetValue(path, out var x);
        var inB = b.TryGetValue(path, out var y);
        return inA == inB && string.Equals(x, y, StringComparison.Ordinal);
    }

    /// <summary>A value as one masked line (<c>key: value</c> run through the review's masking, then cut back to the value).</summary>
    private static string MaskedValue(string path, IReadOnlyDictionary<string, string> keys)
    {
        if (!keys.TryGetValue(path, out var canonical))
        {
            return Removed;
        }

        var key = path[(path.LastIndexOf(ConfigYamlFlatten.Separator) + 1)..];
        var line = key + ": " + ConfigYamlFlatten.Display(canonical).Replace('\r', ' ').Replace('\n', ' ');
        var masked = ConfigDiffReviewBuilder.MaskLines([line])[0];
        var colon = masked.IndexOf(':', StringComparison.Ordinal);
        var shown = colon < 0 ? masked : masked[(colon + 1)..].Trim();
        return shown.Length > 160 ? shown[..159] + "…" : shown.Length == 0 ? "(empty)" : shown;
    }

    // ------------------------------------------------------------------ the text merge

    /// <summary>A line-based three-way merge. Null when the lines cannot be merged without losing one side.</summary>
    internal static string? MergeText(string baseText, string mineText, string theirsText)
    {
        var b = LineDiff.SplitLines(baseText);
        var m = LineDiff.SplitLines(mineText);
        var t = LineDiff.SplitLines(theirsText);

        var toMine = Matches(b, m);
        var toTheirs = Matches(b, t);

        var merged = new List<string>();
        int bi = 0, mi = 0, ti = 0;
        for (var s = 0; s <= b.Length; s++)
        {
            var sync = s < b.Length && toMine[s] >= 0 && toTheirs[s] >= 0;
            if (s < b.Length && !sync)
            {
                continue;
            }

            var mEnd = s == b.Length ? m.Length : toMine[s];
            var tEnd = s == b.Length ? t.Length : toTheirs[s];
            if (mEnd < mi || tEnd < ti)
            {
                return null;
            }

            merged.AddRange(MergeChunk(b[bi..s], m[mi..mEnd], t[ti..tEnd]));
            if (s < b.Length)
            {
                merged.Add(b[s]);
            }

            bi = s + 1;
            mi = mEnd + 1;
            ti = tEnd + 1;
        }

        var eol = theirsText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : mineText.Contains("\r\n", StringComparison.Ordinal) && theirsText.Length == 0 ? "\r\n" : "\n";
        var text = string.Join(eol, merged);
        var finalBreak = theirsText.Length > 0 ? theirsText.EndsWith('\n') : mineText.EndsWith('\n');
        return merged.Count > 0 && finalBreak ? text + eol : text;
    }

    /// <summary>For each base line, the index of the line it matches in <paramref name="other"/>, or -1.</summary>
    private static int[] Matches(string[] b, string[] other)
    {
        var map = new int[b.Length];
        Array.Fill(map, -1);
        foreach (var entry in LineDiff.Compute(b, other))
        {
            if (entry.Change == LineChange.Unchanged && entry.OldIndex is { } old && entry.NewIndex is { } now)
            {
                map[old] = now;
            }
        }

        return map;
    }

    private static string[] MergeChunk(string[] baseChunk, string[] mine, string[] theirs)
    {
        if (mine.SequenceEqual(baseChunk, StringComparer.Ordinal))
        {
            return theirs;
        }

        if (theirs.SequenceEqual(baseChunk, StringComparer.Ordinal) || mine.SequenceEqual(theirs, StringComparer.Ordinal))
        {
            return mine;
        }

        // Both inserted something different at the same place: keep both, the file's first.
        if (baseChunk.Length == 0)
        {
            return [.. theirs, .. mine];
        }

        // The same number of lines on every side: merge line by line (neighbouring keys edited on both sides). A line both changed keeps the file's.
        if (baseChunk.Length == mine.Length && baseChunk.Length == theirs.Length)
        {
            var lines = new string[baseChunk.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = string.Equals(mine[i], baseChunk[i], StringComparison.Ordinal) ? theirs[i]
                    : string.Equals(theirs[i], baseChunk[i], StringComparison.Ordinal) ? mine[i]
                    : theirs[i];
            }

            return lines;
        }

        // Anything else is the file's; the key check afterwards refuses the result if that dropped one of the draft's changes.
        return theirs;
    }
}
