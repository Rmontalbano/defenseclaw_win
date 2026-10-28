using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The two cards CUST-375 derives from config.yaml alone (no gateway, no process): <b>Admission overrides</b> - the per-asset-type, per-severity install-gate
/// actions in <c>admission.*.actions</c> (the Mac's <c>admissionOverrides</c>) - and <b>Operator lists</b> - the block and allow lists in <c>asset_policy</c>
/// (the Mac's <c>OperatorAssetLists</c>, v1.1.32), as one count per asset type. Each is drawn only when config.yaml has something to say, so a
/// 0.8.10 install, which has neither section, looks exactly as before. They are read from the text of the config.yaml the panel already holds, once per parsed
/// config (<see cref="BuildConfiguration"/> calls <see cref="BuildPolicyCards"/> and is rerun on every config reload). Read-only: the lists change through the
/// Tools, Skills, MCPs and Plugins panels, whose actions go through the reviewed CLI commands.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    private ConfigDocument? _policyCardsSource;
    private AdmissionOverrides _admissionOverrides = AdmissionOverrides.Empty;
    private OperatorAssetLists _operatorLists = OperatorAssetLists.Empty;

    public ObservableCollection<ConfigRow> AdmissionOverrideRows { get; } = new();

    public ObservableCollection<ConfigRow> OperatorListRows { get; } = new();

    [ObservableProperty]
    private bool _showAdmissionOverrides;

    [ObservableProperty]
    private bool _showOperatorLists;

    /// <summary><c>7 rules in 3 lists.</c> and the pointer to where they change.</summary>
    [ObservableProperty]
    private string _operatorListsNote = string.Empty;

    /// <summary>Opens the Tools panel, where the operator lists are listed in full and changed through the reviewed commands.</summary>
    [RelayCommand]
    private void OpenOperatorLists() => RequestNavigation("tools");

    /// <summary>Rebuilds both cards from the loaded config.yaml. No I/O; a document already read is not parsed again.</summary>
    internal void BuildPolicyCards()
    {
        var document = Services.Config;
        if (!ReferenceEquals(document, _policyCardsSource))
        {
            _policyCardsSource = document;
            _admissionOverrides = AdmissionOverrides.FromYaml(document?.RawText);
            _operatorLists = OperatorAssetLists.FromYaml(document?.RawText);
        }

        SyncByEquality(
            AdmissionOverrideRows,
            _admissionOverrides.Rows.Select((row, index) => new ConfigRow { Label = row.Label, Value = row.Action, Alternate = index % 2 == 1 }).ToList(),
            static row => row.Label);
        ShowAdmissionOverrides = !_admissionOverrides.IsEmpty;

        var lists = _operatorLists;
        var rows = new List<ConfigRow>();
        if (!lists.IsEmpty)
        {
            foreach (var type in OperatorAssetLists.AssetTypes)
            {
                var blocked = lists.DeniedFor(type).Count;
                var allowed = lists.AllowedFor(type).Count;
                if (blocked + allowed > 0)
                {
                    rows.Add(new ConfigRow { Label = PolicyListLabel(type), Value = CountText(blocked, allowed), Alternate = rows.Count % 2 == 1 });
                }
            }
        }

        SyncByEquality(OperatorListRows, rows, static row => row.Label);
        ShowOperatorLists = rows.Count > 0;
        OperatorListsNote = rows.Count == 0
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"{lists.Total} rule{(lists.Total == 1 ? string.Empty : "s")} in config.yaml asset_policy. Change them with Block and Allow on the Tools, Skills, MCPs and Plugins panels.");
    }

    internal static string PolicyListLabel(string type) => type switch
    {
        "skill" => "Skills",
        "mcp" => "MCP servers",
        "plugin" => "Plugins",
        "tool" => "Tools",
        _ => type,
    };

    internal static string CountText(int blocked, int allowed) =>
        string.Create(CultureInfo.CurrentCulture, $"{blocked} blocked · {allowed} allowed");
}
