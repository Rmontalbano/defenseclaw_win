using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.Redaction;

/// <summary>The status card: what the compiled plan does with every bucket and destination right now.</summary>
public sealed partial class RedactionViewModel
{
    private bool _reading;
    private bool _quickChosen;

    /// <summary>The last status read; null until one succeeds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(Summary), nameof(SummaryTone), nameof(PlanText), nameof(ConfigPathText), nameof(JudgeNote), nameof(StatusChipText), nameof(StatusChipTone))]
    private RedactionStatus? _status;

    /// <summary>True while the rows on screen are from the latest successful read. A failed refresh clears it, and changes are off until it is set again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanQuickApply), nameof(CanApplyPreview), nameof(ChangesBlockedReason))]
    private bool _statusIsCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusError))]
    private string _statusError = string.Empty;

    [ObservableProperty]
    private string _asOf = string.Empty;

    public bool HasStatus => Status is not null;

    public bool HasStatusError => StatusError.Length > 0;

    public ObservableCollection<RedactionBucketRow> BucketRows { get; } = new();

    public ObservableCollection<RedactionDestinationRow> DestinationRows { get; } = new();

    public ObservableCollection<RedactionWarningRow> WarningRows { get; } = new();

    public bool HasWarnings => WarningRows.Count > 0;

    public string Summary => Status?.Summary ?? "The redaction policy has not been read yet.";

    public string SummaryTone => Status?.Tone ?? "Neutral";

    /// <summary>"Plan 870724638c73 · catalog v1": the digest every preview is compared against.</summary>
    public string PlanText => Status is null
        ? string.Empty
        : $"Plan {(Status.PlanDigest.Length > 12 ? Status.PlanDigest[..12] : Status.PlanDigest)} · catalog v{Status.CatalogVersion.ToString(CultureInfo.InvariantCulture)}";

    public string ConfigPathText => DisplayNames.Visible(Status?.ConfigPath);

    /// <summary>The line about the judge-body store, which no profile covers.</summary>
    public string JudgeNote => DisplayNames.Visible(Status?.JudgeBodies.Disclosure);

    /// <summary>Why changes are off, when they are; empty when they are on.</summary>
    public string ChangesBlockedReason =>
        !IsSupported ? UnsupportedMessage
        : !StatusIsCurrent ? "Changes are off until the redaction policy has been read. Refresh, then try again."
        : string.Empty;

    /// <summary>
    /// Reads the status, the profile list and the routes of every destination that has ordered routes, and brings the card and the form up to
    /// date. Overlapping calls collapse into the one in flight. A failed read keeps the rows it had, says so, and turns changes off.
    /// </summary>
    public async Task RefreshAsync()
    {
        // A window that has been closed reads nothing more (an apply that finished after it closed would otherwise re-read into a dead view).
        if (_disposed || !IsSupported || _reading || IsBusy)
        {
            return;
        }

        _reading = true;
        IsBusy = true;
        try
        {
            await ReadPolicyAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing here may escape: this runs fire-and-forget when the window opens.
            TraceFault("refresh", ex);
            StatusError = DisplayNames.Visible("The redaction policy could not be read: " + ex.Message);
            StatusIsCurrent = false;
        }
        finally
        {
            _reading = false;
            IsBusy = false;
        }
    }

    /// <summary>The Refresh button and F5: not while a review is up (the review is a decision about the policy as it was).</summary>
    [RelayCommand]
    private Task Refresh() => Review.IsOpen ? Task.CompletedTask : RefreshAsync();

    private async Task ReadPolicyAsync()
    {
        var status = await ReadStatusAsync().ConfigureAwait(true);
        if (status is null)
        {
            StatusIsCurrent = false;
            return;
        }

        // The profile list and the routes are best effort: without them the form offers the four built-ins and no route to pick, and says so.
        var profiles = await ReadProfileNamesAsync().ConfigureAwait(true);
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        var routes = new Dictionary<string, IReadOnlyList<RedactionRoute>>(StringComparer.Ordinal);
        foreach (var destination in status.RoutedDestinations)
        {
            var (list, note) = await ReadRoutesAsync(destination.Name).ConfigureAwait(true);
            if (list is not null)
            {
                routes[destination.Name] = list;
            }

            if (note.Length > 0)
            {
                notes[destination.Name] = note;
            }
        }

        _routes.Clear();
        foreach (var (name, list) in routes)
        {
            _routes[name] = list;
        }

        ApplyStatus(status, profiles, notes);
    }

    private async Task<RedactionStatus?> ReadStatusAsync()
    {
        var argv = RedactionArgv.Read(RedactionOperation.Status, new RedactionInputs());
        var run = await ToRunAsync(() => RunReadAsync(argv), "setup redaction status").ConfigureAwait(true);
        if (!run.Ok)
        {
            StatusError = DisplayNames.Visible("The redaction policy could not be read. " + run.Error);
            return null;
        }

        if (!RedactionStatusParser.TryParse(run.Stdout, out var status, out var error) || status is null)
        {
            StatusError = DisplayNames.Visible("The redaction policy could not be read: " + error);
            return null;
        }

        return status;
    }

    private async Task<IReadOnlyList<string>?> ReadProfileNamesAsync()
    {
        var argv = RedactionArgv.Read(RedactionOperation.ProfileList, new RedactionInputs());
        var run = await ToRunAsync(() => RunReadAsync(argv), "setup redaction profile list").ConfigureAwait(true);
        return run.Ok && RedactionReadings.TryParseProfileList(run.Stdout, out var list, out _) && list is not null ? list.Names : null;
    }

    private async Task<(IReadOnlyList<RedactionRoute>? Routes, string Note)> ReadRoutesAsync(string destination)
    {
        // A name that cannot be passed to the CLI safely is never put on a command line: its routes are not read, and the card says so.
        if (!RedactionVocabulary.IsStableName(destination))
        {
            return (null, "The routes were not read: this destination's name cannot be passed to the CLI safely.");
        }

        var argv = RedactionArgv.Read(RedactionOperation.RouteList, new RedactionInputs { Destination = destination });
        var run = await ToRunAsync(() => RunReadAsync(argv), "setup redaction route list").ConfigureAwait(true);
        if (!run.Ok)
        {
            return (null, "The routes could not be read. " + run.Error);
        }

        return RedactionReadings.TryParseRoutes(run.Stdout, out var list, out var error) && list is not null
            ? (list.Routes, string.Empty)
            : (null, "The routes could not be read: " + error);
    }

    private void ApplyStatus(RedactionStatus status, IReadOnlyList<string>? profiles, IReadOnlyDictionary<string, string> routeNotes)
    {
        Status = status;
        StatusError = string.Empty;
        StatusIsCurrent = true;
        AsOf = "as of " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

        Replace(BucketRows, status.Buckets.Select(static b => new RedactionBucketRow(b)));
        Replace(
            DestinationRows,
            status.Destinations.Select(d => new RedactionDestinationRow(
                d,
                _routes.TryGetValue(d.Name, out var routes) ? routes : null,
                routeNotes.GetValueOrDefault(d.Name, string.Empty))));
        Replace(WarningRows, status.Warnings.Select(static w => new RedactionWarningRow(w)));
        OnPropertyChanged(nameof(HasWarnings));

        Form.SetProfiles(profiles);
        Form.SetDestinations(status.Destinations);

        // The quick sheet starts on the profile in force when that is one of the four it offers, else on sensitive; later reads leave the choice alone.
        if (!_quickChosen)
        {
            _quickChosen = true;
            QuickProfile = RedactionVocabulary.BuiltInProfiles.Contains(status.UniformProfile) ? status.UniformProfile : "sensitive";
        }

        NotifyActionState();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var next = items.ToArray();
        target.Clear();
        foreach (var item in next)
        {
            target.Add(item);
        }
    }
}
