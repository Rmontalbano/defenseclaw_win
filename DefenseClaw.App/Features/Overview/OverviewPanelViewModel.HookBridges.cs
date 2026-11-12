using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" for an agent hook that is still the older Windows PowerShell EncodedCommand bridge (CUST-448, runtime 1.0.8). Doctor fails that
/// agent's hooks row, because when the hook launcher cannot start, the bridge exits 0 in Constrained Language mode and the call goes through unchecked
/// instead of being blocked. The row's button is a reviewed <c>defenseclaw setup &lt;agent&gt; --yes --restart</c>, taken from the repair doctor names
/// (<see cref="HookBridgeDoctorFix"/>); nothing runs until it is confirmed. The generic "Doctor found N failures" row stays beside it.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The longest doctor sentence the row shows; doctor's detail comes from a file on disk, so it is cut.</summary>
    internal const int MaxHookBridgeDetail = 400;

    /// <summary>When the reviewed <c>setup &lt;connector&gt;</c> repair last succeeded from the row's button, per connector. Not kept across runs of the app.</summary>
    private readonly Dictionary<string, DateTimeOffset> _hookBridgeRepairedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// The repairs the cached doctor result asks for, one per distinct command; empty on a runtime before 1.0.8, with no cache, with a cache older than
    /// <see cref="DoctorStaleAfter"/> (or with no capture time), and, for a connector, with a result captured before the last repair that succeeded here: the
    /// cache is only rewritten by the next doctor run, so the row waits for a newer result instead of outliving its own fix.
    /// </summary>
    private IReadOnlyList<HookBridgeRepair> CurrentHookBridgeRepairs()
    {
        if (!Services.Runtime.Capabilities.Has(RuntimeCapability.PowerShellBridgeFailClosed) ||
            _doctorSnapshot is not { } doctor ||
            doctor.IsStale(DateTimeOffset.UtcNow, DoctorStaleAfter))
        {
            return [];
        }

        var found = new List<HookBridgeRepair>();
        foreach (var check in doctor.Checks)
        {
            if (HookBridgeDoctorFix.From(check) is { } repair &&
                !RepairedSince(repair.Connector, doctor) &&
                found.All(other => other.Command != repair.Command))
            {
                found.Add(repair);
            }
        }

        return found;
    }

    private bool RepairedSince(string connector, DoctorCacheSnapshot doctor) =>
        _hookBridgeRepairedAt.TryGetValue(connector, out var repairedAt) &&
        (doctor.CapturedAt is not { } captured || captured <= repairedAt);

    /// <summary>
    /// The review's follow-up: a repair that succeeded is remembered (so the row goes away until a newer doctor result says otherwise), then the usual refresh.
    /// </summary>
    internal async Task OnHookBridgeRepairFinishedAsync(string connector, DiscoverReviewResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Succeeded)
        {
            _hookBridgeRepairedAt[connector] = now;
            BuildAttention(_snapshot);
        }

        await RefreshAfterActionAsync().ConfigureAwait(true);
    }

    private static string CapHookBridgeDetail(string text) =>
        text.Length <= MaxHookBridgeDetail ? text : text[..(MaxHookBridgeDetail - 1)].TrimEnd() + "…";

    /// <summary>Adds one High row per repair. Called from <see cref="AppendOverviewHealthAttention"/>.</summary>
    private void AppendHookBridgeAttention(List<AttentionRow> rows)
    {
        if (_doctorSnapshot is not { } doctor)
        {
            return;
        }

        var blocked = AttentionActionBlockedReason("hook-bridge-setup", _snapshot);
        foreach (var repair in CurrentHookBridgeRepairs())
        {
            var shown = doctor.Checks
                .Where(check => HookBridgeDoctorFix.From(check)?.Command == repair.Command)
                .Select(check => CapHookBridgeDetail($"Doctor: {DisplayNames.Visible(check.Label)}: {DisplayNames.Visible(check.Detail)}"))
                .First();
            var ran = doctor.CapturedAt is { } at ? $" (doctor ran {Relative(at)})" : string.Empty;

            rows.Add(new AttentionRow
            {
                Title = $"{repair.Agent} hooks need to be set up again",
                Detail = $"Doctor found that {repair.Agent}'s hook registration is an older Windows PowerShell EncodedCommand bridge. If the hook launcher cannot " +
                         "start, that bridge exits 0 in Constrained Language mode, so the call goes through unchecked instead of being blocked. Setup writes the " +
                         $"bridge again in the current form and restarts the gateway. Quit {repair.Agent} first. {shown}{ran}",
                SeverityKey = "High",
                Actions = new AttentionActions(new[]
                {
                    new AttentionAction(
                        repair.ActionId,
                        "Re-run setup",
                        blocked ?? $"Opens a review of {repair.Command}; nothing runs until you confirm.",
                        Enabled: blocked is null),
                }),
            });
        }
    }

    /// <summary>"Re-run setup": the review of the repair behind <paramref name="id"/>. Does nothing unless the cached doctor result still asks for it.</summary>
    private void ReviewHookBridgeRepair(string id)
    {
        if (CurrentHookBridgeRepairs().FirstOrDefault(repair => repair.ActionId == id) is not { } found)
        {
            return;
        }

        Review.Open(
            $"Set up {found.Agent} hooks again?",
            $"Runs {found.Command}: setup replaces {found.Agent}'s older PowerShell EncodedCommand hook with the current native registration, then restarts the " +
            $"gateway. Quit {found.Agent} first.",
            new[]
            {
                new DiscoverStep(found.Argv, $"Write {found.Agent}'s hook registration again and restart the gateway.", CommandTier.StateChanging, CliRunner.LongRunningTimeout),
            },
            onFinished: result => OnHookBridgeRepairFinishedAsync(found.Connector, result, DateTimeOffset.UtcNow),
            restartsGateway: CommandReview.RestartsGatewayFor(found.Argv),
            primaryText: "Re-run setup");
    }
}
