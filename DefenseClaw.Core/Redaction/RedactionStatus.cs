using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.Core.Redaction;

/// <summary>One catalog bucket as <c>setup redaction status --json</c> reports it.</summary>
/// <param name="Name">One of <see cref="RedactionVocabulary.Buckets"/>.</param>
/// <param name="Signals">The signals collected for it (a subset of logs, traces, metrics).</param>
/// <param name="Profile">The redaction profile its events carry into the local store and every destination that follows the defaults.</param>
public sealed record RedactionBucket(string Name, IReadOnlyList<string> Signals, string Profile)
{
    /// <summary>True when the bucket is sent as recorded.</summary>
    public bool IsUnredacted => string.Equals(Profile, RedactionVocabulary.NoRedaction, StringComparison.Ordinal);

    /// <summary>"logs · traces · metrics", or "not collected".</summary>
    public string SignalsText => Signals.Count == 0 ? "not collected" : string.Join(" · ", Signals);

    /// <summary>The tone key of the profile badge: unredacted is a warning, any profile is fine.</summary>
    public string ProfileTone => IsUnredacted ? "Warn" : "Ok";
}

/// <summary>One destination as the status reports it: a place telemetry goes, with the profile it gets there.</summary>
/// <param name="Name">The destination's name (<c>local-sqlite</c>, or one the operator configured).</param>
/// <param name="Kind">sqlite, otlp, splunk_hec, jsonl ...</param>
/// <param name="Enabled">False for a destination configured but switched off.</param>
/// <param name="Generated">True for a destination the runtime builds itself (<c>local-sqlite</c>, the managed enterprise one): its policy is not the operator's.</param>
/// <param name="PolicyForm"><c>implicit_local</c>, <c>capability_default</c>, <c>concise_send</c>, <c>advanced_routes</c> or <c>disabled_no_policy</c>.</param>
/// <param name="Signals">The signals it takes.</param>
/// <param name="Profiles">The profiles its legs carry.</param>
/// <param name="Label">The CLI's own words: <c>unredacted (none)</c>, <c>redacted: sensitive</c>, <c>mixed: none, sensitive</c>.</param>
public sealed record RedactionDestination(
    string Name,
    string Kind,
    bool Enabled,
    bool Generated,
    string PolicyForm,
    IReadOnlyList<string> Signals,
    IReadOnlyList<string> Profiles,
    string Label)
{
    /// <summary>The managed enterprise destination: generated, locked, release-owned.</summary>
    public bool IsManaged => string.Equals(Name, RedactionVocabulary.ManagedDestination, StringComparison.Ordinal);

    /// <summary>True for a destination the operator can send, inherit and route: not a generated one.</summary>
    public bool IsConfigurable => !Generated;

    /// <summary>True when it has ordered routes (<c>route list</c> has something to say).</summary>
    public bool HasRoutes => string.Equals(PolicyForm, "advanced_routes", StringComparison.Ordinal);

    /// <summary>True when any leg carries the profile <c>none</c>.</summary>
    public bool IsUnredacted => Profiles.Contains(RedactionVocabulary.NoRedaction);

    public string SignalsText => Signals.Count == 0 ? "none" : string.Join(" · ", Signals);

    /// <summary>The policy form in words.</summary>
    public string PolicyFormText => PolicyForm switch
    {
        "implicit_local" => "built-in policy",
        "capability_default" => "everything it can take",
        "concise_send" => "one send policy",
        "advanced_routes" => "ordered routes",
        "disabled_no_policy" => "disabled, no policy",
        _ => PolicyForm,
    };

    /// <summary>Why the operator cannot change this destination here; empty for one they can.</summary>
    public string Lock => IsManaged
        ? "Managed by your organisation: locked."
        : Generated ? "Built-in: its policy is generated, so it is read-only here." : string.Empty;

    /// <summary>The tone key of the label: unredacted is a warning, redacted is fine, mixed is a warning.</summary>
    public string Tone => Profiles.Count == 0 ? "Neutral" : IsUnredacted ? "Warn" : "Ok";
}

/// <summary>A warning the compiled plan carries (an unsafe TLS mode on a destination, for instance).</summary>
public sealed record RedactionWarning(string Code, string Path, string Summary);

