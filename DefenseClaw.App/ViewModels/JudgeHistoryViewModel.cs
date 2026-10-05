using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.ViewModels;

/// <summary>One label / value line of a judge response's detail, in the TUI's order.</summary>
public sealed record JudgeHistoryField(string Label, string Value);

/// <summary>
/// One retained judge response, ready to show. Every string that came out of a database passes <see cref="DisplayRedaction"/> here, so
/// nothing a view binds to can carry a credential the judge saw; the raw body is also cut to <see cref="RawDisplayLimit"/> characters.
/// </summary>
public sealed class JudgeHistoryItem
{
    /// <summary>The most characters of a raw body shown (and copied), after masking.</summary>
    public const int RawDisplayLimit = 32_768;

    private const int FieldDisplayLimit = 1_024;

    public JudgeHistoryItem(JudgeResponseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        static string Safe(string? value) => DisplayRedaction.Text(value, FieldDisplayLimit);

        Id = Safe(row.Id);
        TimeText = Safe(row.TimestampText);
        Summary = string.Join(" · ", new[] { row.Kind, row.Direction, row.Action }.Where(part => part.Length > 0).Select(Safe));
        Severity = Safe(row.Severity);
        Tone = ToneOf(row.Severity);
        IsLegacy = row.Source == JudgeHistorySource.LegacyAudit;
        SourceText = IsLegacy ? "audit.db (older)" : "judge_bodies.db";

        var fields = new List<JudgeHistoryField>
        {
            new("Timestamp", TimeText),
            new("Kind", Safe(row.Kind)),
            new("Direction", Safe(row.Direction)),
            new("Action", Safe(row.Action)),
            new("Severity", Severity),
            new("Latency (ms)", Safe(row.LatencyMs)),
        };
        AddIfAny(fields, "Inspected model", Safe(row.InspectedModel));
        AddIfAny(fields, "Judge model", Safe(row.Model));
        AddIfAny(fields, "Request ID", Safe(row.RequestId));
        AddIfAny(fields, "Trace ID", Safe(row.TraceId));
        AddIfAny(fields, "Run ID", Safe(row.RunId));
        AddIfAny(fields, "Input hash", Safe(row.InputHash));

        // Zero is a verdict, not "missing": only an absent value drops the line.
        if (row.Confidence is { } confidence)
        {
            fields.Add(new JudgeHistoryField("Confidence", confidence.ToString("0.000", CultureInfo.InvariantCulture)));
        }
        else
        {
            AddIfAny(fields, "Confidence", Safe(row.ConfidenceText));
        }

        // Shown only when it happened, as in the TUI.
        if (row.FailClosed)
        {
            fields.Add(new JudgeHistoryField("Fail-closed", "yes"));
        }

        AddIfAny(fields, "Prompt template", Safe(row.PromptTemplateId));
        AddIfAny(fields, "Parse error", Safe(row.ParseError));
        fields.Add(new JudgeHistoryField("Source", SourceText));
        Fields = fields;

        var raw = DisplayRedaction.Text(row.Raw, RawDisplayLimit);
        if (row.RawTruncated)
        {
            raw += Environment.NewLine + Environment.NewLine + "[body cut: the stored response is longer than "
                + JudgeHistoryReader.RawLimit.ToString("N0", CultureInfo.InvariantCulture) + " characters]";
        }

        RawText = raw;
        RawNote = row.RawTruncated ? "Raw (redacted, shortened)" : "Raw (redacted)";
    }

    public string Id { get; }

    public string TimeText { get; }

    /// <summary>"injection · prompt · block": what the row is, for the list.</summary>
    public string Summary { get; }

    public string Severity { get; }

    /// <summary>The <c>Tag</c> the tone bar takes: Critical, High, Medium, Low or Neutral.</summary>
    public string Tone { get; }

    public bool IsLegacy { get; }

    public string SourceText { get; }

    public IReadOnlyList<JudgeHistoryField> Fields { get; }

    /// <summary>The raw judge body, masked and bounded.</summary>
    public string RawText { get; }

    public string RawNote { get; }

    /// <summary>Line for a screen reader: the row said in words.</summary>
    public string AutomationName => $"{TimeText}, {Summary}, severity {(Severity.Length == 0 ? "unknown" : Severity)}";

    private static void AddIfAny(List<JudgeHistoryField> fields, string label, string value)
    {
        if (value.Length > 0)
        {
            fields.Add(new JudgeHistoryField(label, value));
        }
    }

    private static string ToneOf(string severity) =>
        severity.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => "Critical",
            "HIGH" => "High",
            "MEDIUM" => "Medium",
            "LOW" => "Low",
            _ => "Neutral",
        };
}

