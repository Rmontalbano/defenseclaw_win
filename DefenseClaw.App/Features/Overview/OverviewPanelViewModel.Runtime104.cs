using System.Diagnostics;
using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.App.Views.Updates;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The rows of "What needs attention" that exist because of DefenseClaw 1.0.4 (CUST-373): what the removed OpenHands and OmniGent connectors left on the
/// host (<c>status --json</c> <c>removed_connector_cleanup</c>), an <c>.install.lock</c> an interrupted install left behind (it keeps every per-user hook
/// blocking), and the hook fail-mode rows under the new rule (a per-user host's outage mode follows the policy tier; a saved <c>closed</c> is not applied,
/// see <see cref="HookFailModeRule"/>). Nothing here runs a command: the cleanup steps are the runtime's text to read, and the install-lock row's button opens
/// the Updates window, where the installer the app already offers is downloaded, verified and reviewed before it runs. Every row is gated by a
/// <see cref="RuntimeCapability"/> or by a field only 1.0.4 sends, so an older runtime looks exactly as before.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The id of the install-lock row's button: opens the Updates window.</summary>
    internal const string OpenInstallerActionId = "open-installer";

    /// <summary>The row title of an install lock nobody holds.</summary>
    internal const string InterruptedInstallTitle = "An interrupted install left a lock";

    /// <summary>The finding of the last <see cref="InterruptedInstallLock"/> read; null when there is none (or while nothing has read yet).</summary>
    private InstallLockFinding? _installLock;

    private int _installLockReading;

    /// <summary>The last install-lock finding (a test reads it).</summary>
    internal InstallLockFinding? LastInstallLock => _installLock;

    /// <summary>True when this runtime follows 1.0.4's per-user hook rules and the installation is not managed: a saved <c>hook_fail_mode</c> is not applied.</summary>
    internal bool SavedFailModeIgnored =>
        HookFailModeRule.SavedValueIgnored(Services.Runtime.Capabilities, Services.Installation.Context.IsManaged);

    /// <summary>Opens the Updates window, where the installer is downloaded, verified and reviewed (the action of the install-lock row).</summary>
    internal Action<AppServices> OpenInstaller { get; set; } = static services => _ = UpdatesWindow.Show(services);

    /// <summary>
    /// Looks for a leftover install lock, once per metrics read (15 s while the panel is active; the lock is a folder stat and one small file). Only a
    /// runtime with <see cref="RuntimeCapability.PerUserHookRules"/> is asked: the hooks of 1.0.3 and earlier never read the lock, so on those it blocks nothing.
    /// Rebuilds the list when the answer changed. Never throws.
    /// </summary>
    internal async Task RefreshInstallLockAsync(CancellationToken cancellationToken)
    {
        if (!Services.Runtime.Capabilities.Has(RuntimeCapability.PerUserHookRules))
        {
            if (_installLock is not null)
            {
                _installLock = null;
                BuildAttention(_snapshot);
            }

            return;
        }

        if (Interlocked.Exchange(ref _installLockReading, 1) == 1)
        {
            return;
        }

        try
        {
            var dataDirectory = Services.Paths.DataDirectory;
            var found = await Task.Run(() => InterruptedInstallLock.Read(dataDirectory, DateTimeOffset.UtcNow, RunningProcessName), cancellationToken).ConfigureAwait(true);
            if (found != _installLock)
            {
                _installLock = found;
                BuildAttention(_snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            // The panel went away mid-read; the next activation reads again.
        }
        finally
        {
            _ = Interlocked.Exchange(ref _installLockReading, 0);
        }
    }

    /// <summary>The name of the process <paramref name="pid"/>, or null when none runs (or it cannot be asked).</summary>
    private static string? RunningProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.ProcessName;
        }
#pragma warning disable CA1031 // A process that vanished or cannot be opened is "not running" for this question.
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>Adds the 1.0.4 rows. Called once from <see cref="BuildAttention"/>, after the fail-mode rows.</summary>
    private void AppendRuntime104Attention(List<AttentionRow> rows)
    {
        AppendRemovedConnectorAttention(rows);
        AppendInterruptedInstallAttention(rows);
    }

    /// <summary>
    /// One row per removed connector that left something behind: the runtime's own list, each item with the step that removes it. The removed connectors'
    /// stubs block every call and some leftovers keep an agent running a hook that no longer exists, so the row is High. Read from <c>status --json</c>
    /// (<see cref="DefenseClawStatus.RemovedConnectorCleanup"/>), which the Overview already runs; a runtime that does not send the field sends no row.
    /// </summary>
    private void AppendRemovedConnectorAttention(List<AttentionRow> rows)
    {
        foreach (var left in _status.RemovedConnectorCleanup)
        {
            var steps = left.Items.Select(static i =>
                $"{i.Path}: {i.What}." + (i.Step.Length > 0 ? " To remove: " + i.Step + "." : string.Empty));

            // 1.0.5 sends its notice with each entry: its teardown removes what an older setup wrote and never blocks, so what is left is information
            // (removed_connectors.py NOTICE). The entry carries it; no capability is needed to know.
            if (left.Notice is { } notice)
            {
                rows.Add(new AttentionRow
                {
                    Title = $"{left.Label}: {left.Items.Count.ToString(CultureInfo.CurrentCulture)} leftover{(left.Items.Count == 1 ? string.Empty : "s")} from an older setup",
                    Detail = notice + " DefenseClaw removed what it could and none of this blocks your agents; what it could not remove is listed here, " +
                             "to remove by hand if you want to. " + string.Join(" ", steps),
                    SeverityKey = "Info",
                });
                continue;
            }

            rows.Add(new AttentionRow
            {
                Title = $"{left.Label} was removed; {left.Items.Count.ToString(CultureInfo.CurrentCulture)} leftover{(left.Items.Count == 1 ? string.Empty : "s")} to remove by hand",
                Detail = $"{left.Label} support was removed in DefenseClaw 1.0.4. DefenseClaw does not delete what it left; remove it yourself. " +
                         string.Join(" ", steps),
                SeverityKey = "High",
            });
        }
    }

    /// <summary>The row for an <c>.install.lock</c> no installer holds, with the button that opens the installer the app already offers.</summary>
    private void AppendInterruptedInstallAttention(List<AttentionRow> rows)
    {
        if (_installLock is not { } found)
        {
            return;
        }

        var holder = found.HolderProcessId is { } pid
            ? $" It names process {pid.ToString(CultureInfo.InvariantCulture)}, which is not a running installer."
            : string.Empty;
        var blocked = InstallationBlockedReason;

        // From 1.0.5 a lock whose installer is proven gone no longer blocks a hook (installLockStale), and doctor --fix removes it unless an interrupted
        // install keeps it (OverviewPanelViewModel.Runtime105.cs). Every other lock, and every lock on 1.0.4, keeps the row below.
        if (Services.Runtime.Capabilities.Has(RuntimeCapability.StaleInstallLockIgnored) && (found.IsStale || found.IsRecovery))
        {
            rows.Add(found.IsStale ? StaleInstallLockRow(found, blocked) : RecoveryInstallLockRow(found, blocked));
            return;
        }

        rows.Add(new AttentionRow
        {
            Title = InterruptedInstallTitle,
            Detail = $"An interrupted install left a lock; agent hooks are blocking until it's cleared. The lock is {found.Path}.{holder} " +
                     "Running the installer again clears it: open it from here, review the command, and let it finish. Calls from your agents are blocked until then.",
            SeverityKey = "High",
            Actions = new AttentionActions(new[]
            {
                new AttentionAction(
                    OpenInstallerActionId,
                    "Open the installer",
                    blocked ?? "Opens the Updates window: the installer is downloaded, verified and shown in a review before anything runs.",
                    Enabled: blocked is null),
            }),
        });
    }

    /// <summary>
    /// The fail-mode rows of "What needs attention". Up to 1.0.3 (and on a managed host) a saved <c>observe</c> with <c>closed</c> is the recurring bad
    /// default and the row says how to fix the file. On a per-user 1.0.4 host a saved <c>closed</c> is not applied, so that row is not shown; what is real
    /// is a strict rule pack under observe (the runtime reports <c>closed</c>), and a saved <c>closed</c> that does not do what its author meant.
    /// </summary>
    private void AppendFailModeMismatchAttention(List<AttentionRow> rows)
    {
        // 1.0.5 and later (per-user): a saved value wins again, and its rows are in OverviewPanelViewModel.Runtime105.cs (CUST-397).
        if (SavedFailModeWins)
        {
            AppendSavedFailModeAttention(rows);
            return;
        }

        var ignored = SavedFailModeIgnored;
        var configured = Services.Config.Config.Guardrail.Connectors;
        if (!ignored)
        {
            foreach (var (name, settings) in configured)
            {
                if (settings.HasFailModeMismatch)
                {
                    rows.Add(new AttentionRow
                    {
                        Title = $"{name}: observe mode with a fail-closed hook",
                        Detail = "The connector only observes, but a hook failure blocks the agent anyway. " +
                                 "Pair observe with hook_fail_mode: open, or move to enforce.",
                        SeverityKey = "High",
                    });
                }
            }

            return;
        }

        foreach (var (name, settings) in configured)
        {
            if (RemovedConnectors.IsRemoved(name))
            {
                continue;
            }

            _connectorModes.TryGetValue(name, out var live);
            var effective = live?.HookFailMode ?? _status.Connector(name)?.FailMode?.Effective;
            var closed = string.Equals(effective, "closed", StringComparison.OrdinalIgnoreCase);
            var observing = string.Equals(live?.GuardrailMode ?? settings.Mode, "observe", StringComparison.OrdinalIgnoreCase);

            if (closed && observing)
            {
                rows.Add(new AttentionRow
                {
                    Title = $"{name}: observe mode with a fail-closed hook",
                    Detail = "The connector only observes, but its rule pack is strict, so a gateway outage blocks the agent anyway. " +
                             "Use the default or permissive rule pack, or move to enforce. " + HookFailModeRule.PerUserOutageRule,
                    SeverityKey = "High",
                });
            }
            else if (settings.SavesFailClosed && !closed)
            {
                rows.Add(new AttentionRow
                {
                    Title = $"{name}: hook_fail_mode: closed is not applied",
                    Detail = $"config.yaml saves hook_fail_mode: closed for {name}, but this runtime does not apply a saved per-user fail mode. " +
                             HookFailModeRule.PerUserOutageRule + " To fail closed during an outage, use the strict rule pack.",
                    SeverityKey = "Medium",
                    Command = HookFailModeRule.StrictPackCommand,
                });
            }
        }
    }

    /// <summary>Opens the installer for the install-lock row (called by <c>RunAttentionAction</c>).</summary>
    private void OpenInstallerForLock() => OpenInstaller(Services);
}
