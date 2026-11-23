using DefenseClaw.Core.Config;

namespace DefenseClaw.Core.Policy;

/// <summary>
/// What config.yaml says about <c>guardrail.cascade</c> (runtime 1.0.10): the settings that decide what each guardrail layer does, read from the file
/// and nothing else. Pure and tolerant: a value of the wrong type, or a word the runtime does not use, reads as "not set"; nothing is written and no
/// command runs. The runtime's own account of the result is <c>defenseclaw guardrail cascade explain</c> (<see cref="GuardrailCascadeExplain"/>), which
/// the Policies panel runs only when asked.
/// <para>
/// <b>Only the negative is claimed about the LLM judge.</b> The runtime runs the judge for a connector when <c>guardrail.judge.enabled</c> is true and
/// <c>guardrail.judge.hook_connectors</c> holds <c>*</c> or the connector (<c>HookConnectorEnabled</c>, config.go@1.0.10:2674-2689) <i>and</i> a
/// working judge model exists, which this file cannot confirm. So <see cref="JudgeNotCovered"/> is true only when one of those two keys already rules it
/// out, never as proof that the judge runs.
/// </para>
/// </summary>
public sealed class GuardrailCascade
{
    /// <summary>The longest word kept of a value (the runtime's words are a handful of letters).</summary>
    public const int MaxWordChars = 24;

    /// <summary>At most this many scoped blocks are looked at (connectors, profiles, a profile's connectors).</summary>
    private const int MaxScopes = 256;

    private static readonly string[] CriticalFloors = { "log", "alert", "confirm", "block" };
    private static readonly string[] UnknownActions = { "pack", "alert", "confirm", "block" };

    /// <summary>The lanes the runtime registers at 1.0.10 and their defaults (cascade_alignment.go, cascade_lanes.go).</summary>
    private static readonly (string Name, string Default)[] Lanes =
    {
        ("alignment", "off"),
        ("effects", "shadow"),
        ("judge_read_skip", "on"),
        ("judge_tool_result_context", "on"),
        ("judge_unmodeled_alert", "on"),
        ("nonshell", "on"),
        ("secret_flow", "shadow"),
    };

    private readonly Dictionary<string, string> _lanes;
    private readonly Dictionary<string, string> _connectorAlignment;
    private readonly bool _judgeEnabled;
    private readonly HashSet<string> _judgeConnectors;

    private GuardrailCascade(
        bool topLevelSet,
        bool scopedSet,
        string unknown,
        string judgeMaxAction,
        string judgeNoVerdict,
        bool judgeBlockNeedsSystemOne,
        string systemOneOnUnsure,
        bool policyModuleOn,
        string criticalFloor,
        bool adminLocked,
        Dictionary<string, string> lanes,
        Dictionary<string, string> connectorAlignment,
        bool profilesSetAlignment,
        bool judgeEnabled,
        HashSet<string> judgeConnectors)
    {
        TopLevelSet = topLevelSet;
        ScopedSet = scopedSet;
        Unknown = unknown;
        JudgeMaxAction = judgeMaxAction;
        JudgeNoVerdict = judgeNoVerdict;
        JudgeBlockNeedsSystemOne = judgeBlockNeedsSystemOne;
        SystemOneOnUnsure = systemOneOnUnsure;
        PolicyModuleOn = policyModuleOn;
        CriticalFloor = criticalFloor;
        AdminLocked = adminLocked;
        _lanes = lanes;
        _connectorAlignment = connectorAlignment;
        ProfilesSetAlignment = profilesSetAlignment;
        _judgeEnabled = judgeEnabled;
        _judgeConnectors = judgeConnectors;
    }

    /// <summary>A config that sets nothing: the 1.0.10 defaults.</summary>
    public static GuardrailCascade Default { get; } = From(ConfigYamlReader.Empty);

    /// <summary><c>guardrail.cascade</c> names at least one key.</summary>
    public bool TopLevelSet { get; }

    /// <summary>A connector scope, a profile scope or a profile's connector scope names at least one cascade key.</summary>
    public bool ScopedSet { get; }

