using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App;

/// <summary>
/// The dashboard window: title bar, status strip, banner region, sidebar, and the shell's two
/// overlays (command palette, keyboard shortcuts).
/// <para>
/// Shell-owned. The sidebar is built from <see cref="PanelCatalog"/> at load time, so a
/// panel landing later never touches this file — and the close button minimizes to the
/// tray, because the tray is the app's real lifecycle.
/// </para>
/// <para>
/// <b>Built on demand.</b> Nothing constructs this window at startup: <see cref="DashboardHost"/>
/// builds it the first time the operator (or a second launch) asks to see it, so a <c>--minimized</c>
/// autostart never pays for it. Code that needs the dashboard therefore must not assume it exists —
/// ask the host, or take the owner as a parameter.
/// </para>
/// <para>
/// <b>Keyboard map</b> (one handler, <see cref="OnWindowPreviewKeyDown"/>; the chords themselves
/// live in <see cref="ShellShortcuts"/>): Ctrl+1…9 / Ctrl+0 / Ctrl+Shift+1…5 jump to the panels in
/// sidebar order, Ctrl+, opens Settings (the pinned footer entry), F5 refreshes the current panel (falling back to a gateway poll), Ctrl+K opens the
/// command palette, F1 — or <c>?</c> outside a text box — the shortcuts list, Esc closes whichever
/// overlay is open. Handled at the window's <i>preview</i> stage so a panel's own controls cannot
/// swallow them, but never while the operator is typing: a plain <c>?</c> is only a shortcut when
/// focus is not in a text-entry control.
/// </para>
/// </summary>
public partial class MainWindow : FluentWindow, IDashboardWindow
{
    private readonly PanelCatalog _catalog;
    private readonly TrayIconService _tray;
    private readonly MainWindowViewModel _viewModel;
    private readonly ShellActions _actions;

    /// <summary>The app's own settings; what <see cref="OnClosing"/> reads to decide between "hide to the tray" and "exit".</summary>
    private readonly AppSettingsStore _settings;
    private readonly ConnectorScope _connectorScope;

    /// <summary>The inbox for deep links; set by <see cref="Wire"/>, which subscribes this window to it.</summary>
    private ShellNavigation _navigation = null!;

    /// <summary>
    /// What the app knows about the connected runtime; set by <see cref="Wire"/>, which subscribes this window to its changes. The sidebar shows a
    /// panel that needs more than 0.8.10 has only while the runtime has it (<see cref="ApplyPanelGates()"/>), and the palette's CLI rows follow it too.
    /// </summary>
    private RuntimeService _runtime = null!;

    /// <summary>The shared Docker look the palette's Compose rows follow; set by <see cref="Wire"/>, which subscribes this window to its changes.</summary>
    private LocalStackAvailability _localStack = null!;

    /// <summary>The shared Terraform look the palette's Splunk dashboards rows follow; set by <see cref="Wire"/> the same way.</summary>
    private TerraformAvailability _terraform = null!;
    private readonly CommandPaletteViewModel _paletteViewModel = new();

    /// <summary>The look controls, or null when the app was built without them (only a test host is).</summary>
    private readonly AppearanceService? _appearance = AppearanceService.Current;

    /// <summary><see cref="Environment.TickCount64"/> when the Appearance flyout last closed; see <see cref="OnAppearanceButtonClick"/>.</summary>
    private long _appearancePopupClosedAt;

    /// <summary>
    /// What had keyboard focus before an overlay opened, so closing it puts the operator back where
    /// they were. Held only while an overlay is open.
    /// </summary>
    private IInputElement? _focusBeforeOverlay;

    /// <summary>The group headings, so the collapsed (icons only) sidebar can hide their text.</summary>
    private readonly List<DcNavigationHeader> _navigationHeaders = new();

    /// <summary>The sidebar entry per panel id, so a keyboard or palette jump can scroll it into view.</summary>
    private readonly Dictionary<string, DcNavigationItem> _navigationItems = new(StringComparer.Ordinal);

    /// <summary>The count on the Alerts entry and the caution mark on the Overview entry; null until <see cref="BuildNavigation"/> has made the entries.</summary>
    private SidebarBadge? _alertsBadge;

    private SidebarBadge? _overviewBadge;

    /// <summary>
    /// One line per panel whose initialization failed, newest fault per panel wins; what
    /// <c>PanelFaultBar</c> shows. Cleared when the operator dismisses the bar. UI thread only.
    /// </summary>
    private readonly List<(string PanelId, string Line)> _panelFaults = new();
    private bool _allowClose;
    private bool _minimizeHintShown;

    public MainWindow(AppServices services, PanelCatalog catalog, TrayIconService tray)
    {
        ArgumentNullException.ThrowIfNull(services);

        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _tray = tray ?? throw new ArgumentNullException(nameof(tray));
        _settings = services.Settings;
        _connectorScope = services.ConnectorScope;

        InitializeComponent();

        _viewModel = new MainWindowViewModel(services);
        DataContext = _viewModel;

        // The strip's chips have a view-model of their own, which the shell one owns.
        StatusStrip.DataContext = _viewModel.Strip;

        // Shell actions and the two overlays. The overlays' DataContexts are set here, not by
        // binding, because they are not the window's view-model; their hosts (in the XAML) bind
        // their visibility to the shell view-model's flags.
        _actions = new ShellActions(
            services,
            _catalog,
            _tray,
            () => IsVisible && WindowState != WindowState.Minimized ? this : null);

        try
        {
            Wire(services);
        }
        catch
        {
            AbandonPartiallyBuilt(services);
            throw;
        }
    }

