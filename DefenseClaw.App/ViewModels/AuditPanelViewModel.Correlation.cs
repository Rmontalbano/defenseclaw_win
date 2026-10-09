using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>One finding listed under "Findings in this run" in the inspector.</summary>
public sealed record AuditFindingItem(string Title, string Severity, string SeverityKey, string Scanner, string Location, string Description)
{
    /// <summary>"scanner · location", whichever of the two there is.</summary>
    public string Origin => string.Join(" · ", new[] { Scanner, Location }.Where(s => s.Length > 0));

    public override string ToString() => $"{Severity} {Title}. {Origin}".Trim();
}

/// <summary>
/// The Mac's Audit extras: the preset strip (All, Risk, Blocks, Scans, Credentials) and the round-A deep link that selects one,
/// "Same run" (the <c>idx_audit_run_id</c> filter), the inspector's "Findings in this run" and "Related events", and Export.
/// </summary>
public sealed partial class AuditPanelViewModel
{
    public const string PresetAll = "all";
    public const string PresetRisk = "risk";
    public const string PresetBlocks = "blocks";
    public const string PresetScans = "scans";
    public const string PresetCredentials = "credentials";

    /// <summary>The most events one export writes (the reader's page cap); the note says so when more match.</summary>
    public const int ExportCap = AuditQuery.MaxLimit;

    /// <summary>How long the inspector's correlation queries may run before the section says it gave up.</summary>
    private static readonly TimeSpan CorrelationTimeout = TimeSpan.FromSeconds(8);

    private static readonly string[] PresetNames = { PresetAll, PresetRisk, PresetBlocks, PresetScans, PresetCredentials };

    private CancellationTokenSource? _correlation;
    private AuditCorrelationReader? _correlationReader;

    /// <summary>The Mac's "Audit View" preset; one of the <c>Preset…</c> names. Combined with every other filter by AND.</summary>
    [ObservableProperty]
    private string _activePreset = PresetAll;

    /// <summary>When set, only the events of this run (<c>run_id</c>); the "Same run" chip.</summary>
    [ObservableProperty]
    private string _runFilter = string.Empty;

    [ObservableProperty]
    private string _exportNote = string.Empty;

    [ObservableProperty]
    private bool _isCorrelating;

    [ObservableProperty]
    private string _relatedNote = string.Empty;

    [ObservableProperty]
    private string _findingsNote = string.Empty;

    /// <summary>Events related to the selected one (at most <see cref="AuditCorrelationReader.RelatedLimit"/>).</summary>
    public ObservableCollection<AuditRow> RelatedEvents { get; } = new();

    /// <summary>Findings of the selected event's run (at most <see cref="AuditCorrelationReader.FindingsLimit"/>).</summary>
    public ObservableCollection<AuditFindingItem> RunFindings { get; } = new();

    /// <summary>The most recent correlation load (a finished one once it has settled); tests await it.</summary>
    internal Task LastCorrelation { get; private set; } = Task.CompletedTask;

    /// <summary>Where Export writes: the test seam. Null shows the save dialog; the function returns the chosen path, or null to cancel.</summary>
    internal Func<string?>? ExportPathPicker { get; set; }

    /// <summary>The most recent export (a finished one once it has settled); tests await it.</summary>
    internal Task LastExport { get; private set; } = Task.CompletedTask;

    public bool HasRunFilter => RunFilter.Length > 0;

    /// <summary>"Run 933f8ff9…" for the chip that clears the run filter.</summary>
    public string RunFilterText => RunFilter.Length > 12 ? $"Run {RunFilter[..8]}…" : $"Run {RunFilter}";

