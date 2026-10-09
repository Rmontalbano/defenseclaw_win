using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// AL4: "Hide until relaunch" for stream-only rows (no audit id, so acknowledge and dismiss cannot name them). The hide is in memory
/// only: the count says how many are hidden, "Show hidden" lists them again, and a new panel (a relaunch) lists every row again.
/// Synthetic gateway rows; nothing here starts a process or writes to disk.
/// </summary>
public sealed class AlertsHideUntilRelaunchTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-1);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public AlertsHideUntilRelaunchTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private static GatewayAlert StreamOnly(string title, int minute) => new()
    {
        Id = string.Empty,
        Severity = "HIGH",
        Action = "scan-finding",
        Target = "/synthetic/" + title,
        Details = title,
        Timestamp = Base.AddMinutes(minute),
    };

    /// <summary>A poll that brings these alerts; a new instance each call, as a fresh /alerts answer is.</summary>
    private static GatewaySnapshot Poll(params GatewayAlert[] alerts) => new()
    {
        PolledAt = Base,
        AlertsFetchedAt = Base,
        RecentAlerts = alerts,
    };

    private static GatewaySnapshot TwoStreamRows() => Poll(StreamOnly("alpha", 1), StreamOnly("beta", 2));

    private static string[] Titles(AlertsPanelViewModel vm) => vm.Alerts.Select(a => a.Headline).ToArray();

    [Fact]
    public void Hide_takes_the_selected_stream_row_out_and_counts_it()
    {
        StaThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(_services);
            vm.Apply(TwoStreamRows());
            Assert.Equal(2, vm.Alerts.Count);

            vm.SelectedAlert = vm.Alerts.Single(a => a.Headline == "alpha");
            Assert.True(vm.HideUntilRelaunchCommand.CanExecute(null));

            vm.HideUntilRelaunchCommand.Execute(null);

            Assert.Equal(new[] { "beta" }, Titles(vm));
            Assert.Equal(1, vm.HiddenUntilRelaunchCount);
            Assert.True(vm.HasHiddenUntilRelaunch);
            Assert.Equal("1 hidden until relaunch", vm.HiddenUntilRelaunchText);
            Assert.Equal("Show hidden", vm.ShowHiddenText);
            Assert.False(vm.HideUntilRelaunchCommand.CanExecute(null));
        });
    }

    [Fact]
    public void A_later_poll_with_the_same_rows_keeps_them_hidden()
    {
        StaThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(_services);
            vm.Apply(TwoStreamRows());
            vm.SelectedAlert = vm.Alerts.Single(a => a.Headline == "alpha");
            vm.HideUntilRelaunchCommand.Execute(null);

            // A stream-only row gets a fresh key on every projection; the hide must follow the row's content, not the key.
            vm.Apply(TwoStreamRows());

            Assert.Equal(new[] { "beta" }, Titles(vm));
            Assert.Equal(1, vm.HiddenUntilRelaunchCount);
        });
    }

    [Fact]
    public void Show_hidden_lists_the_rows_again_and_the_toggle_hides_them_again()
    {
        StaThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(_services);
            vm.Apply(TwoStreamRows());
            vm.SelectedAlert = vm.Alerts.Single(a => a.Headline == "alpha");
            vm.HideUntilRelaunchCommand.Execute(null);

            vm.ToggleShowHiddenUntilRelaunchCommand.Execute(null);

            Assert.True(vm.ShowHiddenUntilRelaunch);
            Assert.Equal("Hide hidden rows", vm.ShowHiddenText);
            Assert.Equal(2, vm.Alerts.Count);
            Assert.Equal(1, vm.HiddenUntilRelaunchCount);

            vm.ToggleShowHiddenUntilRelaunchCommand.Execute(null);

            Assert.Equal(new[] { "beta" }, Titles(vm));
        });
    }

    [Fact]
    public void A_new_panel_after_a_relaunch_shows_the_rows_again()
    {
        StaThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(_services);
            vm.Apply(TwoStreamRows());
            vm.SelectedAlert = vm.Alerts.Single(a => a.Headline == "alpha");
            vm.HideUntilRelaunchCommand.Execute(null);
            Assert.Equal(1, vm.HiddenUntilRelaunchCount);

            var relaunched = new AlertsPanelViewModel(_services);
            relaunched.Apply(TwoStreamRows());

            Assert.Equal(2, relaunched.Alerts.Count);
            Assert.Equal(0, relaunched.HiddenUntilRelaunchCount);
            Assert.False(relaunched.HasHiddenUntilRelaunch);
        });
    }

    [Fact]
    public void A_row_the_cli_can_name_cannot_be_hidden_this_way()
    {
        StaThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(_services);
            vm.Apply(Poll(new GatewayAlert { Id = "finding-1", Severity = "HIGH", Action = "scan-finding", Details = "named", Timestamp = Base }));

            vm.SelectedAlert = Assert.Single(vm.Alerts);
            Assert.True(vm.SelectedAlert.HasAuditId);
            Assert.False(vm.HideUntilRelaunchCommand.CanExecute(null));
        });
    }
}
