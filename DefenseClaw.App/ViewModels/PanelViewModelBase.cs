using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Base class for every panel view-model. Derives from
/// <see cref="ObservableObject"/>, so <c>[ObservableProperty]</c> and
/// <c>[RelayCommand]</c> work in subclasses as long as they are declared
/// <see langword="partial"/>.
/// <para>
/// <b>Panel contract.</b> A panel is exactly two files:
/// <c>Views\Panels\{Name}Panel.xaml</c> (plus its code-behind) and
/// <c>ViewModels\{Name}PanelViewModel.cs</c>. Replacing those files is the only edit a
/// panel needs — navigation registration lives in
/// <see cref="PanelCatalog"/> and never has to change. To keep it that way:
/// </para>
/// <list type="bullet">
///   <item>keep the class names and namespaces exactly as generated,</item>
///   <item>keep the single <see cref="AppServices"/> constructor parameter,</item>
///   <item>do your I/O in <see cref="InitializeAsync"/>, never in the constructor —
///     the shell calls it once, after the view is attached, and swallows nothing.</item>
/// </list>
/// <para>
/// <b>Activation contract.</b> Panel view-models are cached for the life of the process, and
/// this is a tray-resident app: for most of its life the dashboard is hidden and every panel
/// is off screen. A panel that keeps a timer running or a gateway subscription attached
/// therefore keeps the process busy for nobody. So a panel owns that machinery only while
/// <see cref="IsActive"/> — it is the panel on screen, in a window the operator can see —
/// and the shell tells it when that changes:
/// </para>
/// <list type="bullet">
///   <item><see cref="OnActivated"/>: subscribe to the monitor, start timers, and catch up —
///     the panel was blind for however long it was inactive, so re-derive from
///     <c>Services.Monitor.Current</c> before it is seen.</item>
///   <item><see cref="OnDeactivated"/>: unsubscribe and stop every timer. Work already in
///     flight may finish; nothing new may start.</item>
/// </list>
/// <para>
/// The constructor and <see cref="InitializeAsync"/> still build the panel's initial state
/// (they run when the panel is first navigated to, before or as it activates), but must not
/// start timers or attach subscriptions: those belong to <see cref="OnActivated"/>, or the
/// panel runs from the moment it was first visited to the day the process exits. Surfaces
/// that are not panels — the tray icon, its toasts, the shell status strip — are not part of
/// this contract and stay live while the window is hidden.
/// </para>
/// <para>
/// <b>Navigation.</b> A panel that can be deep-linked to ("open Alerts on the critical ones") implements
/// <see cref="IAcceptsNavigation"/>; the catalog calls <c>Accept</c> once, right after <see cref="OnActivated"/>, when a
/// <see cref="NavigationRequest"/> for it is waiting. A panel asks for another with <see cref="RequestNavigation"/>.
/// See <c>docs/PARITY-FOUNDATIONS.md</c>.
/// </para>
/// </summary>
public abstract class PanelViewModelBase : ObservableObject
{
    private int _initialized;

    protected PanelViewModelBase(AppServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        _installationSeen = services.Installation.Context;
    }

    /// <summary>Composition root. Panels pull their Core readers and the monitor from here.</summary>
    protected AppServices Services { get; }

    /// <summary>Panel title, shown in the header. Must match the nav item's label.</summary>
    public abstract string Title { get; }

    /// <summary>One-line description under the title. Optional.</summary>
    public virtual string Description => string.Empty;

    /// <summary>
    /// True while this panel is the one shown, in a dashboard window that is visible and not
    /// minimized. Read and written on the UI thread only. See the type documentation for
    /// what a panel may do while it is false.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Called once, by the shell, the first time the panel is navigated to. Override to
    /// load data. Exceptions propagate to <see cref="PanelCatalog"/>, which surfaces them
    /// rather than swallowing them.
    /// </summary>
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// The panel just became the one on screen, or the window it lives in just became
    /// visible again. Runs on the UI thread, possibly before <see cref="InitializeAsync"/>
    /// has finished on the very first visit, so it must tolerate that. Attach subscriptions,
    /// start timers, and bring the panel up to date with one catch-up pass.
    /// </summary>
    protected virtual void OnActivated()
    {
    }

    /// <summary>
    /// The panel left the screen, or the window was hidden to the tray or minimized. Runs on
    /// the UI thread. Detach every subscription and stop every timer; the view-model stays
    /// cached, and <see cref="OnActivated"/> will run again.
    /// </summary>
    protected virtual void OnDeactivated()
    {
    }

