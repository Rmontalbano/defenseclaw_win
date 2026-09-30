using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Notifications;

/// <summary>
/// The notifier over a real (synthetic) <c>audit.db</c> and the real settings store: the persisted high-water mark is what makes a
/// restart announce what arrived while the app was closed exactly once, and the toggles are read from the store at each look.
/// The toast itself is a recording delegate; no tray icon is ever built.
/// </summary>
public sealed class AlertNotifierTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-2);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly AlertQueueDatabase _database;
    private readonly List<AlertNotifier> _notifiers = new();

    public AlertNotifierTests()
    {
        _services = TestServices.Create(_temp);
        _database = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
    }

    public void Dispose()
    {
        foreach (var notifier in _notifiers)
        {
            notifier.Dispose();
        }

        _services.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private sealed class Toasts
    {
        private readonly object _gate = new();
        private readonly List<AlertToast> _shown = new();
        private readonly SemaphoreSlim _signal = new(0);

        public IReadOnlyList<AlertToast> Shown
        {
            get
            {
                lock (_gate)
                {
                    return _shown.ToArray();
                }
            }
        }

        public void Show(AlertToast toast)
        {
            lock (_gate)
            {
                _shown.Add(toast);
            }

            _ = _signal.Release();
        }

        public async Task<AlertToast> NextAsync()
        {
            Assert.True(await _signal.WaitAsync(TimeSpan.FromSeconds(30)), "No toast was shown.");
            lock (_gate)
            {
                return _shown[^1];
            }
        }

        public async Task<bool> NoneWithinAsync(TimeSpan window) => !await _signal.WaitAsync(window);
    }

    private AlertNotifier Notifier(Toasts toasts)
    {
        var notifier = new AlertNotifier(_services, toasts.Show);
        _notifiers.Add(notifier);
        return notifier;
    }

    private void Finding(string id, int minute, string severity) => _database.AddFinding(id, Base.AddMinutes(minute), severity);

    private long Mark => _services.Settings.Current.Notifications.HighWaterUnixNano;

    private void SetMark(int minute) =>
        _ = _services.Settings.Update(s => s with { Notifications = s.Notifications with { HighWaterUnixNano = AlertToastPolicy.UnixNanos(Base.AddMinutes(minute)) } });

    private static long Nanos(int minute) => AlertToastPolicy.UnixNanos(Base.AddMinutes(minute));

    // ------------------------------------------------------------------ launch

    [Fact]
    public async Task At_launch_what_arrived_while_the_app_was_closed_is_one_toast_and_the_mark_moves_to_the_newest()
    {
        Finding("seen", 1, "CRITICAL");
        SetMark(1);
        Finding("a", 10, "CRITICAL");
        Finding("b", 11, "CRITICAL");
        Finding("c", 12, "CRITICAL");
        var toasts = new Toasts();

        var shown = await Notifier(toasts).LookAsync();

        Assert.True(shown);
        var toast = Assert.Single(toasts.Shown);
        Assert.Equal("3 new CRITICAL findings while DefenseClaw was closed", toast.Body);
        Assert.Equal(Nanos(12), Mark);
    }

    [Fact]
    public async Task A_restart_announces_nothing_again_because_the_mark_was_persisted()
    {
        SetMark(1);
        Finding("a", 10, "CRITICAL");

        var first = new Toasts();
        Assert.True(await Notifier(first).LookAsync());
        Assert.Single(first.Shown);

        // A new process: a new notifier over the same settings file.
        var second = new Toasts();
        Assert.False(await Notifier(second).LookAsync());
        Assert.Empty(second.Shown);
    }

    [Fact]
    public async Task On_a_fresh_install_the_first_look_sets_the_mark_silently_and_a_later_finding_is_announced_as_it_arrives()
    {
        Finding("history-1", 1, "CRITICAL");
        Finding("history-2", 2, "HIGH");
        Assert.Equal(0, Mark);
        var toasts = new Toasts();
        var notifier = Notifier(toasts);

        Assert.False(await notifier.LookAsync());
        Assert.Empty(toasts.Shown);
        Assert.Equal(Nanos(2), Mark);

        Finding("live", 30, "CRITICAL");
        Assert.True(await notifier.LookAsync());

        var toast = Assert.Single(toasts.Shown);
        Assert.Equal("CRITICAL finding", toast.Title);
        Assert.Equal("Target: /synthetic/live", toast.Body);
        Assert.Equal(Nanos(30), Mark);
    }

    [Fact]
    public async Task With_no_audit_database_at_all_a_look_says_nothing_and_a_finding_that_appears_later_is_announced()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var toasts = new Toasts();
        using var notifier = new AlertNotifier(services, toasts.Show);

        Assert.False(await notifier.LookAsync());

        var database = new AlertQueueDatabase(services.Paths.AuditDatabasePath);
        database.AddFinding("first-ever", Base.AddMinutes(3), "HIGH");
        Assert.True(await notifier.LookAsync());

        Assert.Equal("HIGH finding", Assert.Single(toasts.Shown).Title);
        SqliteConnection.ClearAllPools();
    }

    // ------------------------------------------------------------------ the toggles, read from the store at each look

    [Fact]
    public async Task The_toggles_come_from_the_settings_store_each_time_and_the_mark_passes_what_was_switched_off()
    {
        SetMark(1);
        _ = _services.Settings.Update(s => s with { Notifications = s.Notifications with { Critical = false } });
        Finding("c", 10, "CRITICAL");
        Finding("h", 11, "HIGH");
        var toasts = new Toasts();
        var notifier = Notifier(toasts);

        Assert.True(await notifier.LookAsync());
        Assert.Equal("1 new HIGH finding while DefenseClaw was closed", toasts.Shown[^1].Body);

        // Switching CRITICAL on now does not bring the one that was passed back.
        _ = _services.Settings.Update(s => s with { Notifications = s.Notifications with { Critical = true } });
        Assert.False(await notifier.LookAsync());

        Finding("c2", 20, "CRITICAL");
        Assert.True(await notifier.LookAsync());
        Assert.Equal("CRITICAL finding", toasts.Shown[^1].Title);
    }

    // ------------------------------------------------------------------ reset

    [Fact]
    public async Task Resetting_the_history_announces_what_is_outstanding_once_more_as_one_toast()
    {
        SetMark(1);
        Finding("c", 10, "CRITICAL");
        Finding("h", 11, "HIGH");
        Finding("m", 12, "MEDIUM");
        var toasts = new Toasts();
        var notifier = Notifier(toasts);
        Assert.True(await notifier.LookAsync());
        Assert.False(await notifier.LookAsync());

        var announced = await notifier.ResetSeenHistoryAsync();

        Assert.True(announced);
        var toast = toasts.Shown[^1];
        Assert.Equal("Unacknowledged findings", toast.Title);
        Assert.Equal("1 CRITICAL and 1 HIGH findings unacknowledged", toast.Body);
        Assert.Equal(Nanos(12), Mark);

        // And it is history again after that.
        Assert.False(await notifier.LookAsync());
    }

    [Fact]
    public async Task Resetting_with_nothing_outstanding_reports_that_there_was_nothing_to_announce()
    {
        SetMark(5);
        var toasts = new Toasts();

        var announced = await Notifier(toasts).ResetSeenHistoryAsync();

        Assert.False(announced);
        Assert.Empty(toasts.Shown);
        Assert.Equal(0, Mark);
    }

    [Fact]
    public async Task An_acknowledged_finding_is_not_outstanding_so_a_reset_does_not_bring_it_back()
    {
        Finding("kept", 10, "CRITICAL");
        Finding("acked", 11, "CRITICAL");
        _database.Acknowledge("acked");
        var toasts = new Toasts();

        Assert.True(await Notifier(toasts).ResetSeenHistoryAsync());

        Assert.Equal("1 CRITICAL finding unacknowledged", Assert.Single(toasts.Shown).Body);
    }

    // ------------------------------------------------------------------ following the counts

    [Fact]
    public async Task Started_it_looks_when_the_counts_first_arrive_and_again_when_they_change()
    {
        SetMark(1);
        Finding("old", 10, "CRITICAL");
        var toasts = new Toasts();
        var notifier = Notifier(toasts);

        notifier.Start();
        var launch = await toasts.NextAsync();
        Assert.Equal("1 new CRITICAL finding while DefenseClaw was closed", launch.Body);

        Finding("live", 20, "HIGH");
        await _services.AlertCounts.RefreshAsync();
        var live = await toasts.NextAsync();
        Assert.Equal("HIGH finding", live.Title);

        Assert.Equal(2, toasts.Shown.Count);
    }

    [Fact]
    public async Task Starting_starts_the_counts_and_disposing_lets_them_go_idle_again()
    {
        SetMark(1);
        Assert.False(_services.AlertCounts.IsRunning);
        var notifier = Notifier(new Toasts());

        notifier.Start();
        Assert.True(_services.AlertCounts.IsRunning);

        notifier.Dispose();
        Assert.False(_services.AlertCounts.IsRunning);

        // A look after disposal does nothing and throws nothing.
        Assert.False(await notifier.LookAsync());
    }
}
