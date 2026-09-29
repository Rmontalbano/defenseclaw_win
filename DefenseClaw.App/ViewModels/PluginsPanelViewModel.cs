using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Plugins panel: plugins per connector, read with
/// <c>defenseclaw plugin list --json [--connector C]</c> (there is no gateway endpoint for plugins).
/// <para>
/// <b>Real 0.8.10 items:</b> <c>{id, name, description, version, origin, source, status, enabled, connector,
/// scan{target, clean, max_severity, total_findings}, actions{file, runtime, install}, verdict}</c> — a bare
/// array even with <c>--connector</c> (unlike skills, MCP servers and tools), a per-connector wrapper array
/// only on a multi-connector install. Severity therefore comes from <c>scan.max_severity</c>.
/// </para>
/// <para>
/// <b>Listing artifacts.</b> For Claude Code the CLI treats every folder in <c>~\.claude\plugins</c> as a plugin,
/// so <c>marketplaces</c>, <c>cache</c> and <c>known_marketplaces.json.lock</c> come back as three "quarantined"
/// plugins that are really Claude Code's own store. They are kept — dropping them silently would hide what the CLI
/// said — but shown apart, dimmed, with an explanation, and without any action except Info.
/// </para>
/// </summary>
public sealed partial class PluginsPanelViewModel : GovernPanelViewModelBase
{
    /// <summary>Names Claude Code's plugin store contains that DefenseClaw 0.8.10 lists as if they were plugins.</summary>
    private static readonly HashSet<string> ClaudeStoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "marketplaces", "cache", "known_marketplaces.json.lock",
    };

    [ObservableProperty] private bool _isInstallFormOpen;
    [ObservableProperty] private string _installNameOrPath = string.Empty;
    [ObservableProperty] private bool _installForce;
    [ObservableProperty] private bool _installApplyActionPolicy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallFormError))]
    private string _installFormError = string.Empty;

    public PluginsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "Plugins";

    public override string Description => "Plugins installed for each connector, their scan results and enforcement decisions.";

    protected override string Noun => "plugin";

    protected override string NounLabel => "plugin";

    protected override string NounPlural => "plugins";

    protected override string ItemsKey => "plugins";

    public bool HasInstallFormError => InstallFormError.Length > 0;

    protected override string BuildEmptyTitle(string scope) => $"No plugins installed for {scope}";

    protected override string BuildEmptyDetail(string scope)
    {
        var detail =
            "DefenseClaw 0.8.10 reads plugin folders it manages (and, for Codex, ~\\.codex\\plugins). Claude Code's own plugin " +
            "store (~\\.claude\\plugins\\cache and installed_plugins.json) is not read, so plugins installed with Claude Code do " +
            "not appear here. An empty list means nothing was found where it looked, not that no plugin is installed.";
        return ArtifactCount > 0
            ? detail + $" The {ArtifactCount} entr{(ArtifactCount == 1 ? "y" : "ies")} listed below are not plugins."
            : detail;
    }

    protected override GovernRow? ParseRow(JsonElement item, string? groupConnector)
    {
        var id = GovernJson.Str(item, "id") ?? GovernJson.Str(item, "name");
        if (id is null)
        {
            return null;
        }

        var name = GovernJson.Str(item, "name") ?? id;
        var state = GovernJson.Interpret(item);
        var connector = ResolveConnector(GovernJson.Str(item, "connector"), groupConnector);
        var version = GovernJson.Str(item, "version");
        var origin = GovernJson.Str(item, "origin");
        var source = GovernJson.Str(item, "source");
        var enabled = GovernJson.Bool(item, "enabled");
        var verdict = GovernJson.Str(item, "verdict");
        var artifact = IsListingArtifact(id, connector);

        var fields = new List<GovernField>();
        AddField(fields, "Id", id);
        AddField(fields, "Connector", connector);
        AddField(fields, "Version", version);
        AddField(fields, "Origin", origin);
        AddField(fields, "Source", source);
        AddField(fields, "Status", state.Status);
        AddField(fields, "Enabled", enabled is null ? null : enabled.Value ? "yes" : "no");
        AddField(fields, "Verdict", verdict);
        AddField(fields, "Enforcement", state.ActionsText);
        AddField(fields, "Scan", state.ScanLabel);
        if (GovernJson.Obj(item, "scan") is { } scan)
        {
            AddField(fields, "Scan target", GovernJson.Str(scan, "target"));
        }

        var verbs = artifact
            ? GovernVerbs.Info | GovernVerbs.CopyName
            : StandardVerbs(state, canDisable: true, canQuarantine: true) | GovernVerbs.Remove;

        return new GovernRow(this)
        {
            Noun = Noun,
            Name = id,
            Title = name,
            Connector = connector,
            Description = GovernJson.Str(item, "description"),
            MetaLine = JoinMeta(
                ("version", version),
                ("origin", origin),
                ("source", source),
                ("enabled", enabled is null ? null : enabled.Value ? "yes" : "no")),
            StateLabel = state.Label,
            StateTone = state.Tone,
            ScanLabel = state.ScanLabel,
            ScanTone = state.ScanTone,
            ActionsText = state.ActionsText,
            IsArtifact = artifact,
            ArtifactNote = artifact
                ? "Not a plugin (listing artifact). This is part of Claude Code's own plugin store; DefenseClaw 0.8.10 lists it as if it were a plugin."
                : null,
            IsBlocked = state.Blocked,
            IsAllowed = state.Allowed,
            IsQuarantined = state.Quarantined,
            IsDisabled = state.Disabled,
            NeedsAttention = state.NeedsAttention,
            RawJson = GovernJson.Pretty(item),
            Fields = fields,
            Verbs = verbs,
        };
    }

    /// <summary>
    /// True for the entries DefenseClaw 0.8.10 lists for Claude Code that belong to Claude Code's plugin store
    /// rather than being plugins: the three names seen live, and any lock or JSON file in that folder.
    /// </summary>
    private static bool IsListingArtifact(string id, string? connector)
    {
        if (!string.Equals(connector, "claudecode", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ClaudeStoreNames.Contains(id)
               || id.StartsWith('.')
               || id.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               || id.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    protected override string? NoteFor(GovernVerbs verb, GovernRow row) => verb switch
    {
        GovernVerbs.Block =>
            "Blocked plugins are rejected by the admission gate before any scan. A plugin that is already installed keeps running; use Disable or Quarantine for that.",
        GovernVerbs.Allow =>
            "Allow-listed plugins skip the scan gate during install. Allowing also removes the plugin from the block list.",
        GovernVerbs.Remove =>
            "Deletes the plugin's files. There is no quarantine copy and no undo. The CLI prints a reminder to restart the gateway; this command does not restart it.",
        _ => base.NoteFor(verb, row),
    };

    // ---- Install (plugin install) --------------------------------------------------------------------------------

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
        var target = InstallNameOrPath.Trim();
        if (target.Length == 0)
        {
            InstallFormError = "Enter a local path, npm package, clawhub:// URI or https URL.";
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

        // Bare install materializes the plugin into every configured connector that exposes a plugin
        // directory (plugin install --help), so the heading says where it will land.
        var connector = ToolbarConnector();
        if (connector is not null)
        {
            options.Add("--connector");
            options.Add(connector);
        }

        var notes = new List<string> { "Fetches or copies the plugin, places it in the connector's plugin folder and scans it." };
        if (InstallApplyActionPolicy)
        {
            notes.Add("After the scan, the configured plugin_actions policy may quarantine, disable or block it depending on severity.");
        }

        if (InstallForce)
        {
            notes.Add("Force overwrites an existing plugin of the same name.");
        }

        BeginReview(new GovernPlan
        {
            Heading = $"Install plugin “{target}” into {ScopeText(connector)}?",
            Argv = BuildArgv(Noun, "install", options, target),
            Note = string.Join(" ", notes),
            SuccessMessage = $"Installed “{target}”.",
            MinimumTier = InstallForce ? CommandTier.Destructive : null,
            OnSuccess = () => IsInstallFormOpen = false,
        });
    }
}
