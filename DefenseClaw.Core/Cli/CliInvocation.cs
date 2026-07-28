namespace DefenseClaw.Core.Cli;

public enum CliStream
{
    StandardOutput,
    StandardError,
}

/// <summary>A single line of subprocess output, timestamped as it arrived.</summary>
public sealed record CliOutputLine(DateTimeOffset At, CliStream Stream, string Text);

/// <summary>
/// The record the Activity panel renders: exactly what was executed, when, what it
/// printed, and how it ended.
/// <para>
/// Mutable while the process runs — output lines stream in live — then effectively
/// frozen once <see cref="FinishedAt"/> is set. Use <see cref="Snapshot"/> before
/// handing it to another thread.
/// </para>
/// </summary>
public sealed class CliInvocation
{
    private readonly object _gate = new();
    private readonly List<CliOutputLine> _outputLines = new();

    internal CliInvocation(string executable, IReadOnlyList<string> argv, DateTimeOffset startedAt)
    {
        Id = Guid.NewGuid().ToString("n");
        Executable = executable;
        Argv = argv;
        StartedAt = startedAt;
    }

    public string Id { get; }

    /// <summary>Full path of the binary that was launched.</summary>
    public string Executable { get; }

    /// <summary>Arguments exactly as passed — no shell, no quoting games.</summary>
    public IReadOnlyList<string> Argv { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    public int? ExitCode { get; internal set; }

    /// <summary>Set when the process could not be started or was cancelled.</summary>
    public string? FailureReason { get; internal set; }

    /// <summary>True when a secret was piped in on stdin. The secret itself is never stored.</summary>
    public bool UsedStdinSecret { get; internal set; }

    public IReadOnlyList<CliOutputLine> OutputLines
    {
        get
        {
            lock (_gate)
            {
                return _outputLines.ToArray();
            }
        }
    }

    public TimeSpan? Duration => FinishedAt is { } finished ? finished - StartedAt : null;

    public bool Succeeded => ExitCode == 0;

    public bool IsRunning => FinishedAt is null;

    /// <summary>
    /// Display form for the Activity panel and wizard review screens. Quotes only where
    /// a shell would need it; this is for humans, not for re-execution.
    /// </summary>
    public string CommandLine =>
        string.Join(' ', new[] { Executable }.Concat(Argv).Select(Quote));

    internal void Append(CliOutputLine line)
    {
        lock (_gate)
        {
            _outputLines.Add(line);
        }
    }

    /// <summary>Immutable copy, safe to marshal to the UI thread.</summary>
    public CliInvocation Snapshot()
    {
        var copy = new CliInvocation(Executable, Argv, StartedAt)
        {
            FinishedAt = FinishedAt,
            ExitCode = ExitCode,
            FailureReason = FailureReason,
            UsedStdinSecret = UsedStdinSecret,
        };

        copy._outputLines.AddRange(OutputLines);
        return copy;
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
