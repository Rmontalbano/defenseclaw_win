using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

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
public sealed partial class SkillsPanelViewModel : GovernPanelViewModelBase
{
    /// <summary>The CLI's own test of a ClawHub skill name (<c>_CLAWHUB_NAME_RE</c> in cmd_skill.py): no path, no URL, no version suffix.</summary>
    private static readonly Regex ClawHubNamePattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant);

    [ObservableProperty] private bool _isInstallFormOpen;
    [ObservableProperty] private string _installName = string.Empty;
    [ObservableProperty] private bool _installForce;
    [ObservableProperty] private bool _installApplyActionPolicy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallFormError))]
    private string _installFormError = string.Empty;

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

    protected override string? ScannerExecutable => "skill-scanner";

    /// <summary>The same command as the Overview's Scan Skills quick action.</summary>
    protected override IReadOnlyList<string>? ScanAllArgv => OverviewPanelViewModel.ScanSkillsArgv;

    public bool HasInstallFormError => InstallFormError.Length > 0;

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
            Verbs = GovernVerbs.Scan | (bundled
                ? GovernVerbs.Info | GovernVerbs.CopyName
                : StandardVerbs(state, canDisable: true, canQuarantine: true)),
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
    // ---- Install (skill install) ---------------------------------------------------------------------------------

    [RelayCommand]
    private void ToggleInstallForm()
    {
        IsInstallFormOpen = !IsInstallFormOpen;
        InstallFormError = string.Empty;
    }

    protected override bool CloseTransientUi()
    {
        if (!IsInstallFormOpen)
        {
            return false;
        }

        IsInstallFormOpen = false;
        InstallFormError = string.Empty;
        return true;
    }

    [RelayCommand]
    private void SubmitInstallForm()
    {
        var name = InstallName.Trim();
        if (name.Length == 0)
        {
            InstallFormError = "Enter the ClawHub name of the skill.";
            return;
        }

        if (InstallNameProblem(name) is { } problem)
        {
            InstallFormError = problem;
            return;
        }

        InstallFormError = string.Empty;

        var options = new List<string>();
        if (InstallForce)
        {
            options.Add("--force");
        }

        if (InstallApplyActionPolicy)
        {
            options.Add("--action");
        }

        // Bare install puts a copy into every configured connector's skill folder (skill install --help), so the
        // heading says where it will land.
        var connector = ToolbarConnector();
        if (connector is not null)
        {
            options.Add("--connector");
            options.Add(connector);
        }

        var notes = new List<string> { "Downloads the skill from ClawHub, copies it into the connector's skill folder and scans it." };
        if (InstallApplyActionPolicy)
        {
            notes.Add("After the scan, the configured skill_actions policy may quarantine, disable or block it depending on severity.");
        }
        else
        {
            notes.Add("Without the policy option the scan only reports findings; nothing is enforced.");
        }

        if (InstallForce)
        {
            notes.Add("Force overwrites an existing skill of the same name.");
        }

        BeginReview(new GovernPlan
        {
            Heading = $"Install skill “{name}” into {ScopeText(connector)}?",
            Argv = BuildArgv(Noun, "install", options, name),
            Note = string.Join(" ", notes),
            SuccessMessage = $"Installed “{name}”.",
            MinimumTier = InstallForce ? CommandTier.Destructive : null,
            Timeout = CliRunner.ExtendedTimeout,
            OnSuccess = () => IsInstallFormOpen = false,
        });
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be handed to <c>skill install</c>, or null when it can. Unlike <c>plugin install</c> the
    /// target is only ever a ClawHub name: the CLI refuses anything with a path separator, a scheme or a version suffix.
    /// </summary>
    internal static string? InstallNameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ClawHubNamePattern.IsMatch(name)
            ? null
            : "That is not a ClawHub skill name: use letters, digits, '.', '_' and '-' (up to 128 characters), with no path, URL or @version.";
    }
}
