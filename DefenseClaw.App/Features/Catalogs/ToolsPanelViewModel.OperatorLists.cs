using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.ViewModels;

/// <summary>One rule of an operator list, as a line: <c>blocked</c> or <c>allowed</c>, the target (<c>@connector/name</c> or the bare name) and the reason.</summary>
public sealed record OperatorListEntryRow(string Decision, string Target, string Reason)
{
    /// <summary>The tone the decision is drawn in: a block is Bad, an allow is Ok.</summary>
    public string ToneKey => string.Equals(Decision, "blocked", StringComparison.Ordinal) ? "Bad" : "Ok";
}

/// <summary>One asset type's lists: <c>Tools</c> with <c>2 blocked · 1 allowed</c> and its rules.</summary>
public sealed record OperatorListSection(string Title, string Counts, IReadOnlyList<OperatorListEntryRow> Entries);

/// <summary>
/// The read-only "Operator lists" section of the Tools panel (CUST-375): the block and allow lists in config.yaml <c>asset_policy</c> for tools, skills, MCP servers and
/// plugins (<see cref="OperatorAssetLists"/>), read from the text of the config.yaml the app already holds, so it costs no command and cannot disagree with the file.
/// Nothing here changes anything: the Block, Allow and Unblock actions on this panel and on Skills, MCPs and Plugins are the reviewed CLI commands that write these
/// lists, and the section says so. A Secure Client install keeps its decisions in the audit store, so there it says the lists are not the policy.
/// </summary>
public sealed partial class ToolsPanelViewModel
{
    private ConfigDocument? _operatorListsSource;

    public ObservableCollection<OperatorListSection> OperatorListSections { get; } = new();

    [ObservableProperty]
    private bool _showOperatorLists;

    /// <summary><c>7 rules in config.yaml asset_policy</c>, or why there are none to show.</summary>
    [ObservableProperty]
    private string _operatorListsSummary = string.Empty;

    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        BuildOperatorLists();
        await base.InitializeAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Rebuilt on every visit (a comparison unless config.yaml was replaced). It deliberately does not subscribe to config reloads: the base panel's
    /// subscription count is held to one while on screen, and a reload while the panel is open already marks its rows stale.
    /// </summary>
    protected override void OnActivated()
    {
        BuildOperatorLists();
        base.OnActivated();
    }

    /// <summary>Rebuilds the section from the loaded config.yaml. No I/O; a document already read is not parsed again.</summary>
    internal void BuildOperatorLists()
    {
        var document = Services.Config;
        if (ReferenceEquals(document, _operatorListsSource))
        {
            return;
        }

        _operatorListsSource = document;
        var lists = OperatorAssetLists.FromYaml(document.RawText);
        var sections = new List<OperatorListSection>();
        if (!lists.SecureClient)
        {
            foreach (var type in OperatorAssetLists.AssetTypes)
            {
                var denied = lists.DeniedFor(type);
                var allowed = lists.AllowedFor(type);
                if (denied.Count + allowed.Count == 0)
                {
                    continue;
                }

                var entries = denied.Select(rule => new OperatorListEntryRow("blocked", rule.Target, rule.Reason))
                    .Concat(allowed.Select(rule => new OperatorListEntryRow("allowed", rule.Target, rule.Reason)))
                    .ToList();
                sections.Add(new OperatorListSection(OverviewPanelViewModel.PolicyListLabel(type), OverviewPanelViewModel.CountText(denied.Count, allowed.Count), entries));
            }
        }

        OperatorListSections.Clear();
        foreach (var section in sections)
        {
            OperatorListSections.Add(section);
        }

        ShowOperatorLists = lists.SecureClient || sections.Count > 0;
        OperatorListsSummary = lists.SecureClient
            ? "This is a Secure Client install: its block and allow decisions are kept in the audit store, not in config.yaml, so there are no lists to show here."
            : sections.Count == 0
                ? string.Empty
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"{lists.Total} rule{(lists.Total == 1 ? string.Empty : "s")} in config.yaml asset_policy (read-only). Block, Allow and Unblock on this panel and on Skills, MCPs and Plugins run the reviewed commands that change them.")
                  + (lists.Truncated > 0 ? string.Create(CultureInfo.CurrentCulture, $" {lists.Truncated} more are not shown.") : string.Empty);
    }
}
