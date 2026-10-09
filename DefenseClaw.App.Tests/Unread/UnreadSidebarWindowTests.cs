using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Unread;

/// <summary>
/// The sidebar's "new since last visit" capsules (CUST-265) on the real dashboard window - shown far off screen, without activation, over a scratch
/// installation - end to end: a new audit row raises the Audit entry's capsule, opening the panel (by the shell's own navigation request) clears it,
/// what arrives while the panel is open does not come back as new when it is left, and what a screen reader hears is "Audit, N new since last visit".
/// AI Discovery's capsule comes from the Overview's own read of the state file, Activity's from the runner's list. A PNG of the sidebar is written when
/// <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class UnreadSidebarWindowTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly string _auditPath;
    private PanelCatalog? _catalog;
    private MainWindow? _window;
    private int _rows;

    public UnreadSidebarWindowTests()
    {
        _services = TestServices.Create(_temp);
        _auditPath = _services.Paths.AuditDatabasePath;
        AuditTestDatabase.Create(_auditPath, 12);
        _rows = 12;
    }

    public void Dispose()
    {
        UiThread.Run(() =>
        {
            if (_window is not null)
            {
                _window.AllowClose();
                _window.Close();
            }
        });

        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    /// <summary>What one sidebar entry shows, read on the UI thread.</summary>
    private sealed record Look(string Name, Visibility CapsuleVisibility, string? CapsuleText, InfoBadgeSeverity? Severity, double FontSize, string ToolTip, bool HasCapsule, string CapsuleName);

    /// <summary>The dashboard, shown where no one can see it and without taking focus, with its sidebar built.</summary>
    private MainWindow OpenWindow()
    {
        var window = UiThread.Run(() =>
        {
            _catalog = new PanelCatalog(_services);
            var created = new MainWindow(_services, _catalog, UnbuiltTray())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = 1100,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            created.Show();
            return created;
        });
        _window = window;

        UiThread.WaitFor(() => window.RootNavigation.MenuItems.OfType<DcNavigationItem>().Any(), "the sidebar to be built");
        return window;
    }

    private static Look LookAt(MainWindow window, string title) =>
        UiThread.Run(() =>
        {
            var item = window.RootNavigation.MenuItems.OfType<DcNavigationItem>().First(entry => string.Equals(entry.Content as string, title, StringComparison.Ordinal));
            var capsule = item.InfoBadge as InfoBadge;
            return new Look(
                AutomationProperties.GetName(item),
                capsule?.Visibility ?? Visibility.Collapsed,
                capsule?.Value?.ToString(),
                capsule?.Severity,
                capsule?.FontSize ?? 0,
                item.ToolTip as string ?? string.Empty,
                capsule is not null,
                capsule is null ? string.Empty : AutomationProperties.GetName(capsule));
        });

    private static string NameOf(MainWindow window, string title) => LookAt(window, title).Name;

    private static void WaitForName(MainWindow window, string title, string expected) =>
        UiThread.WaitFor(() => LookAt(window, title).Name == expected, $"the {title} entry to be called '{expected}' (it is '{NameOf(window, title)}')");

    private void AddAuditRows(int count)
    {
        var first = _rows;
        AuditEventWriter.AddMany(
            _auditPath,
            count,
            i => ("window-" + (first + i), DateTimeOffset.UtcNow.AddSeconds(-1), "hook_decision", "INFO", "synthetic extra", "claudecode"));
        _rows += count;
    }

    private void Navigate(string panelId) => UiThread.Run(() => _services.Navigation.Request(panelId));

    private static void Png(MainWindow window, string name)
    {
        var directory = Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        UiThread.Run(() =>
        {
            UiThread.Settle();
            var element = window.RootNavigation;
            var width = (int)Math.Ceiling(Math.Min(element.ActualWidth, element.OpenPaneLength + 8));
            var height = (int)Math.Ceiling(element.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

            // The window paints its backdrop (Mica) outside the element, so a render of the element alone is text on nothing: paint the app's own
            // background first, as the window sits on it.
            var backdrop = new DrawingVisual();
            using (var context = backdrop.RenderOpen())
            {
                context.DrawRectangle(Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black, null, new Rect(0, 0, width, height));
            }

            bitmap.Render(backdrop);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            _ = Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, name + ".png"));
            encoder.Save(stream);
        });
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";

    /// <summary>The state file the Overview reads, one signal per component.</summary>
    private void WriteStateFile(params (string Vendor, string Product, DateTimeOffset FirstSeen)[] components)
    {
        var signals = string.Join(
            ", ",
            components.Select((c, i) =>
                "\"fp" + i + "\": { \"signal_id\": \"s" + i + "\", \"vendor\": \"" + c.Vendor + "\", \"product\": \"" + c.Product + "\", " +
                "\"category\": \"ai_cli\", \"detector\": \"process\", \"state\": \"seen\", \"confidence\": 0.9, " +
                "\"first_seen\": \"" + Stamp(c.FirstSeen) + "\", \"last_seen\": \"" + Stamp(DateTimeOffset.UtcNow) + "\" }"));
        File.WriteAllText(_services.Paths.AiDiscoveryStatePath, "{ \"updated_at\": \"" + Stamp(DateTimeOffset.UtcNow) + "\", \"signals\": { " + signals + " } }");
    }

    // ------------------------------------------------------------------ the acceptance

    [Fact]
    public async Task A_new_audit_row_raises_the_capsule_opening_the_panel_clears_it_and_what_arrived_while_it_was_open_does_not_come_back()
    {
        var window = OpenWindow();
        var service = _services.UnreadCounts;

        // The first look at the database sets the marker; nothing is "new" yet and no entry says so.
        UiThread.WaitFor(() => service.TryGetMarker(UnreadCountsService.AuditId, out _), "the first look at the audit database");
        var quiet = LookAt(window, "Audit");
        Assert.Equal("Audit", quiet.Name);
        Assert.Equal(Visibility.Collapsed, quiet.CapsuleVisibility);

        // The gateway writes three events. Nothing has to be pressed: the next alert tick would find them; the test asks for that pass.
        AddAuditRows(3);
        await service.RefreshAsync();
        WaitForName(window, "Audit", "Audit, 3 new since last visit");

        var raised = LookAt(window, "Audit");
        Assert.Equal(Visibility.Visible, raised.CapsuleVisibility);
        Assert.Equal("3", raised.CapsuleText);
        Assert.Equal("3 new since last visit", raised.CapsuleName);
        Assert.Equal(InfoBadgeSeverity.Attention, raised.Severity);
        Assert.True(raised.FontSize >= 12, $"the capsule's text is {raised.FontSize} px");
        Assert.StartsWith("Audit (Ctrl+", raised.ToolTip, StringComparison.Ordinal);
        Assert.EndsWith(" — 3 new since last visit", raised.ToolTip, StringComparison.Ordinal);
        Png(window, "sidebar-unread-audit-3");

        // Opened: by the shell's own request, the way a deep link or a chord does it. The capsule goes with the first frame and the marker moves.
        Navigate("audit");
        WaitForName(window, "Audit", "Audit");
        Assert.Equal(Visibility.Collapsed, LookAt(window, "Audit").CapsuleVisibility);
        UiThread.WaitFor(() => service.TryGetMarker(UnreadCountsService.AuditId, out var marker) && marker == 15, "the marker to reach the head");

        // Two more arrive while the panel is open: it is the panel on screen, so the sidebar says nothing about it.
        AddAuditRows(2);
        await service.RefreshAsync();
        Assert.Equal("Audit", NameOf(window, "Audit"));

        // Left for another panel: the two rows were on the panel that was open, and do not turn into "new".
        Navigate("overview");
        UiThread.WaitFor(() => service.TryGetMarker(UnreadCountsService.AuditId, out var marker) && marker == 17, "the marker to follow the panel being left");
        await service.RefreshAsync();
        Assert.Equal("Audit", NameOf(window, "Audit"));

        // And a row that arrives afterwards is.
        AddAuditRows(1);
        await service.RefreshAsync();
        WaitForName(window, "Audit", "Audit, 1 new since last visit");
    }

    [Fact]
    public async Task Activity_and_AI_Discovery_have_their_own_capsules_and_only_the_panels_with_a_count_have_one()
    {
        // The state file the Overview reads: one component long known. The Overview's first read of it sets AI Discovery's marker.
        WriteStateFile(("Anthropic", "Claude Code", DateTimeOffset.UtcNow.AddDays(-9)));
        var window = OpenWindow();
        var service = _services.UnreadCounts;
        UiThread.WaitFor(() => service.TryGetMarker(UnreadCountsService.AuditId, out _), "the first look at the audit database");
        UiThread.WaitFor(() => service.TryGetMarker(UnreadCountsService.AiDiscoveryId, out _), "the Overview's first read of the state file");

        // A scan finds something new (its first_seen is after the marker); the Overview reads the file again when it is refreshed.
        WriteStateFile(("Anthropic", "Claude Code", DateTimeOffset.UtcNow.AddDays(-9)), ("Cursor", "Cursor", DateTimeOffset.UtcNow.AddHours(1)));
        var overview = UiThread.Run(() => (OverviewPanelViewModel)((OverviewPanel)_catalog!.GetPage(typeof(OverviewPanel))!).DataContext);
        UiThread.Run(() => overview.RefreshCommand.Execute(null));

        // Activity: two entries in the runner's list. Audit: more than 99 rows.
        _ = _services.Cli.RecordHandOff("defenseclaw", new[] { "keys", "set" }, "synthetic entry");
        _ = _services.Cli.RecordHandOff("defenseclaw", new[] { "keys", "fill-missing" }, "synthetic entry");
        AddAuditRows(150);
        await service.RefreshAsync();

        WaitForName(window, "Activity", "Activity, 2 new since last visit");
        WaitForName(window, "AI Discovery", "AI Discovery, 1 new since last visit");
        WaitForName(window, "Audit", "Audit, 99+ new since last visit");
        Assert.Equal("99+", LookAt(window, "Audit").CapsuleText);
        Assert.Equal("2", LookAt(window, "Activity").CapsuleText);
        Assert.Equal("1", LookAt(window, "AI Discovery").CapsuleText);

        // No other entry got a capsule from this: Alerts and Overview have their own, and the rest have none.
        foreach (var title in new[] { "Logs", "Skills", "MCPs", "Plugins", "Tools", "Inventory", "Registries", "Setup", "Policies" })
        {
            var look = LookAt(window, title);
            Assert.False(look.HasCapsule, title + " has a capsule");
            Assert.Equal(title, look.Name);
        }

        Png(window, "sidebar-unread-all");

        // Opening Activity clears its capsule and only its capsule.
        Navigate("activity");
        WaitForName(window, "Activity", "Activity");
        Assert.Equal("Audit, 99+ new since last visit", NameOf(window, "Audit"));
        Assert.Equal("AI Discovery, 1 new since last visit", NameOf(window, "AI Discovery"));
        Png(window, "sidebar-unread-activity-open");
    }

    [Fact]
    public void A_window_that_goes_to_the_tray_detaches_the_counts_and_one_that_comes_back_reads_once()
    {
        var window = OpenWindow();
        var service = _services.UnreadCounts;
        UiThread.WaitFor(() => service.TryGetMarker(UnreadCountsService.AuditId, out _), "the first look at the audit database");
        Assert.True(service.IsRunning);

        // The window's own view-model keeps the alert counts running for as long as the window lives, and these counts are one more listener on the same tick.
        Assert.Equal(2, _services.Monitor.AlertCadenceSubscriberCount);

        // Hidden to the tray: not interactive, so nothing of these counts is attached.
        UiThread.Run(window.Hide);
        UiThread.WaitFor(() => !service.IsRunning, "the counts to detach while the window is in the tray");
        Assert.Equal(1, _services.Monitor.AlertCadenceSubscriberCount);

        var passes = service.PassCount;
        UiThread.Run(window.Show);
        UiThread.WaitFor(() => service.IsRunning && service.PassCount == passes + 1, "the one catch-up pass of the window coming back");
        Assert.Equal(2, _services.Monitor.AlertCadenceSubscriberCount);
    }
}
