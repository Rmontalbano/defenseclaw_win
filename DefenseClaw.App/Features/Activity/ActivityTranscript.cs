using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One line of an Activity entry's transcript, as the output list shows it: what the child printed
/// (<see cref="CliStream.StandardOutput"/>), what it printed on stderr, or a notice this app injected
/// (<see cref="CliStream.Notice"/>, today only the truncation marker).
/// </summary>
public sealed class ActivityOutputLine
{
    /// <summary>The marker at the head of a truncated transcript has no source line, so no position in the append sequence.</summary>
    public const long NoSequence = -1;

    public ActivityOutputLine(long sequence, string text, CliStream stream)
    {
        Sequence = sequence;
        Text = text;
        Stream = stream;
    }

    /// <summary>
    /// Position in the invocation's append sequence (see <see cref="CliInvocation.CopyNewLines"/>) - stable while
    /// earlier lines are trimmed away, so it puts a selection back in transcript order.
    /// </summary>
    public long Sequence { get; }

    public string Text { get; }

    public CliStream Stream { get; }

    public bool IsError => Stream == CliStream.StandardError;

    public bool IsNotice => Stream == CliStream.Notice;

    /// <summary>
    /// What the list draws. A blank line still has to be a row with a height: an empty <c>TextBlock</c> measures
    /// as nothing, and a virtualizing panel would then realize every blank row in a run of them.
    /// </summary>
    public string DisplayText => Text.Length == 0 ? " " : Text;

    /// <summary>The line as a screen reader should say it: the stream is part of the meaning, and colour is not announced.</summary>
    public string AutomationName => Stream switch
    {
        CliStream.StandardError => $"error: {Text}",
        CliStream.Notice => $"notice: {Text}",
        _ => Text,
    };

    public override string ToString() => AutomationName;
}

/// <summary>
/// The rows an Activity entry's output list binds to. An <see cref="ObservableCollection{T}"/> with the two bulk
/// operations a transcript needs: adding many lines at once and dropping the oldest lines.
/// <para>
/// Small changes raise one ordinary event per line, which is what lets a virtualizing list keep its scroll
/// position and its selection across a tick (a trim of the head, for example, moves the view up by exactly the
/// lines removed). A change bigger than <see cref="BulkThreshold"/> raises a single <c>Reset</c> instead: that
/// many individual events cost the list more than rebuilding it, and removing lines one by one from the front of a
/// 200,000-line list is quadratic.
/// </para>
/// </summary>
public sealed class TranscriptCollection : ObservableCollection<ActivityOutputLine>
{
    /// <summary>The most lines a change may touch before it is reported as one <c>Reset</c> rather than one event per line.</summary>
    public const int BulkThreshold = 1_000;

    /// <summary>
    /// Raised just before <see cref="RemoveRange"/> removes anything, while the list bound to this collection still shows
    /// the lines about to go. Lines leave from the front, above whatever an operator scrolled up to read, and a list keeps
    /// its numeric scroll offset when that happens - so the view slides down the transcript by exactly the lines removed.
    /// This is the moment a list can note which line it is showing, to put it back afterwards.
    /// </summary>
    public event EventHandler? HeadRemoving;

    public void AddRange(IReadOnlyList<ActivityOutputLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            return;
        }

        if (lines.Count <= BulkThreshold)
        {
            foreach (var line in lines)
            {
                Add(line);
            }

            return;
        }

        ((List<ActivityOutputLine>)Items).AddRange(lines);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        HeadRemoving?.Invoke(this, EventArgs.Empty);

        if (count <= BulkThreshold)
        {
            for (var i = 0; i < count; i++)
            {
                RemoveAt(index);
            }

            return;
        }