    /// <summary>
    /// Asks the shell to show another panel, optionally telling it something (an <see cref="AlertsFilter"/>, an
    /// <see cref="AuditPreset"/>, a <see cref="LogsPreset"/>, or any payload the target panel understands). The window is
    /// brought up if it is in the tray, the panel selected, and the payload handed to it once it is on screen — see
    /// <see cref="ShellNavigation"/> and <see cref="IAcceptsNavigation"/>. UI thread only.
    /// </summary>
    /// <param name="panelId">The target's <see cref="PanelDescriptor.Id"/>.</param>
    /// <param name="payload">What to tell it, or null to only show it.</param>
    protected void RequestNavigation(string panelId, object? payload = null) => Services.Navigation.Request(panelId, payload);

    /// <summary>
    /// Gives a navigation payload to this panel if it takes one (<see cref="IAcceptsNavigation"/>); a panel that does not simply
    /// has nothing to apply. An exception from <c>Accept</c> is traced, not propagated: it runs inside the catalog's activation
    /// handler, and a panel that cannot use a payload must not stop the operator getting to it. <see cref="PanelCatalog"/> calls
    /// this, right after <see cref="OnActivated"/>.
    /// </summary>
    internal void AcceptNavigation(object payload)
    {
        if (this is not IAcceptsNavigation target)
        {
            return;
        }

        try
        {
            target.Accept(payload);
        }
#pragma warning disable CA1031 // A payload the panel cannot apply must not break navigating to it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"panel '{Title}' could not accept its navigation payload ({payload.GetType().Name}): {ex}");
        }
    }

    /// <summary>Idempotent wrapper the shell calls; runs <see cref="InitializeAsync"/> once.</summary>
    internal Task EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _initialized, 1) == 0
            ? InitializeAsync(cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Moves the panel between active and inactive, calling exactly one of
    /// <see cref="OnActivated"/> / <see cref="OnDeactivated"/> per real transition. Repeating
    /// the current state is a no-op, so the shell can call it freely from several signals.
    /// UI thread only; <see cref="PanelCatalog"/> is the only caller.
    /// </summary>
    internal void SetActive(bool active)
    {
        if (IsActive == active)
        {
            return;
        }

        IsActive = active;

        if (active)
        {
            Services.ConnectorScope.Changed += OnSharedScopeChanged;
            Services.Installation.Changed += OnSharedInstallationChanged;
            OnActivated();
            CatchUpConnectorScope();
            CatchUpInstallation();
        }
        else
        {
            Services.ConnectorScope.Changed -= OnSharedScopeChanged;
            Services.Installation.Changed -= OnSharedInstallationChanged;
            OnDeactivated();
        }
    }

    /// <summary>
    /// Why every control of this panel that changes DefenseClaw is off - the installation is managed or invalid (<c>Services.Installation</c>) -
    /// as a sentence for a tooltip; null while they are on. Bind a disabled control's <c>ToolTip</c> (with <c>ToolTipService.ShowOnDisabled</c>) to
    /// it. The Overview banner, the Settings block and every other disabled control say the same sentence.
    /// </summary>
    public string? InstallationBlockedReason => Services.Installation.BlockedReason;

    /// <summary>
    /// False for a managed or invalid installation. What a command that starts a change uses as its can-execute
    /// (<c>[RelayCommand(CanExecute = nameof(CanChangeInstallation))]</c>) or a button as its <c>IsEnabled</c>; it is raised, and every command of the
    /// panel asked again, when the verdict changes.
    /// </summary>
    public bool CanChangeInstallation => InstallationBlockedReason is null;

    /// <summary>
    /// The installation this app drives turned read-only (or writable again) while this panel was on screen, or did while it was away and the
    /// panel has just come back. <see cref="InstallationBlockedReason"/>, <see cref="CanChangeInstallation"/> and every command's can-execute
    /// are raised again for it (nothing else tells the bindings); a panel that computes anything else from <c>Services.Installation</c> when it
    /// is drawn (a flag that also follows the trust of a list, a tooltip) raises that here. Runs on the UI thread, only when the verdict differs
    /// from the one the panel last saw. The default does nothing.
    /// </summary>
    protected virtual void OnInstallationChanged()
    {
    }

    private InstallationContext _installationSeen;

    /// <summary>The command properties of each panel type (<c>XCommand</c>, from <c>[RelayCommand]</c>), found once.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> CommandProperties = new();

    private void NotifyCommandsOfInstallation()
    {
        var properties = CommandProperties.GetOrAdd(
            GetType(),
            static type => type
                .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && typeof(CommunityToolkit.Mvvm.Input.IRelayCommand).IsAssignableFrom(p.PropertyType))
                .ToArray());

        foreach (var property in properties)
        {
            (property.GetValue(this) as CommunityToolkit.Mvvm.Input.IRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    private void OnSharedInstallationChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CatchUpInstallation();
        }
        else
        {
            _ = dispatcher.BeginInvoke(CatchUpInstallation);
        }
    }

    private void CatchUpInstallation()
    {
        var current = Services.Installation.Context;
        if (!IsActive || current == _installationSeen)
        {
            return;
        }

        _installationSeen = current;
        try
        {
            OnPropertyChanged(nameof(InstallationBlockedReason));
            OnPropertyChanged(nameof(CanChangeInstallation));
            NotifyCommandsOfInstallation();
            OnInstallationChanged();
        }
#pragma warning disable CA1031 // A panel that cannot redraw its controls must not stop the others hearing of the change.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"panel '{Title}' could not follow the installation: {ex}");
        }
    }

    /// <summary>
    /// The shared connector scope (<see cref="AppServices.ConnectorScope"/>) changed while this panel is the one on screen, or had
    /// changed while it was away and the panel has just come back: re-project what it lists (<c>scope.Allows(row.Connector)</c>) or
    /// re-ask for it. Runs on the UI thread, only when the scope itself differs from the one the panel last saw (a roster change that
    /// leaves the scope alone is not a change). A panel that filters rows by connector overrides this; the default does nothing.
    /// </summary>
    protected virtual void OnConnectorScopeChanged()
    {
    }

    /// <summary>The page toolbar's connector chip (one model per panel; the chip hides itself with one connector or none).</summary>
    public ConnectorScopeViewModel ConnectorChip => _connectorChip ??= new ConnectorScopeViewModel(Services.ConnectorScope);

    private ConnectorScopeViewModel? _connectorChip;

    private string? _scopeSeen;

    private void OnSharedScopeChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            CatchUpConnectorScope();
        }
        else
        {
            _ = dispatcher.BeginInvoke(CatchUpConnectorScope);
        }
    }

    private void CatchUpConnectorScope()
    {
        var current = Services.ConnectorScope.Current;
        if (!IsActive || string.Equals(current, _scopeSeen, StringComparison.Ordinal))
        {
            return;
        }

        _scopeSeen = current;
        try
        {
            OnConnectorScopeChanged();
        }
#pragma warning disable CA1031 // A panel that cannot re-project must not break the scope change for the others.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError($"panel '{Title}' could not follow the connector scope: {ex}");
        }
    }

    /// <summary>
    /// Brings a bound collection in line with <paramref name="desired"/> without rebuilding
    /// it, so the visuals for rows that did not change survive: scroll position, text
    /// selection inside a row template and list selection all live in those visuals, and a
    /// <c>Clear()</c> + <c>Add()</c> pass destroys every one of them on each refresh.
    /// <para>
    /// Rows are matched by <paramref name="keyOf"/> (ordinal). Rows whose key is gone are
    /// removed, rows out of position are moved, new keys are inserted, and a matched row is
    /// left alone when <paramref name="isCurrent"/> says it still shows what the desired row
    /// would — optionally after <paramref name="refresh"/> copies over the few fields that
    /// legitimately change under the same key — and replaced in place when it does not. If
    /// keys repeat, the result is still exactly <paramref name="desired"/> by position; only
    /// the reuse is less exact. Lists here are tens of rows, so the search is a plain scan.
    /// Must run on the UI thread, like any mutation of a bound collection.
    /// </para>
    /// </summary>
    /// <param name="target">The bound collection to update.</param>
    /// <param name="desired">The rows it should end up holding, in order.</param>
    /// <param name="keyOf">Stable identity of a row across refreshes.</param>
    /// <param name="isCurrent">(existing, desired) → true to keep <c>existing</c> as is.</param>
    /// <param name="refresh">(existing, desired) → copies live fields onto a kept row.</param>
    protected static void SyncCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired,
        Func<T, string> keyOf,
        Func<T, T, bool> isCurrent,
        Action<T, T>? refresh = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(isCurrent);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in desired)
        {
            _ = wanted.Add(keyOf(row));
        }

        // Removals first, so a row that is going away cannot be mistaken for one that moved.
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(keyOf(target[i])))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            var key = keyOf(want);

            if (i >= target.Count)
            {
                target.Add(want);
                continue;
            }

            if (!string.Equals(keyOf(target[i]), key, StringComparison.Ordinal))
            {
                var found = -1;
                for (var j = i + 1; j < target.Count; j++)
                {
                    if (string.Equals(keyOf(target[j]), key, StringComparison.Ordinal))
                    {
                        found = j;
                        break;
                    }
                }

                if (found < 0)
                {
                    target.Insert(i, want);
                    continue;
                }

                target.Move(found, i);
            }

            var existing = target[i];
            if (isCurrent(existing, want))
            {
                refresh?.Invoke(existing, want);
            }
            else
            {
                target[i] = want;
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}
