using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Cli;

public enum CliStream
{
    StandardOutput,
    StandardError,

    /// <summary>
    /// A line this app injected into the transcript rather than one the child printed —
    /// today only the truncation marker <see cref="CliInvocation"/> emits when it drops
    /// output to stay inside its retention budget.
    /// <para>
    /// Deliberately a third value rather than reusing <see cref="StandardError"/>: several
    /// call sites reduce an invocation to a user-facing message by filtering on a stream
    /// (<c>OutputLines.LastOrDefault(l =&gt; l.Stream == CliStream.StandardError)</c>, or
    /// concatenating stdout and parsing it as JSON). A notice injected into either of those
    /// streams would be reported to the operator as the CLI's own error, or would corrupt a
    /// parse. Notices are visible everywhere output is rendered verbatim — the Activity
    /// panel, the wizard console, the upgrade console — and invisible to stream filters.
    /// </para>
    /// </summary>
    Notice,
}

/// <summary>
/// A single line of an invocation's transcript, timestamped as it arrived.
/// <para>
/// Almost always a line the child process wrote to stdout or stderr. The one exception is
/// <see cref="CliStream.Notice"/>, which this app synthesizes — see
/// <see cref="CliInvocation.MaxRetainedOutputLines"/>.
/// </para>
/// </summary>
public sealed record CliOutputLine(DateTimeOffset At, CliStream Stream, string Text);

/// <summary>
/// The record the Activity panel renders: exactly what was executed, when, what it
/// printed, and how it ended.
/// <para>
/// Mutable while the process runs — output lines stream in live — then effectively
/// frozen once <see cref="FinishedAt"/> is set. Use <see cref="Snapshot"/> before
/// handing it to another thread, or <see cref="CopyNewLines"/> if all you need is the
/// delta since your last read (which is what every live console actually needs).
/// </para>
/// <para>
/// <b>Retention is bounded.</b> <see cref="CliRunner"/> caps its activity ring by entry
/// count only, so without a second cap here a single chatty command would pin its whole
/// transcript in a tray app that runs for weeks. Output is therefore capped per invocation
/// in both lines and bytes; overflow drops the oldest lines and is always reported through
/// an explicit <see cref="CliStream.Notice"/> marker. Nothing is ever dropped silently, and
/// <see cref="ExitCode"/>, <see cref="FailureReason"/> and <see cref="Duration"/> are never
/// affected by trimming — they are separate fields, not transcript lines.
/// </para>
/// </summary>
public sealed class CliInvocation
{
    /// <summary>
    /// Hard ceiling on retained transcript lines per invocation.
    /// <para>
    /// Sized against what an operator actually reads back: a resolver or discovery run over
    /// a large repo prints thousands of lines, but the diagnostic value is concentrated in
    /// the tail (what failed, what the exit path was). 2,000 lines comfortably covers a
    /// normal run end to end.
    /// </para>
    /// </summary>
    public const int MaxRetainedOutputLines = 2_000;

    /// <summary>
    /// Hard ceiling on retained transcript bytes per invocation, and the cap that actually
    /// carries the memory guarantee — a line count alone bounds nothing when a CLI emits
    /// multi-kilobyte JSON on a single line.
    /// <para>
    /// Both caps do real work, and which one binds depends on line width: below roughly 33
    /// characters per line the line cap fires first (progress chatter, short status lines),
    /// above it the byte cap does (JSON dumps, long paths, stack traces).
    /// </para>
    /// <para>
    /// 256 KiB against <see cref="CliRunner.ActivityCapacity"/>'s default 200 entries is a
    /// hard 50 MiB ceiling on all retained CLI text. That worst case needs all 200 ring
    /// entries to have been maximally chatty at once; the ring is in practice dominated by
    /// short status and list commands, so real usage sits far below it. The number that
    /// matters is that a ceiling exists at all — this app sits resident in the tray for
    /// weeks, and unbounded is the one value that is certainly wrong.
    /// </para>
    /// </summary>
    public const long MaxRetainedOutputBytes = 256 * 1024;

