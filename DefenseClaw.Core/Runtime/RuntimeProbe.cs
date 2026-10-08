using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.Core.Runtime;

/// <summary>
/// The text the read-only probes returned. A null screen is a probe that was not run or did not answer; it is never treated as an
/// empty command list.
/// </summary>
/// <param name="VersionJson">Stdout of <c>defenseclaw --version-json</c>.</param>
/// <param name="RootHelp"><c>defenseclaw --help</c>.</param>
/// <param name="SetupHelp"><c>defenseclaw setup --help</c>.</param>
/// <param name="GuardrailHelp"><c>defenseclaw guardrail --help</c>.</param>
/// <param name="ConfigHelp"><c>defenseclaw config --help</c>.</param>
/// <param name="SandboxHelp"><c>defenseclaw sandbox --help</c>, probed only when the root lists <c>sandbox</c>.</param>
/// <param name="AcpHelp"><c>defenseclaw acp --help</c>, probed only when the root lists <c>acp</c>.</param>
/// <param name="RedactionHelp"><c>defenseclaw setup redaction --help</c>, probed only when <c>setup</c> lists <c>redaction</c>.</param>
public sealed record RuntimeProbeScreens(
    string? VersionJson,
    string? RootHelp,
    string? SetupHelp,
    string? GuardrailHelp,
    string? ConfigHelp,
    string? SandboxHelp,
    string? AcpHelp,
    string? RedactionHelp);

/// <summary>
/// Turns probe text into an identity and capability flags. Pure: no process, no clock, no file.
/// <para>
/// <b>What a marker is.</b> A capability is present when the relevant <c>--help</c> screen lists specific subcommands in its
/// <c>Commands:</c> block — the same test the Mac app applies to the setup and discovery-runtime screens. The markers were
/// chosen from the 309 help screens captured from source commit 95159fd and checked against the installed 0.8.10: each is
/// absent on 0.8.10 and present at the pin. Where 0.8.10 has a command of the same name with different contents (its
/// <c>sandbox</c> group is the old Linux-only standalone mode, with only <c>init</c> and <c>setup</c>), the marker is a
/// subcommand only the newer one has.
/// </para>
/// <para>
/// The command list parser accepts lowercase names only and ignores wrapped description lines, and only the column-0
/// <c>Commands:</c> heading counts, so the indented "Commands:" inside 0.8.10's sandbox description is not mistaken for it.
/// </para>
/// </summary>
public static class RuntimeProbe
{
    internal static readonly string[] PolicyModelMarkers = ["protection"];
    internal static readonly string[] AcpMarkers = ["setup", "adopt"];
    internal static readonly string[] RedactionSetupMarkers = ["redaction"];
    internal static readonly string[] RedactionMarkers = ["apply", "bucket", "defaults", "destination", "profile", "remove-all", "route", "status"];
    internal static readonly string[] SandboxMarkers = ["pack", "approvals", "doctor"];
    internal static readonly string[] ConfigMarkers = ["get"];
    internal static readonly string[] RegistryMarkers = ["amp", "devin", "kiro"];

    /// <summary>The version at which the runtime reports configuration schema 8 (the pin self-reports 1.0.0).</summary>
    internal static readonly Version Schema8Version = new(1, 0, 0);

