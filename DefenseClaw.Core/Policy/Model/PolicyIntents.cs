using System.Globalization;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Policy.Model;

/// <summary>A command the Policies panel proposes: the arguments after <c>defenseclaw</c>, and a sentence on what it does.</summary>
/// <param name="Label">The command in words (<c>guardrail block-at HIGH --connector codex</c>).</param>
/// <param name="Args">The arguments as handed to the CLI, without the executable.</param>
/// <param name="Hint">One sentence on what it does.</param>
public sealed record PolicyIntent(string Label, IReadOnlyList<string> Args, string Hint);

/// <summary>
/// The argv of each change the seven-view Policies panel can make, one builder per command, as the runtime's own
/// <c>policy_state</c> builds them (<c>activate_intent</c>, <c>use_pack_intent</c>, <c>mode_intent</c>, <c>threshold_intent</c>,
/// <c>level_intent</c>, <c>hilt_intent</c>, <c>protection_intent</c>). A builder throws <see cref="ArgumentException"/> for a value it
/// would not put on a command line: nothing here can be handed a name that reads as an option, a control character, or a level the CLI
/// does not take. Nothing is ever run here.
/// </summary>
public static partial class PolicyIntents
{
    /// <summary>The longest connector, policy or pack name the panel puts on a command line.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest folder path the panel puts on a command line (Windows' own limit for a path).</summary>
    public const int MaxPathLength = 520;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>True for a connector name the CLI can be handed: ASCII letters, digits, <c>.</c> <c>_</c> <c>-</c>, starting with a letter or digit.</summary>
    public static bool IsSafeConnector(string? name) => IsSafeName(name);

    /// <summary>True for a name (a policy, an opt-in pack, a connector) made of ASCII letters, digits, <c>.</c> <c>_</c> and <c>-</c> that starts with a letter or digit.</summary>
    public static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && !name.Contains("..", StringComparison.Ordinal) && NamePattern().IsMatch(name);

