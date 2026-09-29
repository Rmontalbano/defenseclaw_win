using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.App.ViewModels;

/// <summary>One key/value pair of a source's raw JSON, for the generic detail expander.</summary>
public sealed record RegistryFieldRow(string Key, string Value)
{
    public override string ToString() => $"{Key}: {Value}";
}

/// <summary>
/// One configured registry source, as returned by <c>defenseclaw registry list --json</c>
/// (0.8.10 shape, from the CLI source: <c>id, kind, url, content, auth_env, enabled, auto_sync,
/// sync_interval_hours, last_sync, last_status, entries{total, clean, warning, blocked, error}</c>).
/// Every field beyond the id is optional and every raw field is kept in <see cref="Fields"/>, so a
/// different release's extra keys still show.
/// </summary>
public sealed class RegistrySourceRow
{
    public required string Id { get; init; }

    public string? Kind { get; init; }

    public string? Location { get; init; }

    public string? EntriesSummary { get; init; }

    public required IReadOnlyList<RegistryFieldRow> Fields { get; init; }

    /// <summary>declared content: skill, mcp or both.</summary>
    public string? Content { get; init; }

    public string? AuthEnv { get; init; }

    public bool? Enabled { get; init; }

    public string? LastSync { get; init; }

    public string? LastStatus { get; init; }

    /// <summary>The element's raw JSON, so a refresh can tell a changed row from an unchanged one.</summary>
    public string Signature { get; init; } = string.Empty;

    public string EnabledDisplay => Enabled switch
    {
        true => "Enabled",
        false => "Disabled",
        _ => "—",
    };

    public string EnabledKey => Enabled == true ? "Ok" : "Neutral";

    public string KindDisplay => string.IsNullOrWhiteSpace(Kind) ? "—" : Kind;

    public string ContentDisplay => string.IsNullOrWhiteSpace(Content) ? "—" : Content;

    public string EntriesDisplay => string.IsNullOrWhiteSpace(EntriesSummary) ? "not synced" : EntriesSummary;

    public string LastSyncDisplay => FormatTimestamp(LastSync) ?? "never";

    public string StatusDisplay => string.IsNullOrWhiteSpace(LastStatus) ? "—" : LastStatus;

    public override string ToString() =>
        $"Registry source {Id}, {KindDisplay}, {EnabledDisplay}, entries {EntriesDisplay}, last sync {LastSyncDisplay}";

    internal static string? FormatTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : raw;
    }
}

/// <summary>One cached entry of a source, read from that source's <c>index.json</c> (an <c>EntryVerdict</c>).</summary>
public sealed class RegistryEntryRow
{
    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Status { get; init; }

    public bool Approved { get; init; }

    public bool Rejected { get; init; }

    public string? Severity { get; init; }

    public string? Location { get; init; }

    public string TypeDisplay => string.IsNullOrWhiteSpace(Type) ? "—" : Type;

    public string StatusDisplay => string.IsNullOrWhiteSpace(Status) ? "—" : Status;

    public string SeverityDisplay => string.IsNullOrWhiteSpace(Severity) ? "—" : Severity;

    public string ReviewDisplay => Approved ? "Approved" : Rejected ? "Rejected" : "—";

    public string StatusKey => (Status ?? string.Empty).ToLowerInvariant() switch
    {
        "clean" => "Ok",
        "warning" => "Warn",
        "blocked" or "error" => "Bad",
        _ => "Neutral",
    };

