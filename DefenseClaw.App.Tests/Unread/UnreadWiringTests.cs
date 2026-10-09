using System.Globalization;
using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.Navigation;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Unread;

/// <summary>
/// The window's view-model and the sidebar's capsules (CUST-265): the view-model owns the one subscription that starts the counts (so a window that
/// is abandoned or closed gives it back), words each panel's count for its entry, and starts with the counts a window built later missed.
/// </summary>
public sealed class UnreadViewModelTests : IDisposable
{
    private const string Audit = UnreadCountsService.AuditId;
    private const string Activity = UnreadCountsService.ActivityId;
    private const string Ai = UnreadCountsService.AiDiscoveryId;

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public UnreadViewModelTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what) => await UnreadScene.WaitUntilAsync(condition, what);

    [Fact]
    public void The_view_model_starts_the_counts_while_it_lives_and_gives_them_back_when_it_is_disposed()
    {
        Assert.False(_services.UnreadCounts.IsRunning);

        var vm = new MainWindowViewModel(_services);
        Assert.True(_services.UnreadCounts.IsRunning);

        vm.Dispose();
        Assert.False(_services.UnreadCounts.IsRunning);
        Assert.Equal(0, _services.Monitor.AlertCadenceSubscriberCount);

        // Several windows in a row (the dashboard can be abandoned and rebuilt) leave no trace either.
        for (var i = 0; i < 5; i++)
        {
            new MainWindowViewModel(_services).Dispose();
        }

        Assert.False(_services.UnreadCounts.IsRunning);
    }

    [Fact]
    public void Nothing_counted_yet_means_no_capsule_on_any_entry()
    {
        using var vm = new MainWindowViewModel(_services);

        foreach (var panel in UnreadCountsService.TrackedPanels)
        {
            Assert.False(vm.UnreadFor(panel).IsShown);
            Assert.Equal(UnreadBadgeState.None, vm.UnreadFor(panel));
        }

        Assert.Equal(UnreadBadgeState.None, vm.UnreadFor("overview"));
        Assert.Equal(UnreadBadgeState.None, vm.UnreadFor("no-such-panel"));
    }

    [Fact]
    public async Task Each_entrys_capsule_follows_its_count_worded_for_a_screen_reader()
    {
        using var vm = new MainWindowViewModel(_services);
        var service = _services.UnreadCounts;

        // Activity: runs in the runner's list. AI Discovery: a component first seen after the baseline the first report set.
        service.ReportAiDiscovery(new AiDiscoveryHead(new[] { DateTimeOffset.UtcNow.AddDays(-9).UtcTicks }));
        service.ReportAiDiscovery(new AiDiscoveryHead(new[] { DateTimeOffset.UtcNow.AddDays(-9).UtcTicks, DateTimeOffset.UtcNow.AddHours(1).UtcTicks }));
        for (var i = 0; i < 7; i++)
        {
            _ = _services.Cli.RecordHandOff("defenseclaw", new[] { "keys", "set" }, "synthetic entry");
        }

        await WaitUntilAsync(() => vm.UnreadFor(Activity).IsShown && vm.UnreadFor(Ai).IsShown, "the capsules to be set");

        Assert.Equal("7", vm.UnreadFor(Activity).Text);
        Assert.Equal("7 new since last visit", vm.UnreadFor(Activity).Description);
        Assert.Equal("1", vm.UnreadFor(Ai).Text);
        Assert.Equal("1 new since last visit", vm.UnreadFor(Ai).Description);
        Assert.False(vm.UnreadFor(Audit).IsShown);
    }

    [Fact]
    public async Task More_than_99_is_99_plus_and_the_panel_being_shown_has_no_capsule()
    {
        using var vm = new MainWindowViewModel(_services);
        for (var i = 0; i < 120; i++)
        {
            _ = _services.Cli.RecordHandOff("defenseclaw", new[] { "keys", "set" }, "synthetic entry");
        }

        // The capsule can show after the first runs are counted and before the last ones are: wait for the cap itself.
        await WaitUntilAsync(() => vm.UnreadFor(Activity).Text == "99+", "the Activity capsule to reach 99+");
        Assert.Equal("99+ new since last visit", vm.UnreadFor(Activity).Description);

        await _services.UnreadCounts.PanelShown(Activity);
        await WaitUntilAsync(() => !vm.UnreadFor(Activity).IsShown, "the capsule to go when the panel is shown");
        Assert.Equal(string.Empty, vm.UnreadFor(Activity).Description);
    }

    [Fact]
    public async Task A_view_model_built_after_the_counts_changed_starts_with_what_it_missed()
    {
        for (var i = 0; i < 3; i++)
        {
            _ = _services.Cli.RecordHandOff("defenseclaw", new[] { "keys", "set" }, "synthetic entry");
        }

        // Nothing listens yet, so nothing has counted: the first window's subscription does, and a second window that comes later starts from that.
        using var first = new MainWindowViewModel(_services);
        await WaitUntilAsync(() => first.UnreadFor(Activity).IsShown, "the first window's catch-up");

        using var later = new MainWindowViewModel(_services);
        Assert.Equal("3", later.UnreadFor(Activity).Text);
    }
}

