using System.Text.Json;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.AiRuntime;

/// <summary>Whether one grant is in place, as <c>agent discovery runtime permissions --json</c> could tell.</summary>
public enum AiRuntimeGrantState
{
    /// <summary>In place, or nothing is needed.</summary>
    Granted,

    /// <summary>Checked from this process and not in place.</summary>
    Missing,

    /// <summary>
    /// This process cannot verify it from where it runs (the Windows Security audit policy needs an elevated reader). Not a failure:
    /// reporting it as missing would send the operator to change something that was already right.
    /// </summary>
    Unknown,

    /// <summary>Nothing is asking for it (DNS capture is turned off), so it is not a gap.</summary>
    Off,
}

/// <summary>One thing a plane needs, and how to give it.</summary>
/// <param name="Plane">What it is for: <c>agent actions (C), command lines</c>.</param>
/// <param name="Needs">What is needed: <c>elevated token AND Advanced Audit Policy</c>.</param>
/// <param name="Why">Why the plane needs it.</param>
/// <param name="How">The runtime's own words on how to grant it; empty when nothing is needed.</param>
/// <param name="Granted">True, false, or null when this process cannot tell.</param>
/// <param name="IsOff">The runtime marked it as not asked for.</param>
public sealed record AiRuntimeGrant(string Plane, string Needs, string Why, string How, bool? Granted, bool IsOff)
{
    /// <summary>The state; "off" wins over the check, as in the CLI.</summary>
    public AiRuntimeGrantState State => IsOff
        ? AiRuntimeGrantState.Off
        : Granted switch
        {
            true => AiRuntimeGrantState.Granted,
            false => AiRuntimeGrantState.Missing,
            _ => AiRuntimeGrantState.Unknown,
        };

    /// <summary>The word on the row's chip.</summary>
    public string StateText => State switch
    {
        AiRuntimeGrantState.Granted => "granted",
        AiRuntimeGrantState.Missing => "missing",
        AiRuntimeGrantState.Off => "off",
        _ => "unknown",
    };

    /// <summary>
    /// The shell commands inside <see cref="How"/> (<c>auditpol</c> and <c>reg add</c> lines), written out one per line. <b>Copy-only text:</b>
    /// nothing in this app runs them. Empty when <see cref="How"/> holds no command or the grant is already in place.
    /// </summary>
    public IReadOnlyList<string> CommandLines => State is AiRuntimeGrantState.Granted or AiRuntimeGrantState.Off
        ? []
        : AiRuntimeGuidance.CommandLines(How);
}

/// <summary>The runtime's answer to "what does each plane need on this host?" (<c>agent discovery runtime permissions --json</c>).</summary>
/// <param name="Os">The operating system the guidance is for (<c>windows</c>, <c>linux</c>, <c>darwin</c>).</param>
/// <param name="CheckedThisHost">The grants were probed on the machine the runtime runs on, rather than listed for another OS.</param>
/// <param name="Grants">One per need, in the runtime's order.</param>
public sealed record AiRuntimePermissions(string Os, bool CheckedThisHost, IReadOnlyList<AiRuntimeGrant> Grants)
{
    /// <summary>True for the Windows guidance.</summary>
    public bool IsWindows => Os.Equals("windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>How many grants were checked and found missing.</summary>
    public int MissingCount => Grants.Count(g => g.State == AiRuntimeGrantState.Missing);

    /// <summary>How many this process could not verify.</summary>
    public int UnknownCount => Grants.Count(g => g.State == AiRuntimeGrantState.Unknown);

    /// <summary>Every copy-only command line of every open grant, once each, in order.</summary>
    public IReadOnlyList<string> CommandLines => Grants.SelectMany(g => g.CommandLines).Distinct(StringComparer.Ordinal).ToArray();
}

/// <summary>The outcome of reading the permissions output.</summary>
/// <param name="Permissions">Set when the output was the document the CLI prints.</param>
/// <param name="Message">One sentence when it was not.</param>
public sealed record AiRuntimePermissionsRead(AiRuntimePermissions? Permissions, string Message)
{
    /// <summary>True when <see cref="Permissions"/> is set.</summary>
    public bool IsOk => Permissions is not null;
}

/// <summary>
/// Reads <c>defenseclaw agent discovery runtime permissions --json</c>: <c>{"os", "checked_this_host", "grants": [{"plane", "needs", "why",
/// "how", "probe", "granted", "off"}]}</c> (<c>runtime_permissions</c> in <c>cli/defenseclaw/commands/cmd_agent.py</c>). Pure.
/// </summary>
public static class AiRuntimePermissionsReader
{
    /// <summary>The most grants kept; the CLI prints about six.</summary>
    public const int MaxGrants = 32;

