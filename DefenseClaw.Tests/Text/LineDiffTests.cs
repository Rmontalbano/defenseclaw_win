using DefenseClaw.Core.Text;

namespace DefenseClaw.Tests.Text;

public class LineDiffTests
{
    private static string Render(string? oldText, string? newText)
    {
        var oldLines = LineDiff.SplitLines(oldText);
        var newLines = LineDiff.SplitLines(newText);
        return string.Join(
            "|",
            LineDiff.Compute(oldLines, newLines).Select(e => e.Change switch
            {
                LineChange.Removed => "-" + oldLines[e.OldIndex!.Value],
                LineChange.Added => "+" + newLines[e.NewIndex!.Value],
                _ => " " + newLines[e.NewIndex!.Value],
            }));
    }

    [Fact]
    public void Identical_texts_have_no_changes()
    {
        var entries = LineDiff.Compute("a\nb\nc\n", "a\nb\nc\n");
        Assert.Equal(3, entries.Count);
        Assert.False(LineDiff.HasChanges(entries));
    }

    [Fact]
    public void Line_endings_alone_are_not_a_change()
    {
        Assert.False(LineDiff.HasChanges(LineDiff.Compute("a\r\nb\r\n", "a\nb")));
    }

    [Fact]
    public void A_changed_line_is_a_removal_and_an_addition()
    {
        Assert.Equal(" a|-b|+B| c", Render("a\nb\nc", "a\nB\nc"));
    }

    [Fact]
    public void Insertions_and_deletions_keep_the_shared_lines_in_order()
    {
        Assert.Equal(" a|+x| b|-c| d", Render("a\nb\nc\nd", "a\nx\nb\nd"));
    }

    [Fact]
    public void Empty_sides_are_all_added_or_all_removed()
    {
        Assert.Equal("+a|+b", Render("", "a\nb"));
        Assert.Equal("-a|-b", Render("a\nb", null));
        Assert.Empty(LineDiff.Compute("", ""));
    }

    [Fact]
    public void Indices_point_at_the_original_lines()
    {
        var entries = LineDiff.Compute("a\nb\nc", "a\nc\nd");
        Assert.Contains(new DiffEntry(LineChange.Removed, 1, null), entries);
        Assert.Contains(new DiffEntry(LineChange.Unchanged, 2, 1), entries);
        Assert.Contains(new DiffEntry(LineChange.Added, null, 2), entries);
    }

    [Fact]
    public void A_final_line_break_does_not_make_an_extra_line()
    {
        Assert.Equal(new[] { "a", "b" }, LineDiff.SplitLines("a\nb\n"));
        Assert.Equal(new[] { "a", string.Empty, "b" }, LineDiff.SplitLines("a\r\n\r\nb"));
        Assert.Empty(LineDiff.SplitLines(null));
    }

    [Fact]
    public void Collapse_folds_long_unchanged_runs_and_keeps_context()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 30).Select(i => "line" + i));
        var newText = oldText.Replace("line15", "CHANGED", StringComparison.Ordinal);

        var blocks = LineDiff.Collapse(LineDiff.Compute(oldText, newText), context: 3);

        // fold(11), 3 context, -1 +1, 3 context, fold(12)
        Assert.Equal(5, blocks.Count);
        Assert.Equal(11, blocks[0].SkippedUnchanged);
        Assert.Equal(3, blocks[1].Entries.Count);
        Assert.Equal(2, blocks[2].Entries.Count);
        Assert.Equal(3, blocks[3].Entries.Count);
        Assert.Equal(12, blocks[4].SkippedUnchanged);
    }

    [Fact]
    public void Collapse_leaves_a_short_gap_between_two_changes_unfolded()
    {
        var blocks = LineDiff.Collapse(LineDiff.Compute("a\nb\nc\nd\ne", "A\nb\nc\nd\nE"), context: 3);
        Assert.All(blocks, b => Assert.Equal(0, b.SkippedUnchanged));
    }

    [Fact]
    public void Collapse_of_an_unchanged_text_is_one_fold()
    {
        var blocks = LineDiff.Collapse(LineDiff.Compute("a\nb\nc", "a\nb\nc"), context: 3);
        Assert.Single(blocks);
        Assert.Equal(3, blocks[0].SkippedUnchanged);
    }

    [Fact]
    public void A_very_large_differing_middle_falls_back_to_remove_then_add()
    {
        var oldLines = Enumerable.Range(0, 5000).Select(i => "o" + i).ToArray();
        var newLines = Enumerable.Range(0, 5000).Select(i => "n" + i).ToArray();

        var entries = LineDiff.Compute(oldLines, newLines);

        Assert.Equal(10000, entries.Count);
        Assert.Equal(5000, entries.Count(e => e.Change == LineChange.Removed));
        Assert.Equal(5000, entries.Count(e => e.Change == LineChange.Added));
    }
}
