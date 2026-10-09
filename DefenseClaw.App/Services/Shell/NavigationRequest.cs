using System.Diagnostics;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Services;

/// <summary>
/// "Show this panel, and tell it this": the shell's deep link. What the Mac calls <c>AppState.openAlerts / openAudit /
/// openLogs</c> — the status strip's alert chip opening Alerts on the critical ones, an Overview tile opening Audit on a
/// preset — done once here rather than once per pair of panels.
/// <para>
/// Raise one with <see cref="ShellNavigation.Request(NavigationRequest)"/> (from a panel view-model: the protected
/// <c>RequestNavigation</c> on <see cref="ViewModels.PanelViewModelBase"/>). The shell shows the window and selects the panel;
/// the panel is handed <paramref name="Payload"/> when it becomes the one on screen, through
/// <see cref="IAcceptsNavigation"/>, exactly once.
/// </para>
/// </summary>
/// <param name="PanelId">The <see cref="PanelDescriptor.Id"/> of the panel to show (<c>alerts</c>, <c>audit</c>, <c>logs</c>, …), compared without case.</param>
/// <param name="Payload">What to tell the panel, or null to only show it. One of the records below, or one a panel defines; a panel ignores what it does not understand.</param>
public sealed record NavigationRequest(string PanelId, object? Payload = null);

/// <summary>
/// Implemented by a panel view-model that can be told something when it is navigated to. <see cref="Accept"/> is called on the UI
/// thread, once per request, <b>right after</b> <see cref="ViewModels.PanelViewModelBase.OnActivated"/> has run (the panel is
/// active and subscribed) and before the window has painted the activation — including on the very first visit, when
/// <c>InitializeAsync</c> may not have finished, so it must tolerate a panel that is still loading (apply the payload to state
/// the load will honour, or remember it and apply it when the data arrives).
/// <para>
/// It is also called when the panel is <i>already</i> on screen and a request for it arrives; the contract is the same either
/// way: apply the payload, refresh what depends on it. A payload of a type the panel does not know is ignored, never an error.
/// An exception from here is traced and swallowed, so a bad payload cannot break navigation.
/// </para>
/// </summary>
public interface IAcceptsNavigation
{
    /// <param name="payload">The request's <see cref="NavigationRequest.Payload"/>; never null (a request without one delivers nothing).</param>
    void Accept(object payload);
}

/// <summary>Payload for <c>alerts</c>: open the Alerts panel narrowed to this.</summary>
/// <param name="SeverityFloor">Show only this severity and above (<see cref="AuditSeverity.Critical"/> for "just the critical ones"); null leaves the severity filter alone.</param>
/// <param name="Kind">Which kind of alert: <see cref="KindAll"/> or <see cref="KindBlocks"/> (the Mac's two), or null to leave it alone. A panel that knows more kinds names them itself.</param>
public sealed record AlertsFilter(AuditSeverity? SeverityFloor = null, string? Kind = null)
{
    /// <summary>Every kind of alert.</summary>
    public const string KindAll = "all";

    /// <summary>Only enforcement blocks.</summary>
    public const string KindBlocks = "blocks";
}

/// <summary>Payload for <c>audit</c>: open the Audit panel on a named preset (a saved combination of window, buckets and severity the panel defines).</summary>
/// <param name="Name">The preset's name, as the Audit panel knows it.</param>
public sealed record AuditPreset(string Name);

/// <summary>Payload for <c>logs</c>: open the Logs panel on a named preset (a stream and a filter the panel defines).</summary>
/// <param name="Name">The preset's name, as the Logs panel knows it.</param>
public sealed record LogsPreset(string Name);

/// <summary>
/// Payload for <c>logs</c>: open the Logs panel on its <b>Events</b> stream (every canonical event of <c>audit.db</c>, the 0.8.10 TUI's v8 history) from a clean
/// slate - no search, any severity, every action and event. Alerts stays the findings queue; this is the one click from there to the rest (CUST-262).
/// </summary>
/// <param name="ActionableOnly">True (the default) opens on the TUI's actionable view, with its "Actionable only" switch on; false opens on every event.</param>
public sealed record LogsEvents(bool ActionableOnly = true);

/// <summary>Payload for <c>overview</c>: scroll to a card and put focus on its action (the command palette's "Run doctor"). Nothing is run by it.</summary>
/// <param name="Section">The card, as the Overview knows it: <see cref="DoctorSection"/>.</param>
public sealed record OverviewFocus(string Section)
{
    /// <summary>The Doctor card: focus lands on its Run doctor button, which the operator still has to press.</summary>
    public const string DoctorSection = "doctor";
}

/// <summary>
/// Payload for <c>ai-discovery</c>: open the reviewed "Run an AI discovery scan?" dialog (the palette's and Ctrl+Shift+A's "Scan AI
/// components"). Nothing is run by it; the operator still confirms the review.
/// </summary>
public sealed record AiDiscoveryScan;

