using System.Diagnostics;
using System.Windows;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Services;

/// <summary>One navigable panel: its label, its group, its icon, and how to build it.</summary>
/// <param name="Id">Stable key, used for deep links and for restoring the last panel.</param>
/// <param name="Title">Sidebar label and panel header. Keep in sync with the view's header.</param>
/// <param name="Group">One of <see cref="PanelCatalog.Groups"/>.</param>
/// <param name="Icon">Sidebar glyph.</param>
/// <param name="ViewType">The <see cref="FrameworkElement"/> the navigation frame hosts.</param>
/// <param name="ViewModelFactory">Builds the view-model bound to <paramref name="ViewType"/>.</param>
public sealed record PanelDescriptor(
    string Id,
    string Title,
    string Group,
    SymbolRegular Icon,
    Type ViewType,
    Func<AppServices, PanelViewModelBase> ViewModelFactory);

/// <summary>
/// The single registry of panels, and the bridge between WPF-UI's navigation frame and
/// this app's view/view-model pairs.
/// <para>
/// <b>This file is shell-owned and should not need editing when panel content lands.</b>
/// It names each panel's view type and view-model factory and nothing else — no layout,
/// no data, no per-panel behaviour — so replacing a placeholder
/// <c>{Name}Panel.xaml</c> / <c>{Name}PanelViewModel.cs</c> pair leaves this untouched.
/// Only adding or removing a panel outright is a reason to come back here.
/// </para>
/// <para>
/// <b>Activation.</b> Views and view-models are cached forever (navigating away and back
/// keeps panel state), so "is this panel doing work" cannot be inferred from "does it exist".
/// The catalog is the one place that knows both halves of "is anyone looking at it": whether
/// the panel's view is actually on screen (<see cref="UIElement.IsVisible"/>, which is false
/// once the navigation frame swaps the view out <i>and</i> once the window is hidden to the
/// tray) and whether the dashboard window is interactive at all
/// (<see cref="SetWindowInteractive"/>, which the window feeds because a minimized window
/// still reports itself visible). It combines the two and calls
/// <see cref="PanelViewModelBase.SetActive"/>; see the activation contract on that type.
/// Nothing here subscribes to the navigation control itself, so a WPF-UI upgrade cannot break it.
/// </para>
/// </summary>
public sealed class PanelCatalog : INavigationViewPageProvider
{
    /// <summary>Sidebar groups, in display order.</summary>
    public static readonly IReadOnlyList<string> Groups = new[]
    {
        "Monitor",
        "Govern",
        "Discover",
        "Configure",
    };

    private readonly AppServices _services;
    private readonly Dictionary<Type, FrameworkElement> _views = new();
    private readonly Dictionary<Type, PanelViewModelBase> _viewModels = new();

    /// <summary>
    /// Whether the dashboard window is one the operator can see and use. Starts true so a
    /// host that never reports it (a test harness) still gets panels that work; the real
    /// window reports it immediately, and a view that is not in a visible tree is inactive
    /// regardless of this flag.
    /// </summary>
    private bool _windowInteractive = true;

    public PanelCatalog(AppServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Panels = new PanelDescriptor[]
        {
            // Monitor
            new("overview", "Overview", "Monitor", SymbolRegular.AppsListDetail24,
                typeof(OverviewPanel), s => new OverviewPanelViewModel(s)),
            new("alerts", "Alerts", "Monitor", SymbolRegular.AlertUrgent24,
                typeof(AlertsPanel), s => new AlertsPanelViewModel(s)),
            new("logs", "Logs", "Monitor", SymbolRegular.DocumentBulletList24,
                typeof(LogsPanel), s => new LogsPanelViewModel(s)),
            new("audit", "Audit", "Monitor", SymbolRegular.DatabaseSearch24,
                typeof(AuditPanel), s => new AuditPanelViewModel(s)),
            new("activity", "Activity", "Monitor", SymbolRegular.History32,
                typeof(ActivityPanel), s => new ActivityPanelViewModel(s)),

            // Govern
            new("skills", "Skills", "Govern", SymbolRegular.PuzzlePiece24,
                typeof(SkillsPanel), s => new SkillsPanelViewModel(s)),
            new("mcps", "MCPs", "Govern", SymbolRegular.PlugConnected24,
                typeof(McpsPanel), s => new McpsPanelViewModel(s)),
            new("plugins", "Plugins", "Govern", SymbolRegular.PuzzleCube24,
                typeof(PluginsPanel), s => new PluginsPanelViewModel(s)),
            new("tools", "Tools", "Govern", SymbolRegular.WrenchScrewdriver24,
                typeof(ToolsPanel), s => new ToolsPanelViewModel(s)),

            // Discover
            new("inventory", "Inventory", "Discover", SymbolRegular.BookDatabase24,
                typeof(InventoryPanel), s => new InventoryPanelViewModel(s)),
            new("ai-discovery", "AI Discovery", "Discover", SymbolRegular.Bot24,
                typeof(AiDiscoveryPanel), s => new AiDiscoveryPanelViewModel(s)),
            new("registries", "Registries", "Discover", SymbolRegular.Library24,
                typeof(RegistriesPanel), s => new RegistriesPanelViewModel(s)),

            // Configure
            new("setup", "Setup", "Configure", SymbolRegular.ShieldSettings24,
                typeof(SetupPanel), s => new SetupPanelViewModel(s)),
        };
    }

    /// <summary>Every panel, in sidebar order.</summary>
    public IReadOnlyList<PanelDescriptor> Panels { get; }