    /// <summary>
    /// Test seam: runs at the very end of construction, after every subscription is in place. A test throws from it to
    /// prove that a construction that fails part-way hands them all back (see <see cref="AbandonPartiallyBuilt"/>).
    /// </summary>
    internal static Action? ConstructionProbe { get; set; }

    /// <summary>
    /// The rest of construction: hooks the window, its overlays and the view-model up to each other and to the app-lifetime
    /// objects (the monitor, the catalog, the appearance service). A separate method so the constructor can undo the
    /// subscriptions if any step throws.
    /// </summary>
    private void Wire(AppServices services)
    {
        _paletteViewModel.CommandChosen += OnPaletteCommandChosen;
        _paletteViewModel.CloseRequested += (_, _) => ClosePalette();

        // The palette remembers the last commands it ran in settings.json and lists them first while its search is empty.
        _paletteViewModel.RecentsStore = services.Settings;
        Palette.DataContext = _paletteViewModel;

        Shortcuts.DataContext = ShortcutCatalog.Build(_catalog);
        Shortcuts.CloseRequested += (_, _) => CloseShortcuts();

        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewTextInput += OnWindowPreviewTextInput;

        // A screen reader should hear the gateway state change, not only find it when it looks.
        _viewModel.PropertyChanged += OnShellPropertyChanged;

        // Taskbar icon mirrors the tray shield's gateway-derived state, state badge and all (paused, stopped, warning, a CRITICAL
        // in the last alert poll), so alt-tab tells the same story as the notification area. The tray alone
        // adds the unacknowledged count and the scan glyph: they are not in the snapshot this follows.
        // A multi-size icon, so the title bar, the taskbar and
        // alt-tab each get the frame drawn for their size; cached per state because StateChanged
        // also fires for changes that leave the shield alone (a new detail line, a connector appearing).
        // Ahead of the appearance hookup below: rendering the icon is the step here most likely to throw, and the
        // flyout that hookup binds cannot be unbound again if it has to be abandoned.
        ApplyShieldIcon(ShieldIconFactory.StateFor(services.Monitor.Current));
        services.Monitor.StateChanged += OnMonitorStateChanged;

        // The look (style, light/dark, Mica or solid, dark title bar) is AppearanceService's: it re-skins this window
        // whenever it changes and follows Windows in mode System. WPF-UI's SystemThemeWatcher is deliberately not used
        // here any more - on every OS theme message it forces the system theme, over an explicit choice.
        _appearance?.Attach(this);
        BindAppearanceControls();

        // Panels only run while someone can see them. Hiding to the tray is caught by
        // IsVisibleChanged; a minimized window still reports itself visible, so the window
        // state is watched as well. Both feed the one flag the catalog understands.
        IsVisibleChanged += (_, _) => PublishInteractivity();
        StateChanged += (_, _) => PublishInteractivity();

        // A panel whose InitializeAsync throws would otherwise sit on "Loading…" forever with
        // nothing saying why. Subscribed before the sidebar exists (OnLoaded builds it), so no
        // fault can be missed. Dismissing the bar forgets the faults it listed: the next one
        // shows on its own instead of resurrecting ones the operator already saw.
        _catalog.PanelFaulted += OnPanelFaulted;
        DependencyPropertyDescriptor
            .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            ?.AddValueChanged(PanelFaultBar, OnPanelFaultBarOpenChanged);

        // A deep link (ShellNavigation): select the panel it names. The catalog hands it its payload when it comes up.
        services.Navigation.Requested += OnNavigationRequested;
        services.Navigation.PaletteRequested += OnPaletteRequested;
        _navigation = services.Navigation;

        // The runtime is probed in the background. Its first answer and any change reach the sidebar (a panel that needs more than 0.8.10 has
        // appears when the runtime has it and leaves again if it stops having it) and an open palette (its CLI rows are the runtime's TUI registry).
        services.Runtime.Changed += OnRuntimeChanged;
        _runtime = services.Runtime;

        // The rows that run Docker Compose (setup local-observability ...) are enabled by the shared Docker look, and the ones that run Terraform
        // (setup splunk dashboards ...) by the shared Terraform look: an open palette follows the answers of both.
        services.LocalStack.Changed += OnProbeAnswerChanged;
        _localStack = services.LocalStack;
        services.Terraform.Changed += OnProbeAnswerChanged;
        _terraform = services.Terraform;

        // What Settings asks of the tray (its "Reset seen-alert history" button): the same call the command palette's entry makes.
        _catalog.Hooks.ResetSeenAlertHistory = _tray.ResetSeenAlertHistoryAsync;

        // What the config editor's post-save bar asks of the tray: the reviewed gateway restart, shown over the editor.
        Views.ConfigEditor.ConfigEditorWindow.GatewayRestart = owner => _tray.RunGatewayActionAsync(GatewayAction.Restart, owner);

        // The close button's name says what it does, and what it does is a setting (startup.closeToTray).
        _settings.Changed += OnSettingsChanged;

        Loaded += OnLoaded;

        ConstructionProbe?.Invoke();
    }