    /// <summary>
    /// The line ceiling for a run that asked to keep its whole output
    /// (<see cref="CliRunOptions.RetainFullOutput"/>): 200,000 lines. Roughly a hundred times the
    /// ordinary cap - room for the largest <c>&lt;noun&gt; list --json</c> a real install can print
    /// with an order of magnitude to spare - while still being a ceiling, because a resident tray app
    /// must never hold an unbounded transcript. Past it the run falls back to exactly the ordinary
    /// behaviour: oldest lines dropped, and an explicit <see cref="CliStream.Notice"/> marker.
    /// </summary>
    public const int MaxFullOutputLines = 200_000;

    /// <summary>
    /// The byte ceiling for a run that asked to keep its whole output: 16 MiB, counted the same way
    /// as <see cref="MaxRetainedOutputBytes"/> (UTF-16 text plus a fixed per-line overhead). Whichever
    /// of this and <see cref="MaxFullOutputLines"/> is reached first binds.
    /// </summary>
    public const long MaxFullOutputBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Per-line allowance added to the text's own byte cost, approximating the
    /// <see cref="CliOutputLine"/> record, its slot in the backing list, and the string
    /// object header. Deliberately an estimate: the cap exists to bound growth, not to
    /// account for the heap precisely, and under-counting overhead is what would let a
    /// flood of empty lines slip past the byte budget.
    /// </summary>
    private const int PerLineOverheadBytes = 64;

    // Trimming drops down to a low-water mark rather than to exactly the cap, so the O(n)
    // RemoveRange runs about once per quarter-budget instead of once per appended line.
    // Same reasoning as the Logs panel's RemoveRange-based trim; a per-line RemoveAt(0)
    // would make a chatty command O(n^2).
    private const int TrimLowWaterNumerator = 3;
    private const int TrimLowWaterDenominator = 4;

    private readonly object _gate = new();
    private readonly List<CliOutputLine> _outputLines = new();

    /// <summary>Byte cost of everything currently in <see cref="_outputLines"/>. Guarded by <see cref="_gate"/>.</summary>
    private long _retainedBytes;

    /// <summary>
    /// Lines appended over the invocation's whole life, including ones since trimmed. This
    /// is the sequence space <see cref="CopyNewLines"/> cursors live in, which is why it is
    /// monotonic and never reset — an index into <see cref="_outputLines"/> would shift out
    /// from under an incremental reader the moment a trim happened.
    /// </summary>
    private int _appendedLineCount;

    /// <summary>Lines dropped from the head by trimming. Invariant: <c>_droppedLineCount + _outputLines.Count == _appendedLineCount</c>.</summary>
    private int _droppedLineCount;

    /// <summary>When the most recent trim ran; used to timestamp synthesized truncation markers honestly.</summary>
    private DateTimeOffset _lastTruncationAt;

    /// <summary>Retention ceilings for this invocation: the ordinary caps, or the full-output ceilings.</summary>
    private readonly int _maxLines;

    private readonly long _maxBytes;

    internal CliInvocation(string executable, IReadOnlyList<string> argv, DateTimeOffset startedAt, bool retainFullOutput = false)
    {
        Id = Guid.NewGuid().ToString("n");
        Executable = executable;
        Argv = argv;
        StartedAt = startedAt;
        RetainsFullOutput = retainFullOutput;
        _maxLines = retainFullOutput ? MaxFullOutputLines : MaxRetainedOutputLines;
        _maxBytes = retainFullOutput ? MaxFullOutputBytes : MaxRetainedOutputBytes;
    }

    public string Id { get; }

    /// <summary>
    /// True when the run was started with <see cref="CliRunOptions.RetainFullOutput"/>: its transcript
    /// is kept up to <see cref="MaxFullOutputLines"/> / <see cref="MaxFullOutputBytes"/> instead of the
    /// ordinary <see cref="MaxRetainedOutputLines"/> / <see cref="MaxRetainedOutputBytes"/>.
    /// </summary>
    public bool RetainsFullOutput { get; }

