namespace DefenseClaw.Core.Config;

/// <summary>One line of the Admission overrides card: <c>skill · high</c> and what the install gate does at that severity.</summary>
/// <param name="Label"><c>&lt;asset type&gt; · &lt;severity&gt;</c>.</param>
/// <param name="Action">A shorthand (<c>block</c>, <c>quarantine</c>, <c>warn</c>, <c>allow</c>) or the exact triple (<c>install=block, file=quarantine, runtime=disable</c>).</param>
public sealed record AdmissionOverrideRow(string Label, string Action);

/// <summary>
/// The per-asset-type, per-severity install-gate actions an operator wrote into config.yaml <c>admission.&lt;type&gt;.actions</c> (config_version 9; upstream
/// <c>internal/config/admission.go</c>: <c>AdmissionConfig</c>, <c>AdmissionAssetType.Actions</c>, <c>AdmissionAction</c>). The types are <c>defaults</c>, <c>skill</c>,
/// <c>mcp</c> and <c>plugin</c>; the severities <c>critical</c> to <c>info</c>; an action is a shorthand scalar or an <c>install</c>/<c>file</c>/<c>runtime</c> mapping,
/// as the Mac's <c>admissionOverrides</c> reads them. Only what config.yaml says is listed: a type that inherits everything has no rows, and nothing is derived
/// from the scanner gate the runtime falls back to. Read from text the app already holds; unreadable text is <see cref="Empty"/>.
/// </summary>
public sealed record AdmissionOverrides(IReadOnlyList<AdmissionOverrideRow> Rows)
{
    private static readonly string[] Types = { "defaults", "skill", "mcp", "plugin" };

    private static readonly string[] KnownSeverities = { "critical", "high", "medium", "low", "info" };

    private static readonly string[] TripleKeys = { "install", "file", "runtime", "verdict" };

    public static AdmissionOverrides Empty { get; } = new(Array.Empty<AdmissionOverrideRow>());

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The overrides in <paramref name="yaml"/> (config.yaml's text). Never throws.</summary>
    public static AdmissionOverrides FromYaml(string? yaml)
    {
        if (PolicyYamlTree.Root(yaml) is not { } root)
        {
            return Empty;
        }

        var admission = PolicyYamlTree.Child(root, "admission");
        var rows = new List<AdmissionOverrideRow>();
        foreach (var type in Types)
        {
            var actions = PolicyYamlTree.Child(PolicyYamlTree.Child(admission, type), "actions");
            if (actions is null)
            {
                continue;
            }

            var entries = actions
                .Select(static pair => (Severity: PolicyYamlTree.Raw(pair.Key), pair.Value))
                .Where(static entry => entry.Severity.Length > 0)
                .OrderBy(static entry => SeverityRank(entry.Severity))
                .ThenBy(static entry => entry.Severity, StringComparer.Ordinal);
            foreach (var (severity, value) in entries)
            {
                var text = ActionText(value);
                if (text.Length > 0)
                {
                    rows.Add(new AdmissionOverrideRow(type + " · " + PolicyYamlTree.Shown(severity), text));
                }
            }
        }

        return rows.Count == 0 ? Empty : new AdmissionOverrides(rows);
    }

    private static int SeverityRank(string severity)
    {
        var index = Array.IndexOf(KnownSeverities, severity.ToLowerInvariant());
        return index < 0 ? KnownSeverities.Length : index;
    }

    private static string ActionText(object? node)
    {
        if (node is string)
        {
            return PolicyYamlTree.Shown(node);
        }

        if (PolicyYamlTree.Map(node) is not { } triple)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var key in TripleKeys)
        {
            if (PolicyYamlTree.Shown(triple, key) is { Length: > 0 } value)
            {
                parts.Add(key + "=" + value);
            }
        }

        return string.Join(", ", parts);
    }
}
