using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.Core.ClaudeCode;

/// <summary>
/// What one read of <c>~/.claude/settings.json</c> told us. Only the one field that can
/// override DefenseClaw's hook contract is lifted out; everything else in that file
/// belongs to Claude Code and is none of our business.
/// </summary>
public sealed record ClaudeSettingsSnapshot
{
    /// <summary>False when the file is simply absent — a fresh box, not a fault.</summary>
    public bool Exists { get; init; }

    /// <summary>
    /// Value of <c>env.DEFENSECLAW_FAIL_MODE</c>, or null when the file, the <c>env</c>
    /// block, or the variable is missing. Not normalized: the raw string is what the hook
    /// binary actually sees, so the UI should show it verbatim.
    /// </summary>
    public string? FailModeOverride { get; init; }

    /// <summary>
    /// Why the read failed, when it did. Null for both a clean parse and an absent file —
    /// the two "nothing to report" cases are distinguished by <see cref="Exists"/>.
    /// </summary>
    public string? ReadError { get; init; }

    public required string SettingsPath { get; init; }
}

/// <summary>
/// Reads the one value in Claude Code's own <c>settings.json</c> that can silently
/// override DefenseClaw's hook fail mode.
/// <para>
/// <b>Why this exists.</b> Observed on the 0.8.10 upgrade (2026-07-30): the DefenseClaw
/// Windows Setup installer re-plants <c>"DEFENSECLAW_FAIL_MODE": "closed"</c> into the
/// <c>env</c> block of <c>%USERPROFILE%\.claude\settings.json</c> even when
/// <c>~/.defenseclaw/config.yaml</c> says <c>hook_fail_mode: open</c>. The hook binary
/// obeys the environment variable, not the YAML — during that same upgrade a Claude Code
/// tool call was blocked with <c>blocking claude-code tool (fail mode closed): gateway
/// cold start failed</c> while the installer was restarting the gateway. The CLI's
/// <c>doctor</c> and <c>status</c> print the env-var value; the gateway's <c>/status</c>
/// prints the config value, so neither surface alone can show the disagreement.
/// </para>
/// <para>
/// <b>Strictly read-only.</b> The app never writes this file. It is Claude Code's own
/// configuration, the installer already fights over it, and a third writer racing the
/// other two would turn an annoying default into a corrupted one. Remediation is offered
/// as copyable text and nothing else — the same contract as
/// <c>GatewaySnapshot.InitCommand</c>.
/// </para>
/// </summary>
public sealed class ClaudeSettingsReader
{
    /// <summary>The env-var name the hook binary consults; wins over config.yaml.</summary>
    public const string FailModeVariableName = "DEFENSECLAW_FAIL_MODE";

    /// <summary>
    /// Claude Code tolerates comments and trailing commas in its settings file, and
    /// operators hand-edit it, so the strict defaults would report drift as a parse error.
    /// </summary>
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The variable Claude Code documents for relocating its home-directory files: with it set, "Claude Code
    /// then stores your settings, session history, and plugins there instead" of <c>~/.claude</c>.
    /// </summary>
    public const string ConfigDirVariableName = "CLAUDE_CONFIG_DIR";

    /// <param name="settingsFilePath">
    /// Defaults to <c>%CLAUDE_CONFIG_DIR%\settings.json</c> when that variable is set, else
    /// <c>%USERPROFILE%\.claude\settings.json</c>. Injectable so tests never touch the operator's
    /// live Claude Code install.
    /// </param>
    public ClaudeSettingsReader(string? settingsFilePath = null)
    {
        SettingsPath = settingsFilePath ?? DefaultSettingsPath();
    }

    public string SettingsPath { get; }