        ((List<ActivityOutputLine>)Items).RemoveRange(index, count);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// The incremental line model behind one Activity entry's output list: a window on the invocation's transcript that
/// is brought up to date from the invocation's cursor rather than rebuilt.
/// <para>
/// <b>Append.</b> Each <see cref="Pull"/> asks <see cref="CliInvocation.CopyNewLines"/> for what arrived since the
/// last one and appends only that; the cursor is a position in the invocation's append sequence, so a trim of the
/// invocation cannot make it skip or repeat a line.
/// </para>
/// <para>
/// <b>Trim.</b> The invocation keeps its transcript inside a budget by dropping the oldest lines
/// (<see cref="CliInvocation.MaxRetainedOutputLines"/>, or the far larger ceiling of a run that keeps its whole
/// output). The window mirrors that: after every pull, lines the invocation no longer retains are removed from the
/// head, so the list is never longer than what Copy output and Export log would produce, and never holds more than
/// the invocation's own cap - a long run does not keep growing on screen after the runner has stopped keeping
/// it. A reader that fell so far behind that lines were dropped before it saw them starts over from the oldest
/// retained line.
/// </para>
/// <para>
/// <b>The marker.</b> Once anything has been dropped, the first row is the invocation's own truncation notice
/// (<see cref="CliStream.Notice"/>), replaced in place as the count grows - the same line Copy output and Export log
/// begin with. It is not a source line: it has no sequence and is never counted in <see cref="LineCount"/>.
/// </para>
/// <para>
/// <b>View state.</b> The output list showing this transcript is recycled - the Activity panel reuses one set of
/// elements for whichever entries are in view - so what the operator did to the list cannot live in the list. Where
/// they were reading (<see cref="ReadingSequence"/>) and which lines they selected are kept here, as positions in the
/// append sequence, and handed back to whichever list next shows the transcript. A position in the append sequence
/// stays true while earlier lines are trimmed away, which an index into <see cref="Lines"/> does not.
/// </para>
/// <para>UI thread only: <see cref="Lines"/> is bound to a list.</para>
/// </summary>
public sealed class ActivityTranscript
{
    /// <summary>
    /// The most selected lines that are put back into a list one at a time. A selection of everything is restored in one
    /// step however long the transcript is; a partial one goes through the list's own selection collection, which looks
    /// every line up in the list and so is quadratic. Past this the selection is dropped rather than making a card
    /// scrolling into view stall.
    /// </summary>
    public const int MaxPartialSelectionRestored = 20_000;

    private readonly List<CliOutputLine> _fetched = new();
    private readonly List<ActivityOutputLine> _batch = new();

    /// <summary>Append-sequence positions of the selected lines (<see cref="ActivityOutputLine.NoSequence"/> for the marker).</summary>
    private readonly HashSet<long> _selected = new();

    /// <summary>Where the next <see cref="CliInvocation.CopyNewLines"/> resumes: a position in the append sequence, not an index into <see cref="Lines"/>.</summary>
    private int _cursor;

    /// <summary>Append-sequence position of the first source line held (meaningful while <see cref="LineCount"/> is not 0).</summary>
    private long _headSequence;

    /// <summary>The number of dropped lines the marker row currently reports; 0 while there is no marker.</summary>
    private long _markerDropped;

    public TranscriptCollection Lines { get; } = new();

    /// <summary>Source lines held (the marker is not one).</summary>
    public int LineCount { get; private set; }

    /// <summary>Lines the invocation had dropped, as of the last <see cref="Pull"/>.</summary>
    public long DroppedLineCount { get; private set; }

    private int MarkerOffset => _markerDropped > 0 ? 1 : 0;

    /// <summary>
    /// The first source line in view when the operator scrolled away from the end, as a position in the append sequence;
    /// null while the list follows the end (or has not been scrolled). Written by the list the operator scrolls, read back
    /// by whichever list next shows this transcript.
    /// </summary>
    public long? ReadingSequence { get; set; }

    /// <summary>
    /// Index into <see cref="Lines"/> of the row to put back at the top of a list: the reading position, or the head of the
    /// transcript when that line has since been trimmed away (the reader was at the head, and the head is where they stay).
    /// </summary>
    public int ReadingIndex => ReadingSequence is { } sequence && TryIndexOf(sequence, out var index) ? index : 0;