    /// <summary>The most transcript lines this invocation retains before it drops the oldest.</summary>
    public int RetainedLineLimit => _maxLines;

    /// <summary>The most transcript bytes (as counted by the retention budget) this invocation retains.</summary>
    public long RetainedByteLimit => _maxBytes;

    /// <summary>Full path of the binary that was launched.</summary>
    public string Executable { get; }

    /// <summary>Arguments exactly as passed — no shell, no quoting games.</summary>
    public IReadOnlyList<string> Argv { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    public int? ExitCode { get; internal set; }

    /// <summary>
    /// Set when the run did not end with the child exiting on its own: the process could not be
    /// started, or <see cref="CliRunner"/> stopped it and killed its process tree. The text says
    /// which — <c>timed out after N s</c>, <c>cancelled</c> (the caller's token),
    /// <c>cancelled: DefenseClaw for Windows is exiting</c> (app shutdown), or <c>not started</c>.
    /// <para>
    /// A stopped run has no <see cref="ExitCode"/>: the value a killed process reports is an
    /// artefact of the kill, not something the CLI said, so it is left <c>null</c> rather than
    /// recorded. Any non-null reason therefore means <see cref="Succeeded"/> is false.
    /// </para>
    /// </summary>
    public string? FailureReason { get; internal set; }

    /// <summary>True when a secret was piped in on stdin. The secret itself is never stored.</summary>
    public bool UsedStdinSecret { get; internal set; }

    /// <summary>
    /// True when this app typed a secret at the command's hidden prompt, in a pseudo-console (<see cref="SecretPtyRunner"/>: <c>keys set</c>,
    /// whose prompt reads the console and so cannot be given a piped value). The value is on no command line and in no transcript line, and
    /// the app does not keep it, so such an entry cannot be run again from Activity (<see cref="UsedStdinSecret"/> is the same rule for a
    /// piped one).
    /// </summary>
    public bool UsedPromptSecret { get; internal set; }

    private int _processId;

    /// <summary>
    /// The child's process id from the moment it has started; null before that, and for a run that never started. Internal on purpose:
    /// nothing shows it to the operator. The Core suite uses it to find what a run started below its own child, rather than picking
    /// "the new ping" out of every ping on the machine, which can be somebody else's.
    /// </summary>
    internal int? ProcessId
    {
        get
        {
            var id = Volatile.Read(ref _processId);
            return id == 0 ? null : id;
        }

        set => Volatile.Write(ref _processId, value ?? 0);
    }

    /// <summary>
    /// The <b>names</b> of the environment variables the runner set for this child from
    /// <see cref="CliRunOptions.EnvironmentOverlay"/> (empty values, which are skipped, are not listed),
    /// ordered by name. Only names are ever recorded: the values are applied to the child's environment
    /// block and appear nowhere on this object, in <see cref="Argv"/>, in <see cref="CommandLine"/> or in the
    /// transcript. The Activity panel reads <see cref="EnvironmentDisplay"/> to say so.
    /// </summary>
    public IReadOnlyList<string> EnvironmentNames { get; internal set; } = Array.Empty<string>();

    /// <summary>
    /// A line for the Activity panel: <c>env: SPLUNK_ACCESS_TOKEN=•••</c> — each name with its value masked —
    /// or an empty string when the run had no overlay. Built from <see cref="EnvironmentNames"/>, so it cannot
    /// carry a value; the mask is a fixed literal, not the value's length.
    /// </summary>
    public string EnvironmentDisplay => EnvironmentNames.Count == 0
        ? string.Empty
        : "env: " + string.Join(' ', EnvironmentNames.Select(n => n + "=•••"));

    /// <summary>
    /// True when the run was started with <see cref="CliRunOptions.SurvivesShutdown"/> — in
    /// practice the in-app upgrade installer. Such a run is not stopped by app exit and is
    /// refused by <see cref="CliRunner.Cancel(CliInvocation, out string)"/>: killing an installer
    /// part-way can leave the machine with a half-replaced DefenseClaw. The Activity panel reads
    /// this to disable Cancel with a reason instead of offering a button that can only fail.
    /// </summary>
    public bool SurvivesShutdown { get; internal set; }

    /// <summary>
    /// Set by <see cref="CliRunner.Cancel(CliInvocation, out string)"/> the moment a cancel is
    /// accepted, before the run has finished reporting. Lets a panel show "cancelling…" between
    /// the click and the process tree actually being gone. Once the run finishes,
    /// <see cref="FailureReason"/> (starting with <c>cancelled</c>) is the record of what happened.
    /// </summary>
    public bool CancelRequested { get; internal set; }

    /// <summary>
    /// The whole retained transcript as an independent array, safe to hand to any thread.
    /// <para>
    /// When output has been trimmed, element 0 is a synthesized
    /// <see cref="CliStream.Notice"/> line naming the number of dropped lines; the marker is
    /// generated on read rather than stored, so it always reports the current total.
    /// </para>
    /// <para>
    /// <b>Cost.</b> This copies the entire retained transcript on every call. It is the right
    /// shape for one-shot consumers that read a finished invocation once (error extraction,
    /// JSON parsing of stdout), and the wrong shape for a timer that polls a running
    /// invocation several times a second — those should use <see cref="CopyNewLines"/>.
    /// Never call it in a loop condition or an indexer: each evaluation is a fresh full copy.
    /// </para>
    /// <para>
    /// <b>Not a delta cursor.</b> Do not remember a previous <c>Count</c> and resume from it
    /// next tick. That pattern is only correct while nothing is ever trimmed: this list is a
    /// window over the retained lines, so a trim renumbers it, and a remembered index then
    /// points past the first unread line and skips whatever the trim shifted underneath it.
    /// <see cref="CopyNewLines"/> exists precisely to do incremental reads safely — its
    /// cursors are positions in the append sequence, which trimming cannot renumber.
    /// </para>
    /// </summary>
    public IReadOnlyList<CliOutputLine> OutputLines
    {
        get
        {
            lock (_gate)
            {
                if (_droppedLineCount == 0)
                {
                    return _outputLines.ToArray();
                }

                var withMarker = new CliOutputLine[_outputLines.Count + 1];
                withMarker[0] = TruncationMarker(_lastTruncationAt, _droppedLineCount);
                _outputLines.CopyTo(withMarker, 1);
                return withMarker;
            }
        }
    }

    /// <summary>
    /// Total lines appended over this invocation's life, including any since trimmed away.
    /// Monotonic, and O(1) — this is the cursor value <see cref="CopyNewLines"/> consumes and
    /// returns, and the cheap way to ask "is there anything new?" without copying.
    /// </summary>
    public int OutputCursor
    {
        get
        {
            lock (_gate)
            {
                return _appendedLineCount;
            }
        }
    }

    /// <summary>Lines dropped from the head to stay inside the retention budget. Zero for a normal run.</summary>
    public int DroppedOutputLineCount
    {
        get
        {
            lock (_gate)
            {
                return _droppedLineCount;
            }
        }
    }

    /// <summary>True once anything has been trimmed — i.e. once the transcript is no longer complete.</summary>
    public bool IsOutputTruncated => DroppedOutputLineCount > 0;

    public TimeSpan? Duration => FinishedAt is { } finished ? finished - StartedAt : null;

    public bool Succeeded => ExitCode == 0;

    public bool IsRunning => FinishedAt is null;

    /// <summary>
    /// Display form for the Activity panel and wizard review screens. Quotes only where
    /// a shell would need it; this is for humans, not for re-execution. Argv only — a run's
    /// environment overlay is shown separately by <see cref="EnvironmentDisplay"/>.
    /// </summary>
    public string CommandLine =>
        string.Join(' ', new[] { Executable }.Concat(Argv).Select(Quote));

    /// <summary>
    /// The same command as text that is safe to paste into PowerShell: every argument is one literal string
    /// (<c>&amp; 'C:\...\defenseclaw.exe' skill quarantine -- 'x&amp;calc'</c>), so a name from outside cannot
    /// run anything when it is pasted. <see cref="CommandLine"/> is for reading; this is for the clipboard.
    /// </summary>
    public string PowerShellCommandLine => PowerShellQuoting.CommandLine(Executable, Argv);

    /// <summary>
    /// Appends everything added since <paramref name="fromCursor"/> to
    /// <paramref name="destination"/> and returns the cursor to pass in next time.
    /// <para>
    /// This is the read path for live consoles. A polling consumer holds one cursor and one
    /// reusable buffer, so a tick costs one lock acquisition plus the handful of lines that
    /// actually arrived — not a copy of the accumulated transcript. Pass <c>0</c> for a first
    /// read.
    /// </para>
    /// <para>
    /// <b>Cursors survive trimming.</b> They index the monotonic append sequence, not the
    /// retained list, so a trim never silently renumbers a reader's position. If a reader
    /// fell so far behind that lines were dropped underneath it, the first element appended
    /// is a <see cref="CliStream.Notice"/> marker naming exactly how many that reader missed,
    /// and copying resumes at the oldest still-retained line. A reader is never handed a
    /// duplicate and never handed a gap it was not told about.
    /// </para>
    /// <para>
    /// <b>Threading.</b> Safe to call while the process is still writing on a background
    /// thread. <paramref name="destination"/> is mutated while this invocation's lock is
    /// held, so it must be a collection owned by the caller and must not re-enter this
    /// instance (no observable collection bound to a handler that reads back from here).
    /// </para>
    /// </summary>
    /// <param name="fromCursor">
    /// The value returned by the previous call, or 0. Negative values are treated as 0.
    /// A value past the end (a cursor from a different invocation) copies nothing.
    /// </param>
    /// <param name="destination">Caller-owned sink; lines are appended in arrival order.</param>
    /// <returns>The cursor to pass to the next call.</returns>
    public int CopyNewLines(int fromCursor, ICollection<CliOutputLine> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (_gate)
        {
            var from = fromCursor < 0 ? 0 : fromCursor;
            if (from >= _appendedLineCount)
            {
                return _appendedLineCount;
            }

            if (from < _droppedLineCount)
            {
                destination.Add(TruncationMarker(_lastTruncationAt, _droppedLineCount - from));
                from = _droppedLineCount;
            }

            for (var i = from - _droppedLineCount; i < _outputLines.Count; i++)
            {
                destination.Add(_outputLines[i]);
            }

            return _appendedLineCount;
        }
    }

    /// <summary>
    /// Records one captured line and trims the transcript back inside its budget if that
    /// pushed it over. Called from the process's stdout/stderr callback threads.
    /// </summary>
    internal void Append(CliOutputLine line)
    {
        lock (_gate)
        {
            _outputLines.Add(line);
            _retainedBytes += CostOf(line);
            _appendedLineCount++;
            TrimLocked(line.At);
        }
    }

    /// <summary>
    /// Independent copy, safe to marshal to the UI thread and to read without further
    /// locking: the copy is never appended to, so its transcript cannot shift underneath a
    /// reader the way the live instance's can.
    /// <para>
    /// One copy of the retained transcript, taken under a single lock acquisition. Prefer
    /// <see cref="CopyNewLines"/> when you only need what arrived since your last read;
    /// this call's cost still scales with the retained transcript.
    /// </para>
    /// <para>
    /// Truncation state travels with the copy, so <see cref="OutputLines"/> on the snapshot
    /// carries the same marker the live instance would produce.
    /// </para>
    /// </summary>
    public CliInvocation Snapshot()
    {
        var copy = new CliInvocation(Executable, Argv, StartedAt, RetainsFullOutput)
        {
            FinishedAt = FinishedAt,
            ExitCode = ExitCode,
            FailureReason = FailureReason,
            UsedStdinSecret = UsedStdinSecret,
            UsedPromptSecret = UsedPromptSecret,
            EnvironmentNames = EnvironmentNames,
            SurvivesShutdown = SurvivesShutdown,
            CancelRequested = CancelRequested,
            ProcessId = ProcessId,
        };

        lock (_gate)
        {
            copy._outputLines.Capacity = _outputLines.Count;
            copy._outputLines.AddRange(_outputLines);
            copy._retainedBytes = _retainedBytes;
            copy._appendedLineCount = _appendedLineCount;
            copy._droppedLineCount = _droppedLineCount;
            copy._lastTruncationAt = _lastTruncationAt;
        }

        return copy;
    }

    /// <summary>
    /// Drops oldest lines until the transcript is back under the low-water mark. Caller must
    /// hold <see cref="_gate"/>.
    /// <para>
    /// The newest line is never dropped. That matters for the pathological case of a single
    /// line larger than the entire byte budget (a CLI dumping one giant JSON blob): the cap
    /// exists to bound accumulation across lines, and swallowing the only line there is
    /// would leave an operator staring at an empty console. Such an invocation is allowed to
    /// exceed <see cref="MaxRetainedOutputBytes"/> by exactly one line.
    /// </para>
    /// </summary>
    private void TrimLocked(DateTimeOffset at)
    {
        if (_outputLines.Count <= _maxLines && _retainedBytes <= _maxBytes)
        {
            return;
        }

        var targetLines = _maxLines * TrimLowWaterNumerator / TrimLowWaterDenominator;
        var targetBytes = _maxBytes * TrimLowWaterNumerator / TrimLowWaterDenominator;

        var drop = 0;
        var lines = _outputLines.Count;
        var bytes = _retainedBytes;

        while (drop < _outputLines.Count - 1 && (lines > targetLines || bytes > targetBytes))
        {
            bytes -= CostOf(_outputLines[drop]);
            lines--;
            drop++;
        }

        if (drop == 0)
        {
            return;
        }

        _outputLines.RemoveRange(0, drop);
        _retainedBytes = bytes;
        _droppedLineCount += drop;
        _lastTruncationAt = at;
    }

    /// <summary>
    /// The line an operator sees in place of dropped output. Phrased to state both what was
    /// lost and why, because the alternative — a console that just starts mid-run — reads as
    /// a bug rather than as a documented cap.
    /// </summary>
    private CliOutputLine TruncationMarker(DateTimeOffset at, int droppedLines) =>
        new(
            at,
            CliStream.Notice,
            $"[output truncated] {droppedLines} earlier line(s) dropped — DefenseClaw for Windows retains at " +
            $"most {_maxLines} lines / {_maxBytes / 1024} KiB of output per invocation. " +
            "The exit code and failure reason below are unaffected.");

    /// <summary>Approximate retained cost of a line: the text itself as UTF-16, plus fixed overhead.</summary>
    private static long CostOf(CliOutputLine line) =>
        PerLineOverheadBytes + ((long)line.Text.Length * sizeof(char));

    /// <summary>
    /// Display quoting only: an empty or spaced argument gets quotes. A name that came from outside (a skill, a registry entry) is shown with
    /// its control and format characters spelled out (<c>‮</c>, <c>\n</c>, see <see cref="DisplayNames.Visible"/>), so a row in the
    /// Activity panel is one line and a right-to-left override cannot make it read as another command. <see cref="Argv"/> itself is untouched.
    /// </summary>
    private static string Quote(string value)
    {
        var shown = DisplayNames.Visible(value);
        return shown.Length == 0 || shown.Any(char.IsWhiteSpace) ? $"\"{shown}\"" : shown;
    }
}
