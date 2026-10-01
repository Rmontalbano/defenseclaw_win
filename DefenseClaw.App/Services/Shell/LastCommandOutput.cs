using System.Globalization;
using System.IO;
using System.Text;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services;

/// <summary>
/// What "Copy / Export Last Command Output" (the Mac's Commands menu) works on: the newest entry of the runner's activity ring and
/// its transcript as plain text. The transcript is what the runner already scrubbed of secrets on capture, and argv never holds one.
/// </summary>
internal static class LastCommandOutput
{
    /// <summary>The most recently started invocation, or null when nothing has run.</summary>
    public static CliInvocation? Latest(IReadOnlyList<CliInvocation> activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        return activity.Count == 0 ? null : activity.MaxBy(i => i.StartedAt);
    }

    /// <summary>The output lines, one per line, as shown in Activity.</summary>
    public static string Text(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return string.Join(Environment.NewLine, invocation.OutputLines.Select(l => l.Text));
    }

    /// <summary>The command, how it ended, then its output: the file the export writes.</summary>
    public static string ExportText(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var outcome = invocation.ExitCode is { } code
            ? $"exit code {code.ToString(CultureInfo.InvariantCulture)}"
            : invocation.FailureReason ?? (invocation.FinishedAt is null ? "still running" : "did not finish");

        return new StringBuilder()
            .Append("$ ").AppendLine(invocation.CommandLine)
            .Append("Started ").Append(invocation.StartedAt.ToString("u", CultureInfo.InvariantCulture)).Append(", ").AppendLine(outcome)
            .AppendLine()
            .AppendLine(Text(invocation))
            .ToString();
    }

    /// <summary>A file name that says what ran and when, safe on Windows.</summary>
    public static string SuggestFileName(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var words = invocation.Argv.TakeWhile(a => !a.StartsWith('-')).Take(3);
        var stem = string.Join('-', new[] { Path.GetFileNameWithoutExtension(invocation.Executable) }.Concat(words));
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(bad, '_');
        }

        return $"{stem}-{invocation.StartedAt.ToLocalTime():yyyyMMdd-HHmmss}.log";
    }
}
