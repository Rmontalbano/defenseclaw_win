using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.IO;

namespace DefenseClaw.Core.Gateway.Durability;

/// <summary>
/// An agent program the older install's setup sealed (path and SHA-256 in its <c>hook_contract_lock.json</c>) that is no longer that file: Codex
/// updated itself, or moved. The gateway of DefenseClaw 1.0.4 and later does not start without the program it set up.
/// </summary>
/// <param name="Connector">The connector, e.g. <c>codex</c>.</param>
/// <param name="Label">"Codex 0.47.0" when the lock names the version, else "Codex".</param>
/// <param name="Executable">The sealed path.</param>
/// <param name="Missing">True when the sealed path is gone; false when it is there with other bytes.</param>
public sealed record StaleAgentProgram(string Connector, string Label, string Executable, bool Missing);

/// <summary>
/// The check install.ps1 1.0.5 makes before a rollback changes anything (<c>Get-StaleAgentEvidence</c> and <c>Test-FreshAgentSelection</c>), read the same
/// way so the app can say up front what the installer will do (CUST-399). <see cref="AgentExecutablePin"/> only asks whether the pinned path exists; this
/// also compares the file's SHA-256 with the sealed one. Reads only; a missing, unreadable or odd file is "nothing found", never an error.
/// </summary>
public static class RollbackAgentEvidence
{
    /// <summary>The connectors install.ps1 checks (its <c>foreach ($name in @("codex"))</c>).</summary>
    public static IReadOnlyList<string> Connectors { get; } = ["codex"];

    /// <summary>The value of <c>agent_executable_source</c> (and of a selection's <c>source</c>) the check considers.</summary>
    public const string SetupSelected = "setup-selected";

    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>A selection must stay valid this long past now to count as fresh (the installer's <c>AddMinutes(5)</c>).</summary>
    private static readonly TimeSpan FreshMargin = TimeSpan.FromMinutes(5);

    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex CodexCliPrefix = new(@"^codex-cli\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The sealed agent programs under <paramref name="dataDirectory"/> (the older install's data: <c>previous\data</c>) that changed since their setup and
    /// have no fresh setup selection beside them. Empty when there is nothing to say.
    /// </summary>
    /// <param name="exists">Whether a path is a file; the real file system when null.</param>
    /// <param name="sha256">A file's SHA-256 in lower-case hex, or null when it cannot be read; the real file when null.</param>
    public static IReadOnlyList<StaleAgentProgram> Find(
        string dataDirectory,
        DateTimeOffset now,
        Func<string, bool>? exists = null,
        Func<string, string?>? sha256 = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        exists ??= File.Exists;
        sha256 ??= HashFile;

        if (!TryParse(Path.Combine(dataDirectory, AgentExecutablePin.LockFileName), out var lockRoot) ||
            Member(lockRoot, "connectors") is not { ValueKind: JsonValueKind.Object } connectors)
        {
            return Array.Empty<StaleAgentProgram>();
        }

        var found = new List<StaleAgentProgram>();
        foreach (var name in Connectors)
        {
            if (Member(connectors, name) is not { ValueKind: JsonValueKind.Object } entry ||
                !string.Equals(Text(entry, "agent_executable_source"), SetupSelected, StringComparison.Ordinal))
            {
                continue;
            }

            var exe = Text(entry, "agent_executable");
            var want = Text(entry, "agent_executable_sha256").ToLowerInvariant();
            if (exe.Length == 0 || !Sha256Hex.IsMatch(want))
            {
                continue;
            }

            var version = CodexCliPrefix.Replace(Text(entry, "raw_agent_version"), string.Empty);
            var label = version.Length > 0 ? "Codex " + version : "Codex";
            if (!Safe(exists, exe))
            {
                if (!IsFreshSelection(dataDirectory, name, now, exists, sha256))
                {
                    found.Add(new StaleAgentProgram(name, label, exe, Missing: true));
                }

                continue;
            }

            // A program that cannot be hashed is skipped, as the installer skips it.
            if (sha256(exe) is not { } have)
            {
                continue;
            }

            if (!string.Equals(have, want, StringComparison.OrdinalIgnoreCase) && !IsFreshSelection(dataDirectory, name, now, exists, sha256))
            {
                found.Add(new StaleAgentProgram(name, label, exe, Missing: false));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether <c>agent_selection.json</c> holds a setup selection of <paramref name="connector"/> that has not expired (with five minutes to spare) and
    /// still names the program on disk, by path and SHA-256: the older install's gateway seals it at its next start, so the program is not stale.
    /// </summary>
    private static bool IsFreshSelection(string dataDirectory, string connector, DateTimeOffset now, Func<string, bool> exists, Func<string, string?> sha256)
    {
        if (!TryParse(Path.Combine(dataDirectory, AgentExecutablePin.SelectionFileName), out var root) ||
            Member(Member(root, "selections"), connector) is not { ValueKind: JsonValueKind.Object } selection ||
            !string.Equals(Text(selection, "source"), SetupSelected, StringComparison.Ordinal))
        {
            return false;
        }

        var exe = Text(selection, "executable");
        var want = Text(selection, "sha256").ToLowerInvariant();
        if (!DateTimeOffset.TryParse(Text(selection, "expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expires) ||
            expires <= now + FreshMargin || exe.Length == 0 || !Safe(exists, exe))
        {
            return false;
        }

        return sha256(exe) is { } have && string.Equals(have, want, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Safe(Func<string, bool> exists, string path)
    {
        try
        {
            return exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A member by name, any case (PowerShell's property lookup is case-insensitive); null when absent or the parent is not an object.</summary>
    private static JsonElement? Member(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string Text(JsonElement parent, string name) =>
        Member(parent, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;

    private static bool TryParse(string path, out JsonElement root)
    {
        root = default;
        try
        {
            var text = SharedFile.ReadAllText(path, MaxBytes);
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}