/// <summary>
/// The judge response history window (the TUI's <c>J</c> on Logs → Verdicts): the newest retained LLM-judge responses from
/// <c>judge_bodies.db</c> and the older <c>audit.db</c> table, 20 at a time with "Load more", read-only. A missing database is guidance, a
/// database that cannot be read is an error banner; neither is an exception. Opening it reads a little and never writes.
/// </summary>
public sealed partial class JudgeHistoryViewModel : ObservableObject
{
    private readonly AppServices? _services;
    private readonly Func<JudgeHistoryReader> _readerFactory;
    private int _limit = JudgeHistoryReader.DefaultLimit;
    private int _generation;

    /// <param name="services">The app's services: the data directory and the config the paths come from.</param>
    /// <param name="readerFactory">Test seam: makes the reader for each load. The app builds one from <c>config.yaml</c> every time, so a changed path is picked up.</param>
    public JudgeHistoryViewModel(AppServices services, Func<JudgeHistoryReader>? readerFactory = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _readerFactory = readerFactory ?? DefaultReader;
    }

    public ObservableCollection<JudgeHistoryItem> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private JudgeHistoryItem? _selectedItem;

    [ObservableProperty]
    private string _title = "Judge responses";

    /// <summary>Guidance (files not there yet) or the error text; empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBanner))]
    private string _bannerText = string.Empty;

    /// <summary>True when <see cref="BannerText"/> is a failure rather than guidance.</summary>
    [ObservableProperty]
    private bool _bannerIsError;

    [ObservableProperty]
    private string _bannerTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private string _countText = string.Empty;

    public bool HasSelection => SelectedItem is not null;

    public bool HasBanner => BannerText.Length > 0;

    /// <summary>The banner is a failure to read.</summary>
    public bool ShowError => HasBanner && BannerIsError;

    /// <summary>The banner is guidance: the databases are not there yet.</summary>
    public bool ShowGuidance => HasBanner && !BannerIsError;

    /// <summary>True when there is nothing to list and nothing already said by the banner: the "none persisted yet" state.</summary>
    public bool IsEmpty => !IsLoading && Items.Count == 0 && !HasBanner;

    /// <summary>The text of the empty state, the TUI's.</summary>
    public string EmptyText => "No judge responses persisted yet. Make sure guardrail.retain_judge_bodies is on and that traffic has been inspected.";

    partial void OnBannerTextChanged(string value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Reads the newest rows again, back to the first page.</summary>
    [RelayCommand]
    private Task RefreshAsync()
    {
        _limit = JudgeHistoryReader.DefaultLimit;
        return LoadAsync();
    }

    /// <summary>Shows twenty more (the TUI's cap, raised a page at a time).</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync()
    {
        _limit += JudgeHistoryReader.DefaultLimit;
        return LoadAsync();
    }

    private bool CanLoadMore() => HasMore && !IsLoading;

    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();

    /// <summary>The first read; also what Refresh does.</summary>
    public Task InitializeAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        var generation = ++_generation;
        IsLoading = true;
        try
        {
            JudgeHistoryResult result;
            try
            {
                result = await _readerFactory().ReadAsync(_limit).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (generation != _generation)
            {
                return;
            }

            Apply(result);
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
            }
        }
    }

    private void Apply(JudgeHistoryResult result)
    {
        var keepId = SelectedItem?.Id;
        Items.Clear();
        foreach (var row in result.Rows)
        {
            Items.Add(new JudgeHistoryItem(row));
        }

        SelectedItem = (keepId is { Length: > 0 } ? Items.FirstOrDefault(i => i.Id == keepId) : null) ?? Items.FirstOrDefault();
        HasMore = result.HasMore;

        BannerIsError = result.Status == JudgeHistoryStatus.Error;
        BannerTitle = result.Status switch
        {
            JudgeHistoryStatus.Error => "Judge responses could not be read",
            JudgeHistoryStatus.Missing => "No judge history yet",
            JudgeHistoryStatus.NotInitialized => "No judge history yet",
            _ => string.Empty,
        };

        // The text is a path or a database error: masked like everything else this window shows.
        BannerText = DisplayRedaction.Text(result.Message, 1_024);
        Title = Items.Count == 0 ? (BannerIsError ? "Judge responses - error" : "Judge responses") : $"Judge responses - last {Items.Count.ToString(CultureInfo.InvariantCulture)}";
        CountText = Items.Count == 0
            ? string.Empty
            : HasMore
                ? $"Newest {Items.Count.ToString(CultureInfo.InvariantCulture)} shown; older ones exist"
                : $"{Items.Count.ToString(CultureInfo.InvariantCulture)} shown, all there are";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowGuidance));
    }

    private JudgeHistoryReader DefaultReader()
    {
        var services = _services!;
        string? yaml = null;
        try
        {
            yaml = services.Config.RawText;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // No readable config is the same as one that names no paths: the defaults.
        }

        return JudgeHistoryReader.ForConfig(services.Paths, yaml);
    }
}
