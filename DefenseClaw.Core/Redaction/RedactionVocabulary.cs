using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Redaction;

/// <summary>
/// The fixed words of <c>defenseclaw setup redaction</c> on a runtime that has it (DefenseClaw source commit 95159fd:
/// <c>observability/v8_config.py</c> and <c>cmd_setup_redaction.py</c>). The CLI takes most of them as <c>click.Choice</c>s, so a word that
/// is not on these lists is refused by the CLI; the window offers only these, and checks them again before a command line is built.
/// </summary>
public static partial class RedactionVocabulary
{
    /// <summary>The 14 catalog-v1 buckets (groups of events), in the order the CLI lists them.</summary>
    public static IReadOnlyList<string> Buckets { get; } =
    [
        "compliance.activity", "security.finding", "guardrail.evaluation", "enforcement.action", "model.io", "tool.activity", "asset.scan",
        "asset.lifecycle", "network.egress", "agent.lifecycle", "ai.discovery", "telemetry.ingest", "platform.health", "diagnostic",
    ];

    /// <summary>The three telemetry signals a bucket can be collected as.</summary>
    public static IReadOnlyList<string> Signals { get; } = ["logs", "traces", "metrics"];

    /// <summary>The signals a redaction profile acts on: the preview and the status list legs for these two only (metrics carry no content).</summary>
    public static IReadOnlyList<string> ContentSignals { get; } = ["logs", "traces"];

    /// <summary>The severities a route can select on (<c>--min-severity</c>), lowest first.</summary>
    public static IReadOnlyList<string> Severities { get; } = ["INFO", "LOW", "MEDIUM", "HIGH", "CRITICAL"];

    /// <summary>The detector groups a custom profile can use (<c>--detector</c>).</summary>
    public static IReadOnlyList<string> DetectorGroups { get; } = ["pii", "credentials", "secrets"];

    /// <summary>The classes of field a profile sets a mode for (<c>--field CLASS=MODE</c>), in the order <c>profile show</c> prints them.</summary>
    public static IReadOnlyList<string> FieldClasses { get; } =
        ["metadata", "identifier", "content", "reason", "evidence", "error", "path", "credential"];

    /// <summary>What a profile can do to a field class.</summary>
    public static IReadOnlyList<string> FieldModes { get; } = ["preserve", "detect", "whole", "hash", "remove"];

    /// <summary>The profiles every runtime has. Only <see cref="NoRedaction"/> sends everything as recorded.</summary>
    public static IReadOnlyList<string> BuiltInProfiles { get; } = ["none", "sensitive", "content", "strict"];

    /// <summary>The built-in profiles a custom profile can extend (<c>--extends</c>): all but <c>none</c>.</summary>
    public static IReadOnlyList<string> CustomProfileBases { get; } = ["sensitive", "content", "strict"];

    /// <summary>What a route does with the events it matches (<c>--route-action</c>).</summary>
    public static IReadOnlyList<string> RouteActions { get; } = ["send", "drop"];

    /// <summary>The profile that redacts nothing: "unredacted".</summary>
    public const string NoRedaction = "none";

    /// <summary>What the preview prints for a leg that is not delivered at all, before or after the change.</summary>
    public const string NotDelivered = "not-delivered";

    /// <summary>The bucket wildcard of a send policy or a route selector: every bucket. It must be the only bucket named.</summary>
    public const string AllBuckets = "*";

    /// <summary>The mode word of <c>--field CLASS=MODE</c> that drops the class's own mode and falls back to the base profile's.</summary>
    public const string InheritMode = "inherit";

    /// <summary>The destination the runtime always has: the local SQLite event store. Generated, so its policy is read-only here.</summary>
    public const string LocalDestination = "local-sqlite";

    /// <summary>The destination of a managed enterprise install. Generated, locked, and release-owned.</summary>
    public const string ManagedDestination = "managed-enterprise-ai-defense";

    /// <summary>One line on what a built-in profile does, in the CLI's own words (<c>setup redaction --help</c>).</summary>
    public static string Describe(string profile) => profile switch
    {
        "none" => "No redaction: everything is sent as recorded.",
        "sensitive" => "Removes credentials, hashes file paths, and masks PII, credentials and secrets found in content, reasons, evidence and errors.",
        "content" => "Like sensitive, but replaces content, reasons, evidence and errors whole.",
        "strict" => "Keeps only metadata and identifiers; removes everything else.",
        _ => "A custom profile.",
    };

    /// <summary>True for the two destinations the runtime generates: their policy is not the operator's to send, inherit or route.</summary>
    public static bool IsGeneratedDestination(string? name) =>
        string.Equals(name, LocalDestination, StringComparison.Ordinal) || string.Equals(name, ManagedDestination, StringComparison.Ordinal);

    /// <summary>
    /// True for a name the runtime accepts for a profile, destination or route (<c>^[a-z0-9][a-z0-9_-]{0,63}$</c>): lower-case letters, digits,
    /// <c>_</c> and <c>-</c>, starting with a letter or digit. It cannot read as an option (no leading <c>-</c>) and carries no space, quote or
    /// character the CLI would expand on Windows, so it is safe as a positional argument.
    /// </summary>
    public static bool IsStableName(string? name) => name is { Length: > 0 and <= 64 } && StableName().IsMatch(name);

    /// <summary>
    /// True for a value of a route selector (<c>--source</c>, <c>--connector</c>, <c>--producer-action</c>, <c>--event-name</c>): printable, no
    /// whitespace, not starting with <c>-</c>, and without a character the CLI re-expands on Windows (<c>* ? [ % $</c>, a leading <c>~</c>).
    /// </summary>
    public static bool IsSelectorValue(string? value) =>
        value is { Length: > 0 and <= 128 }
        && value[0] != '-'
        && !Cli.ArgvHazards.IsHazardous(value)
        && value.All(static c => c > ' ' && c != '\u007f' && !char.IsWhiteSpace(c) && !char.IsControl(c));

    // \z, not $: a trailing newline must not pass.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,63}\z", RegexOptions.CultureInvariant, 500)]
    private static partial Regex StableName();
}
