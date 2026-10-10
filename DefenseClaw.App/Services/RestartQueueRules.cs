using System.IO;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.Services;

/// <summary>
/// What does, and does not, leave a gateway restart pending (CUST-267): the pure half of <see cref="RestartQueue"/>, so each rule is a table
/// in a test and the service is only state.
/// <para>
/// <b>No rule of its own for "does this restart the gateway".</b> The answer is <see cref="CommandReview.RestartsGatewayFor"/> - the rule the
/// review states before a run and <see cref="CommandOutcome"/> claims after it ("gateway restarted") - asked of the command without its
/// <c>--no-restart</c>: a verb that would have restarted the gateway and was told not to has saved something the running gateway does not have.
/// That rule knows the <c>setup</c> and <c>guardrail</c> verbs only; for any other verb that takes the flag (<c>agent discovery enable</c>,
/// <c>agent discovery runtime enable</c>) the flag itself is the evidence, because the CLI exits non-zero on an option it does not know.
/// </para>
/// </summary>
internal static class RestartQueueRules
{
    /// <summary>The CLI's own switch: a verb that rewrites config.yaml restarts the gateway unless it is given this.</summary>
    public const string NoRestartFlag = "--no-restart";

    /// <summary>The longest reason the queue keeps; a longer one is cut (it is text for a banner and a row, not a log).</summary>
    public const int MaxReasonLength = 200;

    /// <summary>The longest stretch of a command's words a reason quotes.</summary>
    private const int MaxCommandWords = 60;

    /// <summary>How many sections a config save names before "…".</summary>
    private const int MaxSectionsNamed = 4;

    /// <summary>YAML nesting past this is not compared: a document that deep is called changed.</summary>
    private const int MaxDepth = 64;

    /// <summary>Go's zero time is year 1; a start time before this is the gateway not knowing its own.</summary>
    private static readonly DateTimeOffset EarliestStart = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>How far ahead of the poll a start time may be before it is not believed (clock skew; a hostile answer).</summary>
    private static readonly TimeSpan StartSkew = TimeSpan.FromMinutes(1);

    // ------------------------------------------------------------------ a finished run

    /// <summary>
    /// Why a finished command leaves a restart pending, or null when it does not. It does when it succeeded, ran the <c>defenseclaw</c> CLI with
    /// a standalone <c>--no-restart</c>, is not a read or a preview (<see cref="InstallationGate.IsReadOnly"/>, the runner's own word), and -
    /// for the <c>setup</c> and <c>guardrail</c> verbs <see cref="CommandReview.RestartsGatewayFor"/> knows - would have restarted the gateway
    /// without the flag. A command that restarted it (<see cref="CommandOutcome.GatewayRestarted"/>) clears the queue instead; that is
    /// <see cref="RestartQueue.Note"/>'s, not a rule of this method.
    /// </summary>
    /// <param name="tool">What the argv was handed to: <c>defenseclaw</c> or <c>defenseclaw-gateway</c> (the gateway's verbs take no such flag).</param>
    /// <param name="argv">The arguments, without the executable.</param>
    /// <param name="succeeded">Exit code 0 and nothing that ended the run early.</param>
    public static string? PendingReasonFor(string tool, IReadOnlyList<string> argv, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(argv);

        if (!succeeded || !string.Equals(tool, CommandReview.DefaultExecutable, StringComparison.Ordinal))
        {
            return null;
        }

        // Only a flag counts, as in RestartsGatewayFor: the value of another option ("--connector --no-restart") or anything after "--" is not one.
        var terminator = IndexOfTerminator(argv);
        var options = terminator < 0 ? argv : argv.Take(terminator).ToArray();
        var withoutFlag = new List<string>(argv.Count);
        var told = false;
        for (var i = 0; i < argv.Count; i++)
        {
            if (i < options.Count && string.Equals(argv[i], NoRestartFlag, StringComparison.OrdinalIgnoreCase) && CommandTiers.IsStandaloneFlag(options, i))
            {
                told = true;
                continue;
            }

            withoutFlag.Add(argv[i]);
        }

        if (!told)
        {
            return null;
        }

        // A preview and a read changed nothing: there is nothing for a restart to apply.
        if (InstallationGate.IsReadOnly(tool, argv))
        {
            return null;
        }

        // The verbs the review's rule knows: if it says this one restarts nothing even without the flag, nothing is waiting.
        if (argv.Count > 0 &&
            (string.Equals(argv[0], "setup", StringComparison.OrdinalIgnoreCase) || string.Equals(argv[0], "guardrail", StringComparison.OrdinalIgnoreCase)) &&
            !CommandReview.RestartsGatewayFor(withoutFlag))
        {
            return null;
        }

        return Tidy($"{WordsOf(tool, argv)} ran with {NoRestartFlag}");
    }

