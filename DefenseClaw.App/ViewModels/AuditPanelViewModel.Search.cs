using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The Audit panel's side of CUST-261: the search box's <c>field:value</c> tokens, the Connector column, and the inspector's "Current state".
/// <para>
/// <b>Search.</b> The box is parsed by the one shared parser (<see cref="SearchQuery"/>) and mapped onto the query the list is read with
/// (<see cref="AuditQuerySearch.WithSearch"/>): <c>connector:codex</c>, <c>severity:high</c>, <c>run:</c>, <c>trace:</c>, <c>request:</c>, <c>session:</c>,
/// <c>id:</c>, <c>actor:</c>, <c>type:</c>, <c>target:</c>, <c>action:</c> and <c>details:</c> are filters the database applies (a connector by name, the rest as
/// a part of the column, case ignored), and the free words that are left are the search the panel always had. Because it is the list's own query, the
/// page, "Load more", the total, the live refresh and the export all honour it. A search of any kind pauses "Actionable only" (CUST-262), as the TUI's
/// filter turns its actionable view off.
/// </para>
/// <para>
/// <b>Connector column.</b> In the table only while more than one connector is active (<see cref="ShowConnectorColumn"/>, the shared scope's rule), after
/// Type, as in the TUI; the single-connector table is unchanged.
/// </para>
/// <para>
/// <b>Current state.</b> Selecting an event about a skill, MCP server, plugin or tool (its action says so: <c>skill-block</c>, <c>mcp-unset</c>) looks up
/// what is being done to that item <em>now</em> in the <c>actions</c> table - blocked, allowed, quarantined, disabled - and shows it in the inspector
/// ("Current state: blocked"), the TUI's <c>Current State</c> row. The lookup is one read-only seek on the live database (also while the archive is on
/// screen: the state is about now), never starts a command, and shows nothing when the item has no entry.
/// </para>
/// </summary>
public sealed partial class AuditPanelViewModel
{
    private CancellationTokenSource? _stateSource;
    private EnforcementStateReader? _stateReader;

    /// <summary>The Connector column shows: more than one connector is active (the TUI's <c>show_connector_column</c>, set from the active connector count).</summary>
    public bool ShowConnectorColumn => Services.ConnectorScope.CanScope;

    /// <summary>The roster of connectors changed (the scope itself changing is <see cref="OnConnectorScopeChanged"/>'s): the column may appear or go.</summary>
    private void OnRosterChanged(object? sender, EventArgs e) => ConnectorColumn.Notify(() => OnPropertyChanged(nameof(ShowConnectorColumn)));

    /// <summary>The search box, parsed.</summary>
    private SearchQuery ActiveSearch => SearchQuery.Parse(SearchText);

    /// <summary>
    /// The reader of the <c>actions</c> table (the live database, always), bounded like the inspector's other lookups
    /// (<see cref="AppServices.ReaderTimeouts"/>: 8 s in the app). A test points it at its own database.
    /// </summary>
    internal EnforcementStateReader StateReader
    {
        get => _stateReader ??= new EnforcementStateReader(Services.Paths.AuditDatabasePath, Services.ReaderTimeouts.Audit);
        set => _stateReader = value;
    }

    /// <summary>What is being done to the selected event's skill, MCP server, plugin or tool now (<c>blocked</c>, <c>allowed, quarantined</c>, <c>none</c>); empty when the event is not about one or the item has no entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentState), nameof(CurrentStateLabel))]
    private string _currentStateText = string.Empty;

    /// <summary>The badge tone of <see cref="CurrentStateText"/> (<c>Bad</c>, <c>Warn</c>, <c>Ok</c> or <c>Neutral</c>), the Govern panels' own.</summary>
    [ObservableProperty]
    private string _currentStateTone = "Neutral";

    public bool HasCurrentState => CurrentStateText.Length > 0;

    /// <summary>"Current state: blocked": the row's words for a screen reader and a test.</summary>
    public string CurrentStateLabel => HasCurrentState ? "Current state: " + CurrentStateText : string.Empty;

    /// <summary>The most recent current-state lookup (a finished one once it has settled); tests await it.</summary>
    internal Task LastCurrentState { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The selected event changed: drop the state of the last one (cancelling its lookup) and, for an event about a governed item, look the new one's up.
    /// </summary>
    private void StartCurrentState(AuditRow? row)
    {
        var previous = _stateSource;
        _stateSource = null;
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

        CurrentStateText = string.Empty;
        CurrentStateTone = "Neutral";

        var type = row is null ? string.Empty : AuditTargetType.FromAction(row.Action);
        if (row is null || !AuditTargetType.IsGoverned(type) || row.Target.Length == 0)
        {
            return;
        }

        var source = new CancellationTokenSource();
        _stateSource = source;
        LastCurrentState = ReadCurrentStateAsync(row, type, source);
    }

    private async Task ReadCurrentStateAsync(AuditRow row, string type, CancellationTokenSource source)
    {
        try
        {
            var state = await StateReader.ReadAsync(type, row.Target, row.IsPlatform ? null : row.Connector, cancellationToken: source.Token);

            // Another row was picked (or the panel was reset) while the lookup ran: that one owns the line now.
            if (!ReferenceEquals(_stateSource, source) || state is null)
            {
                return;
            }

            CurrentStateText = state.Summary;
            CurrentStateTone = state.Tone;
        }
        catch (OperationCanceledException)
        {
            // Superseded: nothing to say.
        }
#pragma warning disable CA1031 // The state is an extra on the inspector; a database that is busy or older than the table must never break selecting a row.
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException or TimeoutException)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"audit: the current state of '{type}' could not be read: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
