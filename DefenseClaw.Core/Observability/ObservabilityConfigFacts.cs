using System.Globalization;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Text;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace DefenseClaw.Core.Observability;

/// <summary>One destination as config.yaml declares it: what it is called, what kind it is, and where it sends (as <see cref="EndpointDisplay"/> allows it to be shown).</summary>
/// <param name="Name">The destination's name, as <see cref="ObservabilityPlanParser.CleanName"/> writes it.</param>
/// <param name="Kind">The kind (<c>otlp</c>, <c>splunk_hec</c>, <c>http_jsonl</c>, <c>jsonl</c>, <c>prometheus</c>…), or the preset when it names one; empty when config.yaml names neither.</param>
/// <param name="Endpoint">
/// <c>host[:port]</c> of the destination's endpoint (and of each signal override's, distinct, joined by <c>, </c>); the file a <c>jsonl</c> destination
/// writes; the address a <c>prometheus</c> destination listens on. Never a raw URL, never userinfo, a path or a query: the raw value is not kept.
/// Empty when config.yaml gives none (a preset supplies its own).
/// </param>
public sealed record ConfiguredDestination(string Name, string Kind, string Endpoint);

/// <summary>
/// What the Observability card needs from config.yaml that <c>observability plan --format json</c> does not carry (the plan has rows and delivery
/// limits, and no address, no retention window, no path and no judge-body setting): the local store's retention window and files, whether the raw
/// LLM-judge text is kept, and where each destination sends. These are the source values the plan was compiled from, so they say the same as
/// the TUI's compiled view - except where config.yaml is silent and the runtime's default applies, which is why <see cref="RetentionDays"/> and the
/// two paths are null then and not a guess.
/// <para>
/// <b>Read from the text the app already holds</b> (<see cref="ConfigDocument.RawText"/>): no file is opened and no process runs. A document that
/// cannot be read as a mapping is "says nothing", as it is for <c>JudgeHistoryReader</c>. <b>The raw endpoint is never kept:</b> it is reduced to
/// <see cref="ConfiguredDestination.Endpoint"/> while it is read, so nothing built on this record can show a credential that was in a URL.
/// </para>
/// </summary>
/// <param name="RetentionDays"><c>observability.local.retention_days</c>: a whole number of days, 0 for without limit; null when config.yaml does not set one (or sets one that is not a non-negative whole number).</param>
/// <param name="LocalPath"><c>observability.local.path</c> (the event history), as written; null when not set.</param>
/// <param name="JudgeBodiesPath"><c>observability.local.judge_bodies_path</c>, as written; null when not set.</param>
/// <param name="JudgeCapture"><c>guardrail.retain_judge_bodies</c>: true unless config.yaml says anything but true (the runtime's default is on).</param>
/// <param name="Destinations">The entries of <c>observability.destinations</c> that have a name.</param>
public sealed record ObservabilityConfigFacts(
    long? RetentionDays,
    string? LocalPath,
    string? JudgeBodiesPath,
    bool JudgeCapture,
    IReadOnlyList<ConfiguredDestination> Destinations)
{
    /// <summary>The longest config.yaml read (it is a few KiB).</summary>
    public const int MaxLength = 1024 * 1024;

    public const int MaxDestinations = 256;

    /// <summary>The largest whole-day period a Go <c>time.Duration</c> can hold, which is the runtime's own ceiling for <c>retention_days</c>.</summary>
    public const long MaxRetentionDays = 106_751;

    private const int MaxFieldLength = 1024;

    /// <summary>A config.yaml that says nothing about any of this.</summary>
    public static ObservabilityConfigFacts Empty { get; } = new(null, null, null, true, Array.Empty<ConfiguredDestination>());

    /// <summary>The destination called <paramref name="name"/> (without case), or null.</summary>
    public ConfiguredDestination? Destination(string name) =>
        Destinations.FirstOrDefault(d => string.Equals(d.Name, ObservabilityPlanParser.CleanName(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>The facts in <paramref name="document"/>; <see cref="Empty"/> for a null one.</summary>
    public static ObservabilityConfigFacts FromConfig(ConfigDocument? document) => document is null ? Empty : FromYaml(document.RawText);

    /// <summary>The facts in config.yaml's text. Never throws: text that is not a YAML mapping, or is too large, is <see cref="Empty"/>.</summary>
    public static ObservabilityConfigFacts FromYaml(string? yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.Length > MaxLength)
        {
            return Empty;
        }

        Dictionary<string, object?>? root;
        try
        {
            root = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object?>>(yaml);
        }
        catch (Exception ex) when (ex is YamlException or InvalidCastException or InvalidOperationException or ArgumentException or FormatException)
        {
            return Empty;
        }

        if (root is null)
        {
            return Empty;
        }

        var observability = Map(root.GetValueOrDefault("observability"));
        var local = Map(observability?.GetValueOrDefault("local"));
        var guardrail = Map(root.GetValueOrDefault("guardrail"));

        return new ObservabilityConfigFacts(
            Days(local?.GetValueOrDefault("retention_days")),
            FilePath(local?.GetValueOrDefault("path")),
            FilePath(local?.GetValueOrDefault("judge_bodies_path")),
            guardrail is null || !guardrail.ContainsKey("retain_judge_bodies") || IsTrue(guardrail["retain_judge_bodies"]),
            ReadDestinations(observability?.GetValueOrDefault("destinations")));
    }

    private static List<ConfiguredDestination> ReadDestinations(object? node)
    {
        var result = new List<ConfiguredDestination>();
        if (node is not IEnumerable<object?> items || node is string)
        {
            return result;
        }

        foreach (var item in items)
        {
            if (result.Count >= MaxDestinations || Map(item) is not { } destination ||
                ObservabilityPlanParser.CleanName(Scalar(destination, "name")) is not { Length: > 0 } name)
            {
                continue;
            }

            var kind = Scalar(destination, "preset") ?? Scalar(destination, "kind") ?? string.Empty;
            result.Add(new ConfiguredDestination(name, Cut(DisplayNames.Visible(kind).Trim(), 64), Endpoint(destination, Scalar(destination, "kind"))));
        }

        return result;
    }

    /// <summary>Where a destination sends, as much of it as may be shown (see <see cref="ConfiguredDestination.Endpoint"/>).</summary>
    private static string Endpoint(IDictionary<object, object?> destination, string? kind)
    {
        // A file-writing destination has a path and no host: show the path unless somebody put a URL in it.
        if (string.Equals(kind, "jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return FilePath(destination.GetValueOrDefault("path")) is { } file
                ? file.Contains("://", StringComparison.Ordinal) ? EndpointDisplay.Host(file) : Cut(DisplayNames.Visible(file), MaxFieldLength)
                : string.Empty;
        }

        var hosts = new List<string>();
        void Add(string? raw)
        {
            if (EndpointDisplay.Host(raw) is { Length: > 0 } host && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                hosts.Add(host);
            }
        }

        Add(Scalar(destination, "endpoint"));
        Add(Scalar(destination, "listen"));

        if (Map(destination.GetValueOrDefault("signal_overrides")) is { } overrides)
        {
            foreach (var signal in new[] { "logs", "traces", "metrics" })
            {
                if (Map(overrides.GetValueOrDefault(signal)) is { } one)
                {
                    Add(Scalar(one, "endpoint"));
                }
            }
        }

        return string.Join(", ", hosts);
    }

    /// <summary>A mapping node, whichever key type the deserializer produced; null for anything else.</summary>
    private static IDictionary<object, object?>? Map(object? node) =>
        node switch
        {
            IDictionary<object, object?> map => map,
            IDictionary<string, object?> typed => typed.ToDictionary(static pair => (object)pair.Key, static pair => pair.Value),
            _ => null,
        };

    private static string? Scalar(IDictionary<object, object?> map, string key) =>
        map.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string? FilePath(object? node) =>
        node is string text && !string.IsNullOrWhiteSpace(text) && text.Length <= MaxFieldLength ? text.Trim() : null;

    /// <summary>A whole number of days (0 or more); null for any other text, including a negative number, a fraction, or a number too large to be days.</summary>
    private static long? Days(object? node) =>
        node is string text && long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days <= MaxRetentionDays
            ? days
            : null;

    private static bool IsTrue(object? node) => node is string text && string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private static string Cut(string text, int limit) => text.Length <= limit ? text : text[..limit];
}

internal static class DictionaryLookup
{
    /// <summary>The value for <paramref name="key"/> in a mapping whose keys are plain strings, or null.</summary>
    public static TValue? GetValueOrDefault<TValue>(this IDictionary<object, TValue?> map, string key) =>
        map.TryGetValue(key, out var value) ? value : default;
}