    /// <summary>The cascade is set anywhere in config.yaml (top level or any scope). False is the default: the rule pack decides.</summary>
    public bool IsSet => TopLevelSet || ScopedSet;

    /// <summary><c>unknown</c>: <c>pack</c> (the default), <c>alert</c>, <c>confirm</c> or <c>block</c>; empty when not set to one of those.</summary>
    public string Unknown { get; }

    /// <summary><c>judge.max_action</c> as written (the runtime's default is block); empty when not set.</summary>
    public string JudgeMaxAction { get; }

    /// <summary><c>judge.no_verdict</c> as written (the default is rules); empty when not set.</summary>
    public string JudgeNoVerdict { get; }

    /// <summary><c>judge.block_needs_system_one</c> is true (its default is false).</summary>
    public bool JudgeBlockNeedsSystemOne { get; }

    /// <summary><c>system_one.on_unsure</c> as written (the default is confirm); empty when not set.</summary>
    public string SystemOneOnUnsure { get; }

    /// <summary><c>policy.module.path</c> names a Rego module. Only "on" is said: the path is not shown.</summary>
    public bool PolicyModuleOn { get; }

    /// <summary><c>admin.critical_floor</c>: <c>log</c>, <c>alert</c> (the default), <c>confirm</c> or <c>block</c>.</summary>
    public string CriticalFloor { get; }

    /// <summary>The top-level <c>admin.locked</c> names something (it locks keys against narrower scopes).</summary>
    public bool AdminLocked { get; }

    /// <summary>A profile scope sets <c>lanes.alignment</c> too; its value is not folded into <see cref="AlignmentMode"/>.</summary>
    public bool ProfilesSetAlignment { get; }

    /// <summary>The lane's mode: the file's word when it is one the lane takes, else the lane's default; empty for a lane that is not registered.</summary>
    public string LaneMode(string lane)
    {
        if (_lanes.TryGetValue(lane, out var mode))
        {
            return mode;
        }

        return Lanes.FirstOrDefault(l => string.Equals(l.Name, lane, StringComparison.Ordinal)).Default ?? string.Empty;
    }

    /// <summary>
    /// How <c>guardrail secret-flow status</c> computes the effective mode (cmd_guardrail.py@1.0.10:6815-6827): off when the effects lane or the
    /// secret-flow lane is off, else shadow (unset is shadow). Both lanes are shadow-only at 1.0.10.
    /// </summary>
    public string SecretFlowMode =>
        LaneMode("effects") == "off" || LaneMode("secret_flow") == "off" ? "off" : "shadow";

    /// <summary>
    /// The alignment lane's mode for <paramref name="connector"/>: its own connector scope's <c>lanes.alignment</c> over the top-level one, else off.
    /// Profile scopes are not folded in (<see cref="ProfilesSetAlignment"/> says one sets it too).
    /// </summary>
    public string AlignmentMode(string? connector)
    {
        if (!string.IsNullOrWhiteSpace(connector) && _connectorAlignment.TryGetValue(NormalizeConnector(connector), out var own))
        {
            return own;
        }

        return LaneMode("alignment");
    }

    /// <summary>
    /// True when config.yaml rules out the LLM judge for <paramref name="connector"/>: <c>guardrail.judge.enabled</c> is not true, or
    /// <c>guardrail.judge.hook_connectors</c> has neither <c>*</c> nor the connector. False does not say the judge runs (it also needs a working model).
    /// </summary>
    public bool JudgeNotCovered(string connector)
    {
        if (!_judgeEnabled)
        {
            return true;
        }

        return !_judgeConnectors.Contains("*") && !_judgeConnectors.Contains(NormalizeConnector(connector));
    }

    /// <summary>The runtime's connector key: trimmed, lower-case, <c>claude-code</c> folded into <c>claudecode</c> (normalizeConnectorKey).</summary>
    public static string NormalizeConnector(string? connector)
    {
        var key = (connector ?? string.Empty).Trim().ToLowerInvariant();
        return key == "claude-code" ? "claudecode" : key;
    }