    /// <summary>
    /// True for what <c>guardrail use-pack</c> can be handed: a preset or pack name (<see cref="IsSafeName"/>), or a full folder path
    /// without control characters, not starting with <c>-</c>, within <see cref="MaxPathLength"/>.
    /// </summary>
    public static bool IsSafePackTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target.StartsWith('-') || target.Length > MaxPathLength || target.Any(char.IsControl))
        {
            return false;
        }

        return IsSafeName(target) || Path.IsPathFullyQualified(target);
    }

    private static string Name(string value, string what) =>
        IsSafeName(value) ? value : throw new ArgumentException($"Not a {what} the CLI can be handed.", nameof(value));

    private static List<string> WithConnector(List<string> args, string? connector)
    {
        if (!string.IsNullOrEmpty(connector))
        {
            args.Add("--connector");
            args.Add(Name(connector, "connector name"));
        }

        return args;
    }

    private static string ScopeWords(string? connector) => string.IsNullOrEmpty(connector) ? string.Empty : $" for {connector}";

    /// <summary><c>policy activate NAME</c>.</summary>
    public static PolicyIntent Activate(string name)
    {
        var safe = Name(name, "policy name");
        return new PolicyIntent($"policy activate {safe}", new[] { "policy", "activate", safe }, $"Activate the {safe} policy and reload the gateway.");
    }

    /// <summary><c>guardrail use-pack PACK [--connector C]</c>: a preset name, or the pack's folder.</summary>
    public static PolicyIntent UsePack(string pack, string? connector = null)
    {
        if (!IsSafePackTarget(pack))
        {
            throw new ArgumentException("Not a rule pack the CLI can be handed.", nameof(pack));
        }

        var args = WithConnector(new List<string> { "guardrail", "use-pack", pack }, connector);
        var scope = ScopeWords(connector);
        return new PolicyIntent(
            $"guardrail use-pack {pack}{scope}",
            args,
            $"Switch the guardrail rule pack{(scope.Length > 0 ? scope : " for every connector")} to {pack}.");
    }

    /// <summary><c>guardrail mode observe|action [--connector C]</c>.</summary>
    public static PolicyIntent Mode(string mode, string? connector = null)
    {
        if (mode is not ("observe" or "action"))
        {
            throw new ArgumentException($"Unknown mode '{mode}'.", nameof(mode));
        }

        var args = WithConnector(new List<string> { "guardrail", "mode", mode }, connector);
        return new PolicyIntent($"guardrail mode {mode}{ScopeWords(connector)}", args, $"Set the guardrail mode{ScopeWords(connector)} to {mode}.");
    }

    /// <summary>
    /// <c>policy edit guardrail --block-threshold N [-p NAME]</c> (or <c>--alert-threshold</c>): a named policy's level for LLM traffic through
    /// the guardrail proxy, as a rank 1 to 4. A built-in policy is copied to the policy folder first.
    /// </summary>
    /// <param name="kind"><c>block</c> or <c>alert</c>.</param>
    /// <param name="level">A threshold label (<c>HIGH+</c>).</param>
    /// <param name="policy">The policy to edit; empty means the active one.</param>
    public static PolicyIntent Threshold(string kind, string level, string? policy = null)
    {
        var rank = PolicyLevels.LevelRank(level) ?? throw new ArgumentException($"Unknown level '{level}'.", nameof(level));
        var block = string.Equals(kind, "block", StringComparison.Ordinal);
        if (!block && !string.Equals(kind, "alert", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown kind '{kind}'.", nameof(kind));
        }

        var flag = block ? "--block-threshold" : "--alert-threshold";
        var rankText = rank.ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "policy", "edit", "guardrail", flag, rankText };
        if (!string.IsNullOrEmpty(policy))
        {
            args.Add("-p");
            args.Add(Name(policy, "policy name"));
        }

        var label = $"policy edit guardrail {flag} {rankText}" + (string.IsNullOrEmpty(policy) ? string.Empty : $" -p {policy}");
        return new PolicyIntent(label, args, $"Make the {(string.IsNullOrEmpty(policy) ? "active" : policy)} policy {(block ? "block" : "alert")} LLM traffic at {level}.");
    }

    /// <summary>
    /// <c>guardrail block-at|alert-at LEVEL [--connector C]</c>: a scope's tool-call level. <paramref name="level"/> is a picker value
    /// (<c>CRITICAL</c>, <c>HIGH+</c>, <c>MEDIUM+</c>, <c>LOW+</c>) or <see cref="PolicyLevels.Inherit"/>, which clears the scope's own value.
    /// </summary>
    public static PolicyIntent Level(string kind, string level, string? connector = null)
    {
        string name;
        if (string.Equals(level, PolicyLevels.Inherit, StringComparison.Ordinal))
        {
            name = PolicyLevels.Inherit;
        }
        else
        {
            name = (level ?? string.Empty).Trim().TrimEnd('+').ToUpperInvariant();
            if (PolicyLevels.SeverityRankOf(name) is null)
            {
                throw new ArgumentException($"Unknown level '{level}'.", nameof(level));
            }
        }

        var block = string.Equals(kind, "block", StringComparison.Ordinal);
        if (!block && !string.Equals(kind, "alert", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown kind '{kind}'.", nameof(kind));
        }

        var verb = block ? "block-at" : "alert-at";
        var args = WithConnector(new List<string> { "guardrail", verb, name }, connector);
        return new PolicyIntent(
            $"guardrail {verb} {name}{ScopeWords(connector)}",
            args,
            $"Set the tool-call {(block ? "block" : "alert")} level{ScopeWords(connector)} to {name}.");
    }

    /// <summary>
    /// <c>guardrail hilt on --min-severity SEV [--connector C] --yes</c>, or <c>hilt off ... --yes</c>. <c>--yes</c> skips the CLI's own
    /// prompt, which the app cannot answer; the review in front of the command is where the operator decides.
    /// </summary>
    public static PolicyIntent Hilt(string level, string? connector = null)
    {
        List<string> args;
        if (string.Equals(level, "off", StringComparison.Ordinal))
        {
            args = new List<string> { "guardrail", "hilt", "off" };
        }
        else
        {
            var severity = (level ?? string.Empty).Trim().TrimEnd('+').ToUpperInvariant();
            if (PolicyLevels.SeverityRankOf(severity) is null)
            {
                throw new ArgumentException($"Unknown approval level '{level}'.", nameof(level));
            }

            args = new List<string> { "guardrail", "hilt", "on", "--min-severity", severity };
        }

        _ = WithConnector(args, connector);
        args.Add("--yes");
        var off = string.Equals(level, "off", StringComparison.Ordinal);
        return new PolicyIntent(
            $"guardrail hilt {(off ? "off" : "on " + level)}{ScopeWords(connector)}",
            args,
            $"Set human approval{ScopeWords(connector)} to {level}.");
    }

    /// <summary><c>guardrail protection enable|disable NAME [--connector C]</c>.</summary>
    public static PolicyIntent Protection(string name, bool enable, string? connector = null)
    {
        var safe = Name(name, "pack name");
        var verb = enable ? "enable" : "disable";
        var args = WithConnector(new List<string> { "guardrail", "protection", verb, safe }, connector);
        return new PolicyIntent(
            $"guardrail protection {verb} {safe}{ScopeWords(connector)}",
            args,
            $"Turn {(enable ? "on" : "off")} {safe}{(string.IsNullOrEmpty(connector) ? " for every connector" : ScopeWords(connector))}.");
    }

    /// <summary><c>guardrail validate-pack PATH --json</c>: the read that must say <c>valid</c> before a pack is switched to.</summary>
    public static IReadOnlyList<string> ValidatePack(string folder)
    {
        if (!IsSafePackTarget(folder) || !Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException("Not a rule pack folder the CLI can be handed.", nameof(folder));
        }

        return new[] { "guardrail", "validate-pack", folder, "--json" };
    }
}

