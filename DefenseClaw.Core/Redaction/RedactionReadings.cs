using System.Text.Json;

namespace DefenseClaw.Core.Redaction;

/// <summary><c>profile list --json</c>: the names, built-ins first and then the custom ones the operator defined.</summary>
public sealed record RedactionProfileList(IReadOnlyList<string> Names)
{
    /// <summary>The custom profiles: every name that is not a built-in.</summary>
    public IReadOnlyList<string> Custom => Names.Where(static n => !RedactionVocabulary.BuiltInProfiles.Contains(n)).ToArray();

    public bool Contains(string name) => Names.Contains(name, StringComparer.Ordinal);
}

/// <summary><c>profile show NAME --json</c>: a compiled profile.</summary>
/// <param name="Name">Its name.</param>
/// <param name="BuiltIn">True for one of the four the runtime ships.</param>
/// <param name="Extends">The built-in profile a custom one starts from; empty for a built-in.</param>
/// <param name="Detectors">The detector groups it uses.</param>
/// <param name="FieldModes">What it does to each class of field, in the order the CLI prints them.</param>
public sealed record RedactionProfileDetail(
    string Name,
    bool BuiltIn,
    string Extends,
    IReadOnlyList<string> Detectors,
    IReadOnlyList<RedactionFieldMode> FieldModes)
{
    /// <summary>"built-in" or "custom, extends sensitive".</summary>
    public string Kind => BuiltIn ? "built-in" : Extends.Length > 0 ? $"custom, extends {Extends}" : "custom";

    public string DetectorsText => Detectors.Count == 0 ? "none" : string.Join(", ", Detectors);
}

/// <summary>What a route matches. Empty lists match anything.</summary>
public sealed record RedactionSelector(
    IReadOnlyList<string> Buckets,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Connectors,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> EventNames,
    string MinSeverity)
{
    public static RedactionSelector Any { get; } = new([], [], [], [], [], string.Empty);

    /// <summary>"buckets tool.activity · connector claudecode · HIGH and above", or "everything".</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>();
            AddList(parts, "buckets", Buckets);
            AddList(parts, "sources", Sources);
            AddList(parts, "connectors", Connectors);
            AddList(parts, "actions", Actions);
            AddList(parts, "events", EventNames);
            if (MinSeverity.Length > 0)
            {
                parts.Add($"{MinSeverity} and above");
            }

            return parts.Count == 0 ? "everything" : string.Join(" · ", parts);
        }
    }

    private static void AddList(List<string> parts, string label, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            parts.Add(label + " " + string.Join(", ", values));
        }
    }
}

/// <summary>One source-authored route of a destination, in the order the runtime tries them: the first match wins.</summary>
/// <param name="Position">One-based.</param>
/// <param name="Name">The route's name.</param>
/// <param name="Action">send or drop.</param>
/// <param name="Signals">The signals it routes.</param>
/// <param name="Selector">What it matches.</param>
/// <param name="Profile">The profile it sends with; empty when it inherits.</param>
public sealed record RedactionRoute(int Position, string Name, string Action, IReadOnlyList<string> Signals, RedactionSelector Selector, string Profile)
{
    public bool IsDrop => string.Equals(Action, "drop", StringComparison.Ordinal);

    /// <summary>
    /// "drop logs · buckets tool.activity · HIGH and above", "send logs, traces with strict · everything", or, for a send route that names no
    /// profile of its own, "send logs with each bucket's profile · everything" (what it sends is redacted as the bucket says). Metrics carry no
    /// content, so a route that sends only them has no profile to speak of.
    /// </summary>
    public string Summary
    {
        get
        {
            var profile = IsDrop || !Signals.Any(RedactionVocabulary.ContentSignals.Contains)
                ? string.Empty
                : Profile.Length > 0 ? $" with {Profile}" : " with each bucket's profile";
            return $"{Action} {(Signals.Count == 0 ? "no signals" : string.Join(", ", Signals))}{profile} · {Selector.Text}";
        }
    }
}

/// <summary><c>route list DESTINATION --json</c>.</summary>
public sealed record RedactionRouteList(string Destination, IReadOnlyList<RedactionRoute> Routes);

/// <summary>Parsers for the JSON the read operations print. Total: a wrong shape is a <c>false</c> and a reason, never an exception.</summary>
public static class RedactionReadings
{
    public static bool TryParseProfileList(string? text, out RedactionProfileList? list, out string error)
    {
        list = null;
        if (!RedactionJson.TryReadObject(text, out var root, out error))
        {
            return false;
        }

        if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            error = "The profile list has no profiles.";
            return false;
        }

        list = new RedactionProfileList(RedactionJson.TextList(root, "profiles"));
        return true;
    }

    public static bool TryParseProfile(string? text, out RedactionProfileDetail? profile, out string error)
    {
        profile = null;
        if (!RedactionJson.TryReadObject(text, out var root, out error))
        {
            return false;
        }

        var name = RedactionJson.Text(root, "name");
        if (name.Length == 0)
        {
            error = "The profile has no name.";
            return false;
        }

        var modes = new List<RedactionFieldMode>();
        if (root.TryGetProperty("field_classes", out var classes) && classes.ValueKind == JsonValueKind.Object)
        {
            foreach (var fieldClass in RedactionVocabulary.FieldClasses)
            {
                if (classes.TryGetProperty(fieldClass, out var mode) && mode.ValueKind == JsonValueKind.String)
                {
                    modes.Add(new RedactionFieldMode(fieldClass, mode.GetString() ?? string.Empty));
                }
            }

            // A class this app does not know still shows: after the known ones, in the CLI's order.
            foreach (var extra in classes.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && !RedactionVocabulary.FieldClasses.Contains(p.Name)))
            {
                modes.Add(new RedactionFieldMode(extra.Name, extra.Value.GetString() ?? string.Empty));
            }
        }

        profile = new RedactionProfileDetail(
            name,
            RedactionJson.Flag(root, "built_in"),
            RedactionJson.Text(root, "extends"),
            RedactionJson.TextList(root, "detectors"),
            modes);
        return true;
    }

    public static bool TryParseRoutes(string? text, out RedactionRouteList? routes, out string error)
    {
        routes = null;
        if (!RedactionJson.TryReadObject(text, out var root, out error))
        {
            return false;
        }

        if (!root.TryGetProperty("routes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            error = "The route list has no routes.";
            return false;
        }

        var parsed = new List<RedactionRoute>();
        foreach (var item in list.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object))
        {
            var selector = item.TryGetProperty("selector", out var s) && s.ValueKind == JsonValueKind.Object
                ? new RedactionSelector(
                    RedactionJson.TextList(s, "buckets"),
                    RedactionJson.TextList(s, "sources"),
                    RedactionJson.TextList(s, "connectors"),
                    RedactionJson.TextList(s, "actions"),
                    RedactionJson.TextList(s, "event_names"),
                    RedactionJson.Text(s, "min_severity"))
                : RedactionSelector.Any;

            var action = RedactionJson.Text(item, "action");
            parsed.Add(new RedactionRoute(
                parsed.Count + 1,
                RedactionJson.Text(item, "name"),
                action.Length == 0 ? "send" : action,
                RedactionJson.TextList(item, "signals"),
                selector,
                RedactionJson.Text(item, "redaction_profile")));
        }

        routes = new RedactionRouteList(RedactionJson.Text(root, "destination"), parsed);
        return true;
    }
}