    /// <summary>
    /// The sentence for a connector whose alignment check cannot act, or null: the lane is not off for it and config.yaml rules the judge out for it.
    /// </summary>
    public string? AlignmentNotCoveredLine(string connector)
    {
        if (string.IsNullOrWhiteSpace(connector))
        {
            return null;
        }

        var mode = AlignmentMode(connector);
        if (mode is "off" or "" || !JudgeNotCovered(connector))
        {
            return null;
        }

        var name = Cut(connector.Trim(), 40);
        return $"The alignment check (lanes.alignment {mode}) does nothing for {name}: the LLM judge does not run for it (guardrail.judge.enabled, guardrail.judge.hook_connectors).";
    }

    /// <summary>
    /// The text lines of the Cascade block (plain words, config.yaml's own key names), bounded. <paramref name="connectors"/> are the connectors the
    /// alignment sentence is checked for: the scope's connector, or the roster for the global scope; at most eight are named.
    /// </summary>
    public IReadOnlyList<string> Lines(IEnumerable<string>? connectors = null)
    {
        var lines = new List<string>();
        if (!IsSet)
        {
            lines.Add("Cascade: default (the rule pack decides; " + LaneTail() + ")");
        }
        else
        {
            lines.Add("Cascade: set in config.yaml");
            AddNonDefault(lines);
            lines.Add("Secret-flow tracking: " + SecretFlowMode);
            if (ScopedSet)
            {
                lines.Add("Connector or profile scopes set it too: defenseclaw guardrail cascade explain shows the result.");
            }

            if (ProfilesSetAlignment)
            {
                lines.Add("A profile scope sets lanes.alignment too.");
            }
        }

        if (connectors is not null)
        {
            foreach (var connector in connectors.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            {
                if (AlignmentNotCoveredLine(connector) is { } sentence)
                {
                    lines.Add(sentence);
                }
            }
        }

        return lines;
    }

    /// <summary>The value for an Overview row: <c>default</c> or <c>set (see Policies)</c>.</summary>
    public string Summary => IsSet ? "set (see Policies)" : "default";

    private string LaneTail()
    {
        var effects = LaneMode("effects");
        var flow = LaneMode("secret_flow");
        if (effects == "off")
        {
            return "the effects and secret-flow lanes are off";
        }

        return flow == "off"
            ? "the effects lane only measures, in shadow; the secret-flow lane is off"
            : "the effects and secret-flow lanes only measure, in shadow";
    }

    private void AddNonDefault(List<string> lines)
    {
        if (Unknown.Length > 0 && Unknown != "pack")
        {
            lines.Add("unknown: " + Unknown);
        }

        if (JudgeMaxAction.Length > 0 && JudgeMaxAction != "block")
        {
            lines.Add("judge.max_action: " + JudgeMaxAction);
        }

        if (JudgeNoVerdict.Length > 0 && JudgeNoVerdict != "rules")
        {
            lines.Add("judge.no_verdict: " + JudgeNoVerdict);
        }

        if (JudgeBlockNeedsSystemOne)
        {
            lines.Add("judge.block_needs_system_one: true");
        }

        if (SystemOneOnUnsure.Length > 0 && SystemOneOnUnsure != "confirm")
        {
            lines.Add("system_one.on_unsure: " + SystemOneOnUnsure);
        }

        if (PolicyModuleOn)
        {
            lines.Add("Rego policy module: on");
        }

        if (CriticalFloor != "alert")
        {
            lines.Add("admin.critical_floor: " + CriticalFloor);
        }

        if (AdminLocked)
        {
            lines.Add("admin.locked: set");
        }

        foreach (var (name, defaultMode) in Lanes)
        {
            if (_lanes.TryGetValue(name, out var mode) && mode != defaultMode)
            {
                lines.Add($"lanes.{name}: {mode}");
            }
        }
    }

    // ---- reading ----------------------------------------------------------------------------------------------------------------

    /// <summary>Reads the cascade out of a loaded config.yaml.</summary>
    public static GuardrailCascade From(ConfigDocument? document) => From(ConfigYamlReader.From(document));

    /// <summary>Reads the cascade out of config.yaml text; anything unreadable is the default.</summary>
    public static GuardrailCascade Parse(string? yaml) => From(ConfigYamlReader.Parse(yaml));

    public static GuardrailCascade From(ConfigYamlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var topLevel = reader.Keys("guardrail", "cascade").Count > 0;
        var scoped = false;
        var profilesAlignment = false;
        var connectorAlignment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var connector in reader.Keys("guardrail", "connectors").Take(MaxScopes))
        {
            if (reader.Keys("guardrail", "connectors", connector, "cascade").Count > 0)
            {
                scoped = true;
            }

            if (Word(reader, "guardrail", "connectors", connector, "cascade", "lanes", "alignment") is { } mode && IsLaneMode("alignment", mode))
            {
                connectorAlignment[NormalizeConnector(connector)] = mode;
            }
        }

        foreach (var profile in reader.Keys("guardrail", "profiles").Take(MaxScopes))
        {
            if (reader.Keys("guardrail", "profiles", profile, "cascade").Count > 0)
            {
                scoped = true;
                profilesAlignment |= reader.Has("guardrail", "profiles", profile, "cascade", "lanes", "alignment");
            }

            foreach (var connector in reader.Keys("guardrail", "profiles", profile, "connectors").Take(MaxScopes))
            {
                if (reader.Keys("guardrail", "profiles", profile, "connectors", connector, "cascade").Count > 0)
                {
                    scoped = true;
                    profilesAlignment |= reader.Has("guardrail", "profiles", profile, "connectors", connector, "cascade", "lanes", "alignment");
                }
            }
        }

        var lanes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _) in Lanes)
        {
            if (Word(reader, "guardrail", "cascade", "lanes", name) is { } mode && IsLaneMode(name, mode))
            {
                lanes[name] = mode;
            }
        }

        var unknown = Word(reader, "guardrail", "cascade", "unknown");
        var floor = Word(reader, "guardrail", "cascade", "admin", "critical_floor");
        var judgeConnectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in reader.List("guardrail", "judge", "hook_connectors") ?? Array.Empty<string>())
        {
            var key = NormalizeConnector(item);
            if (key.Length > 0)
            {
                judgeConnectors.Add(key);
            }
        }

        return new GuardrailCascade(
            topLevel,
            scoped,
            unknown is not null && UnknownActions.Contains(unknown) ? unknown : string.Empty,
            Word(reader, "guardrail", "cascade", "judge", "max_action") ?? string.Empty,
            Word(reader, "guardrail", "cascade", "judge", "no_verdict") ?? string.Empty,
            reader.Bool("guardrail", "cascade", "judge", "block_needs_system_one") == true,
            Word(reader, "guardrail", "cascade", "system_one", "on_unsure") ?? string.Empty,
            reader.Text("guardrail", "cascade", "policy", "module", "path") is { Length: > 0 },
            floor is not null && CriticalFloors.Contains(floor) ? floor : "alert",
            reader.Has("guardrail", "cascade", "admin", "locked"),
            lanes,
            connectorAlignment,
            profilesAlignment,
            reader.Bool("guardrail", "judge", "enabled") == true,
            judgeConnectors);
    }

    /// <summary>A scalar that is a single short lower-case word (letters, digits, <c>_</c>, <c>-</c>); null for anything else, so free text never reaches the screen.</summary>
    private static string? Word(ConfigYamlReader reader, params string[] path)
    {
        var text = reader.Text(path)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Length > MaxWordChars)
        {
            return null;
        }

        foreach (var ch in text)
        {
            if (!(ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' || ch is '_' or '-'))
            {
                return null;
            }
        }

        return text;
    }

    private static bool IsLaneMode(string lane, string mode) =>
        lane == "alignment" ? mode is "off" or "shadow" or "alert" or "block" : mode is "off" or "shadow" or "on";

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
