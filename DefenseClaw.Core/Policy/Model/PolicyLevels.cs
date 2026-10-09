using System.Globalization;
using System.Text;

namespace DefenseClaw.Core.Policy.Model;

/// <summary>
/// The tool-call levels one scope resolves to, and where each came from. Mirrors <c>ScopeLevels</c> in the runtime's
/// <c>policy_catalog</c>: the block and alert levels each take the scope's own value, else the global one, else the level of the
/// scope's rule-pack profile, and the alert level is then clamped to the block level (anything that blocks also alerts).
/// </summary>
/// <param name="BlockRank">1 (LOW) to 4 (CRITICAL).</param>
/// <param name="AlertRank">Already clamped to <paramref name="BlockRank"/>.</param>
/// <param name="BlockSource"><c>override</c>, <c>global</c> or <c>pack</c>.</param>
/// <param name="AlertSource">The same for the alert level.</param>
/// <param name="WantedAlertRank">The alert rank before the clamp.</param>
public sealed record ScopeLevels(int BlockRank, int AlertRank, string BlockSource, string AlertSource, int WantedAlertRank)
{
    /// <summary>The block level on the catalog scale (<c>HIGH+</c>).</summary>
    public string BlockAt => PolicyLevels.LevelLabel(BlockRank);

    /// <summary>The alert level on the catalog scale.</summary>
    public string AlertAt => PolicyLevels.LevelLabel(AlertRank);

    /// <summary>True when the alert level was set above the block level and follows it down.</summary>
    public bool AlertClamped => WantedAlertRank > AlertRank;

    /// <summary>The more specific source of the two levels (override over global over pack).</summary>
    public string Source
    {
        get
        {
            foreach (var source in new[] { "override", "global" })
            {
                if (string.Equals(BlockSource, source, StringComparison.Ordinal) || string.Equals(AlertSource, source, StringComparison.Ordinal))
                {
                    return source;
                }
            }

            return "pack";
        }
    }
}

/// <summary>
/// The levels and severities of the Policies model, and the pure rules over them: which action a finding gets at each severity, what
/// "weaker" means, and how the gateway resolves a scope's tool-call levels. A port of the runtime's own
/// <c>defenseclaw.tui.services.policy_state</c> and <c>defenseclaw.policy_catalog</c> (source commit 95159fd), kept line-for-line where
/// the answer decides whether a change needs an acknowledgement; the pinned runtime's test vectors for these functions are in
/// <c>PolicyLevelsTests</c>.
/// </summary>
public static class PolicyLevels
{
    /// <summary>The value that clears a scope's own level: it follows the global or pack level again.</summary>
    public const string Inherit = "inherit";

    /// <summary>The four severities a tool-call level counts in, strongest first.</summary>
    public static readonly IReadOnlyList<string> SeverityOrder = new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW" };

    /// <summary>The levels the block-at picker offers for a policy's LLM traffic.</summary>
    public static readonly IReadOnlyList<string> BlockLevels = new[] { "CRITICAL", "HIGH+", "MEDIUM+" };

    /// <summary>The levels the alert-at picker offers for a policy's LLM traffic.</summary>
    public static readonly IReadOnlyList<string> AlertLevels = new[] { "HIGH+", "MEDIUM+", "LOW+" };

    /// <summary>The tool-call block levels (<c>guardrail block-at</c>), besides <see cref="Inherit"/>.</summary>
    public static readonly IReadOnlyList<string> ToolBlockLevels = new[] { "CRITICAL", "HIGH+", "MEDIUM+" };

    /// <summary>The tool-call alert levels (<c>guardrail alert-at</c>), besides <see cref="Inherit"/>.</summary>
    public static readonly IReadOnlyList<string> ToolAlertLevels = new[] { "CRITICAL", "HIGH+", "MEDIUM+", "LOW+" };

    /// <summary>Human approval: off, or the lowest severity that asks.</summary>
    public static readonly IReadOnlyList<string> HiltLevels = new[] { "off", "CRITICAL", "HIGH+", "MEDIUM+", "LOW+" };