    /// <summary>
    /// A constructor that throws after it has subscribed to app-lifetime objects leaves those subscriptions behind, and with
    /// them the half-built window and its view-model: the monitor's <c>StateChanged</c> keeps calling into both.
    /// <see cref="DashboardHost"/> retries on the next request, so every retry would leak another pair. This hands back
    /// what <see cref="Wire"/> and the view-model took, best effort and never masking the exception on its way up, and closes the window so
    /// it leaves <c>Application.Windows</c> (nobody saw it, so no "still running in the tray" hint).
    /// </summary>
    private void AbandonPartiallyBuilt(AppServices services)
    {
        try
        {
            services.Monitor.StateChanged -= OnMonitorStateChanged;
            services.Navigation.Requested -= OnNavigationRequested;
            _settings.Changed -= OnSettingsChanged;
            services.Navigation.PaletteRequested -= OnPaletteRequested;
            services.Runtime.Changed -= OnRuntimeChanged;
            services.LocalStack.Changed -= OnProbeAnswerChanged;
            services.Terraform.Changed -= OnProbeAnswerChanged;
            _catalog.PanelFaulted -= OnPanelFaulted;
            if (_appearance is not null)
            {
                _appearance.Changed -= OnAppearanceChangedUpdateThemeToggle;
            }

            DependencyPropertyDescriptor
                .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
                ?.RemoveValueChanged(PanelFaultBar, OnPanelFaultBarOpenChanged);

            _viewModel.Dispose();

            _allowClose = true;
            Close();
        }
#pragma warning disable CA1031 // Cleanup after a failed construction must not replace the failure it is cleaning up after.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"MainWindow: cleanup after a failed construction also failed: {ex.Message}");
        }
    }

    private void OnMonitorStateChanged(object? sender, GatewaySnapshotEventArgs e) =>
        ApplyShieldIcon(ShieldIconFactory.StateFor(e.Snapshot));

    /// <summary>
    /// Adds the faulted panel to the banner. Normally already on the UI thread (see
    /// <see cref="PanelCatalog.PanelFaulted"/>); marshals anyway so a future caller that is
    /// not cannot turn a panel fault into a cross-thread exception.
    /// </summary>
    private void OnPanelFaulted(object? sender, PanelFaultEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnPanelFaulted(sender, e));
            return;
        }

        var reason = e.Exception.Message.ReplaceLineEndings(" ").Trim();
        if (reason.Length > 300)
        {
            reason = string.Concat(reason.AsSpan(0, 300), "…");
        }

        var line = $"{e.Panel.Title} — {e.Exception.GetType().Name}: {reason}";
        var existing = _panelFaults.FindIndex(fault => string.Equals(fault.PanelId, e.Panel.Id, StringComparison.Ordinal));
        if (existing >= 0)
        {
            _panelFaults[existing] = (e.Panel.Id, line);
        }
        else
        {
            _panelFaults.Add((e.Panel.Id, line));
        }

        PanelFaultBar.Message =
            string.Join(Environment.NewLine, _panelFaults.Select(fault => fault.Line)) +
            Environment.NewLine +
            "The affected panel may stay empty or on “Loading…”; the rest of the dashboard is unaffected.";
        PanelFaultBar.IsOpen = true;
    }

    private void OnPanelFaultBarOpenChanged(object? sender, EventArgs e)
    {
        if (!PanelFaultBar.IsOpen)
        {
            _panelFaults.Clear();
        }
    }

    /// <summary>
    /// Whether the operator can actually see this window: shown, and not minimized to the
    /// taskbar. The panels' pause/resume hangs off this — see the activation contract on
    /// <see cref="PanelViewModelBase"/>. The tray, its toasts and the shell status strip do
    /// not, and keep running.
    /// </summary>
    private void PublishInteractivity()
    {
        var interactive = IsVisible && WindowState != WindowState.Minimized;
        _catalog.SetWindowInteractive(interactive);

        // The status strip looks for a monitor that has stopped reporting (its Stale chip) only while someone can see it.
        _viewModel.Strip.SetActive(interactive);
    }

    private static readonly Dictionary<ShieldState, System.Windows.Media.ImageSource> ShieldImageCache = new();
    private ShieldState? _currentIconState;

    private void ApplyShieldIcon(ShieldState state)
    {
        if (_currentIconState == state)
        {
            return;
        }

        _currentIconState = state;
        if (!ShieldImageCache.TryGetValue(state, out var image))
        {
            image = ShieldIconFactory.CreateWindowIcon(state);
            ShieldImageCache[state] = image;
        }

        Icon = image;
    }

    /// <summary>Brings the window back from the tray. Used by the tray and by a second launch.</summary>
    public void ShowAndActivate()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        _ = Focus();
    }

    /// <summary>Lets the next close actually close. Only the tray's Exit item calls this.</summary>
    public void AllowClose()
    {
        _allowClose = true;
    }

    /// <summary>
    /// Raised when the close button is pressed while <c>startup.closeToTray</c> is off: the operator wants the app to end, and ending
    /// goes through the one Exit path (the tray's), with its upgrade-running and unsaved-config-editor questions. The window has
    /// already refused the close by then; it really closes only if that path says yes (<see cref="AllowClose"/>).
    /// </summary>
    internal event EventHandler? ExitRequested;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // The window never closes by itself: either it hides (closing the dashboard is not quitting: the tray icon is the app),
            // or, when the operator turned "Closing the window keeps DefenseClaw in the tray" off, the exit is asked for. A window that
            // WPF is closing because the session ends (shutdown has begun) just hides: there is nobody to ask.
            e.Cancel = true;

            var closeToTray = _settings.Current.Startup.CloseToTray;
            if (!closeToTray && !Dispatcher.HasShutdownStarted)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            Hide();

            if (closeToTray && !_minimizeHintShown)
            {
                _minimizeHintShown = true;
                _tray.Notify(
                    "DefenseClaw is still running",
                    "The dashboard closed to the tray and keeps watching the gateway. " +
                    "Right-click the shield and pick Exit to quit for real.");
            }

            return;
        }

        _catalog.PanelFaulted -= OnPanelFaulted;
        _navigation.Requested -= OnNavigationRequested;
        _settings.Changed -= OnSettingsChanged;
        _navigation.PaletteRequested -= OnPaletteRequested;
        _runtime.Changed -= OnRuntimeChanged;
        _localStack.Changed -= OnProbeAnswerChanged;
        _terraform.Changed -= OnProbeAnswerChanged;
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    /// <summary>The close button follows <c>startup.closeToTray</c>; see <see cref="UpdateCloseButtonName"/>. May arrive on any thread.</summary>
    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        if (!e.Affects(AppSettingsSections.Startup))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(UpdateCloseButtonName));
            return;
        }

        UpdateCloseButtonName();
    }

    /// <summary>
    /// Names the title bar's close button for what it does now: "Close to tray" (the window hides and the app keeps watching) or
    /// "Exit DefenseClaw" (it asks to quit), so a screen reader never announces one and does the other.
    /// </summary>
    private void UpdateCloseButtonName()
    {
        if (FindByAutomationId(this, "TitleBarCloseButton") is { } button)
        {
            AutomationProperties.SetName(button, CloseButtonName(_settings.Current.Startup.CloseToTray));
        }
    }

    /// <summary>What the close button is called, for the two settings of <c>startup.closeToTray</c>.</summary>
    internal static string CloseButtonName(bool closeToTray) => closeToTray ? "Close to tray" : "Exit DefenseClaw";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        BuildNavigation();

        // The title-bar buttons and the pane toggle are template parts with no accessible name;
        // name them once the templates exist. Loaded priority: after this pass has laid out.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            NameChromeButtons();
            TightenNavigationPane();
        }));
    }

    /// <summary>
    /// Materializes the sidebar from the catalog: one group heading per group, one item per panel,
    /// in sidebar order. Headings and items are the accessible <see cref="DcNavigationHeader"/> /
    /// <see cref="DcNavigationItem"/> so a screen reader announces "Monitor" and "Overview" (with its
    /// Ctrl+1 accelerator) instead of a type name, and UI Automation can invoke and select them.
    /// </summary>
    private void BuildNavigation()
    {
        foreach (var group in PanelCatalog.Groups)
        {
            // Tighter than WPF-UI's defaults (entries 34 px high instead of 40, headings with 5 px above their
            // text instead of 12) so 13 panels + 4 headings need ~555 px instead of ~760. The header template
            // applies its Margin twice (once as the element's margin, once on its inner grid), so a left margin
            // of 7 puts the heading text 14 px in: on the entries' icon edge. The pane still scrolls (mouse
            // wheel, keyboard focus, and a scroll thumb that stays visible — see TightenNavigationPane) when
            // the window is short.
            var header = new DcNavigationHeader { Text = group, Margin = new Thickness(7, 5, 0, 0) };
            AutomationProperties.SetName(header, group);
            AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
            _navigationHeaders.Add(header);
            _ = RootNavigation.MenuItems.Add(header);

            foreach (var panel in _catalog.InGroup(group))
            {
                // The chord is the panel's place in the chord order, not in the sidebar (see PanelCatalog.ChordOrder).
                var chord = _catalog.ChordTextOf(panel);
                var item = CreateNavigationItem(panel, chord, $"Opens the {panel.Title} panel in the {group} group.");

                // The count on Alerts and the caution mark on Overview (CUST-201); the view-model feeds them (UpdateNavigationBadges).
                if (string.Equals(panel.Id, "alerts", StringComparison.Ordinal))
                {
                    _alertsBadge = new SidebarBadge(item, panel.Title, item.ToolTip as string ?? panel.Title, InfoBadgeSeverity.Critical);
                }
                else if (string.Equals(panel.Id, "overview", StringComparison.Ordinal))
                {
                    _overviewBadge = new SidebarBadge(item, panel.Title, item.ToolTip as string ?? panel.Title, InfoBadgeSeverity.Caution);
                }

                _ = RootNavigation.MenuItems.Add(item);
            }
        }

        // Settings, pinned below the groups (WPF-UI's footer: it stays put while the groups scroll in a short window). Ctrl+, reaches it.
        foreach (var panel in _catalog.FooterPanels)
        {
            var chord = string.Equals(panel.Id, "settings", StringComparison.Ordinal) ? ShellShortcuts.SettingsText : null;
            _ = RootNavigation.FooterMenuItems.Add(CreateNavigationItem(
                panel,
                chord,
                $"Opens {panel.Title}: monitoring, notifications, startup, connection and updates."));
        }

        ApplyPanelGates();
        UpdateNavigationBadges();

        // Collapsed, the sidebar is a 40 px icon strip and a heading's text would be cut off mid-word
        // ("Gove", "Disc"). The heading keeps its height (so the groups stay visibly apart) and its
        // accessible name; only the text is hidden.
        RootNavigation.PaneOpened += (_, _) => ShowGroupHeadings(true);
        RootNavigation.PaneClosed += (_, _) => ShowGroupHeadings(false);
        ShowGroupHeadings(RootNavigation.IsPaneOpen);

        // The panel a navigation request raised before this window existed is waiting for, else the default one.
        RootNavigation.SetPageProviderService(_catalog);
        _ = RootNavigation.Navigate(_catalog.InitialPanel.ViewType);
    }

    /// <summary>
    /// Shows the sidebar entry of every panel the connected runtime offers and hides the rest (<see cref="PanelCatalog.IsOffered"/>): on 0.8.10
    /// the Runtime panel has no entry, and it gets one when the probe says the runtime has the planes. Before the first probe answers every panel
    /// that needs a newer runtime is hidden, never flashed. The window's own chords and the palette follow the same test.
    /// </summary>
    private void ApplyPanelGates() => ApplyPanelGates(_catalog, _navigationItems);

    /// <summary>The same, over any set of sidebar entries by panel id (the window's own, or a test's).</summary>
    internal static void ApplyPanelGates(PanelCatalog catalog, IReadOnlyDictionary<string, DcNavigationItem> items)
    {
        foreach (var panel in catalog.Panels)
        {
            if (items.TryGetValue(panel.Id, out var item))
            {
                item.Visibility = catalog.IsOffered(panel) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>One sidebar entry: the panel's title and glyph, its accessible name, help text and accelerator, and its place in <see cref="_navigationItems"/>.</summary>
    private DcNavigationItem CreateNavigationItem(PanelDescriptor panel, string? chord, string helpText)
    {
        var item = new DcNavigationItem
        {
            Content = panel.Title,
            Icon = new SymbolIcon { Symbol = panel.Icon },
            TargetPageType = panel.ViewType,
            ToolTip = chord is null ? panel.Title : $"{panel.Title} ({chord})",
            Height = 34,
            MinHeight = 34,
            Margin = new Thickness(0, 0, 0, 1),
        };
        _navigationItems[panel.Id] = item;

        AutomationProperties.SetName(item, panel.Title);
        AutomationProperties.SetHelpText(item, helpText);
        if (chord is not null)
        {
            AutomationProperties.SetAcceleratorKey(item, chord);
        }

        return item;
    }

    /// <summary>
    /// A navigation request (see <see cref="ShellNavigation"/>) names a panel: select it, exactly as clicking its sidebar entry
    /// does. Before the sidebar exists (<see cref="OnLoaded"/> has not run) there is nothing to select; the request stays
    /// pending and <see cref="BuildNavigation"/> opens on its panel.
    /// </summary>
    private void OnNavigationRequested(object? sender, NavigationRequestedEventArgs e)
    {
        if (IsLoaded && _catalog.ById(e.Request.PanelId) is { } panel)
        {
            NavigateTo(panel);
        }
    }

    private void ShowGroupHeadings(bool paneOpen)
    {
        foreach (var header in _navigationHeaders)
        {
            header.Opacity = paneOpen ? 1 : 0;
        }
    }

    /// <summary>
    /// Trims the sidebar's chrome and keeps its scroll thumb visible while there is something to scroll to.
    /// <para>
    /// WPF-UI's pane spends ~72 DIPs above the first entry (a 5 px gap under the toggle, a 6 px slot for an
    /// auto-suggest box this app does not use, 4 + 4 px around each separator); in a window that already
    /// cannot show 13 entries and 4 headings at its 620 DIP minimum height, every DIP counts. The names are
    /// WPF-UI template parts, looked up like the title-bar buttons above; a part that is missing simply
    /// keeps its default.
    /// </para>
    /// <para>
    /// WPF-UI's <see cref="DynamicScrollViewer"/> hides its thumb until the mouse moves or the wheel turns,
    /// so a clipped sidebar looked like a sidebar that ends. While the content overflows, the thumb stays up
    /// (an enormous timeout, and the "scrolling" flags held on); when the window grows enough to show
    /// everything, they are released.
    /// </para>
    /// </summary>
    private void TightenNavigationPane()
    {
        SetMarginByName("PART_ToggleButton", new Thickness(0));
        SetMarginByName("AutoSuggestBoxContentPresenter", new Thickness(0));
        SetMarginByName("PART_TopSeparator", new Thickness(0, 2, 0, 2));
        SetMarginByName("PART_FooterSeparator", new Thickness(0, 2, 0, 2));

        if (FindByAutomationId(RootNavigation, "PART_ScrollViewer") is not DynamicScrollViewer viewer)
        {
            return;
        }

        var bar = FindByAutomationId(viewer, "PART_VerticalScrollBar") as DynamicScrollBar;
        viewer.Timeout = int.MaxValue;
        if (bar is not null)
        {
            bar.Timeout = int.MaxValue;
        }

        void HoldThumbWhileOverflowing()
        {
            var overflowing = viewer.ScrollableHeight > 0.5;
            viewer.IsScrollingVertically = overflowing;
            if (bar is not null)
            {
                bar.IsScrolling = overflowing;
            }
        }

        viewer.ScrollChanged += (_, _) => HoldThumbWhileOverflowing();

        // The bar lets go of its "scrolling" state itself when the mouse leaves it; take it back.
        if (bar is not null)
        {
            bar.MouseLeave += (_, _) =>
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(HoldThumbWhileOverflowing));
        }

        HoldThumbWhileOverflowing();
    }

    private void SetMarginByName(string partName, Thickness margin)
    {
        if (FindByAutomationId(RootNavigation, partName) is FrameworkElement element)
        {
            element.Margin = margin;
        }
    }

    /// <summary>
    /// Selects <paramref name="panel"/> in the sidebar, exactly as clicking its entry does, and
    /// scrolls its entry into view: a Ctrl+N or palette jump must land somewhere visible even when
    /// the sidebar is scrolled (or the window is short enough that it scrolls at all).
    /// </summary>
    private void NavigateTo(PanelDescriptor panel)
    {
        // A panel the connected runtime does not offer has no sidebar entry, so nothing may reach it by chord, palette or deep link either.
        if (!_catalog.IsOffered(panel))
        {
            return;
        }

        _ = RootNavigation.Navigate(panel.ViewType);

        if (_navigationItems.TryGetValue(panel.Id, out var item))
        {
            item.BringIntoView();
        }
    }

    // ------------------------------------------------------------------ keyboard

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+key arrives as Key.System with the real key in SystemKey; Alt chords belong to the
        // system menu and to screen readers, so the shell claims none of them.
        if (e.Key == Key.System)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;

        if (_viewModel.IsPaletteOpen || _viewModel.IsShortcutsOpen)
        {
            HandleKeyWhileOverlayOpen(e, modifiers);
            return;
        }

        if (e.Key == Key.K && modifiers == ModifierKeys.Control)
        {
            OpenPalette();
            e.Handled = true;
        }
        else if (e.Key == Key.F1 && modifiers == ModifierKeys.None)
        {
            OpenShortcuts();
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && modifiers == ModifierKeys.None)
        {
            RefreshFromKeyboard();
            e.Handled = true;
        }
        else if (ShellShortcuts.ActionFor(e.Key, modifiers) is { } chordAction)
        {
            RunChordAction(chordAction);
            e.Handled = true;
        }
        else if (ShellShortcuts.IsToggleThemeChord(e.Key, modifiers) && _appearance is not null)
        {
            _appearance.ToggleLightDark();
            e.Handled = true;
        }
        else if (ShellShortcuts.IsCycleConnectorChord(e.Key, modifiers))
        {
            // With one connector (or none) there is nothing to step between: the chord does nothing, and says nothing.
            _ = _connectorScope.Cycle();
            e.Handled = true;
        }
        else if (ShellShortcuts.IsSettingsChord(e.Key, modifiers) && _catalog.ById("settings") is { } settings)
        {
            NavigateTo(settings);
            e.Handled = true;
        }
        else if (ShellShortcuts.PanelIndexFor(e.Key, modifiers) is { } index &&
                 _catalog.PanelForChord(index) is { } target)
        {
            NavigateTo(target);
            e.Handled = true;
        }
    }

    /// <summary>
    /// While an overlay is open the shell claims only the keys that close or swap it; everything
    /// else (typing, arrows, Enter) belongs to the overlay, and panel navigation is off so a
    /// stray Ctrl+3 cannot change the page behind the veil.
    /// </summary>
    private void HandleKeyWhileOverlayOpen(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (e.Key == Key.Escape)
        {
            CloseOverlays();
            e.Handled = true;
        }
        else if (e.Key == Key.K && modifiers == ModifierKeys.Control)
        {
            // Ctrl+K toggles: it closes the palette, and from the shortcuts list it opens the palette.
            if (_viewModel.IsPaletteOpen)
            {
                ClosePalette();
            }
            else
            {
                OpenPalette();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.F1 && modifiers == ModifierKeys.None)
        {
            if (_viewModel.IsShortcutsOpen)
            {
                CloseShortcuts();
            }
            else
            {
                OpenShortcuts();
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// <c>?</c> opens the shortcuts list unless it would be typed text. Keyed off text input rather
    /// than a key code so it works on every keyboard layout (on some, <c>?</c> is not Shift+/).
    /// </summary>
    private void OnWindowPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "?" &&
            !_viewModel.IsPaletteOpen &&
            !_viewModel.IsShortcutsOpen &&
            !IsTextEntryFocused())
        {
            OpenShortcuts();
            e.Handled = true;
        }
    }

    private static bool IsTextEntryFocused() =>
        Keyboard.FocusedElement is TextBoxBase
            or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.ComboBox { IsEditable: true };

    /// <summary>The Mac's Monitor / Commands chords (Ctrl+R, Ctrl+Shift+H / A / D / Y / E): each does what its palette row does.</summary>
    private void RunChordAction(ShellChordAction action)
    {
        switch (action)
        {
            case ShellChordAction.Refresh:
                RefreshFromKeyboard();
                break;
            case ShellChordAction.HealthCheck:
                _actions.RunHealthCheck();
                break;
            case ShellChordAction.ScanAi:
                _actions.ScanAiComponents();
                break;
            case ShellChordAction.Diagnose:
                _ = _actions.DiagnoseInBackgroundAsync();
                break;
            case ShellChordAction.CopyOutput:
                _ = _actions.CopyLastOutput();
                break;
            case ShellChordAction.ExportOutput:
                _ = _actions.ExportLastOutput();
                break;
        }
    }

    /// <summary>F5: the current panel's refresh, or a gateway poll when the panel has none.</summary>
    private void RefreshFromKeyboard()
    {
        if (!_actions.TryRefreshCurrentPanel())
        {
            _actions.RefreshGatewayStatus();
        }
    }

    // ------------------------------------------------------------------ overlays

    private void OnPaletteButtonClick(object sender, RoutedEventArgs e) => OpenPalette();

    /// <summary>A panel asked for the palette (the Overview's Diagnostics menu); same as the status strip's Search button.</summary>
    private void OnPaletteRequested(object? sender, EventArgs e) => OpenPalette();

    private void OnShortcutsButtonClick(object sender, RoutedEventArgs e) => OpenShortcuts();

    private void OpenPalette()
    {
        if (_viewModel.IsPaletteOpen)
        {
            Palette.FocusSearch();
            return;
        }

        CloseShortcuts(restoreFocus: false);
        RememberFocus();

        // Built fresh on every open: toggle titles, the gateway controls' availability and the CLI rows (the connected runtime's TUI registry) are read now.
        var (hiddenNote, hiddenDetail) = _actions.HiddenCommands;
        _paletteViewModel.Load(BuildPaletteCommands(), hiddenNote, hiddenDetail);
        _viewModel.IsPaletteOpen = true;
        Palette.FocusSearch();

        // The rows that run Docker Compose follow the shared Docker look, and the ones that run Terraform the shared Terraform look: make sure
        // neither is stale (nothing happens while it is fresh). A different answer arrives through LocalStack.Changed / Terraform.Changed and
        // swaps the rows.
        _ = _actions.CheckLocalStack();
        _ = _actions.CheckTerraform();
    }

    private IReadOnlyList<ShellCommand> BuildPaletteCommands() =>
        ShellCommandRegistry.Build(_catalog, _actions, NavigateTo, OpenShortcuts, _appearance, _actions.CliCommands, _connectorScope);

    /// <summary>
    /// The runtime answered (the first probe) or changed its answer (an upgrade, a downgrade). The sidebar follows it (a panel that needs more
    /// than 0.8.10 has appears or leaves), and so do the CLI rows, which come from the runtime's TUI registry: an open palette swaps them in
    /// without touching what was typed. A closed one reads them when it opens.
    /// </summary>
    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnRuntimeChanged(sender, e)));
            return;
        }

        ApplyPanelGates();
        ReloadOpenPalette();
    }

    /// <summary>
    /// A shared look changed its answer - Docker's: the rows that run Docker Compose (<c>setup local-observability up</c>, ...) are enabled or
    /// greyed out by it; Terraform's: so are the Splunk dashboards' (<c>setup splunk dashboards plan</c>, ...) - so an open palette swaps them in
    /// without touching what was typed. Raised on the probe's thread.
    /// </summary>
    private void OnProbeAnswerChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => OnProbeAnswerChanged(sender, e)));
            return;
        }

        ReloadOpenPalette();
    }

    /// <summary>Builds the palette's rows again if it is open (a closed one reads them when it opens).</summary>
    private void ReloadOpenPalette()
    {
        if (_viewModel.IsPaletteOpen)
        {
            var (hiddenNote, hiddenDetail) = _actions.HiddenCommands;
            _paletteViewModel.Reload(BuildPaletteCommands(), hiddenNote, hiddenDetail);
        }
    }

    private void ClosePalette(bool restoreFocus = true)
    {
        if (!_viewModel.IsPaletteOpen)
        {
            return;
        }

        _viewModel.IsPaletteOpen = false;
        if (restoreFocus)
        {
            RestoreFocus();
        }
    }

    private void OpenShortcuts()
    {
        if (_viewModel.IsShortcutsOpen)
        {
            Shortcuts.FocusClose();
            return;
        }

        ClosePalette(restoreFocus: false);
        RememberFocus();

        // Built when it opens, not once: which panels have a chord depends on what the connected runtime offers, which can change while the window lives.
        Shortcuts.DataContext = ShortcutCatalog.Build(_catalog);
        _viewModel.IsShortcutsOpen = true;
        Shortcuts.FocusClose();
    }

    private void CloseShortcuts(bool restoreFocus = true)
    {
        if (!_viewModel.IsShortcutsOpen)
        {
            return;
        }

        _viewModel.IsShortcutsOpen = false;
        if (restoreFocus)
        {
            RestoreFocus();
        }
    }

    private void CloseOverlays()
    {
        ClosePalette();
        CloseShortcuts();
    }

    /// <summary>Remembers the focused element unless an overlay already did (palette to shortcuts keeps the original).</summary>
    private void RememberFocus() => _focusBeforeOverlay ??= Keyboard.FocusedElement;

    private void RestoreFocus()
    {
        var target = _focusBeforeOverlay;
        _focusBeforeOverlay = null;

        if (target is UIElement { IsVisible: true, IsEnabled: true } element)
        {
            _ = element.Focus();
        }
    }

    /// <summary>
    /// Runs the palette's choice after the overlay has closed and focus is back, so a command that
    /// opens a window or a review dialog does not fight the overlay for focus. A command that throws
    /// is reported, never allowed to reach the dispatcher's last-resort handler (which would hide
    /// the dashboard over a failed menu entry).
    /// </summary>
    private void OnPaletteCommandChosen(object? sender, ShellCommand command)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            try
            {
                command.Run();
            }