/// <summary>
/// The only commands the Policies panel will start: the read-only catalog reads, the validation of a rule pack, and the seven changes the
/// Mac app's panel allows (<c>guardrail mode|block-at|alert-at|hilt|use-pack|protection</c>, <c>policy activate</c>, <c>policy edit
/// guardrail</c>). It checks the whole shape of an argv, not a prefix, so a catalog that went wrong cannot slip another command in.
/// </summary>
public static class PolicyActionGuard
{
    private static readonly string[] ReadsPolicyList = { "policy", "list", "--json" };
    private static readonly string[] ReadsListPacks = { "guardrail", "list-packs", "--json" };
    private static readonly string[] ReadsProtection = { "guardrail", "protection", "list", "--json" };
    private static readonly string[] ReadsGuardrailConfig = { "config", "show", "--section", "guardrail", "--format", "json" };
    private static readonly string[] ReadsPolicyValidate = { "policy", "validate" };

    /// <summary>The catalog reads, in the order the reader starts them. There is no read for sandbox packs: this app's model has no such view.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> CatalogReads { get; } = new IReadOnlyList<string>[]
    {
        ReadsPolicyList,
        ReadsListPacks,
        ReadsProtection,
        ReadsGuardrailConfig,
    };

    /// <summary>True for one of the read-only catalog commands, a pack validation, or <c>policy validate</c>.</summary>
    public static bool IsAllowedRead(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return Same(argv, ReadsPolicyList) || Same(argv, ReadsListPacks) || Same(argv, ReadsProtection) || Same(argv, ReadsGuardrailConfig)
               || Same(argv, ReadsPolicyValidate) || IsPackValidation(argv);
    }

    /// <summary><c>guardrail validate-pack PATH --json</c> with a folder the CLI can be handed.</summary>
    public static bool IsPackValidation(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return argv.Count == 4
               && argv[0] == "guardrail"
               && argv[1] == "validate-pack"
               && argv[3] == "--json"
               && PolicyIntents.IsSafePackTarget(argv[2])
               && Path.IsPathFullyQualified(argv[2]);
    }

    /// <summary>True for one of the seven changes, with every value in the shape the CLI takes.</summary>
    public static bool IsAllowedChange(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count < 3 || argv.Any(a => a is null || a.Contains('\0', StringComparison.Ordinal)))
        {
            return false;
        }

        if (argv[0] == "policy")
        {
            return IsActivate(argv) || IsThresholdEdit(argv);
        }

        if (argv[0] != "guardrail")
        {
            return false;
        }

        return argv[1] switch
        {
            "mode" => argv[2] is "observe" or "action" && OnlyConnector(argv, 3),
            "block-at" or "alert-at" => IsLevelWord(argv[2]) && OnlyConnector(argv, 3),
            "hilt" => IsHilt(argv),
            "use-pack" => PolicyIntents.IsSafePackTarget(argv[2]) && OnlyConnector(argv, 3),
            "protection" => argv.Count >= 4 && argv[2] is "enable" or "disable" && PolicyIntents.IsSafeName(argv[3]) && OnlyConnector(argv, 4),
            _ => false,
        };
    }

    private static bool IsActivate(IReadOnlyList<string> argv) =>
        argv.Count == 3 && argv[1] == "activate" && PolicyNames.IsSafe(argv[2]);

    private static bool IsThresholdEdit(IReadOnlyList<string> argv)
    {
        if (argv.Count is not (5 or 7) || argv[1] != "edit" || argv[2] != "guardrail" || argv[3] is not ("--block-threshold" or "--alert-threshold"))
        {
            return false;
        }

        if (!int.TryParse(argv[4], NumberStyles.None, CultureInfo.InvariantCulture, out var rank) || rank is < 1 or > 4)
        {
            return false;
        }

        return argv.Count == 5 || (argv[5] == "-p" && PolicyNames.IsSafe(argv[6]));
    }

    private static bool IsLevelWord(string word) => word == PolicyLevels.Inherit || (word.ToUpperInvariant() == word && PolicyLevels.SeverityRankOf(word) is not null);

    private static bool IsHilt(IReadOnlyList<string> argv)
    {
        // hilt off [--connector C] --yes   |   hilt on --min-severity SEV [--connector C] --yes
        int next;
        if (argv[2] == "off")
        {
            next = 3;
        }
        else if (argv[2] == "on" && argv.Count >= 5 && argv[3] == "--min-severity" && IsLevelWord(argv[4]) && argv[4] != PolicyLevels.Inherit)
        {
            next = 5;
        }
        else
        {
            return false;
        }

        if (argv.Count == next + 1)
        {
            return argv[next] == "--yes";
        }

        return argv.Count == next + 3 && argv[next] == "--connector" && PolicyIntents.IsSafeConnector(argv[next + 1]) && argv[next + 2] == "--yes";
    }

    private static bool OnlyConnector(IReadOnlyList<string> argv, int index)
    {
        if (argv.Count == index)
        {
            return true;
        }

        return argv.Count == index + 2 && argv[index] == "--connector" && PolicyIntents.IsSafeConnector(argv[index + 1]);
    }

    private static bool Same(IReadOnlyList<string> argv, string[] expected) => argv.Count == expected.Length && argv.SequenceEqual(expected, StringComparer.Ordinal);
}
