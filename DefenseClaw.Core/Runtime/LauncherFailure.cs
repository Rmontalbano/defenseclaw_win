using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Runtime;

/// <summary>Which of the per-user launcher's own failure lines a probe printed.</summary>
public enum LauncherFailureKind
{
    /// <summary>The launcher state file beside the launcher could not be read.</summary>
    StateUnreadable,

    /// <summary>The launcher state file was read but is not valid (bad JSON or missing fields).</summary>
    StateInvalid,

    /// <summary>The data folder (or DEFENSECLAW_HOME) is a junction or other reparse point.</summary>
    DataFolderLink,

    /// <summary>The launcher could not inspect the data folder.</summary>
    InspectFailed,

    /// <summary>The launcher could not start the per-user CLI (the venv executable is missing, for example).</summary>
    StartFailed,
}

/// <summary>
/// One of the failure lines the native per-user launcher (<c>%USERPROFILE%\.local\bin\defenseclaw.exe</c> since DefenseClaw 1.0.7) prints to stderr
/// before it exits 1, recognised in what a probe printed. The launcher stops before the real CLI runs, so the probe has no version to read; this keeps
/// the launcher's own explanation instead of the generic "no usable answer". Recognising text needs no version gate: no other CLI prints these lines.
/// </summary>
/// <param name="Kind">Which line it is.</param>
/// <param name="Line">The line as printed, without its line ending.</param>
/// <param name="Path">The folder the line names (data-folder lines), else null.</param>
public sealed partial record LauncherFailure(LauncherFailureKind Kind, string Line, string? Path = null)
{
    private const string StatePrefix = "read per-user launcher state: ";

    // Every line starts with "defenseclaw: " at the start of a line and is matched on its known phrase, so a Python traceback, a Click error
    // ("Error: ...", "Usage: defenseclaw ...") or a line that merely mentions the program name never matches.
    [GeneratedRegex(@"^defenseclaw: (?<body>[^\r\n]*)\r?$",RegexOptions.Multiline | RegexOptions.CultureInvariant, 250)]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^data folder ""(?<path>[^""\r\n]*)"" is a reparse point; ", RegexOptions.CultureInvariant, 250)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^inspect data folder ""(?<path>[^""\r\n]*)"": ", RegexOptions.CultureInvariant, 250)]
    private static partial Regex InspectRegex();

    /// <summary>The first launcher failure line in <paramref name="text"/>, or null (null, empty and every other output).</summary>
    public static LauncherFailure? Recognise(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            foreach (Match match in LineRegex().Matches(text))
            {
                var body = match.Groups["body"].Value.TrimEnd();
                var line = "defenseclaw: " + body;

                if (LinkRegex().Match(body) is { Success: true } link)
                {
                    return new LauncherFailure(LauncherFailureKind.DataFolderLink, line, link.Groups["path"].Value);
                }

                if (InspectRegex().Match(body) is { Success: true } inspect)
                {
                    return new LauncherFailure(LauncherFailureKind.InspectFailed, line, inspect.Groups["path"].Value);
                }

                if (body.StartsWith("per-user launcher state is invalid", StringComparison.Ordinal))
                {
                    return new LauncherFailure(LauncherFailureKind.StateInvalid, line);
                }

                if (body.StartsWith("per-user launcher state cannot be read", StringComparison.Ordinal))
                {
                    return new LauncherFailure(LauncherFailureKind.StateUnreadable, line);
                }

                if (body.StartsWith(StatePrefix, StringComparison.Ordinal))
                {
                    // Go's JSON errors read "invalid character ..." / "unexpected end of JSON input" / "json: ..."; anything else is a file error.
                    var detail = body[StatePrefix.Length..];
                    var json = detail.StartsWith("invalid character", StringComparison.Ordinal) ||
                               detail.StartsWith("unexpected end of JSON", StringComparison.Ordinal) ||
                               detail.StartsWith("json:", StringComparison.Ordinal);
                    return new LauncherFailure(json ? LauncherFailureKind.StateInvalid : LauncherFailureKind.StateUnreadable, line);
                }

                if (body.StartsWith("start per-user CLI: ", StringComparison.Ordinal))
                {
                    return new LauncherFailure(LauncherFailureKind.StartFailed, line);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return null;
    }

    /// <summary>What the Settings, Updates and About lines say: the launcher's line quoted, then the fix in plain words.</summary>
    public string UserReason()
    {
        var folder = string.IsNullOrWhiteSpace(Path) ? "the DefenseClaw data folder" : Path;
        var fix = Kind switch
        {
            LauncherFailureKind.DataFolderLink =>
                $"Restore {folder} as a regular folder (not a junction or link), then run the DefenseClaw installer again.",
            LauncherFailureKind.InspectFailed =>
                $"Check that {folder} exists and can be read, then run the DefenseClaw installer (install.ps1) again.",
            _ => "Run the DefenseClaw installer (install.ps1) again to repair the per-user install.",
        };

        return $"The DefenseClaw launcher reported: \"{Line}\". {fix}";
    }
}
