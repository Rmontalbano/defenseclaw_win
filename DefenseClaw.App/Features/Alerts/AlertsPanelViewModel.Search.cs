using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Alerts panel's side of CUST-261: the search box's <c>field:value</c> tokens (<see cref="SearchQuery"/>), the Connector column, and the correlation
/// ids of an alert.
/// <para>
/// <b>Search.</b> The filter box is parsed by the one shared parser: <c>connector:codex</c>, <c>severity:high</c>, <c>run:</c>, <c>trace:</c>, <c>request:</c>,
/// <c>session:</c>, <c>id:</c>, <c>actor:</c>, <c>type:</c>, <c>target:</c>, <c>action:</c> and <c>details:</c> narrow the row's own field (a connector by
/// name, the rest as a part of the value, case ignored; <c>target:"my skill"</c> keeps its space), anything else is free text, matched as a single phrase over
/// the rule, kind, title, action, target, evidence, scanner, tags - and, since CUST-261, the run, severity, source, connector and the correlation ids, so
/// a trace id pasted from another tool finds its alert. Tokens and free text are ANDed with the severity tiles, the kind and the shared connector scope.
/// </para>
/// <para>
/// <b>Connector column.</b> Shown only while there is more than one connector to tell apart (<see cref="ShowConnectorColumn"/>: the shared
/// <see cref="Services.ConnectorScope"/> has a roster of two or more, as the connector chip does). A one-connector install has the table it always had.
/// </para>
/// </summary>
public sealed partial class AlertsPanelViewModel
{
    /// <summary>The Connector column shows: more than one connector is active (the TUI's <c>show_connector_column</c>, set from the active connector count).</summary>
    public bool ShowConnectorColumn => Services.ConnectorScope.CanScope;

    /// <summary>The roster of connectors changed (the scope itself changing is <see cref="OnConnectorScopeChanged"/>'s): the column may appear or go.</summary>
    private void OnRosterChanged(object? sender, EventArgs e) => ConnectorColumn.Notify(() => OnPropertyChanged(nameof(ShowConnectorColumn)));

    /// <summary>The filter box, parsed. Cheap: a pass over the typed text.</summary>
    private SearchQuery ActiveSearch => SearchQuery.Parse(FilterText);
}

public sealed partial class AlertItem
{
    /// <summary>The trace id of the request that raised the finding (OpenTelemetry's 32 hex digits); empty when the source says none.</summary>
    public string TraceId { get; private init; } = string.Empty;

    /// <summary>The request id (one call through the gateway); empty when the source says none.</summary>
    public string RequestId { get; private init; } = string.Empty;

    /// <summary>The agent session the finding belongs to; empty when the source says none.</summary>
    public string SessionId { get; private init; } = string.Empty;

    /// <summary>The audit row's own details text (what the TUI's alert detail prints as <c>Details:</c>); empty for a row that came from the queue alone or from egress.</summary>
    public string Details { get; private init; } = string.Empty;

    /// <summary>The Connector column's cell: the connector, or a dash for a finding that belongs to none (the TUI's <c>—</c>).</summary>
    public string ConnectorCell => string.IsNullOrWhiteSpace(Connector) ? "—" : Connector.Trim();

    /// <summary>
    /// The inspector's Correlation rows: Run ID, Trace ID, Request ID and Session ID, whichever of them this alert has (the TUI's detail lists the same four, in
    /// this order). Empty when it has none.
    /// </summary>
    public IReadOnlyList<AlertField> Correlation => _correlation ??= BuildCorrelation();

    private IReadOnlyList<AlertField>? _correlation;

    private IReadOnlyList<AlertField> BuildCorrelation()
    {
        var rows = new List<AlertField>(4);
        Add(rows, "Run ID", RunId);
        Add(rows, "Trace ID", TraceId);
        Add(rows, "Request ID", RequestId);
        Add(rows, "Session ID", SessionId);
        return rows;
    }

    /// <summary>True when the inspector has correlation rows to show.</summary>
    public bool HasCorrelation => RunId.Length > 0 || TraceId.Length > 0 || RequestId.Length > 0 || SessionId.Length > 0;

