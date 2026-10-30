using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What DefenseClaw 1.0.5 changed in "What needs attention" (CUST-398). An install lock whose installer is gone no longer blocks the hooks, and doctor
/// removes it (the row's button is a reviewed <c>doctor --fix</c> limited to that one repair); a lock an interrupted install keeps still waits for the
/// installer. The gateway's event-history failure carries the time of the last failed write. And an agent whose own settings turn every hook off (Claude
/// Code's <c>disableAllHooks</c>, Codex's <c>[features] hooks = false</c>) is not guarded, whatever DefenseClaw installed: 1.0.5's <c>status</c> shows it
/// DEGRADED and only in its text, so the app reads the two files itself, read-only, on any runtime.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The id of the stale install lock's button: the review of <see cref="ClearStaleInstallLockArgv"/>.</summary>
    internal const string ClearStaleInstallLockActionId = "clear-stale-install-lock";

    /// <summary>The row title of an install lock 1.0.5 ignores.</summary>
    internal const string StaleInstallLockTitle = "A stopped installer left a lock";

    /// <summary>The row title of a lock an interrupted install keeps for the next installer run (1.0.5).</summary>
    internal const string RecoveryInstallLockTitle = "An interrupted install is waiting for the installer";

    /// <summary>Doctor's repair for an install lock left by an installer that is gone (cmd_doctor.py <c>doctor.install.stale-lock.clear</c> at 1.0.5).</summary>
    internal const string StaleInstallLockFixId = "doctor.install.stale-lock.clear";

    /// <summary>
    /// <c>doctor --fix --yes --fix-id doctor.install.stale-lock.clear</c>: the repair doctor names for a stale lock, and only it. A blanket <c>--fix --yes</c>
    /// would also apply every other safe repair, some of which start or restart the gateway.
    /// </summary>
    internal static readonly string[] ClearStaleInstallLockArgv = { "doctor", "--fix", "--yes", "--fix-id", StaleInstallLockFixId };

    /// <summary>What Codex's own remediation for hooks turned off says to run (hook_integrity.py); text to copy, never run by the app.</summary>
    internal const string CodexEnableHooksCommand = "codex features enable hooks";

    /// <summary>Where a <c>/health</c> build puts the time of the last failed event-history write (health.go, 1.0.5).</summary>
    private const string EventHistoryLastFailureAtKey = "event_history_last_failure_at";

    /// <summary>The agent settings that turn hooks off, as of the last read; nothing while nothing has been read.</summary>
    /// <param name="ClaudeSettingsPath">Claude Code's settings.json when it sets <c>disableAllHooks: true</c>; else null.</param>
    /// <param name="CodexConfigPath">Codex's config.toml when it turns hooks off; else null.</param>
    /// <param name="CodexKey">The key that does it there (<c>hooks</c> or <c>codex_hooks</c>).</param>
    internal readonly record struct AgentHookSwitches(string? ClaudeSettingsPath, string? CodexConfigPath, string? CodexKey);

    private AgentHookSwitches _hookSwitches;

    private int _hookSwitchesReading;

    /// <summary>The last agent hook-switch read (a test reads it).</summary>
    internal AgentHookSwitches LastHookSwitches => _hookSwitches;

    // ------------------------------------------------------------------ install lock (1.0.5)

    /// <summary>A lock whose installer is gone and that no interrupted install keeps: 1.0.5's hooks ignore it, and doctor removes it.</summary>
    private AttentionRow StaleInstallLockRow(InstallLockFinding found, string? blocked)
    {
        var holder = found.HolderProcessId is { } pid
            ? $" It names process {pid.ToString(CultureInfo.InvariantCulture)}, which is no longer running."
            : string.Empty;
        return new AttentionRow
        {
            Title = StaleInstallLockTitle,
            Detail = $"An install that stopped left its lock at {found.Path}.{holder} Its installer is gone, so DefenseClaw's hooks ignore the lock: " +
                     "it does not block your agents. Doctor removes it: review the command and run it from here.",
            SeverityKey = "Medium",
            Actions = new AttentionActions(new[]
            {
                new AttentionAction(
                    ClearStaleInstallLockActionId,
                    "Remove the lock",
                    blocked ?? "Opens a review of defenseclaw doctor --fix limited to this one repair; nothing runs until you confirm.",
                    Enabled: blocked is null),
            }),
        };
    }

    /// <summary>
    /// A lock an interrupted install keeps after its installer is gone: 1.0.5 holds no tool call for it (calls follow the gateway-outage rule), but no hook
    /// starts the gateway until the installer runs again, which finishes or undoes that install. The button is the installer, as on 1.0.4.
    /// </summary>
    private AttentionRow RecoveryInstallLockRow(InstallLockFinding found, string? blocked) => new()
    {
        Title = RecoveryInstallLockTitle,
        Detail = $"An install stopped part-way and left its lock at {found.Path}, kept by {found.RecoveryRecord}. Its installer is gone, so agent calls are " +
                 "not held for it: they follow the gateway-outage rule. But no hook starts the gateway until the installer runs again. " +
                 "Running the installer again finishes or undoes that install and clears the lock: open it from here, review the command, and let it finish.",
        SeverityKey = "High",
        Actions = new AttentionActions(new[]
        {
            new AttentionAction(
                OpenInstallerActionId,
                "Open the installer",
                blocked ?? "Opens the Updates window: the installer is downloaded, verified and shown in a review before anything runs.",
                Enabled: blocked is null),
        }),
    };

    /// <summary>"Remove the lock": the review of <see cref="ClearStaleInstallLockArgv"/>. Does nothing unless the last read found a stale lock.</summary>
    private void ClearStaleInstallLock()
    {
        if (_installLock is not { IsStale: true } found)
        {
            return;
        }

        Review.Open(
            "Remove the stale install lock?",
            $"Runs doctor's repair for an install lock left by an installer that is no longer running, and no other repair. Doctor checks again that the " +
            $"installer is gone and that no interrupted install keeps the lock, then removes {found.Path}. A lock an installer has taken over meanwhile stays.",
            new[]
            {
                new DiscoverStep(ClearStaleInstallLockArgv, "Remove the install lock the stopped installer left.", CommandTier.StateChanging),
            },
            onFinished: _ => RefreshAfterInstallLockFixAsync(),
            restartsGateway: CommandReview.RestartsGatewayFor(ClearStaleInstallLockArgv),
            primaryText: "Remove the lock");
    }

    private async Task RefreshAfterInstallLockFixAsync()
    {
        await RefreshAfterActionAsync().ConfigureAwait(true);
        await RefreshInstallLockAsync(ActiveToken).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ event history (1.0.5)

    /// <summary>
    /// The event-history failure (<see cref="FindFailure"/>) with, when the gateway reports it (<c>event_history_last_failure_at</c>, 1.0.5), the time of the
    /// last failed write: <c>sqlite_write_failed · last failure 14:03:22</c>. Null when there is no failure; a build without the time reads as before.
    /// </summary>
    internal static string? EventHistoryFailureText(GatewayHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return FindFailure(health) is not { } failure
            ? null
            : FindFailureTime(health) is { } at ? failure + " · last failure " + FormatResultTime(at) : failure;
    }

    /// <summary><c>event_history_last_failure_at</c> (RFC 3339), looked for where the builds put the failure itself; null when absent or not a time.</summary>
    internal static DateTimeOffset? FindFailureTime(GatewayHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        JsonElement? found = null;
        if (health.Telemetry?.Details is { ValueKind: JsonValueKind.Object } details && details.TryGetProperty(EventHistoryLastFailureAtKey, out var inDetails))
        {
            found = inDetails;
        }
        else if (health.Telemetry?.AdditionalData is { } block && block.TryGetValue(EventHistoryLastFailureAtKey, out var inBlock))
        {
            found = inBlock;
        }
        else if (health.AdditionalData is { } top && top.TryGetValue(EventHistoryLastFailureAtKey, out var inTop))
        {
            found = inTop;
        }

        return found is { ValueKind: JsonValueKind.String } value &&
               DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
    }

    // ------------------------------------------------------------------ agent settings that turn hooks off

    /// <summary>
    /// Reads Claude Code's settings.json and Codex's config.toml for the switch that turns every hook off, once per metrics read (15 s while the panel is
    /// active; two small files, read-only, size-capped), and only for a connector that is active. A managed installation is not asked: its hooks sit in
    /// machine policy these settings cannot turn off (hook_integrity.py). Rebuilds the list when the answer changed. Never throws.
    /// </summary>
    internal async Task RefreshAgentHookSwitchesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _hookSwitchesReading, 1) == 1)
        {
            return;
        }

        try
        {
            var managed = Services.Installation.Context.IsManaged;
            var claude = !managed && IsActiveConnector("claudecode") ? Services.ClaudeSettings : null;
            var codex = !managed && IsActiveConnector("codex") ? Services.CodexConfig : null;
            var found = await Task.Run(
                () =>
                {
                    var settings = claude?.Read();
                    var config = codex?.Read();
                    return new AgentHookSwitches(
                        settings is { DisableAllHooks: true } ? settings.SettingsPath : null,
                        config is { HooksOff: true } ? config.ConfigPath : null,
                        config?.DisabledKey);
                },
                cancellationToken).ConfigureAwait(true);

            if (found != _hookSwitches)
            {
                _hookSwitches = found;
                BuildAttention(_snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // The panel went away mid-read; the next activation reads again.
        }
        finally
        {
            _ = Interlocked.Exchange(ref _hookSwitchesReading, 0);
        }
    }

    /// <summary>True when <paramref name="connector"/> is on the snapshot's roster (config.yaml and the live gateway), in any spelling.</summary>
    private bool IsActiveConnector(string connector) =>
        _snapshot.ActiveConnectors.Any(name => OverviewDetectedConnectors.Normalize(name) == connector);

    /// <summary>Adds the 1.0.5 rows that are not the install lock's. Called once from <see cref="BuildAttention"/>, after the 1.0.4 rows.</summary>
    private void AppendRuntime105Attention(List<AttentionRow> rows)
    {
        // The read is per poll; the connector may have left the roster (or the installation become managed) since: only what is still true is drawn.
        if (Services.Installation.Context.IsManaged)
        {
            return;
        }

        if (_hookSwitches.ClaudeSettingsPath is { } settings && IsActiveConnector("claudecode"))
        {
            rows.Add(new AttentionRow
            {
                Title = "Claude Code: its settings turn every hook off",
                Detail = $"{settings} sets {ClaudeSettingsReader.DisableAllHooksName} to true, so Claude Code runs none of its hooks and DefenseClaw is not guarding " +
                         $"its tool calls. Remove {ClaudeSettingsReader.DisableAllHooksName} from that file (or set it to false), then restart Claude Code. " +
                         "The app never edits it.",
                SeverityKey = "High",
            });
        }

        if (_hookSwitches.CodexConfigPath is { } config && IsActiveConnector("codex"))
        {
            rows.Add(new AttentionRow
            {
                Title = "Codex: its config turns hooks off",
                Detail = $"{config} turns Codex's hooks off ([features] {_hookSwitches.CodexKey} = false), so Codex runs no hook and DefenseClaw is not guarding " +
                         "its tool calls. Run the command below, then restart Codex. The app never runs it or edits the file.",
                SeverityKey = "High",
                Command = CodexEnableHooksCommand,
            });
        }
    }
}