/// <summary>
/// Payload for <c>registries</c> (CUST-276, the Skills and MCPs "Open in Registries" row action; the TUI's <c>R</c> on those panels): open the
/// Registries panel on its Entries tab, narrowed to the cached entries of this type and name in every source, with the entry of
/// <paramref name="SourceId"/> selected and focused (the first match when that source does not list it). Nothing is run by it.
/// </summary>
/// <param name="EntryType">The kind of entry, as the registry cache spells it: <c>skill</c> or <c>mcp</c>.</param>
/// <param name="Name">The entry's name: a skill's name or an MCP server's key.</param>
/// <param name="SourceId">The registry source that promoted the item (the id in its policy rule's <c>registry:&lt;id&gt;</c> reason); null when it is not known.</param>
public sealed record RegistryFocus(string EntryType, string Name, string? SourceId = null);

/// <summary>What <see cref="ShellNavigation.Requested"/> carries.</summary>
public sealed class NavigationRequestedEventArgs : EventArgs
{
    public NavigationRequestedEventArgs(NavigationRequest request)
    {
        Request = request;
    }

    public NavigationRequest Request { get; }
}

/// <summary>
/// The inbox for <see cref="NavigationRequest"/>s: the one place a request waits between "someone asked" and "the panel is
/// on screen". Lives on <see cref="AppServices"/> so anything with the composition — a panel, the tray, the status strip —
/// can raise one, and the shell objects that act on them (<see cref="PanelCatalog"/>, the window, the app) find the same inbox.
/// <para>
/// <b>Consume once; one outstanding.</b> A request is held until the panel it names becomes active, which takes it
/// (<see cref="TryTake"/>) and is given its payload; a second request while one is waiting replaces it — the operator's
/// latest click wins, and a panel that was never shown never receives a stale one. A request for a panel that is already on
/// screen is delivered at once. A request for a panel that does not exist is dropped with a trace.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Request(NavigationRequest)"/>, and so <see cref="Requested"/>, belong to the UI thread:
/// the subscribers switch panels. The state itself is locked, so reading <see cref="Pending"/> from anywhere is safe.
/// </para>
/// </summary>
public sealed class ShellNavigation
{
    private readonly object _gate = new();
    private NavigationRequest? _pending;

    /// <summary>
    /// Raised after a request is stored as <see cref="Pending"/>: the shell shows the dashboard and selects the panel; the
    /// catalog delivers at once if the panel is already on screen. On the caller's thread; a subscriber that throws is traced
    /// and skipped.
    /// </summary>
    public event EventHandler<NavigationRequestedEventArgs>? Requested;

    /// <summary>
    /// Raised by <see cref="RequestPalette"/>: a panel asking the shell to open its command palette (the Overview's Diagnostics
    /// menu). Not a panel request, so nothing waits in the inbox: the dashboard window, which owns the palette, opens it, or
    /// ignores the call while it is not built. On the caller's thread (the UI thread).
    /// </summary>
    public event EventHandler? PaletteRequested;

    /// <summary>Asks the shell to open the command palette; see <see cref="PaletteRequested"/>.</summary>
    public void RequestPalette() => PaletteRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The request waiting for its panel, or null. Replaced by the next <see cref="Request(NavigationRequest)"/>.</summary>
    public NavigationRequest? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Asks for <paramref name="panelId"/> to be shown, with <paramref name="payload"/> for it. See <see cref="NavigationRequest"/>.</summary>
    public void Request(string panelId, object? payload = null) => Request(new NavigationRequest(panelId, payload));

    /// <summary>Stores <paramref name="request"/> (replacing one still waiting) and raises <see cref="Requested"/>.</summary>
    public void Request(NavigationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PanelId);

        lock (_gate)
        {
            _pending = request;
        }

        var handlers = Requested;
        if (handlers is null)
        {
            return;
        }

        var args = new NavigationRequestedEventArgs(request);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<NavigationRequestedEventArgs>)handler)(this, args);
            }
#pragma warning disable CA1031 // One subscriber failing to act must not stop the others acting.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError($"navigation: a Requested subscriber threw: {ex}");
            }
        }
    }

    /// <summary>
    /// Takes the pending request if it is for <paramref name="panelId"/> (without case), leaving none pending: the consume-once.
    /// Null when nothing is pending or it is for another panel, which stays pending for its own. Called by the catalog when a
    /// panel activates.
    /// </summary>
    internal NavigationRequest? TryTake(string panelId)
    {
        lock (_gate)
        {
            if (_pending is { } request && string.Equals(request.PanelId, panelId, StringComparison.OrdinalIgnoreCase))
            {
                _pending = null;
                return request;
            }

            return null;
        }
    }

    /// <summary>Drops <paramref name="request"/> if it is still the pending one (a request for a panel that does not exist).</summary>
    internal void Discard(NavigationRequest request)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_pending, request))
            {
                _pending = null;
            }
        }
    }
}