    /// <summary>
    /// Every panel in the order the sidebar shows them: group by group (<see cref="Groups"/>),
    /// catalog order within a group. This — not <see cref="Panels"/>, which only happens to be
    /// grouped already — is the order the Ctrl+1…9 / Ctrl+0 / Ctrl+Shift+1…3 chords count in.
    /// </summary>
    public IReadOnlyList<PanelDescriptor> SidebarOrder =>
        _sidebarOrder ??= Groups.SelectMany(InGroup).ToArray();

    private PanelDescriptor[]? _sidebarOrder;

    /// <summary>The panel the shell opens on first launch.</summary>
    public PanelDescriptor Default => Panels[0];

    /// <summary>
    /// Raised when a panel's <c>InitializeAsync</c> throws, so the shell can say so. Without a
    /// listener such a panel would sit on its "Loading…" state forever with nothing to explain
    /// it; <c>MainWindow</c> subscribes and shows a banner naming the panel and the error.
    /// <para>
    /// Raised on the UI thread: <see cref="GetPage"/> runs there and the initialization
    /// continuation resumes on that context (<c>ConfigureAwait(true)</c>), and that is what lets
    /// a subscriber touch controls directly.
    /// </para>
    /// </summary>
    public event EventHandler<PanelFaultEventArgs>? PanelFaulted;

    public IEnumerable<PanelDescriptor> InGroup(string group) =>
        Panels.Where(p => string.Equals(p.Group, group, StringComparison.Ordinal));

    public PanelDescriptor? ByViewType(Type viewType) =>
        Panels.FirstOrDefault(p => p.ViewType == viewType);

    public PanelDescriptor? ById(string id) =>
        Panels.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The panel on screen right now — the one whose view-model the catalog last marked active —
    /// or null before the first navigation and while the window is hidden or minimized. What the
    /// shell's F5 and "Refresh" palette entry act on. UI thread only.
    /// </summary>
    public PanelDescriptor? ActivePanel
    {
        get
        {
            foreach (var (pageType, viewModel) in _viewModels)
            {
                if (viewModel.IsActive)
                {
                    return ByViewType(pageType);
                }
            }

            return null;
        }
    }

    /// <summary>The view-model of <see cref="ActivePanel"/>, or null. UI thread only.</summary>
    public PanelViewModelBase? ActiveViewModel
    {
        get
        {
            foreach (var viewModel in _viewModels.Values)
            {
                if (viewModel.IsActive)
                {
                    return viewModel;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Tells the catalog whether the dashboard window is currently one the operator can see
    /// and use: shown, and not minimized. Called by the window on every visibility or
    /// window-state change. Every cached panel is re-evaluated, so a panel that was on
    /// screen when the window went to the tray deactivates now, and reactivates — with its
    /// catch-up pass — when the window comes back. UI thread only.
    /// </summary>
    public void SetWindowInteractive(bool interactive)
    {
        if (_windowInteractive == interactive)
        {
            return;
        }

        _windowInteractive = interactive;

        foreach (var (pageType, view) in _views)
        {
            UpdateActivation(view, _viewModels[pageType]);
        }
    }

    /// <summary>
    /// A panel is active exactly when its view is on screen in an interactive window. The
    /// view-model, not the view, gets told: the view has nothing to pause.
    /// </summary>
    private void UpdateActivation(FrameworkElement view, PanelViewModelBase viewModel) =>
        viewModel.SetActive(_windowInteractive && view.IsVisible);

    /// <summary>
    /// Called by WPF-UI's navigation frame. Builds the view, binds a freshly created
    /// view-model, and kicks off the panel's one-shot initialization. Instances are
    /// cached, so navigating away and back keeps panel state. The view starts out
    /// inactive; it activates when the frame puts it on screen.
    /// </summary>
    public object? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);

        if (_views.TryGetValue(pageType, out var cached))
        {
            return cached;
        }

        var descriptor = ByViewType(pageType);
        if (descriptor is null)
        {
            return null;
        }

        if (Activator.CreateInstance(pageType) is not FrameworkElement view)
        {
            return null;
        }

        var viewModel = descriptor.ViewModelFactory(_services);
        view.DataContext = viewModel;
        _views[pageType] = view;
        _viewModels[pageType] = viewModel;

        // IsVisible flips when the frame attaches or detaches the view AND when the window
        // that hosts it is shown or hidden, so this one handler covers navigation and the
        // hide-to-tray case; SetWindowInteractive covers the minimized case it cannot see.
        // Loaded/Unloaded say the same thing about the visual tree and cost nothing extra
        // (SetActive ignores a repeated state), so they back the primary signal up rather
        // than leaving activation resting on one framework event.
        view.IsVisibleChanged += (_, _) => UpdateActivation(view, viewModel);
        view.Loaded += (_, _) => UpdateActivation(view, viewModel);
        view.Unloaded += (_, _) => UpdateActivation(view, viewModel);

        _ = InitializeAsync(descriptor, viewModel);
        return view;
    }

    private async Task InitializeAsync(PanelDescriptor descriptor, PanelViewModelBase viewModel)
    {
        try
        {
            await viewModel.EnsureInitializedAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // One panel failing to load must not take down the shell.
        catch (Exception ex)
        {
            Trace.TraceError($"Panel '{descriptor.Id}' failed to initialize: {ex}");
            PanelFaulted?.Invoke(this, new PanelFaultEventArgs(descriptor, ex));
        }
#pragma warning restore CA1031
    }
}

public sealed class PanelFaultEventArgs : EventArgs
{
    public PanelFaultEventArgs(PanelDescriptor panel, Exception exception)
    {
        Panel = panel;
        Exception = exception;
    }

    public PanelDescriptor Panel { get; }

    public Exception Exception { get; }
}
