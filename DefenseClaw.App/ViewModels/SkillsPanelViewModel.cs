using System.Text.Json;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Skills panel: the skills each configured connector has installed, with their scan
/// result and enforcement decisions, read with <c>defenseclaw skill list --json</c>.
/// <para>
/// <b>Why the CLI, not <c>GET /skills</c>.</b> The REST endpoint answers <c>gateway: not connected</c> on a
/// standalone install, while the CLI reads the connector's skill folders and the audit database directly.
/// Real 0.8.10 items look like <c>{name, description, source, status, eligible, disabled, bundled, [connector],
/// [homepage], [scan{target, clean, max_severity, total_findings, severity_counts}], [actions{file, runtime,
/// install}], verdict}</c>; a single-connector install prints them as a bare array, a multi-connector one (or
/// <c>--connector</c>) wraps them per connector as <c>{"connector": …, "skills": […]}</c> — both are handled by
/// <see cref="GovernJson.Flatten"/>. Row actions (block, allow, unblock, disable, enable, quarantine, restore,
/// info) are built by <see cref="GovernPanelViewModelBase"/>.
/// </para>
/// </summary>
public sealed class SkillsPanelViewModel : GovernPanelViewModelBase
{
    public SkillsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Skills";

    public override string Description => "Skills installed for each connector, their scan results and enforcement decisions.";

    protected override string Noun => "skill";

    protected override string NounLabel => "skill";

    protected override string NounPlural => "skills";

    protected override string ItemsKey => "skills";

    protected override string BuildEmptyTitle(string scope) => $"No skills installed for {scope}";

    protected override string BuildEmptyDetail(string scope) =>
        "DefenseClaw 0.8.10 lists the skill folders of each connector (for Claude Code, ~\\.claude\\skills). " +
        "Skills that ship inside plugins, or live in a project's .claude\\skills folder, are not visible to it, " +
        "so an empty list here means nothing was found in the folders it looked at, not that nothing is installed.";

    protected override GovernRow? ParseRow(JsonElement item, string? groupConnector)
    {
        var name = GovernJson.Str(item, "name");
        if (name is null)
        {
            return null;
        }

        var state = GovernJson.Interpret(item);
        var connector = ResolveConnector(GovernJson.Str(item, "connector"), groupConnector);
        var bundled = GovernJson.Bool(item, "bundled") == true;
        var eligible = GovernJson.Bool(item, "eligible");
        var source = GovernJson.Str(item, "source");

        var fields = new List<GovernField>();
        AddField(fields, "Name", name);
        AddField(fields, "Connector", connector);
        AddField(fields, "Status", state.Status);
        AddField(fields, "Source", source);
        AddField(fields, "Eligible", eligible is null ? null : eligible.Value ? "yes" : "no");
        AddField(fields, "Bundled", bundled ? "yes" : null);
        AddField(fields, "Homepage", GovernJson.Str(item, "homepage"));
        AddField(fields, "Enforcement", state.ActionsText);
        AddField(fields, "Scan", state.ScanLabel);
        if (GovernJson.Obj(item, "scan") is { } scan)
        {
            AddField(fields, "Scan target", GovernJson.Str(scan, "target"));
            if (GovernJson.Obj(scan, "severity_counts") is { } counts)
            {
                AddField(
                    fields,
                    "Findings",
                    string.Join(
                        " · ",
                        new[] { "critical", "high", "medium", "low", "info" }
                            .Select(k => (Key: k, Count: GovernJson.Int(counts, k)))
                            .Where(p => p.Count is > 0)
                            .Select(p => $"{p.Key} {p.Count}")));
            }
        }

        return new GovernRow(this)
        {
            Noun = Noun,
            Name = name,
            Connector = connector,
            Description = GovernJson.Str(item, "description"),
            MetaLine = JoinMeta(("source", source), ("bundled", bundled ? "yes" : null), ("eligible", eligible == false ? "no" : null)),
            StateLabel = state.Label,
            StateTone = state.Tone,
            ScanLabel = state.ScanLabel,
            ScanTone = state.ScanTone,
            ActionsText = state.ActionsText,
            IsBlocked = state.Blocked,
            IsAllowed = state.Allowed,
            IsQuarantined = state.Quarantined,
            IsDisabled = state.Disabled,
            NeedsAttention = state.NeedsAttention,
            RawJson = GovernJson.Pretty(item),
            Fields = fields,
            // A bundled skill ships with the connector: it can be inspected but not blocked, disabled or moved.
            Verbs = bundled
                ? GovernVerbs.Info | GovernVerbs.CopyName
                : StandardVerbs(state, canDisable: true, canQuarantine: true),
        };
    }

    protected override string? NoteFor(GovernVerbs verb, GovernRow row) => verb switch
    {
        GovernVerbs.Block =>
            "Blocks future installs of this skill: it is rejected before any scan. A skill that is already installed keeps running; use Disable or Quarantine for that.",
        GovernVerbs.Allow =>
            "Allow-listed skills skip the scan gate during install. Allowing also removes the skill from the block list.",
        _ => base.NoteFor(verb, row),
    };
}