/// <summary>The local judge-body store, which no profile covers (<c>guardrail.retain_judge_bodies</c>).</summary>
/// <param name="Capture">True when the raw text of LLM-judge requests and responses is kept.</param>
/// <param name="Path">The database file.</param>
/// <param name="RetentionDays">How long it is kept; 0 is without limit.</param>
public sealed record RedactionJudgeBodies(bool Capture, string Path, int RetentionDays)
{
    /// <summary>Says plainly that the store is outside every profile (the CLI's own disclosure, shortened).</summary>
    public string Disclosure => !Capture
        ? "The raw LLM-judge text is not kept (guardrail.retain_judge_bodies is false)."
        : $"The raw LLM-judge request and response text is kept unredacted ({(RetentionDays == 0 ? "with no retention limit" : $"for {RetentionDays.ToString(CultureInfo.InvariantCulture)} days")}); a prompt can quote a secret. No redaction profile covers this store.";
}

/// <summary>
/// <c>defenseclaw setup redaction status --json</c>: what the compiled plan does with every bucket and destination right now.
/// </summary>
public sealed record RedactionStatus(
    string ConfigPath,
    string PlanDigest,
    int CatalogVersion,
    IReadOnlyList<RedactionBucket> Buckets,
    IReadOnlyList<RedactionDestination> Destinations,
    IReadOnlyList<RedactionWarning> Warnings,
    RedactionJudgeBodies JudgeBodies)
{
    /// <summary>The distinct profiles of the buckets, built-ins first.</summary>
    public IReadOnlyList<string> BucketProfiles => Buckets
        .Select(static b => b.Profile)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(static p => IndexOfBuiltIn(p))
        .ThenBy(static p => p, StringComparer.Ordinal)
        .ToArray();

    /// <summary>The one profile every bucket uses; empty when they differ or there are none.</summary>
    public string UniformProfile => BucketProfiles.Count == 1 ? BucketProfiles[0] : string.Empty;

    /// <summary>True when any bucket is sent as recorded.</summary>
    public bool HasUnredactedBucket => Buckets.Any(static b => b.IsUnredacted);

    /// <summary>True when every bucket is sent as recorded: the runtime's default, and what <c>status</c> says to change.</summary>
    public bool IsUnredacted => Buckets.Count > 0 && Buckets.All(static b => b.IsUnredacted);

    /// <summary>The destinations the operator can send, inherit and route.</summary>
    public IReadOnlyList<RedactionDestination> ConfigurableDestinations => Destinations.Where(static d => d.IsConfigurable).ToArray();

    /// <summary>The configurable destinations that have ordered routes to list.</summary>
    public IReadOnlyList<RedactionDestination> RoutedDestinations => Destinations.Where(static d => d.IsConfigurable && d.HasRoutes).ToArray();

    /// <summary>The tone key of the summary: any unredacted bucket is a warning (the TUI shows redaction in a warning colour when it is off).</summary>
    public string Tone => Buckets.Count == 0 ? "Neutral" : HasUnredactedBucket ? "Warn" : "Ok";

    /// <summary>One sentence for the top of the card.</summary>
    public string Summary
    {
        get
        {
            if (Buckets.Count == 0)
            {
                return "The runtime reported no buckets.";
            }

            if (UniformProfile is { Length: > 0 } only)
            {
                return only == RedactionVocabulary.NoRedaction
                    ? "Redaction is off: every bucket is sent as recorded."
                    : $"Every bucket uses the {only} profile.";
            }

            var parts = Buckets
                .GroupBy(static b => b.Profile, StringComparer.Ordinal)
                .OrderBy(static g => IndexOfBuiltIn(g.Key))
                .ThenBy(static g => g.Key, StringComparer.Ordinal)
                .Select(static g => $"{g.Key} on {g.Count().ToString(CultureInfo.InvariantCulture)}");
            return "Mixed profiles: " + string.Join(", ", parts) + $" of {Buckets.Count.ToString(CultureInfo.InvariantCulture)} buckets.";
        }
    }

    /// <summary>The short form for a status chip: "Redaction: none", "Redaction: sensitive", "Redaction: mixed".</summary>
    public string ChipText => Buckets.Count == 0
        ? "Redaction: unknown"
        : UniformProfile is { Length: > 0 } only ? $"Redaction: {only}" : "Redaction: mixed";

    private static int IndexOfBuiltIn(string profile)
    {
        for (var i = 0; i < RedactionVocabulary.BuiltInProfiles.Count; i++)
        {
            if (string.Equals(RedactionVocabulary.BuiltInProfiles[i], profile, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return RedactionVocabulary.BuiltInProfiles.Count;
    }
}

/// <summary>
/// Reads the output of the read operations. Every method is total: a missing member is its default, a wrong shape is a <c>false</c> with
/// a reason, and nothing throws. A CLI that prints a banner line before the JSON still parses (the first line that starts <c>{</c> wins).
/// </summary>
public static class RedactionStatusParser
{
    /// <summary>Parses <c>setup redaction status --json</c>.</summary>
    public static bool TryParse(string? text, out RedactionStatus? status, out string error)
    {
        status = null;
        if (!RedactionJson.TryReadObject(text, out var root, out error))
        {
            return false;
        }

        if (!root.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
        {
            error = "The status has no list of buckets.";
            return false;
        }

        var planDigest = RedactionJson.Text(root, "plan_digest");
        if (planDigest.Length == 0)
        {
            error = "The status has no plan digest.";
            return false;
        }

        var parsedBuckets = new List<RedactionBucket>();
        foreach (var item in buckets.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object))
        {
            var name = RedactionJson.Text(item, "name");
            if (name.Length > 0)
            {
                parsedBuckets.Add(new RedactionBucket(name, RedactionJson.TextList(item, "signals"), RedactionJson.Text(item, "redaction_profile")));
            }
        }

        var destinations = new List<RedactionDestination>();
        if (root.TryGetProperty("destinations", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object))
            {
                var name = RedactionJson.Text(item, "name");
                if (name.Length == 0)
                {
                    continue;
                }

                destinations.Add(new RedactionDestination(
                    name,
                    RedactionJson.Text(item, "kind"),
                    RedactionJson.Flag(item, "enabled"),
                    RedactionJson.Flag(item, "generated"),
                    RedactionJson.Text(item, "policy_form"),
                    RedactionJson.TextList(item, "signals"),
                    RedactionJson.TextList(item, "redaction_profiles"),
                    RedactionJson.Text(item, "redaction")));
            }
        }

        var judge = root.TryGetProperty("judge_bodies", out var j) && j.ValueKind == JsonValueKind.Object
            ? new RedactionJudgeBodies(RedactionJson.Flag(j, "capture"), RedactionJson.Text(j, "path"), RedactionJson.Number(j, "retention_days"))
            : new RedactionJudgeBodies(false, string.Empty, 0);

        status = new RedactionStatus(
            RedactionJson.Text(root, "config"),
            planDigest,
            Math.Max(RedactionJson.Number(root, "bucket_catalog_version"), 0),
            parsedBuckets,
            destinations,
            RedactionJson.Warnings(root),
            judge);
        return true;
    }
}

/// <summary>Small shared readers over <see cref="JsonElement"/> for the redaction outputs.</summary>
internal static class RedactionJson
{
    /// <summary>
    /// The first JSON object in <paramref name="text"/>: the whole text when it is one, else the text from the first line that starts with
    /// <c>{</c> (the CLI can print a notice before JSON). Fails with a reason, never throws.
    /// </summary>
    public static bool TryReadObject(string? text, out JsonElement root, out string error)
    {
        root = default;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "The command printed nothing.";
            return false;
        }

        string? lastError = null;
        foreach (var candidate in Candidates(trimmed))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    lastError = "The output is JSON, but not an object.";
                    continue;
                }

                root = document.RootElement.Clone();
                error = string.Empty;
                return true;
            }
            catch (JsonException ex)
            {
                lastError = ex.Message;
            }
        }

        error = lastError ?? "The output is not JSON.";
        return false;
    }

    private static IEnumerable<string> Candidates(string trimmed)
    {
        if (trimmed.StartsWith('{'))
        {
            yield return trimmed;
        }

        var from = 0;
        while (true)
        {
            var index = trimmed.IndexOf("\n{", from, StringComparison.Ordinal);
            if (index < 0)
            {
                yield break;
            }

            yield return trimmed[(index + 1)..];
            from = index + 1;
        }
    }

    public static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    public static bool Flag(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static int Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    public static IReadOnlyList<string> TextList(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.String).Select(static e => e.GetString() ?? string.Empty).Where(static s => s.Length > 0).ToArray()
            : [];

    public static IReadOnlyList<RedactionWarning> Warnings(JsonElement root) =>
        root.TryGetProperty("warnings", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(static e => e.ValueKind == JsonValueKind.Object)
                .Select(static e => new RedactionWarning(Text(e, "code"), Text(e, "path"), Text(e, "summary")))
                .ToArray()
            : [];
}