/// <summary>
/// The catalog is where a panel coming on screen and leaving it are known (CUST-265): a panel with a count has its capsule cleared and its marker moved when it
/// is shown, its marker moved again when it is left or the window goes to the tray, and the counts stop while the window cannot be seen. Probe panels with the
/// tracked panels' ids, each hosted in a real (offscreen) window so its view becomes visible and invisible the way a navigation frame's does.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PanelCatalogUnreadTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly List<string> _log = new();
    private readonly List<OffscreenHost> _hosts = new();
    private readonly PanelCatalog _catalog;
    private int _rows = 8;

    public PanelCatalogUnreadTests()
    {
        _services = TestServices.Create(_temp);
        AuditTestDatabase.Create(_services.Paths.AuditDatabasePath, _rows);
        _catalog = new PanelCatalog(
            _services,
            new[]
            {
                new PanelDescriptor(UnreadCountsService.AuditId, "Audit", "Monitor", SymbolRegular.Home24, typeof(ProbeAView), s => new ProbeViewModel(s, "audit", _log)),
                new PanelDescriptor(UnreadCountsService.ActivityId, "Activity", "Monitor", SymbolRegular.Home24, typeof(ProbeBView), s => new ProbeViewModel(s, "activity", _log)),
                new PanelDescriptor("settings", "Settings", "App", SymbolRegular.Home24, typeof(ProbeCView), s => new PlainProbeViewModel(s, _log)),
            });
    }

    public void Dispose()
    {
        UiThread.Run(() =>
        {
            foreach (var host in _hosts)
            {
                host.Dispose();
            }
        });
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private UnreadCountsService Service => _services.UnreadCounts;

    private T Show<T>(out OffscreenHost host)
        where T : FrameworkElement
    {
        var view = (T)_catalog.GetPage(typeof(T))!;
        host = new OffscreenHost(view, 100, 100);
        _hosts.Add(host);
        return view;
    }

    private void Hide(OffscreenHost host)
    {
        _ = _hosts.Remove(host);
        host.Dispose();
        UiThread.Settle();
    }

    private void AddAuditRows(int count)
    {
        var first = _rows;
        AuditEventWriter.AddMany(
            _services.Paths.AuditDatabasePath,
            count,
            i => ("catalog-" + (first + i).ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddSeconds(-1), "hook_decision", "INFO", "synthetic extra", "claudecode"));
        _rows += count;
    }

    private long Marker(string panelId) => Service.TryGetMarker(panelId, out var marker) ? marker : -1;

    [Fact]
    public async Task A_panel_shown_has_its_marker_moved_and_one_left_has_it_moved_again_through_the_catalog()
    {
        Service.Changed += OnChanged;
        try
        {
            await UnreadScene.WaitUntilAsync(() => Marker(UnreadCountsService.AuditId) == 8, "the first look at the audit database");

            AddAuditRows(3);
            await Service.RefreshAsync();
            Assert.Equal(3, Service.CountOf(UnreadCountsService.AuditId));

            // Shown by the catalog (an activation, not a call the test makes): the count goes and the marker is the head.
            UiThread.Run(() => _ = Show<ProbeAView>(out _));
            await UnreadScene.WaitUntilAsync(() => Marker(UnreadCountsService.AuditId) == 11, "the marker to reach the head on arrival");
            Assert.Equal(0, Service.CountOf(UnreadCountsService.AuditId));

            // Two arrive while it is open; leaving (the view comes off screen) moves the marker over them.
            AddAuditRows(2);
            UiThread.Run(() => Hide(_hosts[0]));
            await UnreadScene.WaitUntilAsync(() => Marker(UnreadCountsService.AuditId) == 13, "the marker to follow the panel being left");
            await Service.RefreshAsync();
            Assert.Equal(0, Service.CountOf(UnreadCountsService.AuditId));
        }
        finally
        {
            Service.Changed -= OnChanged;
        }
    }

    [Fact]
    public async Task A_panel_with_no_count_is_not_known_to_the_markers()
    {
        Service.Changed += OnChanged;
        try
        {
            await UnreadScene.WaitUntilAsync(() => Marker(UnreadCountsService.AuditId) == 8, "the first look at the audit database");

            UiThread.Run(() => _ = Show<ProbeCView>(out _));
            UiThread.Run(() => Hide(_hosts[0]));

            Assert.Equal(-1, Marker("settings"));
            Assert.Equal(8, Marker(UnreadCountsService.AuditId));
        }
        finally
        {
            Service.Changed -= OnChanged;
        }
    }

    [Fact]
    public async Task A_window_in_the_tray_moves_the_marker_of_the_panel_it_leaves_and_stops_the_counts_and_coming_back_starts_them_again()
    {
        Service.Changed += OnChanged;
        try
        {
            await UnreadScene.WaitUntilAsync(() => Marker(UnreadCountsService.AuditId) == 8, "the first look at the audit database");
            UiThread.Run(() => _ = Show<ProbeAView>(out _));
            await UnreadScene.WaitUntilAsync(() => Service.CountOf(UnreadCountsService.AuditId) == 0 && Marker(UnreadCountsService.AuditId) == 8, "the panel to be shown");
            Assert.True(Service.IsRunning);

            // Rows arrive while the panel is on screen, then the window is hidden to the tray: the panel deactivates, which is leaving it.
            AddAuditRows(4);
            UiThread.Run(() => _catalog.SetWindowInteractive(false));
            await UnreadScene.WaitUntilAsync(() => Marker(UnreadCountsService.AuditId) == 12, "the marker to follow the panel the window left");
            Assert.False(Service.IsRunning);
            Assert.Equal(0, _services.Monitor.AlertCadenceSubscriberCount);

            // Back: the panel is shown again and the counts run again, with one catch-up pass.
            var passes = Service.PassCount;
            UiThread.Run(() => _catalog.SetWindowInteractive(true));
            await UnreadScene.WaitUntilAsync(() => Service.IsRunning && Service.PassCount > passes, "the counts to start again");
            Assert.Equal(1, _services.Monitor.AlertCadenceSubscriberCount);
            Assert.Equal(0, Service.CountOf(UnreadCountsService.AuditId));
        }
        finally
        {
            Service.Changed -= OnChanged;
        }
    }

    private static void OnChanged(object? sender, UnreadChangedEventArgs e)
    {
    }
}