    /// <summary>guardrail.rego <c>severity_rank</c>; <c>policy edit guardrail --block-threshold N</c>.</summary>
    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.Ordinal)
    {
        ["CRITICAL"] = 4,
        ["HIGH"] = 3,
        ["MEDIUM"] = 2,
        ["LOW"] = 1,
    };

    private static readonly Dictionary<int, string> LevelForRank = new()
    {
        [4] = "CRITICAL",
        [3] = "HIGH+",
        [2] = "MEDIUM+",
        [1] = "LOW+",
    };

    /// <summary>The catalog's threshold labels, by how little they catch (lower is stricter; "none" blocks nothing and is the weakest).</summary>
    private static readonly Dictionary<string, int> ThresholdRanks = new(StringComparer.Ordinal)
    {
        ["LOW+"] = 1,
        ["MEDIUM+"] = 2,
        ["HIGH+"] = 3,
        ["CRITICAL"] = 4,
        ["NONE"] = 5,
    };

    /// <summary>What a finding at a severity turns into, strongest first.</summary>
    private static readonly Dictionary<string, int> ActionStrength = new(StringComparer.Ordinal)
    {
        ["block"] = 3,
        ["ask"] = 2,
        ["alert"] = 1,
        ["allow"] = 0,
    };

    private static readonly Dictionary<string, string> ActionWords = new(StringComparer.Ordinal)
    {
        ["block"] = "block",
        ["ask"] = "ask a human",
        ["alert"] = "alert",
        ["allow"] = "allow",
    };

    /// <summary>Rule-pack presets, strictest last.</summary>
    private static readonly Dictionary<string, int> PackStrictness = new(StringComparer.Ordinal)
    {
        ["permissive"] = 1,
        ["default"] = 2,
        ["strict"] = 3,
    };

    /// <summary>The (block, alert) ranks of each rule-pack profile: the gateway's <c>guardrailProfileThresholds</c>.</summary>
    private static readonly Dictionary<string, (int Block, int Alert)> ProfileRanks = new(StringComparer.Ordinal)
    {
        ["strict"] = (2, 1),
        ["permissive"] = (4, 3),
        ["default"] = (4, 2),
    };

    /// <summary>Tool-call block and alert levels per rule-pack profile (decision.go <c>guardrailThresholdsForConnector</c>).</summary>
    private static readonly Dictionary<string, (string Block, string Alert)> ProfileLevelLabels = new(StringComparer.Ordinal)
    {
        ["strict"] = ("MEDIUM+", "LOW+"),
        ["permissive"] = ("CRITICAL", "HIGH+"),
        ["default"] = ("CRITICAL", "MEDIUM+"),
    };

    // ---- ranks and labels --------------------------------------------------------------------------------------------------

    /// <summary>How little a threshold label catches (1 = LOW+ ... 5 = none); null when it is not a label.</summary>
    public static int? ThresholdRank(string? label) =>
        ThresholdRanks.TryGetValue((label ?? string.Empty).Trim().ToUpperInvariant(), out var rank) ? rank : null;

    /// <summary><c>CRITICAL</c> to 4, <c>HIGH+</c> to 3, <c>MEDIUM+</c> to 2, <c>LOW+</c> to 1; null otherwise.</summary>
    public static int? LevelRank(string? label)
    {
        var text = (label ?? string.Empty).Trim().ToUpperInvariant();
        foreach (var (rank, level) in LevelForRank)
        {
            if (string.Equals(level, text, StringComparison.Ordinal))
            {
                return rank;
            }
        }

        return null;
    }

    /// <summary>How little approval asks for (1 = LOW+ ... 4 = CRITICAL, 5 = off).</summary>
    public static int HiltRank(string? label) => LevelRank(label) ?? 5;

    /// <summary>The severity rank of a stored level (<c>HIGH</c> is 3); null for anything the gateway would ignore.</summary>
    public static int? SeverityRankOf(string? stored) =>
        SeverityRank.TryGetValue((stored ?? string.Empty).Trim().ToUpperInvariant(), out var rank) ? rank : null;

    /// <summary>A stored <c>block_at</c> / <c>alert_at</c> as <c>CRITICAL</c> to <c>LOW</c>; empty for inherit and for anything the gateway would ignore.</summary>
    public static string LevelValue(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        return SeverityRank.ContainsKey(text) ? text : string.Empty;
    }

    /// <summary>Severity rank to the catalog scale (4 to <c>CRITICAL</c>, 3 to <c>HIGH+</c> ...); <c>none</c> for anything else.</summary>
    public static string LevelLabel(int rank) => LevelForRank.TryGetValue(rank, out var label) ? label : "none";

    /// <summary>Severity rank to the stored level (4 to <c>CRITICAL</c>, 3 to <c>HIGH</c> ...); empty if unknown.</summary>
    public static string LevelName(int rank)
    {
        foreach (var (name, value) in SeverityRank)
        {
            if (value == rank)
            {
                return name;
            }
        }

        return string.Empty;
    }

    /// <summary>A guardrail severity-rank threshold (<c>HIGH</c>, <c>3</c>) as the display scale; <c>none</c> when it is neither.</summary>
    public static string ThresholdLabel(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToUpperInvariant();
        int? rank = null;
        if (text.Length > 0 && text.All(char.IsAsciiDigit))
        {
            rank = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        else if (SeverityRank.TryGetValue(text, out var named))
        {
            rank = named;
        }

        return rank is { } r && LevelForRank.TryGetValue(r, out var label) ? label : "none";
    }

    /// <summary>A stored level (<c>HIGH</c>) as a picker value (<c>HIGH+</c>); <see cref="Inherit"/> when it is empty.</summary>
    public static string PickerLevel(string? stored) =>
        SeverityRankOf(stored) is { } rank ? LevelForRank[rank] : Inherit;

    /// <summary>The human-approval posture: <c>off</c>, or the lowest severity that asks (<c>HIGH+</c>).</summary>
    public static string HiltLabel(bool enabled, string? minSeverity)
    {
        if (!enabled)
        {
            return "off";
        }

        var label = ThresholdLabel(string.IsNullOrWhiteSpace(minSeverity) ? "HIGH" : minSeverity);
        return string.Equals(label, "none", StringComparison.Ordinal) ? "HIGH+" : label;
    }

    /// <summary><c>action</c> or <c>observe</c> (anything else observes).</summary>
    public static string ModeLabel(string? value) =>
        string.Equals((value ?? string.Empty).Trim(), "action", StringComparison.OrdinalIgnoreCase) ? "action" : "observe";

    /// <summary>The text cut to <paramref name="width"/> characters with an ellipsis; 0 keeps it whole.</summary>
    public static string Fit(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width <= 0 || text.Length <= width)
        {
            return text;
        }

        return text[..Math.Max(0, width - 1)].TrimEnd() + "…";
    }

    // ---- rule-pack profiles ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The posture profile the gateway derives from a rule-pack folder: its base name, lowercased; <c>strict</c> and <c>permissive</c> keep
    /// theirs, everything else reads as <c>default</c> (<c>guardrailProfileForDir</c>). A composed pack (<c>protected-SCOPE/PROFILE</c>)
    /// ends in its base pack's profile.
    /// </summary>
    public static string PackProfile(string? folder)
    {
        var raw = (folder ?? string.Empty).Trim().TrimEnd('/', '\\');
        if (raw.Length == 0)
        {
            return "default";
        }

        var cut = raw.LastIndexOfAny(new[] { '/', '\\' });
        var name = (cut >= 0 ? raw[(cut + 1)..] : raw).ToLowerInvariant();
        return name is "strict" or "permissive" ? name : "default";
    }

    /// <summary>The (blocks at, alerts at) labels for tool calls under the pack in <paramref name="folder"/>.</summary>
    public static (string Block, string Alert) ProfileLevelsFor(string? folder) => ProfileLevelLabels[PackProfile(folder)];

    /// <summary>
    /// The tool-call levels a scope resolves to, exactly as the gateway does (<c>guardrailLevelThresholds</c>): each of block and alert takes
    /// the scope's own value, else the global one, else its rule-pack profile's, and the alert rank is then clamped to the block rank.
    /// </summary>
    /// <param name="packFolder">The scope's rule-pack folder.</param>
    /// <param name="globalOwn">The global <c>block_at</c> / <c>alert_at</c> as stored ("" for unset).</param>
    /// <param name="connectorOwn">The connector's own values, or null for the global scope.</param>
    public static ScopeLevels ResolveLevels(string? packFolder, (string Block, string Alert) globalOwn = default, (string Block, string Alert)? connectorOwn = null)
    {
        var (packBlock, packAlert) = ProfileRanks[PackProfile(packFolder)];

        (int Rank, string Source) Pick(bool block, int packRank)
        {
            if (connectorOwn is { } own)
            {
                var value = LevelValue(block ? own.Block : own.Alert);
                if (value.Length > 0)
                {
                    return (SeverityRank[value], "override");
                }
            }

            var shared = LevelValue(block ? globalOwn.Block : globalOwn.Alert);
            return shared.Length > 0 ? (SeverityRank[shared], "global") : (packRank, "pack");
        }

        var (blockRank, blockSource) = Pick(true, packBlock);
        var (alertRank, alertSource) = Pick(false, packAlert);
        return new ScopeLevels(blockRank, Math.Min(alertRank, blockRank), blockSource, alertSource, alertRank);
    }

    /// <summary>"set for codex", "set globally" or "from the strict pack".</summary>
    public static string LevelOrigin(string source, string scope, string pack, string profile)
    {
        if (string.Equals(source, "override", StringComparison.Ordinal))
        {
            return $"set for {scope}";
        }

        if (string.Equals(source, "global", StringComparison.Ordinal))
        {
            return "set globally";
        }

        var named = pack.Length > 0 ? pack : profile;
        return string.Equals(named, profile, StringComparison.Ordinal) ? $"from the {named} pack" : $"from the {named} pack ({profile} levels)";
    }

    // ---- what a finding gets -----------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>(severity, action)</c> from CRITICAL to LOW, in the gateway's order: the block level wins first, then human approval, then the
    /// alert level (<c>guardrailRuntimeActionForConnector</c>). This is the action-mode answer; observe mode only logs what it would do.
    /// </summary>
    public static IReadOnlyList<(string Severity, string Action)> SeverityActions(string? blockAt, string? alertAt, string? hilt)
    {
        var block = LevelRank(blockAt) ?? 5;
        var alert = LevelRank(alertAt) ?? 5;
        var ask = HiltRank(hilt);
        var rows = new List<(string, string)>(SeverityOrder.Count);
        foreach (var severity in SeverityOrder)
        {
            var rank = SeverityRank[severity];
            string action;
            if (rank >= block)
            {
                action = "block";
            }
            else if (rank >= ask)
            {
                action = "ask";
            }
            else if (rank >= alert)
            {
                action = "alert";
            }
            else
            {
                action = "allow";
            }

            rows.Add((severity, action));
        }

        return rows;
    }

    private static string SeveritySpan(List<string> severities) =>
        severities.Count == 0 ? string.Empty : LevelForRank[SeverityRank[severities[^1]]];

    /// <summary>One plain sentence: what the scope's tool calls get at each severity.</summary>
    public static string PostureSummary(string scope, string mode, string blockAt, string alertAt, string hilt)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["block"] = new(),
            ["ask"] = new(),
            ["alert"] = new(),
            ["allow"] = new(),
        };
        foreach (var (severity, action) in SeverityActions(blockAt, alertAt, hilt))
        {
            groups[action].Add(severity);
        }

        var observe = !string.Equals(mode, "action", StringComparison.Ordinal);
        var verbs = observe
            ? new Dictionary<string, string> { ["block"] = "block", ["ask"] = "ask a human for", ["alert"] = "alert on" }
            : new Dictionary<string, string> { ["block"] = "blocks", ["ask"] = "asks a human for", ["alert"] = "alerts on" };

        var parts = new List<string>();
        foreach (var action in new[] { "block", "ask", "alert" })
        {
            if (groups[action].Count > 0)
            {
                parts.Add($"{verbs[action]} {SeveritySpan(groups[action])}");
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(observe ? "allow every severity" : "allows every severity");
        }

        var clause = parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
        var subject = scope.Length == 0 || string.Equals(scope, PolicyScopes.Global, StringComparison.Ordinal) ? "The global default" : scope;
        return observe ? $"{subject} logs only; in action mode it would {clause}." : $"{subject} {clause}.";
    }

    /// <summary>The severity-to-action matrix, one line per severity.</summary>
    public static IReadOnlyList<string> MatrixLines(string mode, string blockAt, string alertAt, string hilt)
    {
        var observe = !string.Equals(mode, "action", StringComparison.Ordinal);
        var lines = new List<string>();
        foreach (var (severity, action) in SeverityActions(blockAt, alertAt, hilt))
        {
            var word = ActionWords[action];
            if (observe && action is "block" or "ask")
            {
                word = $"log (would {word})";
            }

            lines.Add($"  {severity,-9} {word}");
        }

        return lines;
    }

    // ---- what "weaker" means -----------------------------------------------------------------------------------------------

    /// <summary>The severities whose action gets weaker (block, then ask, then alert, then allow).</summary>
    public static IReadOnlyList<string> ActionsWeaken(IReadOnlyList<(string Severity, string Action)> before, IReadOnlyList<(string Severity, string Action)> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var old = before.ToDictionary(p => p.Severity, p => p.Action, StringComparer.Ordinal);
        var weaker = new List<string>();
        foreach (var (severity, action) in after)
        {
            var was = old.TryGetValue(severity, out var previous) ? previous : "allow";
            if (ActionStrength.GetValueOrDefault(action, 0) < ActionStrength.GetValueOrDefault(was, 0))
            {
                weaker.Add(severity);
            }
        }

        return weaker;
    }

    /// <summary>Action to observe stops every block.</summary>
    public static bool ModeWeakens(string? oldMode, string newMode) =>
        string.Equals(string.IsNullOrEmpty(oldMode) ? "observe" : oldMode, "action", StringComparison.Ordinal)
        && !string.Equals(newMode, "action", StringComparison.Ordinal);

    /// <summary>Raising a block or alert level catches fewer severities.</summary>
    public static bool ThresholdWeakens(string? oldLabel, string? newLabel) =>
        ThresholdRank(oldLabel) is { } before && ThresholdRank(newLabel) is { } after && after > before;

    /// <summary>Turning approval off, or asking for fewer severities.</summary>
    public static bool HiltWeakens(string? oldLabel, string? newLabel) => HiltRank(newLabel) > HiltRank(oldLabel);

    /// <summary>Whether switching any of <paramref name="oldPacks"/> to <paramref name="newPack"/> moves to a looser preset.</summary>
    public static bool PackWeakens(IEnumerable<string> oldPacks, string newPack)
    {
        ArgumentNullException.ThrowIfNull(oldPacks);
        if (!PackStrictness.TryGetValue(newPack ?? string.Empty, out var newRank))
        {
            return false;
        }

        return oldPacks.Any(old => PackStrictness.GetValueOrDefault(old, 0) > newRank);
    }

    // ---- comparing named policies ------------------------------------------------------------------------------------------

    /// <summary>
    /// Ways <paramref name="replacement"/> protects less than <paramref name="current"/> (empty when it does not): a threshold that catches
    /// fewer severities, a firewall that goes from deny to allow by default, or human approval switched off.
    /// </summary>
    public static IReadOnlyList<string> PolicyWeakenings(NamedPolicy? current, NamedPolicy? replacement)
    {
        var reasons = new List<string>();
        if (current is null || replacement is null)
        {
            return reasons;
        }

        foreach (var (label, before, after) in new[]
                 {
                     ("blocks", current.BlockAt, replacement.BlockAt),
                     ("alerts on", current.AlertAt, replacement.AlertAt),
                     ("blocks installs of", current.InstallBlockAt, replacement.InstallBlockAt),
                 })
        {
            if (ThresholdWeakens(before, after))
            {
                reasons.Add($"{label} {after} instead of {before}");
            }
        }

        if (string.Equals(current.FirewallDefault, "deny", StringComparison.Ordinal) && string.Equals(replacement.FirewallDefault, "allow", StringComparison.Ordinal))
        {
            reasons.Add("the firewall allows by default instead of denying");
        }

        if (current.Hilt is true && replacement.Hilt is false)
        {
            reasons.Add("human approval is turned off");
        }

        return reasons;
    }

    /// <summary><c>(label, active value, new value)</c> rows for the activation preview.</summary>
    public static IReadOnlyList<(string Label, string Before, string After)> PolicyComparison(NamedPolicy? current, NamedPolicy replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        static Dictionary<string, string> Values(NamedPolicy? policy)
        {
            if (policy is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Block at"] = Or(policy.BlockAt, "-"),
                ["Alert at"] = Or(policy.AlertAt, "-"),
                ["Install block at"] = Or(policy.InstallBlockAt, "-"),
                ["Firewall default"] = Or(policy.FirewallDefault, "unchanged"),
                ["Human approval"] = HiltWord(policy.Hilt),
            };
        }

        var before = Values(current);
        return Values(replacement).Select(p => (p.Key, before.GetValueOrDefault(p.Key, "-"), p.Value)).ToArray();
    }

    /// <summary>What activating the policy changes besides the thresholds.</summary>
    public static IReadOnlyList<string> PolicySideEffects(NamedPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var effects = new List<string>();
        if (policy.AddsWebhooks)
        {
            effects.Add("adds its webhooks to yours");
        }

        if (policy.SetsCisco)
        {
            effects.Add("changes Cisco AI Defense settings");
        }

        if (policy.ScannerOverrides > 0)
        {
            effects.Add($"{policy.ScannerOverrides.ToString(CultureInfo.InvariantCulture)} scanner override{(policy.ScannerOverrides != 1 ? "s" : string.Empty)}");
        }

        return effects;
    }

    /// <summary><c>on</c>, <c>off</c> or <c>inherit</c> for a policy's human-approval setting.</summary>
    public static string HiltWord(bool? hilt) => hilt switch
    {
        true => "on",
        false => "off",
        _ => "inherit",
    };

    /// <summary>The text, or <paramref name="fallback"/> when it is empty.</summary>
    internal static string Or(string? text, string fallback) => string.IsNullOrEmpty(text) ? fallback : text;

    /// <summary>The words of a list, joined with commas and a final "and".</summary>
    internal static string AndJoin(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
        };
    }

    /// <summary>The builder helper for a multi-line text.</summary>
    internal static string Lines(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return builder.ToString();
    }
}
