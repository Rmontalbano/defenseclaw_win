using System.Collections.Specialized;
using System.Globalization;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// The incremental line model behind an Activity entry's output list, against real <see cref="CliInvocation"/>
/// instances (built by <see cref="InvocationFactory"/>) so the retention, trimming and cursor rules under test are the
/// runner's own. No WPF: the model is plain collections.
/// </summary>
public class ActivityTranscriptTests
{
    private static List<NotifyCollectionChangedAction> Watch(ActivityTranscript transcript)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)transcript.Lines).CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    private static string[] Texts(ActivityTranscript transcript) => transcript.Lines.Select(l => l.Text).ToArray();

    private static string[] Retained(CliInvocation invocation) => invocation.OutputLines.Select(l => l.Text).ToArray();

    // ------------------------------------------------------------------ append via the cursor

    [Fact]
    public void A_pull_appends_only_what_arrived_since_the_last_one()
    {
        var invocation = InvocationFactory.Create();
        var transcript = new ActivityTranscript();
        var events = Watch(transcript);

        InvocationFactory.AppendNumbered(invocation, 3);
        Assert.True(transcript.Pull(invocation));
        Assert.Equal(new[] { "line 1", "line 2", "line 3" }, Texts(transcript));
        Assert.Equal(3, transcript.LineCount);

        InvocationFactory.AppendNumbered(invocation, 2, first: 4);
        Assert.True(transcript.Pull(invocation));
        Assert.Equal(new[] { "line 1", "line 2", "line 3", "line 4", "line 5" }, Texts(transcript));

        // Five lines went in through five Add events: nothing was rebuilt, so a bound list keeps its scroll position and selection.
        Assert.Equal(Enumerable.Repeat(NotifyCollectionChangedAction.Add, 5), events);
    }

    [Fact]
    public void A_pull_with_nothing_new_changes_nothing()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.AppendNumbered(invocation, 4);
        var transcript = new ActivityTranscript();
        _ = transcript.Pull(invocation);
        var events = Watch(transcript);

        Assert.False(transcript.Pull(invocation));
        Assert.False(transcript.Pull(invocation));

        Assert.Empty(events);
        Assert.Equal(4, transcript.Lines.Count);
    }

    [Fact]
    public void An_invocation_that_has_printed_nothing_yields_an_empty_model()
    {
        var transcript = new ActivityTranscript();

        Assert.False(transcript.Pull(InvocationFactory.Create()));

        Assert.Empty(transcript.Lines);
        Assert.Equal(0, transcript.LineCount);
        Assert.Equal("Output", transcript.Describe());
    }

    [Fact]
    public void Lines_carry_their_stream_and_their_position_in_the_append_sequence()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.Append(invocation, "ok line");
        InvocationFactory.Append(invocation, "something failed", CliStream.StandardError);
        InvocationFactory.Append(invocation, string.Empty);

        var transcript = new ActivityTranscript();
        _ = transcript.Pull(invocation);

        var lines = transcript.Lines;
        Assert.Equal(new long[] { 0, 1, 2 }, lines.Select(l => l.Sequence));
        Assert.Equal(new[] { CliStream.StandardOutput, CliStream.StandardError, CliStream.StandardOutput }, lines.Select(l => l.Stream));
        Assert.False(lines[0].IsError);
        Assert.True(lines[1].IsError);
        Assert.Equal("error: something failed", lines[1].AutomationName);
        Assert.Equal("ok line", lines[0].AutomationName);

        // A blank line is still a row with a height: an empty TextBlock would measure as nothing.
        Assert.Equal(" ", lines[2].DisplayText);
        Assert.Equal(string.Empty, lines[2].Text);
    }

    [Fact]
    public void A_transcript_that_was_already_long_when_first_read_arrives_whole()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.AppendNumbered(invocation, 1_500);
        var transcript = new ActivityTranscript();

        _ = transcript.Pull(invocation);

        Assert.Equal(Retained(invocation), Texts(transcript));
    }

    // ------------------------------------------------------------------ trimming

    [Fact]
    public void While_a_run_streams_past_the_cap_the_model_stays_exactly_what_the_invocation_retains()
    {
        var invocation = InvocationFactory.Create();
        var transcript = new ActivityTranscript();

        // Pulled the way the panel does it: a tick at a time while the process is still writing.
        for (var batch = 0; batch < 30; batch++)
        {
            InvocationFactory.AppendNumbered(invocation, 100, first: (batch * 100) + 1);
            _ = transcript.Pull(invocation);

            Assert.True(
                transcript.Lines.Count <= invocation.RetainedLineLimit + 1,
                $"{transcript.Lines.Count} rows after {(batch + 1) * 100} lines, cap is {invocation.RetainedLineLimit} (+1 marker)");
            Assert.Equal(Retained(invocation), Texts(transcript));
        }

        Assert.True(invocation.IsOutputTruncated);
        Assert.Equal(CliStream.Notice, transcript.Lines[0].Stream);
        Assert.Equal(ActivityOutputLine.NoSequence, transcript.Lines[0].Sequence);
        Assert.Equal("line 3000", transcript.Lines[^1].Text);
        Assert.Equal(invocation.DroppedOutputLineCount, transcript.DroppedLineCount);
        Assert.Equal(transcript.Lines.Count - 1, transcript.LineCount);
    }

    [Fact]
    public void After_a_trim_the_kept_lines_are_still_one_unbroken_run()
    {
        var invocation = InvocationFactory.Create();
        var transcript = new ActivityTranscript();

        for (var batch = 0; batch < 25; batch++)
        {
            InvocationFactory.AppendNumbered(invocation, 100, first: (batch * 100) + 1);
            _ = transcript.Pull(invocation);
        }

        var sequences = transcript.Lines.Skip(1).Select(l => l.Sequence).ToArray();
        Assert.Equal(Enumerable.Range(0, sequences.Length).Select(i => sequences[0] + i), sequences);
        Assert.Equal(invocation.DroppedOutputLineCount, sequences[0]);
        Assert.Equal(invocation.OutputCursor - 1, sequences[^1]);
    }

    [Fact]
    public void The_truncation_marker_is_replaced_in_place_as_the_count_grows_and_never_duplicated()
    {
        var invocation = InvocationFactory.Create();
        var transcript = new ActivityTranscript();

        InvocationFactory.AppendNumbered(invocation, 2_100);
        _ = transcript.Pull(invocation);
        var firstMarker = transcript.Lines[0].Text;
        Assert.Equal(CliStream.Notice, transcript.Lines[0].Stream);
        Assert.Contains($"{invocation.DroppedOutputLineCount} earlier line(s) dropped", firstMarker, StringComparison.Ordinal);

        // Enough further output for a second trim.
        var events = Watch(transcript);
        InvocationFactory.AppendNumbered(invocation, 700, first: 2_101);
        _ = transcript.Pull(invocation);

        Assert.Single(transcript.Lines, l => l.IsNotice);
        Assert.NotEqual(firstMarker, transcript.Lines[0].Text);
        Assert.Contains($"{invocation.DroppedOutputLineCount} earlier line(s) dropped", transcript.Lines[0].Text, StringComparison.Ordinal);
        Assert.Contains(NotifyCollectionChangedAction.Replace, events);
        Assert.Equal(Retained(invocation), Texts(transcript));
    }

    [Fact]
    public void A_reader_that_fell_behind_a_trim_starts_over_from_the_oldest_retained_line()
    {
        var invocation = InvocationFactory.Create();
        var transcript = new ActivityTranscript();
        InvocationFactory.AppendNumbered(invocation, 100);
        _ = transcript.Pull(invocation);

        // The panel was away (or the entry collapsed) while the run printed several caps' worth.
        InvocationFactory.AppendNumbered(invocation, 6_000, first: 101);
        Assert.True(transcript.Pull(invocation));

        Assert.Equal(Retained(invocation), Texts(transcript));
        Assert.Single(transcript.Lines, l => l.IsNotice);
        Assert.DoesNotContain(transcript.Lines, l => l.Text == "line 50");
        Assert.Equal("line 6100", transcript.Lines[^1].Text);
    }

    [Fact]
    public void A_run_that_keeps_its_whole_output_holds_far_more_than_the_ordinary_cap()
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, "skill", "list", "--json");
        InvocationFactory.AppendNumbered(invocation, 50_000);
        var transcript = new ActivityTranscript();

        _ = transcript.Pull(invocation);

        Assert.False(invocation.IsOutputTruncated);
        Assert.Equal(50_000, transcript.Lines.Count);
        Assert.Equal("line 1", transcript.Lines[0].Text);
        Assert.Equal("line 50000", transcript.Lines[^1].Text);
        Assert.Equal("Output · 50,000 lines".Replace(",", CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator, StringComparison.Ordinal), transcript.Describe());
    }

    [Fact]
    public void Beyond_the_full_output_ceiling_the_oldest_lines_go_with_one_reset_and_a_marker()
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, "skill", "list", "--json");
        var transcript = new ActivityTranscript();

        // One-character lines keep the line ceiling (200,000) the binding one, not the 16 MiB byte ceiling.
        for (var i = 0; i < CliInvocation.MaxFullOutputLines - 1_000; i++)
        {
            InvocationFactory.Append(invocation, "x");
        }

        _ = transcript.Pull(invocation);
        Assert.False(invocation.IsOutputTruncated);
        Assert.Equal(CliInvocation.MaxFullOutputLines - 1_000, transcript.Lines.Count);

        // 3,000 more crosses the ceiling: the runner drops a quarter of it at once.
        for (var i = 0; i < 3_000; i++)
        {
            InvocationFactory.Append(invocation, "x");
        }

        var events = Watch(transcript);
        _ = transcript.Pull(invocation);

        Assert.True(invocation.IsOutputTruncated);
        Assert.True(invocation.DroppedOutputLineCount > TranscriptCollection.BulkThreshold);
        Assert.Equal(invocation.OutputLines.Count, transcript.Lines.Count);
        Assert.Equal(CliStream.Notice, transcript.Lines[0].Stream);
        Assert.Equal(invocation.OutputCursor - 1L, transcript.Lines[^1].Sequence);

        // Tens of thousands of lines leaving the head is one Reset (plus the marker going in), not tens of thousands of Removes.
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, events);
        Assert.Contains(NotifyCollectionChangedAction.Reset, events);
    }

    // ------------------------------------------------------------------ how a change is reported to the list

    [Fact]
    public void A_few_lines_raise_one_event_each_and_a_flood_raises_a_single_reset()
    {
        var few = new TranscriptCollection();
        var fewEvents = new List<NotifyCollectionChangedAction>();
        few.CollectionChanged += (_, e) => fewEvents.Add(e.Action);
        few.AddRange(Enumerable.Range(0, 50).Select(i => new ActivityOutputLine(i, "x", CliStream.StandardOutput)).ToArray());
        Assert.Equal(50, fewEvents.Count(a => a == NotifyCollectionChangedAction.Add));

        var many = new TranscriptCollection();
        var manyEvents = new List<NotifyCollectionChangedAction>();
        many.CollectionChanged += (_, e) => manyEvents.Add(e.Action);
        many.AddRange(Enumerable.Range(0, TranscriptCollection.BulkThreshold + 2).Select(i => new ActivityOutputLine(i, "x", CliStream.StandardOutput)).ToArray());
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, manyEvents);
        Assert.Equal(TranscriptCollection.BulkThreshold + 2, many.Count);

        manyEvents.Clear();
        many.RemoveRange(0, TranscriptCollection.BulkThreshold + 1);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, manyEvents);
        Assert.Single(many);

        manyEvents.Clear();
        var removing = 0;
        many.HeadRemoving += (_, _) => removing++;
        many.RemoveRange(0, 1);
        Assert.Equal(1, removing);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Remove }, manyEvents);
    }

    // ------------------------------------------------------------------ copying a selection

    [Fact]
    public void A_selection_copies_in_transcript_order_however_it_was_made()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.AppendNumbered(invocation, 6);
        var transcript = new ActivityTranscript();
        _ = transcript.Pull(invocation);

        // Clicked bottom-up, then ctrl-clicked: the list reports them in the order they were selected.
        var selection = new[] { transcript.Lines[4], transcript.Lines[1], transcript.Lines[2] };

        var text = ActivityTranscript.FormatLines(selection, out var count);

        Assert.Equal(3, count);
        Assert.Equal(new[] { "line 2", "line 3", "line 5" }, LineEndings.Normalize(text).Split('\n'));
    }

    [Fact]
    public void An_empty_selection_copies_nothing()
    {
        var text = ActivityTranscript.FormatLines(Array.Empty<ActivityOutputLine>(), out var count);

        Assert.Equal(0, count);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void The_truncation_marker_sorts_first_in_a_selection()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.AppendNumbered(invocation, 2_100);
        var transcript = new ActivityTranscript();
        _ = transcript.Pull(invocation);

        var text = LineEndings.Normalize(ActivityTranscript.FormatLines(new[] { transcript.Lines[2], transcript.Lines[0] }, out _)).Split('\n');

        Assert.StartsWith("[output truncated]", text[0], StringComparison.Ordinal);
        Assert.Equal(transcript.Lines[2].Text, text[1]);
    }

    // ------------------------------------------------------------------ header text

    [Fact]
    public void The_header_states_how_many_lines_are_shown_and_how_many_were_dropped()
    {
        var invocation = InvocationFactory.Create();
        var transcript = new ActivityTranscript();

        InvocationFactory.Append(invocation, "only line");
        _ = transcript.Pull(invocation);
        Assert.Equal("Output · 1 line", transcript.Describe());

        InvocationFactory.AppendNumbered(invocation, 199, first: 2);
        _ = transcript.Pull(invocation);
        Assert.Equal("Output · 200 lines", transcript.Describe());

        InvocationFactory.AppendNumbered(invocation, 2_000, first: 201);
        _ = transcript.Pull(invocation);
        Assert.Equal(
            $"Output · {transcript.LineCount.ToString("N0", CultureInfo.CurrentCulture)} lines shown, " +
            $"{invocation.DroppedOutputLineCount.ToString("N0", CultureInfo.CurrentCulture)} earlier dropped",
            transcript.Describe());
    }

    // ------------------------------------------------------------------ the row that owns a model

    [Fact]
    public void A_row_shows_the_transcript_and_follows_new_output_by_default()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.AppendNumbered(invocation, 10);

        var row = new ActivityRow(invocation);

        Assert.Equal(10, row.Output.Count);
        Assert.Same(row.Transcript.Lines, row.Output);
        Assert.False(row.IsOutputEmpty);
        Assert.Equal("Output · 10 lines", row.OutputHeader);
        Assert.True(row.IsFollowing);
        Assert.True(row.IsRunning);
    }

    [Fact]
    public void A_row_that_has_seen_no_output_says_so_until_the_first_line_arrives()
    {
        var invocation = InvocationFactory.Create();
        var row = new ActivityRow(invocation);
        Assert.True(row.IsOutputEmpty);
        Assert.Equal("Output", row.OutputHeader);

        InvocationFactory.Append(invocation, "first");
        row.Tick();

        Assert.False(row.IsOutputEmpty);
        Assert.Equal("Output · 1 line", row.OutputHeader);
    }

    [Fact]
    public void A_ticking_row_appends_new_lines_without_touching_the_ones_it_has()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.AppendNumbered(invocation, 5);
        var row = new ActivityRow(invocation);
        var before = row.Output.ToArray();
        var events = new List<NotifyCollectionChangedAction>();
        row.Output.CollectionChanged += (_, e) => events.Add(e.Action);

        InvocationFactory.AppendNumbered(invocation, 3, first: 6);
        row.Tick();
        row.Tick();

        Assert.Equal(8, row.Output.Count);
        Assert.All(before, line => Assert.Contains(line, row.Output));
        Assert.Equal(Enumerable.Repeat(NotifyCollectionChangedAction.Add, 3), events);
    }

    [Fact]
    public void A_finished_row_still_matches_copy_output_line_for_line()
    {
        var invocation = InvocationFactory.Create();
        InvocationFactory.AppendNumbered(invocation, 2_600);
        InvocationFactory.Append(invocation, "failed", CliStream.StandardError);
        InvocationFactory.Finish(invocation, 1);

        var row = new ActivityRow(invocation);

        // Copy output and Export log read invocation.OutputLines; the list must show the same thing.
        Assert.Equal(Retained(invocation), row.Output.Select(l => l.Text).ToArray());
        Assert.False(row.IsRunning);
        Assert.Equal("exit 1", row.ExitBadgeText);
    }

    [Fact]
    public void Copying_an_empty_selection_says_what_to_do_instead()
    {
        string? notice = null;
        var row = new ActivityRow(InvocationFactory.Create(), runner: null, notify: message => notice = message);

        row.CopyLines(Array.Empty<ActivityOutputLine>());

        Assert.NotNull(notice);
        Assert.Contains("Select one or more output lines", notice, StringComparison.Ordinal);
    }
}