    /// <summary>Where a position in the append sequence sits in <see cref="Lines"/>; false when that line is no longer held.</summary>
    public bool TryIndexOf(long sequence, out int index)
    {
        if (sequence == ActivityOutputLine.NoSequence)
        {
            index = 0;
            return MarkerOffset == 1;
        }

        var offset = sequence - _headSequence;
        if (LineCount > 0 && offset >= 0 && offset < LineCount)
        {
            index = MarkerOffset + (int)offset;
            return true;
        }

        index = -1;
        return false;
    }

    /// <summary>How many lines are selected (lines that left the transcript are no longer counted).</summary>
    public int SelectedCount => _selected.Count;

    /// <summary>True when every row the list holds, the marker included, is selected - the one selection restored in a single step.</summary>
    public bool IsEverythingSelected => Lines.Count > 0 && _selected.Count == Lines.Count;

    /// <summary>Applies the change a list reported to its selection.</summary>
    public void NoteSelectionChanged(IEnumerable<ActivityOutputLine> added, IEnumerable<ActivityOutputLine> removed)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(removed);

        foreach (var line in removed)
        {
            _ = _selected.Remove(line.Sequence);
        }

        foreach (var line in added)
        {
            _ = _selected.Add(line.Sequence);
        }
    }

    /// <summary>Makes the selection exactly <paramref name="selected"/> (the list was rebuilt, and says what it kept).</summary>
    public void ReplaceSelection(IEnumerable<ActivityOutputLine> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        _selected.Clear();
        foreach (var line in selected)
        {
            _ = _selected.Add(line.Sequence);
        }
    }

    /// <summary>
    /// The selected rows, in transcript order, for a list to select one at a time. A selection of everything is left to
    /// <see cref="IsEverythingSelected"/> (one step, however long); a partial one bigger than
    /// <see cref="MaxPartialSelectionRestored"/> is dropped here, so the list and the transcript agree that nothing is
    /// selected rather than the list quietly holding a different selection from the one that was made.
    /// </summary>
    public IReadOnlyList<ActivityOutputLine> LinesToReselect()
    {
        if (_selected.Count == 0 || IsEverythingSelected)
        {
            return Array.Empty<ActivityOutputLine>();
        }

        if (_selected.Count > MaxPartialSelectionRestored)
        {
            _selected.Clear();
            return Array.Empty<ActivityOutputLine>();
        }

        var rows = new List<ActivityOutputLine>(_selected.Count);
        foreach (var sequence in _selected.Order())
        {
            if (TryIndexOf(sequence, out var index))
            {
                rows.Add(Lines[index]);
            }
        }

        return rows;
    }

