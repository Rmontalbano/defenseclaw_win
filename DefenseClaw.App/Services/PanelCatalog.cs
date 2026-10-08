using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Runtime;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Services;

/// <summary>One navigable panel: its label, its group, its icon, and how to build it.</summary>
/// <param name="Id">Stable key, used for deep links and for restoring the last panel.</param>
/// <param name="Title">Sidebar label and panel header. Keep in sync with the view's header.</param>
/// <param name="Group">One of <see cref="PanelCatalog.Groups"/>, or <see cref="PanelCatalog.FooterGroup"/> for a panel pinned below them.</param>
/// <param name="Icon">Sidebar glyph.</param>
/// <param name="ViewType">The <see cref="FrameworkElement"/> the navigation frame hosts.</param>
/// <param name="ViewModelFactory">Builds the view-model bound to <paramref name="ViewType"/>.</param>
/// <param name="Requires">
/// The runtime capability the panel needs, or null (the default) for a panel 0.8.10 already supports. A panel that requires one is
/// offered only when the connected runtime has it: see <see cref="PanelCatalog.Gate"/>.
/// </param>
public sealed record PanelDescriptor(
    string Id,
    string Title,
    string Group,
    SymbolRegular Icon,
    Type ViewType,
    Func<AppServices, PanelViewModelBase> ViewModelFactory,
    RuntimeCapability? Requires = null);

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
    /// <summary>
    /// Whether <paramref name="panel"/> may be offered right now: open for a panel that requires nothing, and for one whose required
    /// capability the connected runtime has; closed (with the standard sentence) otherwise, including before the runtime has been probed.
    /// The command palette uses it today; a panel that sets <see cref="PanelDescriptor.Requires"/> is a one-line change there.
    /// </summary>
    internal GateDecision Gate(PanelDescriptor panel) =>
        RuntimeGate.Check(_services.Runtime.Capabilities, panel.Requires);

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

    /// <summary>The appearance service whose <see cref="AppearanceService.Changed"/> this catalog listens to (see <see cref="GetPage"/>).</summary>
    private AppearanceService? _appearance;

    public PanelCatalog(AppServices services)
        : this(services, panels: null)
    {
    }

    /// <summary>
    /// <paramref name="panels"/> replaces the real panel list; a test seam, so the activation and navigation plumbing can be
    /// exercised with panels that are three lines long instead of the real ones.
    /// </summary>
    internal PanelCatalog(AppServices services, IReadOnlyList<PanelDescriptor>? panels)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        // A navigation request for a panel that is already on screen has nothing left to activate it: deliver it here.
        // (One for a panel that is not is delivered by UpdateActivation when it comes up.) The catalog lives as long as the
        // app, like the inbox it listens to.
        _services.Navigation.Requested += OnNavigationRequested;

        Panels = panels ?? new PanelDescriptor[]
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
                typeof(SetupPanel), s => new SetupPanelViewModel(s)
                {
                    // The "Guardrail controls" tile (CUST-222/225): HILT, block message and judge, in their own window.
                    OpenGuardrailControls = () => DefenseClaw.App.Views.Guardrail.GuardrailControlsWindow.Open(s, System.Windows.Application.Current?.MainWindow),
                }),
            new("policies", "Policies", "Configure", SymbolRegular.Gavel24,
                typeof(PoliciesPanel), s => new PoliciesPanelViewModel(s)),

            // Footer: the app's own settings, below the groups (Ctrl+,).
            new("settings", "Settings", FooterGroup, SymbolRegular.Settings24,
                typeof(SettingsPanel), s => new SettingsPanelViewModel(s, Hooks)),
        };
    }

    /// <summary>
    /// The group of the panels the sidebar pins below the others, under a separator (Settings). Not one of <see cref="Groups"/>, so
    /// they are not in <see cref="SidebarOrder"/> and take no number chord: Settings has Ctrl+, instead.
    /// </summary>
    public const string FooterGroup = "App";

    /// <summary>
    /// What a panel can ask of the shell that is neither the services nor another panel: the tray's "reset seen-alert history".
    /// The dashboard window wires it when it is built (<c>MainWindow</c>), so a panel can only ever reach it while one is on screen.
    /// </summary>
    internal ShellHooks Hooks { get; } = new();

    /// <summary>Every panel, in sidebar order.</summary>
    public IReadOnlyList<PanelDescriptor> Panels { get; }

    /// <summary>The panels pinned at the foot of the sidebar (<see cref="FooterGroup"/>), in order: Settings.</summary>
    public IEnumerable<PanelDescriptor> FooterPanels => InGroup(FooterGroup);

    /// <summary>
    /// Every panel in the order the sidebar shows them: group by group (<see cref="Groups"/>),
    /// catalog order within a group. This — not <see cref="Panels"/>, which only happens to be
    /// grouped already — is the order the Ctrl+1…9 / Ctrl+0 / Ctrl+Shift+1…3 chords count in.
    /// The footer panels (<see cref="FooterPanels"/>) are not in it.
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
    /// view-model, not the view, gets told: the view has nothing to pause. A panel that has just
    /// become active is then given the navigation request waiting for it, if any (see
    /// <see cref="ShellNavigation"/>) — after <c>OnActivated</c>, so it is live when it hears.
    /// </summary>
    private void UpdateActivation(FrameworkElement view, PanelViewModelBase viewModel)
    {
        var wasActive = viewModel.IsActive;
        viewModel.SetActive(_windowInteractive && view.IsVisible);

        if (!wasActive && viewModel.IsActive && ByViewType(view.GetType()) is { } descriptor)
        {
            RememberPanel(descriptor);
            DeliverPending(descriptor, viewModel);
        }
    }

    /// <summary>
    /// "Reopen on the last panel" (<c>startup.rememberLastPanel</c>): a panel that comes on screen is written down as the one to open on,
    /// when the operator asked for that. Once per panel visit, and only when it is not already the one remembered: the settings file is
    /// written synchronously, so nothing here runs per scroll or per poll.
    /// </summary>
    private void RememberPanel(PanelDescriptor descriptor)
    {
        var startup = _services.Settings.Current.Startup;
        if (!startup.RememberLastPanel || string.Equals(startup.LastPanelId, descriptor.Id, StringComparison.Ordinal))
        {
            return;
        }

        _ = _services.Settings.Update(settings => settings with { Startup = settings.Startup with { LastPanelId = descriptor.Id } });
    }

    /// <summary>
    /// The panel the window should open on: the one a navigation request is waiting for (a request raised while no window
    /// existed, which built it), else the last one shown when the operator asked to reopen on it (<c>startup.rememberLastPanel</c>;
    /// a panel that no longer exists is ignored), else <see cref="Default"/>.
    /// </summary>
    public PanelDescriptor InitialPanel
    {
        get
        {
            if (_services.Navigation.Pending is { } request && ById(request.PanelId) is { } requested)
            {
                return requested;
            }

            var startup = _services.Settings.Current.Startup;
            return startup.RememberLastPanel && startup.LastPanelId is { Length: > 0 } last && ById(last) is { } remembered
                ? remembered
                : Default;
        }
    }

    /// <summary>
    /// Hands <paramref name="descriptor"/>'s panel the request waiting for it, once: the request is taken (so no later
    /// activation sees it again) whether or not the panel takes a payload, and a payload goes to it only if it implements
    /// <see cref="IAcceptsNavigation"/>. A request raised without a payload only needed the panel shown, which it now is.
    /// </summary>
    private void DeliverPending(PanelDescriptor descriptor, PanelViewModelBase viewModel)
    {
        if (_services.Navigation.TryTake(descriptor.Id) is { Payload: { } payload })
        {
            viewModel.AcceptNavigation(payload);
        }
    }

    private void OnNavigationRequested(object? sender, NavigationRequestedEventArgs e)
    {
        if (ById(e.Request.PanelId) is not { } descriptor)
        {
            Trace.TraceWarning($"navigation: there is no panel '{e.Request.PanelId}'; the request is dropped.");
            _services.Navigation.Discard(e.Request);
            return;
        }

        // Already on screen: nothing will activate it, so it is told now. Not built, or built and off screen (another
        // panel is showing, or the window is in the tray): the request waits in the inbox for its activation.
        if (_viewModels.TryGetValue(descriptor.ViewType, out var viewModel) && viewModel.IsActive)
        {
            DeliverPending(descriptor, viewModel);
        }
    }

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
        FollowAppearance(view);

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

    /// <summary>
    /// Gives a panel the style's UI font, now and after every later change of style. A window's font is what everything in it
    /// inherits (AppearanceService puts it there), but the navigation frame hosts a page in a content presenter that does not
    /// pass the inherited font on, so without this Linear's and TUI's fonts reached the chrome and every separate window and
    /// never a panel's content. The reference is a live one (<c>DcUiFontFamily</c>), and Default clears it: its panels keep
    /// what they inherit with nothing set, the system message font, exactly as before there were styles.
    /// </summary>
    private void FollowAppearance(FrameworkElement view)
    {
        var current = AppearanceService.Current;
        if (!ReferenceEquals(current, _appearance))
        {
            if (_appearance is not null)
            {
                _appearance.Changed -= OnAppearanceChanged;
            }

            _appearance = current;
            if (current is not null)
            {
                current.Changed += OnAppearanceChanged;
            }
        }

        ApplyFont(view);
    }

    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        foreach (var view in _views.Values)
        {
            ApplyFont(view);
        }
    }

    private void ApplyFont(FrameworkElement view)
    {
        if (_appearance is { EffectiveStyle: not AppearanceStyle.Default })
        {
            view.SetResourceReference(TextElement.FontFamilyProperty, AppearanceTokens.UiFontFamily);
        }
        else
        {
            view.ClearValue(TextElement.FontFamilyProperty);
        }
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