#pragma warning disable CA1031 // A failed palette command must not take the dashboard down.
            catch (Exception ex)
            {
                Trace.TraceError($"Palette command '{command.Id}' failed: {ex}");
                _tray.Notify(command.Title, $"That did not work: {ex.Message}", H.NotifyIcon.Core.NotificationIcon.Warning);
            }
#pragma warning restore CA1031
        }));
    }

    // ------------------------------------------------------------------ appearance

    /// <summary>
    /// Wires the title-bar toggle and the flyout to the appearance service, or hides both when there is none. The toggle's
    /// icon, tooltip and accessible name follow the service, so they always say where the next click goes.
    /// </summary>
    private void BindAppearanceControls()
    {
        if (_appearance is null)
        {
            AppearanceButton.Visibility = Visibility.Collapsed;
            ThemeToggleButton.Visibility = Visibility.Collapsed;
            return;
        }

        AppearanceFlyoutControl.Bind(_appearance);
        AppearanceFlyoutControl.CloseRequested += (_, _) => CloseAppearanceFlyout();
        AppearancePopup.CustomPopupPlacementCallback = PlaceAppearanceFlyout;

        _appearance.Changed += OnAppearanceChangedUpdateThemeToggle;
        UpdateThemeToggle();
    }

    // Named (not a lambda) so a half-built window can unsubscribe it. (The strip needs nothing here: it measures its chips in the font on screen.)
    private void OnAppearanceChangedUpdateThemeToggle(object? sender, EventArgs e) => UpdateThemeToggle();

    /// <summary>The toggle shows where a click goes: a sun while dark (to light), a moon while light (to dark).</summary>
    private void UpdateThemeToggle()
    {
        if (_appearance is null)
        {
            return;
        }

        var toLight = _appearance.IsDark;
        ThemeToggleIcon.Symbol = toLight ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;

        var verb = toLight ? "Switch to light mode" : "Switch to dark mode";
        ThemeToggleButton.ToolTip = $"{verb} ({ShellShortcuts.ToggleThemeText})";
        AutomationProperties.SetName(ThemeToggleButton, verb);
        AutomationProperties.SetHelpText(
            ThemeToggleButton,
            "Changes between light and dark. If the app was following Windows, it stops following until you choose System again in Appearance.");
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e) => _appearance?.ToggleLightDark();

    private void OnAppearanceButtonClick(object sender, RoutedEventArgs e)
    {
        // A light-dismiss popup closes on the press that lands on its own button, so by the time the click arrives it
        // is already shut - and would open again, making the button impossible to close with. A click that closely
        // follows a close is that press.
        if (Environment.TickCount64 - _appearancePopupClosedAt < 300)
        {
            return;
        }

        if (AppearancePopup.IsOpen)
        {
            CloseAppearanceFlyout();
            return;
        }

        AppearanceFlyoutControl.Refresh();
        AppearancePopup.IsOpen = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(AppearanceFlyoutControl.FocusSelected));
    }

    /// <summary>Closes the flyout and gives focus back to the button that opened it.</summary>
    private void CloseAppearanceFlyout()
    {
        AppearancePopup.IsOpen = false;
        _ = AppearanceButton.Focus();
    }

    private void OnAppearancePopupClosed(object? sender, EventArgs e) => _appearancePopupClosedAt = Environment.TickCount64;

    /// <summary>Right edges of the flyout and its button line up, and it opens just under the title bar.</summary>
    private static CustomPopupPlacement[] PlaceAppearanceFlyout(Size popupSize, Size targetSize, Point offset) =>
        new[] { new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.None) };

    // ------------------------------------------------------------------ accessibility

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.StateLabel) or nameof(MainWindowViewModel.StateAutomationName))
        {
            // The state pill is a polite live region; WPF does not raise the event on its own when
            // bound text changes. No peer exists unless a UI Automation client is attached.
            UIElementAutomationPeer.FromElement(StateText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.AlertBadgeText)
                 or nameof(MainWindowViewModel.AlertBadgeDescription)
                 or nameof(MainWindowViewModel.OverviewBadgeDescription))
        {
            UpdateNavigationBadges();
        }
    }

    /// <summary>
    /// Shows the sidebar badges the view-model describes. The view-model raises on the UI thread, but the counts service may
    /// raise from a pool thread when nothing is hosting a dispatcher, so this marshals like <see cref="OnPanelFaulted"/>.
    /// </summary>
    private void UpdateNavigationBadges()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpdateNavigationBadges());
            return;
        }

        _alertsBadge?.Set(_viewModel.AlertBadgeText, _viewModel.AlertBadgeDescription);
        _overviewBadge?.Set("!", _viewModel.OverviewBadgeDescription);
    }

    /// <summary>
    /// Gives the template-supplied icon-only buttons their names (title-bar minimize / maximize /
    /// close and the sidebar's pane toggle). WPF-UI ships them with an automation id but no name, so
    /// a screen reader announced four unlabeled buttons.
    /// </summary>
    private void NameChromeButtons()
    {
        NameByAutomationId("TitleBarMinimizeButton", "Minimize");
        NameByAutomationId("TitleBarMaximizeButton", "Maximize or restore");
        NameByAutomationId("TitleBarCloseButton", CloseButtonName(_settings.Current.Startup.CloseToTray));
        NameByAutomationId("NavigationToggleButton", "Toggle sidebar");
    }

    private void NameByAutomationId(string automationId, string name)
    {
        if (FindByAutomationId(this, automationId) is { } element &&
            string.IsNullOrEmpty(AutomationProperties.GetName(element)))
        {
            AutomationProperties.SetName(element, name);
        }
    }

    private static DependencyObject? FindByAutomationId(DependencyObject parent, string automationId)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (string.Equals(AutomationProperties.GetAutomationId(child), automationId, StringComparison.Ordinal) ||
                (child is FrameworkElement { Name: { Length: > 0 } name } && string.Equals(name, automationId, StringComparison.Ordinal)))
            {
                return child;
            }

            if (FindByAutomationId(child, automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
