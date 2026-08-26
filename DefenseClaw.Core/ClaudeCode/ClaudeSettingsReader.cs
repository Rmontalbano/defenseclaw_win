using System.Text.Json;
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

    /// <param name="settingsFilePath">
    /// Defaults to <c>%USERPROFILE%\.claude\settings.json</c>. Injectable so tests never
    /// touch the operator's live Claude Code install.
    /// </param>
    public ClaudeSettingsReader(string? settingsFilePath = null)
    {
        SettingsPath = settingsFilePath ?? DefaultSettingsPath();
    }

    public string SettingsPath { get; }

    public static string DefaultSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "settings.json");

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
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath), ParseOptions);
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
    /// The documented one-liner that flips settings.json to match the gateway. Displayed
    /// and copyable; the app never runs it, mirroring <c>GatewaySnapshot.InitCommand</c>.
    /// The operator stays the only writer of a file the installer already contests.
    /// </summary>
    public string RemediationCommand =>
        $"""$s = "$env:USERPROFILE\.claude\settings.json"; $j = Get-Content $s -Raw | ConvertFrom-Json; $j.env.{ClaudeSettingsReader.FailModeVariableName} = '{GatewayFailMode}'; $j | ConvertTo-Json -Depth 32 | Set-Content $s -Encoding utf8""";

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