    /// <summary>
    /// Brings the window up to date with <paramref name="invocation"/>. Safe to call while the process is still
    /// writing on another thread. Returns whether <see cref="Lines"/> changed.
    /// </summary>
    public bool Pull(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var changed = false;

        _fetched.Clear();
        var newCursor = invocation.CopyNewLines(_cursor, _fetched);
        _cursor = newCursor;

        // CopyNewLines puts a synthesized notice in front when this reader fell behind a trim. That notice only knows
        // how many lines this reader missed; the head marker below reports the total, so it is left out here (and is
        // not a position in the append sequence, so it must not be counted in it either).
        var fresh = 0;
        foreach (var line in _fetched)
        {
            if (line.Stream != CliStream.Notice)
            {
                fresh++;
            }
        }

        if (fresh > 0)
        {
            var firstSequence = newCursor - (long)fresh;
            if (LineCount > 0 && _headSequence + LineCount != firstSequence)
            {
                // Lines went missing between the last pull and this one: what is held is not contiguous with what
                // arrived. Every held line is older than the invocation's retained window, so start over from it.
                Lines.RemoveRange(MarkerOffset, LineCount);
                LineCount = 0;
                _selected.RemoveWhere(static sequence => sequence != ActivityOutputLine.NoSequence);
            }

            if (LineCount == 0)
            {
                _headSequence = firstSequence;
            }

            _batch.Clear();
            var sequence = firstSequence;
            foreach (var line in _fetched)
            {
                if (line.Stream != CliStream.Notice)
                {
                    _batch.Add(new ActivityOutputLine(sequence++, line.Text, line.Stream));
                }
            }

            Lines.AddRange(_batch);
            LineCount += _batch.Count;
            changed = true;
        }

        ReleaseBuffers();

        // Mirror the invocation's own head trim.
        var dropped = invocation.DroppedOutputLineCount;
        DroppedLineCount = dropped;

        var stale = (int)Math.Min(LineCount, Math.Max(0, dropped - _headSequence));
        if (stale > 0)
        {
            Lines.RemoveRange(MarkerOffset, stale);
            LineCount -= stale;
            _headSequence += stale;
            changed = true;

            // A list drops a selected line from its own selection when the line leaves, but no list may be showing this
            // transcript right now; the selection kept here must not name lines that are gone.
            var head = _headSequence;
            _selected.RemoveWhere(sequence => sequence != ActivityOutputLine.NoSequence && sequence < head);
        }

        if (dropped != _markerDropped && ApplyMarker(invocation, dropped))
        {
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The scratch lists are reused tick to tick so a steady stream allocates nothing, but a first pull of a 200,000-line
    /// transcript would leave them holding that much capacity (and references to every line) for as long as the entry lives.
    /// </summary>
    private void ReleaseBuffers()
    {
        const int KeepCapacity = 4096;

        _fetched.Clear();
        _batch.Clear();
        if (_fetched.Capacity > KeepCapacity)
        {
            _fetched.TrimExcess();
            _batch.TrimExcess();
        }
    }

    /// <summary>
    /// Text of a set of selected rows in transcript order, one line each (the marker sorts first), and how many
    /// lines that is. A list reports its selection in the order it was made, which is not the order it is read in.
    /// </summary>
    public static string FormatLines(IEnumerable<ActivityOutputLine> selected, out int count)
    {
        ArgumentNullException.ThrowIfNull(selected);

        var ordered = selected.OrderBy(line => line.Sequence).ToList();
        count = ordered.Count;

        var builder = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                _ = builder.Append(Environment.NewLine);
            }

            _ = builder.Append(ordered[i].Text);
        }

        return builder.ToString();
    }

    /// <summary>The line count as the entry's header states it: <c>Output</c>, <c>Output · 1,204 lines</c>, or with what was dropped.</summary>
    public string Describe()
    {
        if (LineCount == 0 && DroppedLineCount == 0)
        {
            return "Output";
        }

        var shown = LineCount.ToString("N0", CultureInfo.CurrentCulture);
        var noun = LineCount == 1 ? "line" : "lines";
        return DroppedLineCount > 0
            ? $"Output · {shown} {noun} shown, {DroppedLineCount.ToString("N0", CultureInfo.CurrentCulture)} earlier dropped"
            : $"Output · {shown} {noun}";
    }

    /// <summary>
    /// Puts the invocation's own truncation notice at the head, or replaces the one that is there. Read from
    /// <see cref="CliInvocation.OutputLines"/> - a full copy of the retained transcript, but only when a trim has
    /// just happened, which is once per quarter of the budget rather than once per tick - so the text is always the
    /// one Copy output and Export log start with, whatever the runner decides it says.
    /// </summary>
    private bool ApplyMarker(CliInvocation invocation, long dropped)
    {
        if (dropped == 0)
        {
            return false;
        }

        var retained = invocation.OutputLines;
        if (retained.Count == 0 || retained[0].Stream != CliStream.Notice)
        {
            // Nothing to show yet; the next pull tries again because _markerDropped is unchanged.
            return false;
        }

        var marker = new ActivityOutputLine(ActivityOutputLine.NoSequence, retained[0].Text, CliStream.Notice);
        if (_markerDropped == 0)
        {
            Lines.Insert(0, marker);
        }
        else
        {
            Lines[0] = marker;
        }

        _markerDropped = dropped;
        return true;
    }
}
