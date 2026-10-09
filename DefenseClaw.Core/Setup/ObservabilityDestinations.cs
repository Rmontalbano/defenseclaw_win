using DefenseClaw.Core.Text;

namespace DefenseClaw.Core.Setup;

/// <summary>
/// The kinds of observability destination the 0.8.10 CLI knows (<c>observability/destination_test.py</c>): the three that deliver to a remote
/// address, and the four that are local or pull-based and so cannot be connectivity-tested.
/// </summary>
public static class ObservabilityKinds
{
    /// <summary>Delivers to a remote address; <c>setup observability test</c> connects to it.</summary>
    public static readonly IReadOnlyList<string> Remote = ["otlp", "http_jsonl", "splunk_hec"];

    /// <summary>Stores on this PC or waits to be scraped: a database, a file, the console, a Prometheus listener. The CLI refuses to test them.</summary>
    public static readonly IReadOnlyList<string> Local = ["sqlite", "jsonl", "console", "prometheus"];

    /// <summary>True for a kind the CLI lists as local or pull-based. A kind nobody here has heard of is not local: the CLI decides what it can do.</summary>
    public static bool IsLocal(string? kind) =>
        kind is { Length: > 0 } && Local.Contains(kind.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One row of <c>defenseclaw setup observability list --json</c> (0.8.10 <c>cmd_setup_observability._print_v8_destination_list</c>): a
/// destination of the compiled plan, secret-free by construction. The row has <b>no member for a raw address</b>: the CLI's <c>target</c> is
/// reduced to its host when the row is read (<see cref="EndpointHost.ForDestination"/>), so nothing downstream can show the path or query of one.
/// </summary>
/// <param name="Name">The destination's name; what every verb is given.</param>
/// <param name="Kind"><c>otlp</c>, <c>http_jsonl</c>, <c>splunk_hec</c>, <c>sqlite</c>, <c>jsonl</c>, <c>console</c>, <c>prometheus</c>.</param>
/// <param name="Enabled">Whether the destination receives events.</param>
/// <param name="Generated">True for a destination the compiler adds itself (<c>local-sqlite</c>): it is not in config.yaml, so the CLI can neither disable nor remove it.</param>
/// <param name="Signals">The signals it is sent (<c>logs</c>, <c>traces</c>, <c>metrics</c>).</param>
/// <param name="Capabilities">The signals it could be sent.</param>
/// <param name="Policy">The form its routing is written in (<c>concise</c>, <c>advanced</c>, ...), as the CLI names it.</param>
/// <param name="BucketCount">How many event buckets its routes select.</param>
/// <param name="Redaction">The CLI's label: <c>not-applicable</c>, <c>unredacted (none)</c>, <c>redacted: sensitive</c>, <c>mixed: none, strict</c>.</param>
/// <param name="Endpoint">The host (and port) it delivers to, or a local destination's path; empty when it has neither.</param>
/// <param name="UnsupportedHere">The CLI's <c>platform_status</c> is <c>unsupported</c>: the destination belongs to a local stack this operating system cannot run.</param>
public sealed record ObservabilityDestination(
    string Name,
    string Kind,
    bool Enabled,
    bool Generated,
    IReadOnlyList<string> Signals,
    IReadOnlyList<string> Capabilities,
    string Policy,
    int BucketCount,
    string Redaction,
    string Endpoint,
    bool UnsupportedHere)
{
    /// <summary>The name of the one destination that always exists; the CLI says it "is mandatory and cannot be disabled or removed".</summary>
    public const string MandatoryName = "local-sqlite";

    /// <summary>The CLI's own word for the state: <c>enabled</c>, <c>disabled</c> or <c>unsupported</c> (what its table prints).</summary>
    public string State => UnsupportedHere ? "unsupported" : Enabled ? "enabled" : "disabled";

    /// <summary>True when the CLI will not connectivity-test this kind.</summary>
    public bool IsLocal => ObservabilityKinds.IsLocal(Kind);

    /// <summary>True for the destination the compiler cannot do without.</summary>
    public bool IsMandatory => string.Equals(Name, MandatoryName, StringComparison.Ordinal);

    /// <summary>True when what it is sent is not redacted at all (<c>unredacted (none)</c>), or only partly (<c>mixed: none, strict</c>).</summary>
    public bool SendsUnredacted =>
        Redaction.StartsWith("unredacted", StringComparison.OrdinalIgnoreCase) ||
        (Redaction.StartsWith("mixed", StringComparison.OrdinalIgnoreCase) && Redaction.Contains("none", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads <c>defenseclaw setup observability list --json</c>. Every field the CLI prints is optional here except the two a row cannot do
/// without (<c>name</c> and <c>enabled</c>); a row without them fails the read - a destination left out of the list is a destination the
/// operator believes is not there. Nothing printed is a failed read, not an empty list (see <see cref="SetupJson"/>).
/// </summary>
public static class ObservabilityDestinationParser
{
    public static bool TryParse(string? text, out IReadOnlyList<ObservabilityDestination> rows, out string error) =>
        SetupJson.TryReadRows(text, "The destination list", Read, out rows, out error);

    private static ObservabilityDestination Read(System.Text.Json.JsonElement element)
    {
        var kind = SetupJson.StringOf(element, "kind").Trim();
        return new ObservabilityDestination(
            Name: SetupJson.RequiredName(element, "name"),
            Kind: kind,
            Enabled: SetupJson.RequiredFlag(element, "enabled"),
            Generated: SetupJson.Flag(element, "generated"),
            Signals: SetupJson.Strings(element, "signals"),
            Capabilities: SetupJson.Strings(element, "capabilities"),
            Policy: DisplayNames.Visible(SetupJson.StringOf(element, "policy").Trim()),
            BucketCount: Math.Max(0, SetupJson.Number(element, "bucket_count")),
            Redaction: DisplayNames.Visible(SetupJson.StringOf(element, "redaction").Trim()),
            Endpoint: EndpointHost.ForDestination(kind, SetupJson.StringOf(element, "target")),
            UnsupportedHere: string.Equals(SetupJson.StringOf(element, "platform_status").Trim(), "unsupported", StringComparison.OrdinalIgnoreCase));
    }
}