/// <summary>
/// What the Overview's read of the AI discovery state file hands the AI Discovery capsule (CUST-265): when each component was first seen, from the text it
/// already holds. Nothing else reads the file for it, and a read that fails says nothing.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewAiDiscoveryFeedTests : IDisposable
{
    private readonly OverviewScene _scene = OverviewScene.Create(seedAgents: false, seedAudit: false);

    public void Dispose() => _scene.Dispose();

    private string StatePath => _scene.Temp.File("ai_discovery_state.json");

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "Z";

    private static string State(params (string Product, DateTimeOffset FirstSeen)[] components) =>
        "{ \"version\": 2, \"updated_at\": \"" + Stamp(DateTimeOffset.UtcNow) + "\", \"signals\": { " +
        string.Join(", ", components.Select((c, i) =>
            "\"fp" + i + "\": { \"name\": \"" + c.Product + "\", \"vendor\": \"Acme\", \"product\": \"" + c.Product + "\", \"category\": \"ai_cli\", \"confidence\": 0.9, \"state\": \"seen\", " +
            "\"first_seen\": \"" + Stamp(c.FirstSeen) + "\", \"last_seen\": \"" + Stamp(DateTimeOffset.UtcNow) + "\" }")) + " } }";

    [Fact]
    public async Task The_first_read_sets_the_marker_and_a_component_first_seen_after_it_is_counted_at_the_next_read()
    {
        var unread = _scene.Services.UnreadCounts;
        var vm = new OverviewPanelViewModel(_scene.Services);
        File.WriteAllText(StatePath, State(("Known", DateTimeOffset.UtcNow.AddDays(-20)), ("Older", DateTimeOffset.UtcNow.AddDays(-2))));

        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.True(unread.TryGetMarker(UnreadCountsService.AiDiscoveryId, out _));
        Assert.Equal(0, unread.CountOf(UnreadCountsService.AiDiscoveryId));

        File.WriteAllText(StatePath, State(("Known", DateTimeOffset.UtcNow.AddDays(-20)), ("Older", DateTimeOffset.UtcNow.AddDays(-2)), ("Fresh", DateTimeOffset.UtcNow.AddHours(1)), ("Fresher", DateTimeOffset.UtcNow.AddHours(2))));
        await vm.RefreshAgentsAsync(CancellationToken.None);

        Assert.Equal(2, unread.CountOf(UnreadCountsService.AiDiscoveryId));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_says_nothing_and_a_missing_one_says_there_is_nothing()
    {
        var unread = _scene.Services.UnreadCounts;
        var vm = new OverviewPanelViewModel(_scene.Services);

        // No file: the Overview says nothing has been recorded; the capsule's baseline is set and nothing is new.
        await vm.RefreshAgentsAsync(CancellationToken.None);
        Assert.Equal(0, unread.CountOf(UnreadCountsService.AiDiscoveryId));

        File.WriteAllText(StatePath, State(("Fresh", DateTimeOffset.UtcNow.AddHours(1))));
        await vm.RefreshAgentsAsync(CancellationToken.None);
        Assert.Equal(1, unread.CountOf(UnreadCountsService.AiDiscoveryId));

        // A file caught half-written is not "nothing new": the last count stands.
        File.WriteAllText(StatePath, "{ broken");
        await vm.RefreshAgentsAsync(CancellationToken.None);
        Assert.Contains("could not be read", vm.AgentsNote, StringComparison.Ordinal);
        Assert.Equal(1, unread.CountOf(UnreadCountsService.AiDiscoveryId));
    }
}