    /// <summary>How many of the filters behind the "Filters" expander are off their defaults (the preset and the search box are not among them).</summary>
    public int ActiveFilterCount =>
        (string.Equals(SelectedBucket, AnyBucket, StringComparison.Ordinal) ? 0 : 1)
        + (SelectedSeverity == SeverityOption.Any ? 0 : 1)
        + (ReferenceEquals(SelectedConnector, ConnectorOption.All) ? 0 : 1)
        + (SelectedRange == DefaultRange ? 0 : 1)
        + (string.IsNullOrWhiteSpace(ActionFilter) ? 0 : 1);

    public string FiltersHeader => ActiveFilterCount > 0 ? $"Filters ({ActiveFilterCount.ToString(CultureInfo.InvariantCulture)})" : "Filters";

    partial void OnActivePresetChanged(string value) => Reload();

    partial void OnRunFilterChanged(string value)
    {
        OnPropertyChanged(nameof(HasRunFilter));
        OnPropertyChanged(nameof(RunFilterText));
        Reload();
    }

    /// <summary>The preset's own minimum severity: Risk is HIGH and CRITICAL; a stricter minimum the operator chose still wins.</summary>
    internal static AuditSeverity? PresetMinimumSeverity(string preset, AuditSeverity? chosen)
    {
        if (!string.Equals(preset, PresetRisk, StringComparison.Ordinal))
        {
            return chosen;
        }

        return chosen is { } c && c > AuditSeverity.High ? c : AuditSeverity.High;
    }

    /// <summary>The preset's action/details substrings, from the Mac's <c>AuditView.load</c>; null for All and Risk.</summary>
    internal static IReadOnlyList<string>? PresetActionTerms(string preset) => preset switch
    {
        PresetBlocks => new[] { "block", "reject", "enforce", "quarantine" },
        PresetScans => new[] { "scan" },
        PresetCredentials => new[] { "key", "token", "credential", "auth", "rotat" },
        _ => null,
    };

    /// <summary>
    /// A deep link (<see cref="IAcceptsNavigation"/>): an <see cref="AuditPreset"/> (the Overview's Blocks tile sends
    /// <c>blocks</c>) selects that preset and clears the search and run filters, the Mac's <c>applyPendingPanelRequest</c>; the
    /// other filters are left as the operator set them. A name the panel does not know is ignored.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is not AuditPreset preset)
        {
            return;
        }

        var name = (preset.Name ?? string.Empty).Trim().ToLowerInvariant();
        if (Array.IndexOf(PresetNames, name) < 0)
        {
            return;
        }