    public static string DefaultSettingsPath(Func<string, string?>? getEnvironmentVariable = null)
    {
        // A blank value is "not set", as it is for every other variable this app reads.
        var configured = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(ConfigDirVariableName)?.Trim();
        return configured is { Length: > 0 }
            ? Path.Combine(configured, "settings.json")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                "settings.json");
    }

    /// <summary>
    /// Reads the file once. Synchronous on purpose: settings.json is a few hundred bytes
    /// and the poll loop wants the answer inline, not an extra continuation per tick.
    /// </summary>
    public ClaudeSettingsSnapshot Read()
    {
        if (!File.Exists(SettingsPath))
        {
            // No Claude Code settings at all is the quiet, healthy case: nothing is
            // overriding the gateway's stated contract.
            return new ClaudeSettingsSnapshot { SettingsPath = SettingsPath };
        }

        try
        {
            using var document = JsonDocument.Parse(DefenseClaw.Core.IO.SharedFile.ReadAllText(SettingsPath), ParseOptions);
            return new ClaudeSettingsSnapshot
            {
                Exists = true,
                SettingsPath = SettingsPath,
                FailModeOverride = ReadFailMode(document.RootElement),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A half-written file (the installer mid-rewrite) or a locked one must degrade
            // the banner, never the poll loop. File.Exists is re-probed because a
            // FileNotFoundException here means the file vanished between the two calls.
            return new ClaudeSettingsSnapshot
            {
                Exists = File.Exists(SettingsPath),
                SettingsPath = SettingsPath,
                ReadError = ex.Message,
            };
        }
    }

    /// <summary>
    /// Only a JSON string counts. Claude Code passes the <c>env</c> block through to the
    /// child process verbatim, so a boolean or a number there is a typo the hook will
    /// never act on — reporting it as an override would invent drift that does not exist.
    /// </summary>
    private static string? ReadFailMode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("env", out var env) &&
        env.ValueKind == JsonValueKind.Object &&
        env.TryGetProperty(FailModeVariableName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// settings.json and the gateway disagree about the hook fail mode, and the hook obeys
/// settings.json. Produced by <see cref="Evaluate"/>; null means "they agree" or "there
/// was nothing to compare", never "we did not look".
/// </summary>
public sealed record FailModeDrift
{
    /// <summary>Gateway-side value came from an authenticated <c>/status</c> read.</summary>
    public const string StatusSource = "/status";

    /// <summary>Gateway-side value came from config.yaml, because <c>/status</c> was mute.</summary>
    public const string ConfigSource = "config.yaml";

    /// <summary>What <c>env.DEFENSECLAW_FAIL_MODE</c> says — the effective behavior.</summary>
    public required string EnvFailMode { get; init; }

    /// <summary>What DefenseClaw believes the hook contract is. Losing side.</summary>
    public required string GatewayFailMode { get; init; }

    /// <summary><see cref="StatusSource"/> or <see cref="ConfigSource"/>.</summary>
    public required string GatewaySource { get; init; }

    /// <summary><c>observe</c> or <c>enforce</c>, whichever surface could tell us.</summary>
    public string? GuardrailMode { get; init; }

    public required string SettingsPath { get; init; }

    /// <summary>
    /// Observe mode with an effective fail-closed hook: the connector is only meant to
    /// watch, yet a mere gateway restart blocks the very agent it is watching. Observed on
    /// the 0.8.10 upgrade, where a cold-start window produced <c>blocking claude-code tool
    /// (fail mode closed): gateway cold start failed</c> on a box whose config.yaml said
    /// <c>open</c>. This is the pairing worth a Critical row rather than a High one.
    /// </summary>
    public bool IsDangerousPairing =>
        string.Equals(GuardrailMode, "observe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(EnvFailMode, "closed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Placeholder in <see cref="RemediationTemplate"/> for the single-quoted settings.json
    /// path. Substituted by <see cref="RemediationCommand"/>; never appears in its output.
    /// </summary>
    private const string PathPlaceholder = "@@PATH@@";

    /// <summary>Placeholder in <see cref="RemediationTemplate"/> for the fail-mode word.</summary>
    private const string ModePlaceholder = "@@MODE@@";

    /// <summary>
    /// The copyable one-liner, as a template. A raw literal (not interpolated) so PowerShell's
    /// braces and dollar signs need no escaping and the text reads exactly as it will be pasted.
    /// <para>
    /// <b>Why it is written this way — the first version could destroy the file.</b> It was
    /// <c>Get-Content -Raw | ConvertFrom-Json; …; $j | ConvertTo-Json | Set-Content -Encoding utf8</c>.
    /// The reader above tolerates comments and trailing commas (Claude Code does, and operators
    /// hand-edit this file), but <c>ConvertFrom-Json</c> is strict. On such a file the parse
    /// failed, <c>$j</c> stayed <c>$null</c>, and the <c>;</c>-chained line carried on regardless
    /// into <c>$null | ConvertTo-Json | Set-Content</c>. Measured on Windows PowerShell 5.1, that
    /// last pipeline emits nothing for a null input, so the file was left alone (and the operator
    /// was told nothing useful); PowerShell 7 emits the text <c>null</c> for it, which would
    /// replace the operator's settings.json with those four bytes. The path that <i>did</i> run
    /// to the end was no better: <c>ConvertTo-Json</c> reformats the whole file and drops every
    /// comment, and 5.1's <c>Set-Content -Encoding utf8</c> prepends a byte-order mark. So each
    /// property of the current form answers one of those:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>No JSON round-trip.</b> The file is treated as text and only the one
    /// <c>"DEFENSECLAW_FAIL_MODE": "…"</c> pair is replaced by a regular expression, so comments,
    /// key order, indentation, line endings and every other byte survive. Nothing is parsed, so a
    /// file the strict parser would have rejected is no longer a way to lose it. The replacement
    /// keeps the original spacing around the colon (capture group 1).
    /// </description></item>
    /// <item><description>
    /// <b>Exactly one match, or nothing happens.</b> Zero matches (the key is spelled with
    /// unicode escapes, or is gone) and several (a comment or a second block also mentions it)
    /// are both ambiguous, so the command throws before touching anything and says to edit by
    /// hand. The value pattern accepts escaped quotes, so an odd but valid value still matches.
    /// </description></item>
    /// <item><description>
    /// <b><c>$ErrorActionPreference = 'Stop'</c>, inside <c>&amp; { … }</c>.</b> With the default
    /// <c>Continue</c>, a failed read or copy would let the statements after it run against
    /// empty state. Stop aborts the whole line on the first error. The script block gives the
    /// preference and the <c>$s $v $p $t $b</c> variables a child scope, so pasting this into an
    /// interactive prompt neither leaves the session in Stop mode nor leaks variables.
    /// </description></item>
    /// <item><description>
    /// <b>A timestamped backup before the write</b>, made only after the match check passed, so
    /// a refused run leaves no litter. Milliseconds are in the name so two runs in one second
    /// cannot overwrite the pre-edit copy with an already-edited one.
    /// </description></item>
    /// <item><description>
    /// <b>UTF-8 without a byte-order mark</b>, via <c>[IO.File]::WriteAllText</c> with
    /// <c>UTF8Encoding($false)</c>. <c>ReadAllText</c> detects and drops any existing BOM.
    /// </description></item>
    /// </list>
    /// <para>
    /// The path is the one this drift was read from, embedded as a single-quoted literal (so
    /// <c>$</c> and <c>%</c> in a profile name are inert and <c>[IO.File]</c> takes it as a plain
    /// path, never a wildcard), and the mode is restricted to a plain word — see
    /// <see cref="RemediationCommand"/>. ASCII only: this is pasted through terminals that
    /// mangle typographic characters. No newline, so it stays one copyable line.
    /// </para>
    /// </summary>
    private const string RemediationTemplate = """
        & { $ErrorActionPreference = 'Stop'; $s = '@@PATH@@'; $v = '@@MODE@@'; $p = '("DEFENSECLAW_FAIL_MODE"\s*:\s*)"(?:[^"\\]|\\.)*"'; $t = [IO.File]::ReadAllText($s); if ([regex]::Matches($t, $p).Count -ne 1) { throw "Expected exactly one DEFENSECLAW_FAIL_MODE entry in $s. Nothing was changed - edit the file by hand." }; $b = $s + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'); Copy-Item -LiteralPath $s -Destination $b; [IO.File]::WriteAllText($s, [regex]::Replace($t, $p, '${1}"' + $v + '"'), (New-Object Text.UTF8Encoding $false)); Write-Host "Updated $s (backup: $b)" }
        """;

    /// <summary>
    /// What <see cref="RemediationCommand"/> may embed as the fail mode: a plain word such as
    /// <c>open</c> or <c>closed</c>. The value comes from the gateway's <c>/status</c> or from
    /// config.yaml and lands inside a script the operator pastes into a shell, so anything else —
    /// a quote of any kind, a <c>$</c>, a backtick, a newline — is refused rather than escaped.
    /// Anchored with <c>\z</c>, not <c>$</c>, because <c>$</c> also matches before a trailing
    /// newline and <c>"open\n"</c> must not slip through.
    /// </summary>
    private static readonly Regex PlainWord = new(@"^[A-Za-z0-9_.-]{1,32}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// The one-liner that flips settings.json to match the gateway. Displayed and copyable; the
    /// app never runs it, mirroring <c>GatewaySnapshot.InitCommand</c>. The operator stays the
    /// only writer of a file the installer already contests.
    /// <para>
    /// It edits <see cref="SettingsPath"/> as text — a single targeted replacement after a
    /// backup, aborting unless the key occurs exactly once — rather than parsing and
    /// re-serializing the JSON. <see cref="RemediationTemplate"/> explains why: the earlier
    /// parse-and-rewrite form could replace a hand-edited settings.json with the literal
    /// <c>null</c>.
    /// </para>
    /// <para>
    /// When <see cref="GatewayFailMode"/> is not a plain word (see <see cref="PlainWord"/>) the
    /// result is a PowerShell comment saying so instead of a command: pasting it changes nothing.
    /// The value itself is deliberately not echoed into that comment, since a newline in it would
    /// end the comment and begin executable text.
    /// </para>
    /// </summary>
    public string RemediationCommand =>
        PlainWord.IsMatch(GatewayFailMode)
            // Mode first, path last: the mode is a validated plain word and cannot contain a
            // placeholder, whereas a path is free text and must not be rescanned for one.
            ? RemediationTemplate
                .Replace(ModePlaceholder, GatewayFailMode, StringComparison.Ordinal)
                .Replace(PathPlaceholder, PowerShellSingleQuoted(SettingsPath), StringComparison.Ordinal)
            : "# No command generated: the gateway reported a hook fail mode that is not a plain word, " +
              $"so it is not safe to paste into a script. Set env.{ClaudeSettingsReader.FailModeVariableName} " +
              "in Claude Code's settings.json by hand.";

    /// <summary>
    /// Makes <paramref name="value"/> safe to place between single quotes in PowerShell. Inside
    /// a single-quoted string the only special character is the quote itself, doubled to escape
    /// it — but PowerShell treats the typographic single quotes (U+2018, U+2019, U+201A, U+201B)
    /// as quote characters too, so each of those is doubled as well. Otherwise a profile folder
    /// named with an apostrophe would end the string early.
    /// </summary>
    private static string PowerShellSingleQuoted(string value) => Cli.PowerShellQuoting.SingleQuoted(value);

    /// <summary>
    /// Compares the env override against whatever the gateway will admit to.
    /// <para>
    /// <paramref name="runtime"/> is preferred because <c>/status</c> describes the hook
    /// contract actually loaded. <paramref name="configured"/> is the fallback that
    /// matters most: <c>/status</c> is unavailable exactly when the gateway is down, which
    /// is precisely when a fail-closed hook bites.
    /// </para>
    /// </summary>
    /// <returns>Null when there is no override, no gateway-side value, or the two agree.</returns>
    public static FailModeDrift? Evaluate(
        ClaudeSettingsSnapshot settings,
        ConnectorMode? runtime,
        GuardrailConnectorSettings? configured)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // No override means the hook falls through to the gateway's own contract, so
        // there is nothing that could drift.
        if (string.IsNullOrWhiteSpace(settings.FailModeOverride))
        {
            return null;
        }

        var envFailMode = settings.FailModeOverride.Trim();

        string? gatewayFailMode = null;
        string? gatewaySource = null;

        if (runtime?.HookFailMode is { } fromStatus && !string.IsNullOrWhiteSpace(fromStatus))
        {
            gatewayFailMode = fromStatus.Trim();
            gatewaySource = StatusSource;
        }
        else if (configured?.HookFailMode is { } fromConfig && !string.IsNullOrWhiteSpace(fromConfig))
        {
            gatewayFailMode = fromConfig.Trim();
            gatewaySource = ConfigSource;
        }

        if (gatewayFailMode is null || gatewaySource is null)
        {
            return null;
        }

        if (string.Equals(envFailMode, gatewayFailMode, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new FailModeDrift
        {
            EnvFailMode = envFailMode,
            GatewayFailMode = gatewayFailMode,
            GatewaySource = gatewaySource,
            GuardrailMode = runtime?.GuardrailMode ?? configured?.Mode,
            SettingsPath = settings.SettingsPath,
        };
    }
}