    /// <summary>The first words of the command as the TUI labels it (<c>guardrail hilt</c>, <c>setup claudecode</c>), drawn safe for one line.</summary>
    private static string WordsOf(string tool, IReadOnlyList<string> argv)
    {
        var label = CommandNextAction.LabelFor(tool, argv);
        var words = label.Length > tool.Length ? label[(tool.Length + 1)..] : string.Empty;
        words = DisplayNames.Visible(words).Trim();
        if (words.Length > MaxCommandWords)
        {
            words = words[..MaxCommandWords].TrimEnd() + "…";
        }

        return words.Length == 0 ? "a command" : words;
    }

    private static int IndexOfTerminator(IReadOnlyList<string> argv)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], "--", StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // ------------------------------------------------------------------ a config save

    /// <summary>What the config editor's reason starts with.</summary>
    public const string ConfigSavedText = "config.yaml saved in the config editor";

    /// <summary>What the reason of a restore from a backup says.</summary>
    public const string ConfigRestoredText = "config.yaml restored from a backup in the config editor";

    /// <summary>
    /// Why a save of <paramref name="after"/> over <paramref name="before"/> leaves a restart pending, or null when it changed no value. The gateway
    /// reads config.yaml when it starts, so what it has is the keys and values; a comment, spacing, blank lines, key order or a different
    /// style of quoting a string cannot change what it reads and queue nothing. The reason names the top-level sections that differ
    /// (<c>guardrail, llm</c>) and never a value. A text that does not parse cannot be compared and is called changed.
    /// </summary>
    /// <param name="before">What config.yaml held.</param>
    /// <param name="after">What it holds now.</param>
    /// <param name="what">How the reason starts: <see cref="ConfigSavedText"/> or <see cref="ConfigRestoredText"/>.</param>
    public static string? ConfigSaveReason(string before, string after, string what = ConfigSavedText)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(what);

        var change = CompareConfig(before, after);
        if (!change.Changed)
        {
            return null;
        }

        var shown = change.Sections.Take(MaxSectionsNamed).ToArray();
        if (shown.Length == 0)
        {
            return what;
        }

        return $"{what} ({string.Join(", ", shown)}{(change.Sections.Count > shown.Length ? ", …" : string.Empty)})";
    }

    /// <summary>The keys and values of two config.yaml texts compared, and which top-level sections differ.</summary>
    internal readonly record struct ConfigChange(bool Changed, IReadOnlyList<string> Sections);

    internal static ConfigChange CompareConfig(string before, string after)
    {
        YamlNode? oldRoot;
        YamlNode? newRoot;
        try
        {
            oldRoot = RootOf(before);
            newRoot = RootOf(after);
        }
        catch (YamlException)
        {
            return new ConfigChange(true, Array.Empty<string>());
        }

        if (oldRoot is not YamlMappingNode oldMap || newRoot is not YamlMappingNode newMap)
        {
            return new ConfigChange(!Same(oldRoot, newRoot, 0), Array.Empty<string>());
        }

        var changed = false;
        var sections = new List<string>();
        foreach (var (key, value) in newMap.Children)
        {
            if (!oldMap.Children.TryGetValue(key, out var previous) || !Same(previous, value, 0))
            {
                changed = true;
                NameSection(sections, key);
            }
        }

        foreach (var (key, _) in oldMap.Children)
        {
            if (!newMap.Children.ContainsKey(key))
            {
                changed = true;
                NameSection(sections, key);
            }
        }

        return new ConfigChange(changed, sections);
    }

    private static YamlNode? RootOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // A refused text reads like one that does not parse: the caller reports the change as unknown (CUST-341).
        if (DefenseClaw.Core.Config.ConfigYamlGuard.Refusal(text) is { } refusal)
        {
            throw new YamlException(refusal);
        }

        var stream = new YamlStream();
        using var reader = new StringReader(text);
        stream.Load(reader);
        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
    }

    /// <summary>
    /// True when two nodes say the same thing to the gateway: the same text for a scalar (plain against quoted counts - <c>true</c> and
    /// <c>"true"</c> are a bool and a string), the same items in the same order for a list, the same keys with the same values for a map.
    /// </summary>
    private static bool Same(YamlNode? a, YamlNode? b, int depth)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || depth > MaxDepth)
        {
            return false;
        }

        switch (a)
        {
            case YamlScalarNode x when b is YamlScalarNode y:
                return string.Equals(x.Value, y.Value, StringComparison.Ordinal) && (x.Style == ScalarStyle.Plain) == (y.Style == ScalarStyle.Plain);

            case YamlSequenceNode xs when b is YamlSequenceNode ys:
                return xs.Children.Count == ys.Children.Count && xs.Children.Zip(ys.Children).All(pair => Same(pair.First, pair.Second, depth + 1));

            case YamlMappingNode xm when b is YamlMappingNode ym:
                return xm.Children.Count == ym.Children.Count &&
                       xm.Children.All(pair => ym.Children.TryGetValue(pair.Key, out var other) && Same(pair.Value, other, depth + 1));

            default:
                return false;
        }
    }

    /// <summary>A section is named only when its key is a plain identifier (letters, digits, <c>_ - .</c>): config.yaml is the operator's file, and a key is still text from outside.</summary>
    private static void NameSection(List<string> sections, YamlNode key)
    {
        if (key is YamlScalarNode { Value: { Length: > 0 and <= 40 } name } && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') &&
            !sections.Contains(name, StringComparer.Ordinal))
        {
            sections.Add(name);
        }
    }

    // ------------------------------------------------------------------ the gateway's start

    /// <summary>
    /// When the gateway the snapshot describes started, or null when it does not say: <c>/health</c>'s <c>started_at</c>, else the poll's time less the
    /// <c>uptime_ms</c> it reported. A start time that is not believable (before 2000 - Go's zero time - or ahead of the poll) is not one.
    /// </summary>
    public static DateTimeOffset? GatewayStart(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Health is not { } health)
        {
            return null;
        }

        var polled = snapshot.PolledAt > DateTimeOffset.MinValue ? snapshot.PolledAt : (DateTimeOffset?)null;
        if (health.StartedAt is { } started && started >= EarliestStart && (polled is null || started <= polled.Value + StartSkew))
        {
            return started;
        }

        // Ten years in milliseconds: an uptime past that is a number the gateway made up, and TimeSpan would refuse it.
        if (polled is { } at && at >= EarliestStart && health.UptimeMs is > 0 and < 315_360_000_000L)
        {
            return at - TimeSpan.FromMilliseconds(health.UptimeMs);
        }

        return null;
    }

    // ------------------------------------------------------------------ text

    /// <summary>A reason as the queue keeps it: control characters spelled out, one line, trimmed, cut at <see cref="MaxReasonLength"/>.</summary>
    public static string Tidy(string? reason)
    {
        var text = DisplayNames.Visible(reason?.Trim()).Trim();
        return text.Length > MaxReasonLength ? text[..(MaxReasonLength - 1)].TrimEnd() + "…" : text;
    }
}