    /// <summary>Reads the CLI's output. A banner line before the JSON is skipped; anything that is not the document is a sentence, not a guess.</summary>
    public static AiRuntimePermissionsRead Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new AiRuntimePermissionsRead(null, "The command printed nothing.");
        }

        var start = output.IndexOf('{', StringComparison.Ordinal);
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return new AiRuntimePermissionsRead(null, "The command's output is not JSON.");
        }

        try
        {
            using var document = JsonDocument.Parse(output[start..(end + 1)]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("grants", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return new AiRuntimePermissionsRead(null, "The command's output has no list of grants.");
            }

            var grants = new List<AiRuntimeGrant>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || grants.Count >= MaxGrants)
                {
                    continue;
                }

                grants.Add(new AiRuntimeGrant(
                    Plane: AiRuntimeJson.Clean(AiRuntimeJson.Str(item, "plane"), AiRuntimeJson.NameLimit),
                    Needs: AiRuntimeJson.Prose(AiRuntimeJson.Str(item, "needs"), AiRuntimeJson.TextLimit),
                    Why: AiRuntimeJson.Prose(AiRuntimeJson.Str(item, "why"), AiRuntimeJson.TextLimit),
                    How: AiRuntimeJson.Prose(AiRuntimeJson.Str(item, "how"), AiRuntimeJson.TextLimit),
                    Granted: AiRuntimeJson.TriState(item, "granted"),
                    IsOff: AiRuntimeJson.Flag(item, "off")));
            }

            return new AiRuntimePermissionsRead(
                new AiRuntimePermissions(
                    AiRuntimeJson.Clean(AiRuntimeJson.Str(root, "os"), 32).ToLowerInvariant(),
                    AiRuntimeJson.Flag(root, "checked_this_host"),
                    grants),
                string.Empty);
        }
        catch (JsonException)
        {
            return new AiRuntimePermissionsRead(null, "The command's output is not valid JSON.");
        }
    }
}

/// <summary>
/// Turns the runtime's "how to grant it" sentences into command lines to copy. The runtime words them for a person
/// (<c>auditpol /set /subcategory:"Process Creation" /success:enable /failure:enable   (also User Account Management, Sensitive Privilege
/// Use)</c>); this writes out the three commands that sentence means, and the <c>reg add</c> line as it stands.
/// <para>
/// <b>Strict on purpose.</b> A line is produced only for the exact shapes below, with a narrow alphabet for every variable part. The lines
/// are shown for an operator to paste into an elevated prompt; text that came from outside must not be able to turn one into something
/// else. Anything that does not match (<c>run the gateway elevated</c>, <c>set an audit ACE on the credential paths you care about</c>) is
/// guidance, not a command, and stays a sentence. The app never runs any of this.
/// </para>
/// </summary>
public static partial class AiRuntimeGuidance
{
    private const int TimeoutMilliseconds = 1000;

    [GeneratedRegex(
        @"auditpol\s+/set\s+/subcategory:""(?<name>[A-Za-z][A-Za-z ]{0,63})""\s+/success:enable\s+/failure:enable(?:\s*\(\s*also\s+(?<more>[A-Za-z][A-Za-z ,]{0,255})\))?",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex AuditPolLine();

    [GeneratedRegex(
        @"reg\s+add\s+(?<key>HKLM\\[A-Za-z0-9_.\\\-]{1,200})\s+/v\s+(?<value>[A-Za-z0-9_]{1,80})\s+/t\s+REG_DWORD\s+/d\s+1\s+/f",
        RegexOptions.CultureInvariant,
        TimeoutMilliseconds)]
    private static partial Regex RegAddLine();

    /// <summary>The command lines <paramref name="how"/> spells out, in the order it gives them; empty when it holds none.</summary>
    public static IReadOnlyList<string> CommandLines(string? how)
    {
        if (string.IsNullOrWhiteSpace(how))
        {
            return [];
        }

        var lines = new List<string>();
        try
        {
            foreach (Match match in AuditPolLine().Matches(how))
            {
                var names = new List<string> { match.Groups["name"].Value.Trim() };
                if (match.Groups["more"].Success)
                {
                    names.AddRange(match.Groups["more"].Value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(n => n.Length > 0 && n.All(c => char.IsAsciiLetter(c) || c == ' ')));
                }

                foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    lines.Add($"auditpol /set /subcategory:\"{name}\" /success:enable /failure:enable");
                }
            }

            foreach (Match match in RegAddLine().Matches(how))
            {
                lines.Add($"reg add {match.Groups["key"].Value} /v {match.Groups["value"].Value} /t REG_DWORD /d 1 /f");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Text slow enough to hit the timeout is not a command line anyone should paste.
            return [];
        }

        return lines;
    }
}
