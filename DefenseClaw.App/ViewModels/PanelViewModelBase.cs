using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;

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
/// </summary>
public abstract class PanelViewModelBase : ObservableObject
{
    private int _initialized;

    protected PanelViewModelBase(AppServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
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
            OnActivated();
        }
        else
        {
            OnDeactivated();
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