        BatchFilterChanges(() =>
        {
            // The links come from live counts (Overview's Blocks tile), so they mean the live log.
            SourceKey = SourceLive;
            ActivePreset = name;
            SearchText = string.Empty;
            RunFilter = string.Empty;
        });
    }

    /// <summary>Runs <paramref name="change"/> and reloads once, whatever number of filters it set (see <see cref="ResetFiltersCommand"/>).</summary>
    private void BatchFilterChanges(Action change)
    {
        _reloadDeferrals++;
        try
        {
            change();
        }
        finally
        {
            _reloadDeferrals--;
        }

        if (_reloadDeferrals == 0)
        {
            _reloadPending = false;
            Reload();
        }
    }

    /// <summary>"Same run": narrows the list to the events of the row's run, in every time range (a run can be older than the default window).</summary>
    public void ShowSameRun(AuditRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.HasRun)
        {
            return;
        }

        BatchFilterChanges(() =>
        {
            RunFilter = row.RunId;
            SelectedRange = TimeRangeOption.AllTime;
        });
    }

    [RelayCommand]
    private void FilterBySameTarget(AuditRow? row)
    {
        if (row is not null)
        {
            ShowSameTarget(row);
        }
    }

    [RelayCommand]
    private void FilterBySameRun(AuditRow? row)
    {
        if (row is not null)
        {
            ShowSameRun(row);
        }
    }

    /// <summary>Removes the run filter (the chip's X).</summary>
    [RelayCommand]
    private void ClearRunFilter() => RunFilter = string.Empty;

    private AuditCorrelationReader Correlation
    {
        get
        {
            var source = ActiveReader ?? Services.Audit;
            return _correlationReader is { } reader
                   && reader.IsImmutable == source.IsImmutable
                   && string.Equals(reader.DatabasePath, source.DatabasePath, StringComparison.Ordinal)
                ? reader
                : _correlationReader = new AuditCorrelationReader(source.DatabasePath, source.IsImmutable);
        }
    }

    /// <summary>
    /// The selected event changed: drop what the inspector listed for the last one (cancelling its queries, a running statement
    /// included) and look up the new one's related events and run findings.
    /// </summary>
    private void StartCorrelation(AuditRow? row)
    {
        var previous = _correlation;
        _correlation = null;
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            finally
            {
                previous.Dispose();
            }
        }

        RelatedEvents.Clear();
        RunFindings.Clear();
        RelatedNote = string.Empty;
        FindingsNote = string.Empty;
        IsCorrelating = false;

        if (row is null || ActiveReader is not { Exists: true })
        {
            return;
        }

        if (!row.HasRun && !row.HasTarget)
        {
            RelatedNote = "This event has no run id or target to relate it by.";
            return;
        }

        var source = new CancellationTokenSource(CorrelationTimeout);
        _correlation = source;
        LastCorrelation = CorrelateAsync(row, source);
    }

    private async Task CorrelateAsync(AuditRow row, CancellationTokenSource source)
    {
        var token = source.Token;
        IsCorrelating = true;
        try
        {
            await Task.WhenAll(LoadRelatedAsync(row, source, token), LoadFindingsAsync(row, source, token));
        }
        finally
        {
            if (ReferenceEquals(_correlation, source))
            {
                IsCorrelating = false;
            }
        }
    }

    private async Task LoadRelatedAsync(AuditRow row, CancellationTokenSource source, CancellationToken token)
    {
        try
        {
            var related = await Correlation.RelatedAsync(row.Id, row.RunId, row.Target, row.TimestampNanos, token);
            if (!ReferenceEquals(_correlation, source))
            {
                return;
            }

            foreach (var e in related.Events)
            {
                RelatedEvents.Add(new AuditRow(e));
            }

            RelatedNote = (related.Basis, related.Events.Count) switch
            {
                (RelatedBasis.Run, 0) => "No other events in this run.",
                (RelatedBasis.Run, _) => "Other events of the same run, newest first.",
                (RelatedBasis.Target, 0) => "No other events about this target within an hour of this one.",
                (RelatedBasis.Target, _) => "Other events about the same target within an hour of this one, newest first.",
                _ => string.Empty,
            };
        }
        catch (OperationCanceledException) when (ReferenceEquals(_correlation, source))
        {
            RelatedNote = "Related events took too long to look up and were skipped.";
        }
        catch (OperationCanceledException)
        {
            // Superseded by another selection: that one owns the section now.
        }
#pragma warning disable CA1031 // The inspector's extras must never break the inspector.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException && ReferenceEquals(_correlation, source))
        {
            RelatedNote = $"Related events could not be read: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    private async Task LoadFindingsAsync(AuditRow row, CancellationTokenSource source, CancellationToken token)
    {
        if (!row.HasRun)
        {
            return;
        }

        try
        {
            var findings = await Correlation.FindingsInRunAsync(row.RunId, token);
            if (!ReferenceEquals(_correlation, source))
            {
                return;
            }

            foreach (var f in findings)
            {
                RunFindings.Add(new AuditFindingItem(
                    f.Title,
                    f.Severity.Length > 0 ? f.Severity.ToUpperInvariant() : "—",
                    AuditRow.KeyFor(AuditSeverityExtensions.Parse(f.Severity)),
                    f.Scanner,
                    f.Location,
                    f.Description));
            }

            FindingsNote = findings.Count == 0 ? "No scan findings were recorded for this run." : string.Empty;
        }
        catch (OperationCanceledException) when (ReferenceEquals(_correlation, source))
        {
            FindingsNote = "Findings took too long to look up and were skipped.";
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException && ReferenceEquals(_correlation, source))
        {
            FindingsNote = $"Findings could not be read: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Exports the list as the current filters and preset define it, newest first, to a JSON or CSV file the operator picks
    /// (the extension chooses the format). At most <see cref="ExportCap"/> events: the note says how many were written and how
    /// many matched. Read-only: it only reads audit.db and writes the chosen file.
    /// </summary>
    [RelayCommand]
    private Task ExportAsync() => LastExport = ExportCoreAsync();

    private async Task ExportCoreAsync()
    {
        if (ActiveReader is not { Exists: true } reader)
        {
            ExportNote = IsArchive
                ? "There is no readable archive to export from."
                : "There is no audit database to export from yet.";
            return;
        }

        var path = (ExportPathPicker ?? PickExportPath)();
        if (path is null)
        {
            return;
        }

        var format = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)
            ? AuditExportFormat.Csv
            : AuditExportFormat.Json;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        try
        {
            // The whole of every value: a file the operator asked for must not quietly leave out a long details (the panel's own
            // limit is for what it can show). Such a query is not remembered by the reader.
            var query = BuildQuery(null) with { Limit = ExportCap, PayloadLimitBytes = AuditQuery.NoPayloadLimit };
            var platformOnly = SelectedConnector.PlatformOnly;

            // A file made while the list is narrowed to the actionable events holds what the list holds (the TUI's export is its filtered rows).
            var actionable = IsActionableApplied;
            var pageRead = reader.QueryAsync(query, token);
            var totalRead = platformOnly || actionable || query.ActionAnyOf is { Count: > 0 }
                ? null
                : reader.CountAsync(query with { After = null, Limit = PageSize }, token);
            await Task.WhenAll(pageRead, totalRead ?? Task.CompletedTask);

            var page = await pageRead;
            var events = platformOnly ? page.Events.Where(e => e.Connector is null).ToList() : page.Events.ToList();
            if (actionable)
            {
                events = events.Where(ActionableRule.IsActionable).ToList();
            }

            var text = AuditExport.Serialize(events, format);

            // Excel reads a BOM-less CSV as the system code page; JSON is UTF-8 by definition, where a BOM is noise.
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == AuditExportFormat.Csv);
            await File.WriteAllTextAsync(path, text, encoding, token);

            var written = events.Count.ToString("N0", CultureInfo.CurrentCulture);
            var total = totalRead is null ? (int?)null : await totalRead;
            ExportNote = actionable
                ? page.HasMore
                    ? $"Exported {written} actionable events from the newest {ExportCap.ToString("N0", CultureInfo.CurrentCulture)} read (the cap; older events were not looked at) to {path}."
                    : $"Exported {written} actionable events to {path}."
                : page.HasMore || (total is { } t && t > events.Count)
                    ? total is { } all
                        ? $"Exported the newest {written} of {all.ToString("N0", CultureInfo.CurrentCulture)} matching events (the cap is {ExportCap.ToString("N0", CultureInfo.CurrentCulture)}) to {path}."
                        : $"Exported the newest {written} matching events (the cap is {ExportCap.ToString("N0", CultureInfo.CurrentCulture)}; more match) to {path}."
                    : $"Exported {written} matching events to {path}.";
        }
        catch (OperationCanceledException)
        {
            ExportNote = "The export took too long and was stopped.";
        }
#pragma warning disable CA1031 // A busy database or a read-only target must degrade to a note.
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ExportNote = $"Could not export: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    private static string? PickExportPath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export audit events",
            FileName = $"audit-events-{DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            Filter = "JSON (*.json)|*.json|CSV (*.csv)|*.csv",
        };

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var chosen = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return chosen == true ? dialog.FileName : null;
    }
}
