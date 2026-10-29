using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Tools panel: the tool block/allow rules DefenseClaw holds, read with
/// <c>defenseclaw tool list --json [--connector C]</c>.
/// <para>
/// <b>What "a tool" is here.</b> There is no tool catalog on a standalone install (<c>GET /tools/catalog</c> answers
/// "not connected"); <c>tool list</c> returns only the rules an operator or the auto-blocker created. Real 0.8.10
/// output is an array of per-connector groups <c>{"connector": c, "tools": [{name, connector, scope, status, reason,
/// updated_at}]}</c> (plus a trailing <c>{"connector": null, "scope": "source", …}</c> group when audit-only source
/// rows exist; a lone group object with <c>--connector</c>). <c>status</c> is the install action, <c>block</c> or
/// <c>allow</c> — not "blocked". <c>scope</c> says which rule this is, and it decides the flag an action needs:
/// <c>connector</c> rows are <c>@C/tool</c> (<c>--connector C</c>), <c>global</c> rows are the fallback for every
/// connector (no flag; the CLI repeats them under each connector's group, so they are listed once) and
/// <c>source</c> rows are audit-only (<c>--source S</c>).
/// </para>
/// <para>
/// A tool that has no rule yet is not listed, so the panel also has a "manage by name" card: block, allow or
/// check the effective status of any tool name, scoped by the toolbar.
/// </para>
/// </summary>
public sealed partial class ToolsPanelViewModel : GovernPanelViewModelBase
{
    private static readonly string[] ToolStatusFilters = { StatusAll, "Blocked", "Allowed" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManageError))]
    private string _manageError = string.Empty;

    [ObservableProperty] private string _manageToolName = string.Empty;

    public ToolsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Tools";

    public override string Description => "Tool block and allow rules, per connector or for every connector.";

    protected override string Noun => "tool";

    protected override string NounLabel => "tool";

    protected override string NounPlural => "tool rules";

    protected override string ItemsKey => "tools";

    protected override string InfoVerb => "status";

    protected override IReadOnlyList<string> StatusFilterChoices => ToolStatusFilters;

    public bool HasManageError => ManageError.Length > 0;

    protected override string BuildEmptyTitle(string scope) => $"No tool rules for {scope}";

    protected override string BuildEmptyDetail(string scope) =>
        "Tools have no catalog on a standalone install: this list shows only the tools that have been blocked or allowed. " +
        "Nothing here means no tool is being blocked or allowed by name; every tool still goes through the normal scan. " +
        "Use “Manage a tool by name” below to add a rule or to check what applies to a tool.";

    protected override GovernRow? ParseRow(JsonElement item, string? groupConnector)
    {
        var rawName = GovernJson.Str(item, "name");
        if (rawName is null)
        {
            return null;
        }

        var scope = GovernJson.Str(item, "scope") ?? "global";
        var status = GovernJson.Str(item, "status") ?? "none";
        var blocked = status.StartsWith("block", StringComparison.OrdinalIgnoreCase);
        var allowed = status.StartsWith("allow", StringComparison.OrdinalIgnoreCase);

        // The CLI names a source-scoped row "<source>/<tool>"; the verbs want the two apart.
        string name = rawName;
        string? sourceScope = null;
        if (scope == "source")
        {
            var slash = rawName.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && slash < rawName.Length - 1)
            {
                sourceScope = rawName[..slash];
                name = rawName[(slash + 1)..];
            }
        }

        // A global rule is listed under every connector's group with that connector filled in; it belongs to none.
        var connector = scope == "connector" ? ResolveConnector(GovernJson.Str(item, "connector"), groupConnector) : null;
        var state = new GovernItemState(blocked, allowed, false, false, blocked ? "blocked" : allowed ? "allowed" : status, null, null, null, null, null);

        var scopeText = scope switch
        {
            "connector" => connector is null ? "one connector" : $"connector {connector}",
            "source" => sourceScope is null ? "a source (audit only)" : $"source {sourceScope} (audit only)",
            _ => "every connector (fallback)",
        };

        var verbs = GovernVerbs.Info | GovernVerbs.CopyName | GovernVerbs.Unblock;
        if (scope != "source")
        {
            verbs |= blocked ? GovernVerbs.Allow : GovernVerbs.Block;
        }

        var reason = GovernJson.Str(item, "reason");
        var updated = FormatTimestamp(GovernJson.Str(item, "updated_at"));

        var fields = new List<GovernField>();
        AddField(fields, "Tool", name);
        AddField(fields, "Applies to", scopeText);
        AddField(fields, "Status", state.Label);
        AddField(fields, "Reason", reason);
        AddField(fields, "Updated", updated);

        return new GovernRow(this)
        {
            Noun = Noun,
            Name = name,
            Connector = connector,
            RuleScope = scope,
            SourceScope = sourceScope,
            Description = scope == "source"
                ? "Source-scoped rules are recorded for audit only; the runtime never reads them, so they do not block or allow anything."
                : null,
            MetaLine = JoinMeta(("applies to", scopeText)),
            StateLabel = state.Label,
            StateTone = state.Tone,
            ScanLabel = string.Empty,
            Reason = reason is null || reason == "-" ? null : reason,
            UpdatedText = updated,
            IsBlocked = blocked,
            IsAllowed = allowed,
            RawJson = GovernJson.Pretty(item),
            Fields = fields,
            Verbs = verbs,
            InfoLabel = "Status",
        };
    }

    // ---- Scope: the row decides, not the toolbar -----------------------------------------------------------------

    /// <summary>
    /// A connector rule is removed or changed with <c>--connector</c>, a source rule with <c>--source</c>, a global
    /// rule with neither. The toolbar's scope is deliberately not applied to a row: <c>tool unblock X --connector C</c>
    /// against a global row would target a different, probably absent, entry and leave the row in place.
    /// </summary>
    protected override string AppendRowScope(List<string> options, GovernRow row)
    {
        switch (row.RuleScope)
        {
            case "connector" when !string.IsNullOrWhiteSpace(row.Connector):
                options.Add("--connector");
                options.Add(row.Connector);
                return ScopeText(row.Connector);

            case "source" when !string.IsNullOrWhiteSpace(row.SourceScope):
                options.Add("--source");
                options.Add(row.SourceScope);
                return $"source “{row.SourceScope}” (audit only)";

            default:
                return "ALL configured connectors (the fallback rule)";
        }
    }

    protected override void AppendInfoScope(List<string> options, GovernRow row)
    {
        switch (row.RuleScope)
        {
            case "connector" when !string.IsNullOrWhiteSpace(row.Connector):
                options.Add("--connector");
                options.Add(row.Connector);
                break;

            case "source" when !string.IsNullOrWhiteSpace(row.SourceScope):
                options.Add("--source");
                options.Add(row.SourceScope);
                break;
        }
    }

    protected override string HeadingFor(GovernVerbs verb, GovernRow row, string scope) => verb switch
    {
        GovernVerbs.Unblock when row.RuleScope == "global" =>
            $"Remove “{row.Name}” from the block/allow lists for ALL configured connectors? This clears the fallback rule and every connector-specific override for this tool.",
        GovernVerbs.Unblock => $"Remove the rule for “{row.Name}” scoped to {scope}?",
        _ => base.HeadingFor(verb, row, scope),
    };

    protected override string? NoteFor(GovernVerbs verb, GovernRow row) => verb switch
    {
        GovernVerbs.Block =>
            "Blocks the tool at runtime for the scope shown. Resolution order: block @connector/tool, allow @connector/tool, block tool, allow tool, then the normal scan.",
        GovernVerbs.Allow =>
            "An allowed tool skips rule, pattern and judge scanning at runtime. Write tools still get CodeGuard. Resolution order: block @connector/tool, allow @connector/tool, block tool, allow tool, then the normal scan.",
        GovernVerbs.Unblock => "Removes the rule only; the tool goes back to the normal scan.",
        _ => base.NoteFor(verb, row),
    };

    // ---- Manage a tool by name -----------------------------------------------------------------------------------

    [RelayCommand]
    private void ManageBlock() => ManageByName(GovernVerbs.Block);

    [RelayCommand]
    private void ManageAllow() => ManageByName(GovernVerbs.Allow);

    [RelayCommand]
    private void ManageUnblock() => ManageByName(GovernVerbs.Unblock);

    [RelayCommand]
    private void ManageStatus() => ManageByName(GovernVerbs.Info);

    /// <summary>A stand-in row for a tool that has no rule yet, scoped like the toolbar: one connector, or the fallback rule.</summary>
    private void ManageByName(GovernVerbs verb)
    {
        var name = ManageToolName.Trim();
        if (name.Length == 0)
        {
            ManageError = "Enter a tool name first, for example delete_file.";
            return;
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.StartsWith('@'))
        {
            ManageError = "Enter the bare tool name. Use the connector scope in the toolbar instead of an @connector/ prefix.";
            return;
        }

        ManageError = string.Empty;
        var connector = ToolbarConnector();
        var row = new GovernRow(this)
        {
            Noun = Noun,
            Name = name,
            Connector = connector,
            RuleScope = connector is null ? "global" : "connector",
            Verbs = verb,
        };

        ((IGovernRowHost)this).OnRowAction(row, verb);
    }
}