    /// <summary>
    /// The subcommand names a Click help screen lists under its column-0 <c>Commands:</c> heading. Empty when there is no such
    /// heading (a leaf command, or text that is not help at all).
    /// </summary>
    public static IReadOnlySet<string> ParseCommands(string? help)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(help))
        {
            return names;
        }

        var inBlock = false;
        foreach (var raw in help.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inBlock)
            {
                inBlock = line.TrimEnd() == "Commands:";
                continue;
            }

            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                break;
            }

            // The name column sits at two spaces (three at most); a wrapped description is indented further.
            var indent = line.Length - line.TrimStart().Length;
            if (indent > 3)
            {
                continue;
            }

            var token = line.TrimStart().Split([' ', '\t'], 2)[0];
            if (IsCommandName(token))
            {
                _ = names.Add(token);
            }
        }

        return names;
    }

    private static bool IsCommandName(string token)
    {
        if (token.Length == 0 || token[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in token)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses <c>--version-json</c> output: the first line that is a JSON object with a non-blank string <c>version</c>.
    /// Stdout and stderr arrive combined, so other lines are skipped. Null for anything else.
    /// </summary>
    public static RuntimeIdentity? ParseVersionJson(string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("version", out var version) ||
                    version.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(version.GetString()))
                {
                    continue;
                }

                var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString())
                    ? n.GetString()!.Trim()
                    : "defenseclaw-cli";
                int? schema = root.TryGetProperty("schema_version", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var parsedSchema)
                    ? parsedSchema
                    : null;

                return new RuntimeIdentity(name, version.GetString()!.Trim(), schema, source);
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    /// <summary>The leading <c>major.minor[.patch]</c> of a version string (a leading <c>v</c> and any suffix are ignored).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] is 'v' or 'V'))
        {
            span = span[1..];
        }

        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == '.'))
        {
            end++;
        }

        var parts = span[..end].ToString().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 4)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = numbers.Length switch
        {
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    /// <summary>
    /// Decides what the screens say. Unknown (and so every capability absent) when the version document did not parse, or when
    /// neither the root nor the setup screen could be read — a runtime that cannot list its own commands cannot be judged.
    /// </summary>
    public static RuntimeSnapshot Evaluate(RuntimeProbeScreens screens, string source, string? fingerprint, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(screens);

        var identity = ParseVersionJson(screens.VersionJson, source);
        if (identity is null)
        {
            return RuntimeSnapshot.Failed(
                "The runtime did not report a version (defenseclaw --version-json gave no usable answer).", fingerprint, at);
        }

        if (screens.RootHelp is null && screens.SetupHelp is null)
        {
            return RuntimeSnapshot.Failed(
                $"The runtime reports {identity.Display} but its command list could not be read.", fingerprint, at);
        }

        var root = ParseCommands(screens.RootHelp);
        var setup = ParseCommands(screens.SetupHelp);
        var guardrail = ParseCommands(screens.GuardrailHelp);
        var config = ParseCommands(screens.ConfigHelp);
        var sandbox = ParseCommands(screens.SandboxHelp);
        var acp = ParseCommands(screens.AcpHelp);
        var redaction = ParseCommands(screens.RedactionHelp);

        var present = new List<RuntimeCapability>();
        var notes = new List<string>();

        if (HasAll(guardrail, PolicyModelMarkers))
        {
            present.Add(RuntimeCapability.PolicyModel);
        }

        if (HasAll(acp, AcpMarkers))
        {
            present.Add(RuntimeCapability.AcpGuard);
        }

        if (HasAll(setup, RedactionSetupMarkers) && HasAll(redaction, RedactionMarkers))
        {
            present.Add(RuntimeCapability.RedactionAdvanced);
        }

        if (HasAll(sandbox, SandboxMarkers))
        {
            present.Add(RuntimeCapability.Sandbox);

            // The screen says where sandboxes run. A runtime that names Linux and macOS and not Windows is telling us the
            // commands exist but the machine cannot use them; the panel decides what to do with that, About just says it.
            if (screens.SandboxHelp is { } text &&
                !text.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("Linux", StringComparison.Ordinal))
            {
                notes.Add("Sandboxes: the runtime's help says they run on Linux and macOS only, so they may not work on this PC.");
            }
        }

        if (HasAll(config, ConfigMarkers) && identity.ParsedVersion is { } parsed && parsed >= Schema8Version)
        {
            present.Add(RuntimeCapability.CanonicalSchema8);
        }

        if (HasAll(setup, RegistryMarkers))
        {
            present.Add(RuntimeCapability.TuiRegistry);
        }

        // A screen the probe wanted but did not get is worth a line: the capability it would have shown is absent for want of evidence.
        AddMissing(notes, screens.GuardrailHelp, "guardrail --help");
        AddMissing(notes, screens.ConfigHelp, "config --help");
        if (root.Contains("acp"))
        {
            AddMissing(notes, screens.AcpHelp, "acp --help");
        }

        if (root.Contains("sandbox"))
        {
            AddMissing(notes, screens.SandboxHelp, "sandbox --help");
        }

        if (setup.Contains("redaction"))
        {
            AddMissing(notes, screens.RedactionHelp, "setup redaction --help");
        }

        return new RuntimeSnapshot(identity, new RuntimeCapabilities(true, present, setup, notes), null, fingerprint, at);
    }

    private static bool HasAll(IReadOnlySet<string> commands, string[] required) =>
        commands.Count > 0 && required.All(commands.Contains);

    private static void AddMissing(List<string> notes, string? screen, string label)
    {
        if (screen is null)
        {
            notes.Add($"'{label}' could not be read, so what depends on it is treated as unavailable.");
        }
    }
}
