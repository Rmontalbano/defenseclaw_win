namespace DefenseClaw.Core.Text;

/// <summary>What a diff line is: in both texts, only in the old one, or only in the new one.</summary>
public enum LineChange
{
    Unchanged,
    Removed,
    Added,
}

/// <summary>
/// One line of a line diff. Carries positions, not text, so a caller that must not show the real text (the config editor
/// masks secrets) renders whatever it likes for the line at <see cref="OldIndex"/> / <see cref="NewIndex"/>.
/// </summary>
/// <param name="Change">Whether the line is shared, removed or added.</param>
/// <param name="OldIndex">0-based line in the old text; null for an added line.</param>
/// <param name="NewIndex">0-based line in the new text; null for a removed line.</param>
public readonly record struct DiffEntry(LineChange Change, int? OldIndex, int? NewIndex);

/// <summary>A stretch of the diff to show: either changes with their context, or a run of unchanged lines that is folded away.</summary>
/// <param name="Entries">The entries to draw; empty for a folded run.</param>
/// <param name="SkippedUnchanged">How many unchanged lines this folded run stands for; 0 for a shown run.</param>
public sealed record DiffBlock(IReadOnlyList<DiffEntry> Entries, int SkippedUnchanged);

/// <summary>
/// A pure line diff (longest common subsequence) of two texts, with the unchanged stretches between changes folded away.
/// No I/O, no UI, no dependency: lines are split on LF or CRLF, so two texts that differ only in line endings diff as equal.
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// Above this many cells (old lines x new lines, after the shared head and tail are trimmed) the exact answer is
    /// not worth its memory and the whole differing middle is reported as removed-then-added.
    /// </summary>
    internal const long MaxCells = 16_000_000;

    /// <summary>Splits on LF or CRLF; a final line break does not make an extra empty line, and empty text has no lines.</summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var lines = text.Split('\n');
        var count = lines.Length;
        if (lines[count - 1].Length == 0)
        {
            count--;
        }

        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            result[i] = line.EndsWith('\r') ? line[..^1] : line;
        }

        return result;
    }

    /// <summary>The line-by-line difference between <paramref name="oldText"/> and <paramref name="newText"/>, in reading order.</summary>
    public static IReadOnlyList<DiffEntry> Compute(string? oldText, string? newText) =>
        Compute(SplitLines(oldText), SplitLines(newText));

    /// <summary>The same over already-split lines.</summary>
    public static IReadOnlyList<DiffEntry> Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);

        var entries = new List<DiffEntry>(Math.Max(oldLines.Count, newLines.Count));

        var head = 0;
        while (head < oldLines.Count && head < newLines.Count && string.Equals(oldLines[head], newLines[head], StringComparison.Ordinal))
        {
            entries.Add(new DiffEntry(LineChange.Unchanged, head, head));
            head++;
        }

        var tail = 0;
        while (oldLines.Count - tail > head && newLines.Count - tail > head &&
               string.Equals(oldLines[oldLines.Count - 1 - tail], newLines[newLines.Count - 1 - tail], StringComparison.Ordinal))
        {
            tail++;
        }

        var oldEnd = oldLines.Count - tail;
        var newEnd = newLines.Count - tail;
        var n = oldEnd - head;
        var m = newEnd - head;

        if (n == 0 || m == 0 || (long)n * m > MaxCells)
        {
            for (var i = head; i < oldEnd; i++)
            {
                entries.Add(new DiffEntry(LineChange.Removed, i, null));
            }

            for (var j = head; j < newEnd; j++)
            {
                entries.Add(new DiffEntry(LineChange.Added, null, j));
            }
        }
        else
        {
            // lcs[i, j] = length of the longest common subsequence of old[head+i..] and new[head+j..].
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            {
                for (var j = m - 1; j >= 0; j--)
                {
                    lcs[i, j] = string.Equals(oldLines[head + i], newLines[head + j], StringComparison.Ordinal)
                        ? lcs[i + 1, j + 1] + 1
                        : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }

            int a = 0, b = 0;
            while (a < n && b < m)
            {
                if (string.Equals(oldLines[head + a], newLines[head + b], StringComparison.Ordinal))
                {
                    entries.Add(new DiffEntry(LineChange.Unchanged, head + a, head + b));
                    a++;
                    b++;
                }
                else if (lcs[a + 1, b] >= lcs[a, b + 1])
                {
                    entries.Add(new DiffEntry(LineChange.Removed, head + a, null));
                    a++;
                }
                else
                {
                    entries.Add(new DiffEntry(LineChange.Added, null, head + b));
                    b++;
                }
            }

            for (; a < n; a++)
            {
                entries.Add(new DiffEntry(LineChange.Removed, head + a, null));
            }

            for (; b < m; b++)
            {
                entries.Add(new DiffEntry(LineChange.Added, null, head + b));
            }
        }

        for (var k = 0; k < tail; k++)
        {
            entries.Add(new DiffEntry(LineChange.Unchanged, oldEnd + k, newEnd + k));
        }

        return entries;
    }

    /// <summary>True when any entry is an addition or a removal.</summary>
    public static bool HasChanges(IReadOnlyList<DiffEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Any(e => e.Change != LineChange.Unchanged);
    }

    /// <summary>
    /// Groups <paramref name="entries"/> into blocks: each change with <paramref name="context"/> unchanged lines on either
    /// side is shown, and a longer unchanged stretch between (or before, or after) them becomes one folded block. A run that
    /// is only slightly longer than the context it would hide stays shown, so a fold always hides at least two lines.
    /// </summary>
    public static IReadOnlyList<DiffBlock> Collapse(IReadOnlyList<DiffEntry> entries, int context = 3)
    {
        ArgumentNullException.ThrowIfNull(entries);
        context = Math.Max(0, context);

        var blocks = new List<DiffBlock>();
        var i = 0;
        while (i < entries.Count)
        {
            if (entries[i].Change != LineChange.Unchanged)
            {
                var start = i;
                while (i < entries.Count && entries[i].Change != LineChange.Unchanged)
                {
                    i++;
                }

                blocks.Add(new DiffBlock(entries.Skip(start).Take(i - start).ToArray(), 0));
                continue;
            }

            var runStart = i;
            while (i < entries.Count && entries[i].Change == LineChange.Unchanged)
            {
                i++;
            }

            var runLength = i - runStart;
            var before = runStart > 0 ? context : 0;
            var after = i < entries.Count ? context : 0;

            if (runLength - before - after < 2)
            {
                blocks.Add(new DiffBlock(entries.Skip(runStart).Take(runLength).ToArray(), 0));
                continue;
            }

            if (before > 0)
            {
                blocks.Add(new DiffBlock(entries.Skip(runStart).Take(before).ToArray(), 0));
            }

            blocks.Add(new DiffBlock(Array.Empty<DiffEntry>(), runLength - before - after));

            if (after > 0)
            {
                blocks.Add(new DiffBlock(entries.Skip(i - after).Take(after).ToArray(), 0));
            }
        }

        return blocks;
    }
}
