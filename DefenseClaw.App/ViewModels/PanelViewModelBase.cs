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
    /// Called once, by the shell, the first time the panel is navigated to. Override to
    /// load data. Exceptions propagate to <see cref="PanelCatalog"/>, which surfaces them
    /// rather than swallowing them.
    /// </summary>
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Idempotent wrapper the shell calls; runs <see cref="InitializeAsync"/> once.</summary>
    internal Task EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
        Interlocked.Exchange(ref _initialized, 1) == 0
            ? InitializeAsync(cancellationToken)
            : Task.CompletedTask;
}
