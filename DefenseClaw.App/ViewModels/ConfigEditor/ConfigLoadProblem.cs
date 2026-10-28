using System;
using System.Globalization;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.IO;
using YamlDotNet.Core;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>Why the file on disk is not an editable config (<see cref="ConfigLoadProblem"/>).</summary>
public enum ConfigLoadProblemKind
{
    /// <summary>The file could not be read (locked, no permission, gone mid-read).</summary>
    Unreadable,

    /// <summary>The file was read and is not valid YAML.</summary>
    NotYaml,

    /// <summary>The file was read or sized and this app refuses to parse it (over the size limit, or flow collections nested too deep).</summary>
    Refused,
}

/// <summary>
/// The state the config editor shows when config.yaml on disk cannot be loaded (CUST-377; the Mac app surfaces an unloadable config.yaml the same way,
/// defenseclaw_mac v1.1.31-v1.1.34): what is wrong, where in the file when the parser says, and what the operator can do next - open it in an editor, show it
/// in Explorer, press Reload - instead of an empty or half-built form. The reason is the parser's or the system's own message and never a value from the file.
/// </summary>
/// <param name="Kind">What kind of problem it is.</param>
/// <param name="Title">One line for the heading.</param>
/// <param name="Reason">The parser's or the system's words.</param>
/// <param name="Line">1-based line of the first error, when the parser gave one.</param>
/// <param name="Column">1-based column of the first error, when the parser gave one.</param>
/// <param name="Guidance">What to do about it, and what is and is not possible meanwhile.</param>
public sealed record ConfigLoadProblem(ConfigLoadProblemKind Kind, string Title, string Reason, int? Line, int? Column, string Guidance)
{
    /// <summary><c>Line 12, column 3</c>, or empty when the parser gave no position.</summary>
    public string Location =>
        Line is { } line
            ? string.Create(CultureInfo.InvariantCulture, $"Line {line}") + (Column is { } column ? string.Create(CultureInfo.InvariantCulture, $", column {column}") : string.Empty)
            : string.Empty;

    public bool HasLocation => Line is not null;

    /// <summary>The problem for a read that failed.</summary>
    public static ConfigLoadProblem FromRead(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is FileTooLargeException
            ? new ConfigLoadProblem(
                ConfigLoadProblemKind.Refused,
                "config.yaml is too large to load",
                exception.Message,
                null,
                null,
                "This app does not open a config.yaml this large. Edit it down in your editor, then press Reload. Save stays off until it loads.")
            : new ConfigLoadProblem(
                ConfigLoadProblemKind.Unreadable,
                "config.yaml could not be read",
                exception.Message,
                null,
                null,
                "Another program may have it open, or this account may not be allowed to read it. Close what holds it or fix the permissions, then press Reload. Save stays off until it loads.");
    }

    /// <summary>The problem for a file that was read and did not parse.</summary>
    public static ConfigLoadProblem FromParse(ConfigParseException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // The parser's position, when it has one: YamlException.Start is 1-based, and zero means "no position".
        int? line = null;
        int? column = null;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is YamlException { Start.Line: > 0 } yaml)
            {
                line = (int)yaml.Start.Line;
                column = yaml.Start.Column > 0 ? (int)yaml.Start.Column : null;
                break;
            }
        }

        if (exception.InnerException is null)
        {
            // ConfigYamlGuard's refusal (size or flow depth): no parser ran, so there is no position.
            return new ConfigLoadProblem(
                ConfigLoadProblemKind.Refused,
                "config.yaml is refused by this app",
                exception.Message,
                null,
                null,
                "RAW shows the file as it is. Edit it by hand (here or in your editor) and press Reload; FORM stays off until it loads.");
        }

        return new ConfigLoadProblem(
            ConfigLoadProblemKind.NotYaml,
            "config.yaml is not valid YAML",
            exception.Message,
            line,
            column,
            "RAW shows the file exactly as it is on disk. Fix the line named here and Save, or open the file in your editor and press Reload. FORM is off until the file parses.");
    }
}
