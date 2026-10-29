using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Gateway.Durability;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// One button on an attention row: a reviewed action (CUST-352). <paramref name="Enabled"/> is false with the reason as <paramref name="Tip"/> when
/// the installation is read-only or the action does not make sense right now.
/// </summary>
public sealed record AttentionAction(string Id, string Label, string Tip, bool Enabled = true);

/// <summary>The buttons of an attention row, compared by value so a row that did not change is not drawn again.</summary>
public sealed class AttentionActions : IEquatable<AttentionActions>
{
    public static readonly AttentionActions None = new(Array.Empty<AttentionAction>());

    public AttentionActions(IReadOnlyList<AttentionAction> items)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
    }

    public IReadOnlyList<AttentionAction> Items { get; }

    public int Count => Items.Count;

    public bool Equals(AttentionActions? other) => other is not null && Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => Equals(obj as AttentionActions);

    public override int GetHashCode() => Items.Count;
}

/// <summary>The ids of the durability actions an attention row can carry.</summary>
internal static class AttentionActionIds
{
    /// <summary><c>defenseclaw setup codex --yes --mode &lt;current&gt;</c>.</summary>
    public const string ReselectCodex = "reselect-codex";

    /// <summary>The reviewed file move that clears a stuck pending write.</summary>
    public const string ClearStuckWrite = "clear-stuck-write";

    /// <summary>Puts back what <see cref="ClearStuckWrite"/> moved.</summary>
    public const string PutBack = "put-back";

    /// <summary><c>defenseclaw guardrail fail-mode open --yes</c>.</summary>
    public const string SwitchToFailOpen = "fail-open";
}

/// <summary>
/// The "What needs attention" rows for the gateway's durability (CUST-352) and the commands behind their buttons. Pure: it derives rows from the
/// snapshot (its <see cref="GatewaySnapshot.Durability"/> and <see cref="GatewaySnapshot.LastFailure"/>) and runs nothing. The three live incidents
/// of 2026-10-15 are the rows: Codex's executable moved after an update, a guarded write of Codex's config.toml that never finished, and Codex
/// blocked because its hooks fail closed while the gateway is down.
/// </summary>
internal static class DurabilityAttention
{
    /// <summary>The argv of "Re-select Codex": the mode is always passed, because <c>setup codex --yes</c> without it flipped observe to action live.</summary>
    public static IReadOnlyList<string> ReselectArgv(string mode) => new[] { "setup", "codex", "--yes", "--mode", mode };

    /// <summary>The argv of "Switch to fail open". No <c>--connector</c>: that option is for multi-connector installs, and a single-connector one refuses it.</summary>
    public static IReadOnlyList<string> FailOpenArgv { get; } = new[] { "guardrail", "fail-mode", "open", "--yes" };

    /// <summary>
    /// The policy mode to hand <c>setup codex</c>, and where it came from: what the gateway last logged for Codex (<c>policy_mode=</c>, the mode that is
    /// running), then the connector's mode in config.yaml (<c>enforce</c> is <c>action</c>), then <c>observe</c>, which never blocks.
    /// </summary>
    public static (string Mode, string Source) ResolveCodexMode(IReadOnlyList<string> logLines, string? configuredMode)
    {
        ArgumentNullException.ThrowIfNull(logLines);

        if (GatewayLogClassifier.LatestPolicyMode(logLines, "codex") is { } logged)
        {
            return (logged, "the mode the gateway last logged for Codex (policy_mode in gateway.log)");
        }

        var configured = configuredMode?.Trim().ToLowerInvariant() switch
        {
            "observe" => "observe",
            "action" or "enforce" => "action",
            _ => null,
        };
        return configured is not null
            ? (configured, "the Codex connector's mode in config.yaml")
            : ("observe", "no current mode could be found, so observe, which never blocks");
    }