    /// <summary>approve / reject take <c>--type {skill,mcp}</c> only.</summary>
    public bool CanReview => string.Equals(Type, "skill", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, "mcp", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{TypeDisplay} entry {Name}, status {StatusDisplay}, review {ReviewDisplay}";
}

/// <summary>
/// View-model for the Registries panel: external skill / MCP catalog sources DefenseClaw can sync from.
/// <para>
/// <b>Reads</b> come from the read-only <c>defenseclaw registry list --json</c> (one CLI call per
/// activation or Refresh, cached with an "as of" time) and, for the selected source's entries, its
/// <c>registries\&lt;id&gt;\index.json</c> cache file. <b>Every change</b> — add, sync, enable/disable,
/// remove, approve/reject, require — is a reviewed CLI command (see <see cref="DiscoverActionReview"/>):
/// the exact argv, its tier, and an exit-code-gated follow-up; nothing here edits config.yaml itself.
/// A machine with no registry sources is a normal state (this one has none), so it gets a designed
/// empty state that explains what a registry is, not an error.
/// </para>
/// </summary>
public sealed partial class RegistriesPanelViewModel : PanelViewModelBase
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);

    private const long MaxIndexBytes = 4L * 1024 * 1024;
    private const int MaxEntries = 5_000;

    // 2-64 chars of the kebab/underscore alphabet; the CLI's own check is the authority, this only
    // keeps a value that could escape the registries folder (or start with '-') out of a path or argv.
    private static readonly Regex SourceIdPattern = new("^[a-z0-9][a-z0-9_-]{1,63}$", RegexOptions.Compiled);

    private bool _loadRunning;
    private DateTimeOffset? _loadedAt;
    private int _entriesSequence;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCliError))]
    [NotifyPropertyChangedFor(nameof(ShowNotConfigured))]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    private string? _cliErrorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    // HasConfigSection is derived from this and drives the "config.yaml does have a registry section"
    // block. Without the notification the binding read the empty initial value once and never again.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigSection))]
    private string? _configSectionYaml;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSources))]
    [NotifyPropertyChangedFor(nameof(ShowNotConfigured))]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    [NotifyPropertyChangedFor(nameof(ShowPolicyCard))]
    private bool _hasSources;

    /// <summary>False until the first read finishes, so the panel does not flash "no sources" while it is still looking.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotConfigured))]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSource))]
    [NotifyPropertyChangedFor(nameof(HasNoSelectedSource))]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledText))]
    private RegistrySourceRow? _selectedSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedEntry))]
    [NotifyPropertyChangedFor(nameof(CanReviewEntry))]
    private RegistryEntryRow? _selectedEntry;

    [ObservableProperty]
    private string? _entriesMessage;

    [ObservableProperty]
    private bool _isEntriesLoading;

    /// <summary>Result line of the last reviewed action ("Synced corp-skills"), shown above the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    private string? _actionMessage;

    [ObservableProperty]
    private Wpf.Ui.Controls.InfoBarSeverity _actionSeverity = Wpf.Ui.Controls.InfoBarSeverity.Success;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SkillsRegistryOptional))]
    [NotifyPropertyChangedFor(nameof(SkillsRegistryText))]
    [NotifyPropertyChangedFor(nameof(ShowPolicyCard))]
    private bool _skillsRegistryRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(McpsRegistryOptional))]
    [NotifyPropertyChangedFor(nameof(McpsRegistryText))]
    [NotifyPropertyChangedFor(nameof(ShowPolicyCard))]
    private bool _mcpsRegistryRequired;

    public RegistriesPanelViewModel(AppServices services)
        : base(services)
    {
        Review = new DiscoverActionReview(services);
    }

    public override string Title => "Registries";

    public override string Description =>
        "External skill / MCP catalog sources DefenseClaw can sync from: add, sync, approve and require them.";

    /// <summary>The shared confirm-and-run dialog every change on this panel goes through.</summary>
    public DiscoverActionReview Review { get; }

    public bool HasCliError => !string.IsNullOrEmpty(CliErrorMessage);

    public bool HasConfigSection => !string.IsNullOrEmpty(ConfigSectionYaml);

    public bool HasNoSources => !HasSources;

    /// <summary>The normal standalone state: the read worked and there is simply nothing configured.</summary>
    public bool ShowNotConfigured => HasLoaded && HasNoSources && !HasCliError;

    /// <summary>The read failed and there is nothing cached to show: a different message from "not configured".</summary>
    public bool ShowUnavailable => HasLoaded && HasNoSources && HasCliError;

    public bool HasSelectedSource => SelectedSource is not null;

    public bool HasNoSelectedSource => SelectedSource is null;

    public bool HasSelectedEntry => SelectedEntry is not null;

    public bool CanReviewEntry => SelectedEntry is { CanReview: true };

    public bool HasActionMessage => !string.IsNullOrEmpty(ActionMessage);

    public string ToggleEnabledText => SelectedSource?.Enabled == false ? "Enable…" : "Disable…";

    public bool SkillsRegistryOptional => !SkillsRegistryRequired;

    public bool McpsRegistryOptional => !McpsRegistryRequired;

    public string SkillsRegistryText => SkillsRegistryRequired
        ? "Skills: registry required (a skill must match an approved registry entry)"
        : "Skills: registry optional";

    public string McpsRegistryText => McpsRegistryRequired
        ? "MCP servers: registry required (a server must match an approved registry entry)"
        : "MCP servers: registry optional";

    /// <summary>The default-deny switches only matter once there are sources (or one is already on and needs turning off).</summary>
    public bool ShowPolicyCard => HasSources || SkillsRegistryRequired || McpsRegistryRequired;

    public ObservableCollection<RegistrySourceRow> Sources { get; } = new();

    public ObservableCollection<RegistryEntryRow> Entries { get; } = new();

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(cancellationToken).ConfigureAwait(true);

    /// <summary>One-shot catch-up when the panel comes back on screen after the data has gone stale; no timer.</summary>
    protected override void OnActivated()
    {
        if (_loadRunning || (_loadedAt is { } at && DateTimeOffset.Now - at < StaleAfter))
        {
            return;
        }

        _ = LoadSafelyAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ActionMessage = null;
        await LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Esc: closes the add form. (The review dialog handles its own Esc.)</summary>
    public bool HandleEscape()
    {
        if (Review.IsOpen)
        {
            return Review.HandleEscape();
        }

        if (IsAddFormOpen)
        {
            IsAddFormOpen = false;
            return true;
        }

        return false;
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A background catch-up must not take the panel down; the message is shown in the banner.
        catch (Exception ex)
        {
            Trace.TraceError($"Registries catch-up failed: {ex}");
            CliErrorMessage = $"Could not refresh registry sources: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_loadRunning)
        {
            return;
        }

        _loadRunning = true;
        IsLoading = true;
        CliErrorMessage = null;
        var previousId = SelectedSource?.Id;
        var rows = new List<RegistrySourceRow>();

        // try/finally, not a trailing assignment: a cancelled or faulted read must not leave the
        // progress ring spinning over a panel that has already stopped trying.
        try
        {
            try
            {
                var invocation = await DiscoverCli.RunReadOnlyAsync(
                    Services, new[] { "registry", "list", "--json" }, cancellationToken).ConfigureAwait(true);

                if (invocation.FailureReason is { Length: > 0 } reason)
                {
                    // FailureReason is not only "could not start": it also says "timed out after
                    // 120 s" and "cancelled", so the sentence must not claim a start failure.
                    CliErrorMessage = $"'defenseclaw registry list' did not complete: {reason}";
                }
                else if (invocation.ExitCode != 0)
                {
                    var stderr = DiscoverCli.Stderr(invocation);
                    CliErrorMessage = string.IsNullOrWhiteSpace(stderr)
                        ? $"'defenseclaw registry list' exited {invocation.ExitCode}."
                        : $"'defenseclaw registry list' exited {invocation.ExitCode}: {stderr}";
                }
                else
                {
                    ParseSources(DiscoverCli.Stdout(invocation), rows);
                }
            }
            catch (CliNotFoundException ex)
            {
                CliErrorMessage = $"'defenseclaw' was not found on PATH: {ex.Message}";
            }

            // A failed read keeps the previous rows (stale but useful) instead of blanking the list.
            if (!HasCliError)
            {
                SyncCollection(
                    Sources,
                    rows,
                    r => r.Id,
                    (existing, wanted) => string.Equals(existing.Signature, wanted.Signature, StringComparison.Ordinal));
            }

            HasSources = Sources.Count > 0;
            _loadedAt = DateTimeOffset.Now;
            var asOf = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            StatusMessage = HasCliError
                ? $"Could not read sources · tried {asOf}"
                : Sources.Count == 0
                    ? $"No registry sources · as of {asOf}"
                    : $"{Sources.Count} registry source{(Sources.Count == 1 ? string.Empty : "s")} · as of {asOf}";

            LoadConfigFallback();
            LoadRegistryRequired();

            if (previousId is not null)
            {
                SelectedSource = Sources.FirstOrDefault(s => string.Equals(s.Id, previousId, StringComparison.Ordinal));
            }

            HasLoaded = true;
        }
        finally
        {
            IsLoading = false;
            _loadRunning = false;
        }

        // Entries change on disk after a sync/approve; re-read them for whatever is selected.
        await LoadEntriesAsync(SelectedSource).ConfigureAwait(true);
    }

    /// <summary>
    /// Falls back to whatever raw registry section survives in config.yaml — this model has no typed
    /// section for it (see <c>DefenseClawConfig.KnownSections</c>), so it round-trips as verbatim YAML
    /// text. The key the CLI writes is <c>registries</c>; <c>registry</c> is kept as a second look.
    /// </summary>
    private void LoadConfigFallback()
    {
        ConfigSectionYaml = Services.Config.SectionText("registries") ?? Services.Config.SectionText("registry");
    }

    private void LoadRegistryRequired()
    {
        var text = Services.Config.SectionText("asset_policy");
        SkillsRegistryRequired = ReadRegistryRequired(text, "skill");
        McpsRegistryRequired = ReadRegistryRequired(text, "mcp");
    }

    /// <summary><c>asset_policy.&lt;type&gt;.registry_required</c> from the raw section text; absent means off.</summary>
    internal static bool ReadRegistryRequired(string? sectionYaml, string type)
    {
        if (string.IsNullOrWhiteSpace(sectionYaml))
        {
            return false;
        }

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(sectionYaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                return false;
            }

            return TryChild(root, "asset_policy") is YamlMappingNode policy
                && TryChild(policy, type) is YamlMappingNode typed
                && TryChild(typed, "registry_required") is YamlScalarNode flag
                && bool.TryParse(flag.Value, out var required)
                && required;
        }
        catch (YamlException)
        {
            return false;
        }
    }

    private static YamlNode? TryChild(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    partial void OnSelectedSourceChanged(RegistrySourceRow? value)
    {
        SelectedEntry = null;
        _ = LoadEntriesSafelyAsync(value);
    }

    private async Task LoadEntriesSafelyAsync(RegistrySourceRow? source)
    {
        try
        {
            await LoadEntriesAsync(source).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Entries are a convenience view; a failure is reported in the entries message.
        catch (Exception ex)
        {
            Trace.TraceError($"Registry entries read failed: {ex}");
            EntriesMessage = $"Could not read the cached entries: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Reads <c>registries\&lt;id&gt;\index.json</c> for <paramref name="source"/> (capped at 4 MiB /
    /// 5,000 entries). The id is checked against <see cref="SourceIdPattern"/> first, so a value from
    /// the CLI's JSON can never point outside the registries folder.
    /// </summary>
    private async Task LoadEntriesAsync(RegistrySourceRow? source)
    {
        var sequence = ++_entriesSequence;

        if (source is null || !SourceIdPattern.IsMatch(source.Id))
        {
            Entries.Clear();
            EntriesMessage = source is null ? null : "This source id is not a plain name, so its cache is not read here.";
            IsEntriesLoading = false;
            return;
        }

        IsEntriesLoading = true;
        var rows = new List<RegistryEntryRow>();
        string? message = null;
        var path = Path.Combine(Services.Paths.DataDirectory, "registries", source.Id, "index.json");

        try
        {
            if (!File.Exists(path))
            {
                message = "Nothing cached yet. Sync this source to fetch and scan its entries.";
            }
            else if (new FileInfo(path).Length > MaxIndexBytes)
            {
                message = "The cached index is larger than 4 MiB, so it is not shown here. Use 'defenseclaw registry entries' in a terminal.";
            }
            else
            {
                var text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
                message = ParseIndex(text, rows);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            message = $"Could not read the cached index: {ex.Message}";
        }

        if (sequence != _entriesSequence)
        {
            return;
        }

        Entries.Clear();
        foreach (var row in rows)
        {
            Entries.Add(row);
        }

        EntriesMessage = message;
        IsEntriesLoading = false;
    }

    /// <summary>Fills <paramref name="rows"/> from an index.json body; returns a message when there is nothing to show.</summary>
    internal static string? ParseIndex(string json, List<RegistryEntryRow> rows)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        JsonElement list = default;
        var found = false;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "verdicts", "entries" })
            {
                if (root.TryGetProperty(key, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
                {
                    list = candidate;
                    found = true;
                    break;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            list = root;
            found = true;
        }

        if (!found)
        {
            return "The cached index has no entry list yet. Sync this source.";
        }

        foreach (var element in list.EnumerateArray())
        {
            if (rows.Count >= MaxEntries)
            {
                return $"Showing the first {MaxEntries:N0} entries.";
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var name = JsonString(element, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            rows.Add(new RegistryEntryRow
            {
                Name = name,
                Type = JsonString(element, "type"),
                Status = JsonString(element, "status"),
                Approved = JsonBool(element, "approved"),
                Rejected = JsonBool(element, "rejected"),
                Severity = JsonString(element, "severity"),
                Location = JsonString(element, "source_url") ?? JsonString(element, "url") ?? JsonString(element, "target"),
            });
        }

        return rows.Count == 0 ? "The last sync cached no entries." : null;
    }

    private static string? JsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool JsonBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private void ParseSources(string json, List<RegistrySourceRow> rows)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(DiscoverCli.TrimToJson(json));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                index++;
                rows.Add(MapSource(element, index));
            }
        }
        catch (JsonException ex)
        {
            CliErrorMessage = $"'defenseclaw registry list --json' did not return valid JSON: {ex.Message}";
        }
    }

    private static RegistrySourceRow MapSource(JsonElement element, int fallbackIndex)
    {
        var fields = new List<RegistryFieldRow>();
        string? id = null;
        string? kind = null;
        string? location = null;
        string? entries = null;
        string? content = null;
        string? authEnv = null;
        string? lastSync = null;
        string? lastStatus = null;
        bool? enabled = null;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var display = FormatValue(property.Value);
                fields.Add(new RegistryFieldRow(property.Name, display));

                switch (property.Name.ToLowerInvariant())
                {
                    case "id":
                    case "name":
                        id ??= display;
                        break;
                    case "type":
                    case "kind":
                        kind ??= display;
                        break;
                    case "url":
                    case "source":
                    case "location":
                    case "repo":
                        location ??= display;
                        break;
                    case "entries":
                    case "entry_count":
                    case "count":
                        entries ??= SummarizeEntries(property.Value) ?? display;
                        break;
                    case "content":
                        content ??= display;
                        break;
                    case "auth_env":
                        authEnv ??= string.IsNullOrEmpty(display) || display == "—" ? null : display;
                        break;
                    case "enabled":
                        if (enabled is null)
                        {
                            enabled = property.Value.ValueKind switch
                            {
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => (bool?)null,
                            };
                        }

                        break;
                    case "last_sync":
                        lastSync ??= property.Value.ValueKind == JsonValueKind.String ? display : null;
                        break;
                    case "last_status":
                        lastStatus ??= property.Value.ValueKind == JsonValueKind.String ? display : null;
                        break;
                }
            }
        }
        else
        {
            fields.Add(new RegistryFieldRow("value", FormatValue(element)));
        }

        return new RegistrySourceRow
        {
            Id = id ?? $"source-{fallbackIndex.ToString(CultureInfo.InvariantCulture)}",
            Kind = kind,
            Location = location,
            EntriesSummary = entries,
            Fields = fields,
            Content = content,
            AuthEnv = authEnv,
            Enabled = enabled,
            LastSync = lastSync,
            LastStatus = lastStatus,
            Signature = element.GetRawText(),
        };
    }

    /// <summary><c>{"total":12,"clean":9,"warning":2,"blocked":1,"error":0}</c> → "12 (9 clean, 2 warning, 1 blocked)"; a bare number stays a number.</summary>
    private static string? SummarizeEntries(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        static long Count(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

        var total = Count(value, "total");
        if (total == 0)
        {
            return "0";
        }

        var parts = new List<string>();
        foreach (var (name, label) in new[] { ("clean", "clean"), ("warning", "warning"), ("blocked", "blocked"), ("error", "error") })
        {
            var n = Count(value, name);
            if (n > 0)
            {
                parts.Add($"{n.ToString(CultureInfo.InvariantCulture)} {label}");
            }
        }

        var head = total.ToString(CultureInfo.InvariantCulture);
        return parts.Count == 0 ? head : $"{head} ({string.Join(", ", parts)})";
    }

    private static string FormatValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Null => "—",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.GetRawText(),
    };
}
