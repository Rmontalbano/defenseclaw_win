using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Registries actions. Every one opens the shared review dialog with the exact argv (flags checked
/// against <c>defenseclaw registry &lt;verb&gt; --help</c> on 0.8.10) and re-reads the panel when it finishes.
/// The floor tier is always at least StateChanging, whatever <see cref="CommandTiers.Classify"/> says: a
/// source id can be any kebab-case word, including <c>list</c>, which the classifier alone would read as a
/// read-only verb.
/// </summary>
public sealed partial class RegistriesPanelViewModel
{
    private static readonly Regex EnvNamePattern = new("^[A-Z_][A-Z0-9_]{0,63}$", RegexOptions.Compiled);

    private static readonly Regex SecretInUrlPattern = new(
        @"://[^/\s:@]+:[^/\s@]+@|[?&](token|key|secret|password|pwd|apikey|api_key|access_token|auth)=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ---- Add form ---------------------------------------------------------------------------

    [ObservableProperty]
    private bool _isAddFormOpen;

    [ObservableProperty]
    private string _addId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddUrlIsRequired))]
    [NotifyPropertyChangedFor(nameof(AddUrlHint))]
    private string _addKind = "http_yaml";

    [ObservableProperty]
    private string _addContent = "skill";

    [ObservableProperty]
    private string _addUrl = string.Empty;

    [ObservableProperty]
    private string _addAuthEnv = string.Empty;

    [ObservableProperty]
    private bool _addEnabled = true;

    [ObservableProperty]
    private bool _addSyncAfter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddValidationMessage))]
    private string? _addValidationMessage;

    /// <summary>The kinds <c>registry add --kind</c> accepts (from its help).</summary>
    public IReadOnlyList<string> AddKindOptions { get; } =
        new[] { "clawhub", "smithery", "skills_sh", "http_yaml", "http_json", "git", "file" };

    /// <summary>The values <c>registry add --content</c> accepts (from its help).</summary>
    public IReadOnlyList<string> AddContentOptions { get; } = new[] { "skill", "mcp", "both" };

    public bool HasAddValidationMessage => !string.IsNullOrEmpty(AddValidationMessage);

    public bool AddUrlIsRequired => AddKind is "http_yaml" or "http_json" or "git" or "file";

    public string AddUrlHint => AddKind switch
    {
        "http_yaml" => "HTTPS URL of a YAML manifest.",
        "http_json" => "HTTPS URL of a JSON manifest.",
        "git" => "Git repository URL; the repository must contain defenseclaw-registry.yaml.",
        "file" => "Absolute path of a manifest file on this machine, for example C:\\registries\\catalog.yaml.",
        _ => "Optional for this kind; leave empty to use its built-in address.",
    };

    [RelayCommand]
    private void OpenAddForm()
    {
        AddValidationMessage = null;
        IsAddFormOpen = true;
    }

    [RelayCommand]
    private void CancelAddForm()
    {
        IsAddFormOpen = false;
        AddValidationMessage = null;
    }

    [RelayCommand]
    private void SubmitAdd()
    {
        var id = AddId.Trim();
        var url = AddUrl.Trim();
        var authEnv = AddAuthEnv.Trim();

        var problem = ValidateAdd(id, url, authEnv);
        AddValidationMessage = problem;
        if (problem is not null)
        {
            return;
        }

        var argv = new List<string> { "registry", "add", id, "--kind", AddKind, "--content", AddContent };
        if (url.Length > 0)
        {
            argv.Add("--url");
            argv.Add(url);
        }

        if (authEnv.Length > 0)
        {
            argv.Add("--auth-env");
            argv.Add(authEnv);
        }

        argv.Add(AddEnabled ? "--enabled" : "--disabled");
        argv.Add("--non-interactive");
        argv.Add("--json");

        var steps = new List<DiscoverStep>
        {
            new(argv, "Add the source to config.yaml. This does not contact it."),
        };

        // Only a source that will be enabled can be synced by name in the same breath; the sync runs
        // only if the add exited 0 (the review runs steps in order and stops at the first failure).
        var syncAfter = AddSyncAfter && AddEnabled;
        if (syncAfter)
        {
            steps.Add(SyncStep(id));
        }

        Review.Open(
            $"Add registry source “{id}”?",
            "Registers a catalog source in config.yaml. Adding never fetches anything; a sync does " +
            "(the second step, if you ticked “Sync after adding”). " +
            (authEnv.Length > 0
                ? $"The source will read its bearer token from the environment variable {authEnv}; the token itself is never entered here."
                : "No credentials are used."),
            steps,
            result => AfterAddAsync(result, id, syncAfter),
            primaryText: syncAfter ? "Add and sync" : "Add source");
    }

    private async Task AfterAddAsync(DiscoverReviewResult result, string id, bool synced)
    {
        var addSucceeded = result.Invocations.Count > 0 && result.Invocations[0].ExitCode == 0
            && result.Invocations[0].FailureReason is not { Length: > 0 };

        if (addSucceeded)
        {
            IsAddFormOpen = false;
            AddId = string.Empty;
            AddUrl = string.Empty;
            AddAuthEnv = string.Empty;
            AddSyncAfter = false;
            AddValidationMessage = null;
        }

        await AfterActionAsync(
            result,
            success: synced ? $"Added “{id}” and synced it." : $"Added “{id}”. It has not been synced yet.",
            failure: addSucceeded
                ? $"Added “{id}”, but the sync did not finish. See the result in the dialog and in Activity."
                : $"“{id}” was not added. See the result in the dialog and in Activity.").ConfigureAwait(true);

        if (addSucceeded)
        {
            SelectedSource = Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        }
    }

    private string? ValidateAdd(string id, string url, string authEnv)
    {
        if (!SourceIdPattern.IsMatch(id))
        {
            return "The id must be 2 to 64 characters: lowercase letters, digits, '-' or '_', starting with a letter or digit.";
        }

        if (Sources.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return $"A source named “{id}” already exists.";
        }

        if (AddUrlIsRequired && url.Length == 0)
        {
            return "This kind needs a URL or path.";
        }

        if (url.Length > 0)
        {
            if (SecretInUrlPattern.IsMatch(url))
            {
                return "That address looks like it carries a credential (user:password@ or a token in the query string). " +
                    "Put the token in an environment variable and enter that variable's name below instead.";
            }

            if (AddKind == "file")
            {
                if (!Path.IsPathRooted(url))
                {
                    return "A file source needs an absolute path.";
                }
            }
            else if (AddKind is "http_yaml" or "http_json" &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "Enter an http(s) URL.";
            }
        }

        if (authEnv.Length > 0 && !EnvNamePattern.IsMatch(authEnv))
        {
            return "Enter the NAME of an environment variable in UPPER_SNAKE_CASE (for example CORP_REGISTRY_TOKEN), never the token itself.";
        }

        return null;
    }

    // ---- Source actions ------------------------------------------------------------------------

    [RelayCommand]
    private void SyncSelected()
    {
        if (SelectedSource is not { } source)
        {
            return;
        }

        Review.Open(
            $"Sync “{source.Id}”?",
            SyncExplanation,
            new[] { SyncStep(source.Id) },
            result => AfterActionAsync(result, $"Synced “{source.Id}”.", $"Sync of “{source.Id}” did not finish."),
            primaryText: "Sync");
    }

    [RelayCommand]
    private void SyncAll()
    {
        Review.Open(
            "Sync every enabled registry source?",
            SyncExplanation + " Disabled sources are skipped.",
            new[] { SyncStep(null) },
            result => AfterActionAsync(result, "Synced every enabled source.", "The sync did not finish."),
            primaryText: "Sync all");
    }

    private const string SyncExplanation =
        "Fetches the source's manifest over the network, scans each entry with the skill and MCP scanners, " +
        "and promotes clean entries into asset_policy (config.yaml). It also rewrites the source's cache. " +
        "MCP servers that run as a local process (stdio) are not scanned, because scanning them would start " +
        "the publisher's package.";

    private static DiscoverStep SyncStep(string? id) =>
        new(
            id is null
                ? new[] { "registry", "sync", "--all", "--json" }
                : new[] { "registry", "sync", id, "--json" },
            id is null
                ? "Fetch, scan and promote entries from every enabled source."
                : $"Fetch, scan and promote entries from {id}.",
            CommandTier.StateChanging,
            CliRunner.ExtendedTimeout);

    [RelayCommand]
    private void ToggleSelectedEnabled()
    {
        if (SelectedSource is not { } source)
        {
            return;
        }

        var enable = source.Enabled == false;
        var argv = new[] { "registry", "edit", source.Id, enable ? "--enabled" : "--disabled", "--non-interactive", "--json" };

        Review.Open(
            enable ? $"Enable “{source.Id}”?" : $"Disable “{source.Id}”?",
            enable
                ? "Turns the source back on so “Sync all” includes it. Nothing is fetched until a sync runs."
                : "Turns the source off so “Sync all” skips it. Its cache and any policy rules it already promoted stay as they are.",
            new[] { new DiscoverStep(argv, "Change only the enabled flag of this source.") },
            result => AfterActionAsync(
                result,
                enable ? $"Enabled “{source.Id}”." : $"Disabled “{source.Id}”.",
                $"“{source.Id}” was not changed."),
            primaryText: enable ? "Enable" : "Disable");
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (SelectedSource is not { } source)
        {
            return;
        }

        var argv = new[] { "registry", "remove", source.Id, "--non-interactive", "--json" };

        Review.Open(
            $"Remove “{source.Id}”?",
            "Deletes the source from config.yaml, removes the asset_policy rules it promoted (the ones whose " +
            "reason is registry:" + source.Id + "), and deletes its cache folder. Skills and MCP servers " +
            "that were only allowed because of those rules stop being allowed.",
            new[] { new DiscoverStep(argv, "Remove the source, its promoted rules and its cache.", CommandTier.Destructive) },
            result => AfterActionAsync(result, $"Removed “{source.Id}”.", $"“{source.Id}” was not removed."),
            primaryText: "Remove source");
    }

    // ---- Entry actions -------------------------------------------------------------------------

    [RelayCommand]
    private void ApproveEntry() => ReviewEntry(approve: true);

    [RelayCommand]
    private void RejectEntry() => ReviewEntry(approve: false);

    private void ReviewEntry(bool approve)
    {
        if (SelectedSource is not { } source || SelectedEntry is not { CanReview: true } entry)
        {
            return;
        }

        var verb = approve ? "approve" : "reject";
        var type = entry.TypeDisplay.ToLowerInvariant();

        // A name that starts with '-' would be read as an option: put the options first and the names
        // after "--". The normal (and far more common) form keeps the names first, as the help shows.
        var argv = entry.Name.StartsWith('-')
            ? new[] { "registry", verb, "--type", type, "--json", "--", source.Id, entry.Name }
            : new[] { "registry", verb, source.Id, entry.Name, "--type", type, "--json" };

        Review.Open(
            approve
                ? $"Approve {type} “{entry.Name}”?"
                : $"Reject {type} “{entry.Name}”?",
            approve
                ? "Marks the entry approved, even if the scanner has not run, and keeps it approved across syncs. " +
                  "It then re-runs promotion from the cached manifest (no network), so an allow rule for it " +
                  "lands in asset_policy now (config.yaml)."
                : "Marks the entry rejected: it is never promoted and stays blocked in future syncs. Any rule " +
                  "an earlier sync promoted for it is removed from asset_policy (config.yaml).",
            new[]
            {
                new DiscoverStep(
                    argv,
                    approve
                        ? $"Approve {entry.Name} from {source.Id}."
                        : $"Reject {entry.Name} from {source.Id}."),
            },
            result => AfterActionAsync(
                result,
                approve ? $"Approved “{entry.Name}”." : $"Rejected “{entry.Name}”.",
                $"“{entry.Name}” was not changed."),
            primaryText: approve ? "Approve" : "Reject");
    }

    // ---- Registry required (default-deny) ---------------------------------------------------------

    /// <summary>Parameter is <c>skill:on</c>, <c>skill:off</c>, <c>mcp:on</c> or <c>mcp:off</c>.</summary>
    [RelayCommand]
    private void SetRegistryRequired(string? spec)
    {
        var parts = (spec ?? string.Empty).Split(':');
        if (parts.Length != 2 || parts[0] is not ("skill" or "mcp") || parts[1] is not ("on" or "off"))
        {
            return;
        }

        var type = parts[0];
        var enable = parts[1] == "on";
        var noun = type == "skill" ? "skills" : "MCP servers";
        var argv = new[] { "registry", "require", "--type", type, enable ? "--enabled" : "--disabled", "--json" };

        Review.Open(
            enable ? $"Require a registry entry for {noun}?" : $"Make the registry optional for {noun}?",
            enable
                ? $"Turns on asset_policy.{type}.registry_required for the whole install (every active connector " +
                  $"is reconciled to inherit it). From then on a {(type == "skill" ? "skill" : "MCP server")} " +
                  "must match a rule from a registry to be admitted; anything else falls back to the default action."
                : $"Turns off asset_policy.{type}.registry_required for the whole install, so {noun} no longer " +
                  "need a registry rule to be admitted.",
            new[] { new DiscoverStep(argv, $"Set registry_required for {noun}.") },
            result => AfterActionAsync(
                result,
                enable ? $"Registry now required for {noun}." : $"Registry now optional for {noun}.",
                $"The setting for {noun} was not changed."),
            warning: enable
                ? "With no approved registry entries, this blocks every " + (type == "skill" ? "skill" : "MCP server") +
                  " that is not already covered by a rule. Sync and approve entries first."
                : null,
            primaryText: enable ? "Require registry" : "Make optional");
    }

    // ---- After a run ------------------------------------------------------------------------------

    private async Task AfterActionAsync(DiscoverReviewResult result, string success, string failure)
    {
        // The CLI edited config.yaml; pick that up now rather than waiting for the file watcher.
        Services.ReloadConfig();

        ActionMessage = result.Succeeded ? success : failure;
        ActionSeverity = result.Succeeded
            ? Wpf.Ui.Controls.InfoBarSeverity.Success
            : Wpf.Ui.Controls.InfoBarSeverity.Error;

        // A catch-up read may already be running; wait for it (bounded) so this one is not skipped.
        for (var i = 0; i < 50 && _loadRunning; i++)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    [RelayCommand]
    private void DismissActionMessage() => ActionMessage = null;
}