    /// <summary>The sentence the gateway-stopped row and the tray flyout add when gateway.log says why the last start ended, or empty.</summary>
    public static string StoppedSentence(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.State == AppGatewayState.GatewayStopped && snapshot.LastFailure is { } reason
            ? "The gateway stops at start: " + reason.Describe()
            : string.Empty;
    }

    /// <summary>The tray flyout's line for an agent that is blocked (empty when none is).</summary>
    public static string BlockedNote(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.IsAgentBlocked ? AgentBlock.Title(snapshot.Durability!) : string.Empty;
    }

    /// <summary>
    /// The rows, most urgent first. <paramref name="blockedReason"/> says, for an action id, why it cannot be pressed now (the installation's sentence
    /// before any other), or null; <paramref name="now"/> is the clock the ages are told against.
    /// </summary>
    public static IReadOnlyList<AttentionRow> Rows(GatewaySnapshot snapshot, Func<string, string?> blockedReason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(blockedReason);

        var rows = new List<AttentionRow>();
        if (snapshot.Durability is not { } facts)
        {
            return rows;
        }

        var stopped = snapshot.State == AppGatewayState.GatewayStopped;
        var because = stopped && snapshot.LastFailure is not null ? " " + StoppedSentence(snapshot) : string.Empty;

        if (snapshot.IsAgentBlocked)
        {
            var names = facts.FailClosedConnectors.Select(GatewayLogClassifier.FriendlyConnector).ToArray();
            var who = names.Length == 1 ? names[0] + "'s" : "Those agents'";
            rows.Add(new AttentionRow
            {
                Title = AgentBlock.Title(facts),
                Detail = $"{who} hooks fail closed (hooks\\.hookcfg), so every action the agent takes is blocked while the gateway is not running. " +
                         "Start the gateway, or switch to fail open so the agent works while the gateway is down." + because,
                SeverityKey = "Critical",
                OffersStart = true,
                Actions = Actions(blockedReason, (AttentionActionIds.SwitchToFailOpen, "Switch to fail open",
                    "Review defenseclaw guardrail fail-mode open --yes. Nothing changes until you confirm.")),
            });
        }

        // Incident A. The pinned path is checked while the gateway is still up: the next start is the one that fails.
        var stale = snapshot.LastFailure as GatewayExitReason.StaleAgentExecutable;
        var missingPath = facts.CodexExecutableMissing ? facts.CodexPin!.Executable : facts.CodexPin is null ? stale?.Path : null;
        if (missingPath is not null)
        {
            rows.Add(new AttentionRow
            {
                Title = "Codex's executable moved after an update",
                Detail = $"DefenseClaw still points at {missingPath}, which no longer exists. " +
                         (stopped
                             ? "That is why the gateway stops at start. "
                             : "The gateway is up now, but it will fail the next time it starts. ") +
                         "Re-select Codex runs setup again so DefenseClaw finds the new one, keeping Codex's current mode. Quit Codex first.",
                SeverityKey = stopped ? "Critical" : "High",
                Actions = Actions(blockedReason, (AttentionActionIds.ReselectCodex, "Re-select Codex",
                    "Review defenseclaw setup codex --yes with Codex's current mode. Nothing runs until you confirm.")),
            });
        }

        // Incident B. DefenseClaw has no recovery command for it.
        if (facts.PendingWrites.Count > 0)
        {
            var stuckNow = stopped && snapshot.LastFailure is GatewayExitReason.StuckConfigWrite;
            rows.Add(new AttentionRow
            {
                Title = facts.PendingWrites.Count == 1 && facts.PendingWrites[0].IsCodexConfig
                    ? "A write to Codex's config.toml is stuck"
                    : $"{facts.PendingWrites.Count.ToString(CultureInfo.CurrentCulture)} guarded config write(s) never finished",
                Detail = Describe(facts.PendingWrites, now) + (StateSentence(facts.PendingWrites) is { Length: > 0 } states ? " " + states : string.Empty) +
                         " If the agent rewrote the file in between, every later write is refused (\"requested recovery leaf is occupied by an unrecognized inode\"), " +
                         "and the gateway exits at start. DefenseClaw has no command to recover from this. " +
                         "Clearing it moves the pending files to a quarantine folder (nothing is deleted; you can put them back) while the gateway is stopped." +
                         (stuckNow ? " This is why the gateway stops at start." : string.Empty),
                SeverityKey = stuckNow ? "Critical" : "High",
                Actions = Actions(blockedReason, (AttentionActionIds.ClearStuckWrite, "Clear the stuck pending write", ClearTip(facts.PendingWrites))),
            });
        }

        if (facts.Quarantine is { } quarantine)
        {
            rows.Add(new AttentionRow
            {
                Title = "Files moved aside from a stuck write",
                Detail = $"{quarantine.Files.ToString(CultureInfo.CurrentCulture)} file(s) are in {quarantine.Directory} (moved {Age(now - quarantine.CreatedUtc)} ago). " +
                         "Put back restores them to where they came from; Re-run Codex setup asks setup to write Codex's config again.",
                SeverityKey = "Info",
                Actions = Actions(
                    blockedReason,
                    (AttentionActionIds.PutBack, "Put back", "Review putting the moved files back where they came from."),
                    (AttentionActionIds.ReselectCodex, "Re-run Codex setup", "Review defenseclaw setup codex --yes with Codex's current mode.")),
            });
        }

        return rows;
    }

