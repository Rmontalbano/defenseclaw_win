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
/// live in <see cref="ShellShortcuts"/>): Ctrl+1…9 / Ctrl+0 / Ctrl+Shift+1…3 jump to the panels in
/// sidebar order, F5 refreshes the current panel (falling back to a gateway poll), Ctrl+K opens the
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

        InitializeComponent();

        _viewModel = new MainWindowViewModel(services);
        DataContext = _viewModel;

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
        Palette.DataContext = _paletteViewModel;

        Shortcuts.DataContext = ShortcutCatalog.Build(_catalog);
        Shortcuts.CloseRequested += (_, _) => CloseShortcuts();

        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewTextInput += OnWindowPreviewTextInput;
        SizeChanged += (_, _) => UpdateStripDensity();

        // A screen reader should hear the gateway state change, not only find it when it looks.
        _viewModel.PropertyChanged += OnShellPropertyChanged;

        // Taskbar icon mirrors the tray shield, state badge and all, so alt-tab tells the same
        // story as the notification area. A multi-size icon, so the title bar, the taskbar and
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
    private void PublishInteractivity() =>
        _catalog.SetWindowInteractive(IsVisible && WindowState != WindowState.Minimized);

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

    /// <summary>
    /// Window width (DIPs) below which the version chip steps aside in the status strip. Measured, not
    /// guessed: the chip (with its 8 px gap) costs ~120 DIPs, and the detail sentence next to the state pill
    /// needs ~220 to show in full ("Gateway responding on 127.0.0.1:18970."), so it fits from about 1000 up;
    /// 1040 leaves the sentence a little air.
    /// </summary>
    private const double VersionChipMinWidth = 1040;

    /// <summary>
    /// Window width (DIPs) below which the connector chip steps aside as well. The window's own MinWidth is
    /// 940 and the strip has ~290 spare DIPs there once the connector chip (~140) is placed, so at the
    /// minimum width the connector is still shown — only a very long connector list or detail line trims.
    /// </summary>
    private const double ConnectorChipMinWidth = 900;

    /// <summary>
    /// Drops the supporting chips from the status strip as the window narrows — version first, then
    /// connector — so the state pill, the alert chip and the actions never clip at the 940 DIP minimum.
    /// Both facts stay available elsewhere (tray flyout, Overview), so hiding them loses nothing. The width
    /// is in DIPs: a 940 DIP window is 2115 px on a 225 % display, which is what a screenshot of the minimum
    /// size shows.
    /// </summary>
    private void UpdateStripDensity()
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        VersionSlot.Visibility = width >= VersionChipMinWidth ? Visibility.Visible : Visibility.Collapsed;
        ConnectorSlot.Visibility = width >= ConnectorChipMinWidth ? Visibility.Visible : Visibility.Collapsed;
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

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // Closing the dashboard is not quitting: the tray icon is the app.
            e.Cancel = true;
            Hide();

            if (!_minimizeHintShown)
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
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        BuildNavigation();
        UpdateStripDensity();

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
        var index = 0;

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
                var chord = ShellShortcuts.PanelChordText(index++);

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
                AutomationProperties.SetHelpText(item, $"Opens the {panel.Title} panel in the {group} group.");
                if (chord is not null)
                {
                    AutomationProperties.SetAcceleratorKey(item, chord);
                }

                _ = RootNavigation.MenuItems.Add(item);
            }
        }

        // Collapsed, the sidebar is a 40 px icon strip and a heading's text would be cut off mid-word
        // ("Gove", "Disc"). The heading keeps its height (so the groups stay visibly apart) and its
        // accessible name; only the text is hidden.
        RootNavigation.PaneOpened += (_, _) => ShowGroupHeadings(true);
        RootNavigation.PaneClosed += (_, _) => ShowGroupHeadings(false);
        ShowGroupHeadings(RootNavigation.IsPaneOpen);

        RootNavigation.SetPageProviderService(_catalog);
        _ = RootNavigation.Navigate(_catalog.Default.ViewType);
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
        else if (ShellShortcuts.IsToggleThemeChord(e.Key, modifiers) && _appearance is not null)
        {
            _appearance.ToggleLightDark();
            e.Handled = true;
        }
        else if (ShellShortcuts.PanelIndexFor(e.Key, modifiers) is { } index &&
                 index < _catalog.SidebarOrder.Count)
        {
            NavigateTo(_catalog.SidebarOrder[index]);
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

        // Built fresh on every open: toggle titles and the gateway controls' availability are read now.
        _paletteViewModel.Load(ShellCommandRegistry.Build(_catalog, _actions, NavigateTo, OpenShortcuts, _appearance));
        _viewModel.IsPaletteOpen = true;
        Palette.FocusSearch();
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
        NameByAutomationId("TitleBarCloseButton", "Close to tray");
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