    private static void Add(List<AlertField> rows, string name, string value)
    {
        if (value.Length > 0)
        {
            rows.Add(new AlertField(name, value));
        }
    }

    /// <summary>
    /// Copy details for one alert, in the TUI's multi-line form (<c>copy_detail_text</c>, its <c>y</c>): severity, action, target, timestamp, what the row
    /// says, then whichever of Run ID, Trace ID, Request ID and Session ID it has - the ids a person pastes into another tool to follow the request. A hook
    /// call reads its decision, mode and timing as labelled lines, as the TUI does. Several rows are copied one line each (<see cref="CopyLine"/>).
    /// </summary>
    public string CopyDetailText
    {
        get
        {
            var lines = new List<string>(10)
            {
                "Severity: " + DisplaySeverity,
                "Action: " + Action,
                "Target: " + (RawTarget.Length > 0 ? RawTarget : TargetRef),
                "Timestamp: " + Timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            };

            var hook = StructuredDetailParser.IsHook(Action) ? StructuredDetailParser.InspectorRows(Details) : Array.Empty<DetailPair>();
            if (hook.Count > 0)
            {
                lines.AddRange(hook.Select(pair => pair.Label + ": " + pair.Value));
            }
            else
            {
                var says = Details.Length > 0 ? Details : string.Equals(Headline, Action, StringComparison.Ordinal) ? string.Empty : Headline;
                if (says.Length > 0)
                {
                    lines.Add("Details: " + says);
                }
            }

            foreach (var id in Correlation)
            {
                lines.Add(id.Name + ": " + id.Value);
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// The severity the TUI prints for the row: its bucket (CRITICAL, HIGH, MEDIUM, LOW - a spelling like WARN or FATAL is the one it is coloured as), else
    /// the stored word.
    /// </summary>
    private string DisplaySeverity => SeverityKey == "Info" ? Severity : SeverityKey.ToUpperInvariant();

    /// <summary>True when this alert passes the search: every token on its field, the free text as a phrase in any of the fields a free search covers.</summary>
    internal bool Matches(SearchQuery search)
    {
        ArgumentNullException.ThrowIfNull(search);
        return search.IsEmpty || search.Matches(ValuesOf, Searchable());
    }

    /// <summary>The values this alert has for a search field. A connector is the connector alone: the shared scope's rule.</summary>
    private IEnumerable<string?> ValuesOf(SearchField field) => field switch
    {
        SearchField.Connector => new[] { Connector },
        SearchField.Severity => new[] { Severity, SeverityKey },
        SearchField.Run => new[] { RunId },
        SearchField.Id => new[] { AuditId },
        SearchField.Actor => new[] { Source },
        SearchField.Type => new[] { Kind, AuditTargetType.FromAction(Action) },
        SearchField.Target => new[] { TargetRef, RawTarget },
        SearchField.Action => new[] { Action },
        SearchField.Details => new[] { Details, Headline, Evidence },
        SearchField.Trace => new[] { TraceId },
        SearchField.Request => new[] { RequestId },
        SearchField.Session => new[] { SessionId },
        _ => Array.Empty<string?>(),
    };

    /// <summary>Everything a free-text search looks in: what it always did, and (CUST-261) the run, severity, source, connector, details and correlation ids.</summary>
    private IEnumerable<string?> Searchable()
    {
        yield return RuleId;
        yield return Kind;
        yield return Headline;
        yield return Action;
        yield return TargetRef;
        yield return Evidence;
        yield return Scanner;
        yield return Tags;
        yield return RunId;
        yield return Severity;
        yield return Source;
        yield return Connector;
        yield return Details;
        yield return TraceId;
        yield return RequestId;
        yield return SessionId;
    }

    /// <summary>A string out of a gateway alert's unmodelled members, under any of <paramref name="names"/> (the gateway spells ids <c>trace_id</c>); empty when there is none.</summary>
    private static string Extra(GatewayAlert alert, params string[] names)
    {
        if (alert.AdditionalData is not { } extra)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            if (extra.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return string.Empty;
    }
}