    private static AttentionActions Actions(Func<string, string?> blockedReason, params (string Id, string Label, string Tip)[] actions) =>
        new(actions
            .Select(a => blockedReason(a.Id) is { } why
                ? new AttentionAction(a.Id, a.Label, why, false)
                : new AttentionAction(a.Id, a.Label, a.Tip))
            .ToArray());

    private static string Describe(IReadOnlyList<CasPendingWrite> writes, DateTimeOffset now)
    {
        var parts = writes.Take(3).Select(w =>
            $"{w.TargetPath ?? "an unreadable receipt"} (begun {Age(now - w.LastWriteUtc)} ago, id {w.Id[..8]}, {w.Phases})");
        var more = writes.Count > 3 ? $" and {(writes.Count - 3).ToString(CultureInfo.CurrentCulture)} more" : string.Empty;
        return "DefenseClaw began a guarded write of " + string.Join("; ", parts) + more + " and it never finished.";
    }

    /// <summary>The plain-English state of one write (CUST-360), from its hash check (<see cref="CasWriteText.State"/>).</summary>
    internal static string StateText(CasWriteState state) => CasWriteText.State(state);

    /// <summary>One line per write (<c>settings.json — never applied</c>), plus a caution when any could not be verified. Empty when none was checked.</summary>
    internal static string StateSentence(IReadOnlyList<CasPendingWrite> writes) => CasWriteText.Sentence(writes);

    /// <summary>The clear button's tooltip: a stronger one when a write could not be verified (the button stays; the operator decides).</summary>
    internal static string ClearTip(IReadOnlyList<CasPendingWrite> writes) =>
        CasWriteText.AnyUnverified(writes)
            ? "Review a file move (not a DefenseClaw command) into a quarantine folder. Nothing is deleted. At least one write could not be verified " +
              "against its receipt: look at the files the review lists before you confirm."
            : "Review a file move (not a DefenseClaw command) into a quarantine folder. Nothing is deleted.";

    /// <summary>"3 minutes", "2 hours", "4 days".</summary>
    internal static string Age(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes < 2 ? "under 2 minutes"
            : span.TotalHours < 2 ? string.Create(CultureInfo.CurrentCulture, $"{(int)span.TotalMinutes} minutes")
            : span.TotalDays < 2 ? string.Create(CultureInfo.CurrentCulture, $"{(int)span.TotalHours} hours")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)span.TotalDays} days");
    }
}
