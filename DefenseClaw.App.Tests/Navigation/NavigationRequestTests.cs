using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Navigation;

/// <summary>The inbox on its own: one outstanding request, taken once, raised to whoever listens.</summary>
public sealed class ShellNavigationTests
{
    [Fact]
    public void A_request_is_held_as_pending_and_announced_once()
    {
        var inbox = new ShellNavigation();
        var seen = new List<NavigationRequest>();
        inbox.Requested += (_, e) => seen.Add(e.Request);

        inbox.Request("alerts", new AlertsFilter(AuditSeverity.Critical));

        var request = Assert.Single(seen);
        Assert.Equal("alerts", request.PanelId);
        Assert.Equal(new AlertsFilter(AuditSeverity.Critical), request.Payload);
        Assert.Same(request, inbox.Pending);
    }

    [Fact]
    public void A_second_request_replaces_one_nobody_has_taken()
    {
        var inbox = new ShellNavigation();

        inbox.Request("alerts", new AlertsFilter(Kind: AlertsFilter.KindBlocks));
        inbox.Request("audit", new AuditPreset("last-hour"));

        Assert.Equal("audit", inbox.Pending!.PanelId);
        Assert.Null(inbox.TryTake("alerts"));
        Assert.NotNull(inbox.TryTake("audit"));
    }

    [Fact]
    public void A_request_is_taken_once_and_only_by_the_panel_it_names_without_regard_to_case()
    {
        var inbox = new ShellNavigation();
        inbox.Request("Alerts", new AlertsFilter());

        Assert.Null(inbox.TryTake("logs"));
        Assert.NotNull(inbox.Pending);

        var taken = inbox.TryTake("alerts");
        Assert.Equal("Alerts", taken!.PanelId);
        Assert.Null(inbox.Pending);
        Assert.Null(inbox.TryTake("alerts"));
    }

    [Fact]
    public void Discard_drops_only_the_request_it_is_given()
    {
        var inbox = new ShellNavigation();
        var first = new NavigationRequest("nowhere");
        inbox.Request(first);
        inbox.Request("audit");

        inbox.Discard(first);
        Assert.Equal("audit", inbox.Pending!.PanelId);

        inbox.Discard(inbox.Pending);
        Assert.Null(inbox.Pending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_request_with_no_panel_is_refused(string? panelId)
    {
        var inbox = new ShellNavigation();

        _ = Assert.ThrowsAny<ArgumentException>(() => inbox.Request(panelId!));
        Assert.Null(inbox.Pending);
        _ = Assert.Throws<ArgumentNullException>(() => inbox.Request((NavigationRequest)null!));
    }

    [Fact]
    public void A_subscriber_that_throws_neither_loses_the_request_nor_silences_the_others()
    {
        var inbox = new ShellNavigation();
        var second = 0;
        inbox.Requested += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        inbox.Requested += (_, _) => second++;

        inbox.Request("audit");

        Assert.Equal(1, second);
        Assert.Equal("audit", inbox.Pending!.PanelId);
    }

    [Fact]
    public void A_subscriber_that_takes_the_request_leaves_nothing_pending_for_the_ones_after_it()
    {
        var inbox = new ShellNavigation();
        inbox.Requested += (_, e) => Assert.NotNull(inbox.TryTake(e.Request.PanelId));
        NavigationRequest? seenPending = new("unset");
        inbox.Requested += (_, _) => seenPending = inbox.Pending;

        inbox.Request("audit");

        Assert.Null(seenPending);
    }

    [Fact]
    public void The_payloads_are_plain_values_with_no_filter_by_default()
    {
        Assert.Null(new AlertsFilter().SeverityFloor);
        Assert.Null(new AlertsFilter().Kind);
        Assert.Equal(new AlertsFilter(AuditSeverity.High, "blocks"), new AlertsFilter(AuditSeverity.High, AlertsFilter.KindBlocks));
        Assert.Equal("all", AlertsFilter.KindAll);
        Assert.Equal(new AuditPreset("critical"), new AuditPreset("critical"));
        Assert.NotEqual<object>(new AuditPreset("errors"), new LogsPreset("errors"));
        Assert.Equal("errors", new LogsPreset("errors").Name);
        Assert.Null(new NavigationRequest("audit").Payload);
    }
}

internal sealed class ProbeAView : Grid
{
}

internal sealed class ProbeBView : Grid
{
}

internal sealed class ProbeCView : Grid
{
}

/// <summary>A panel that takes navigation payloads and writes down everything that happens to it, in order.</summary>
internal sealed class ProbeViewModel : PanelViewModelBase, IAcceptsNavigation
{
    private readonly string _name;
    private readonly List<string> _log;

    public ProbeViewModel(AppServices services, string name, List<string> log)
        : base(services)
    {
        _name = name;
        _log = log;
    }

    public override string Title => _name;

    public List<object> Received { get; } = new();

    public bool ThrowOnAccept { get; set; }

    protected override void OnActivated() => _log.Add(_name + ":activated");

    protected override void OnDeactivated() => _log.Add(_name + ":deactivated");

    public void Accept(object payload)
    {
        _log.Add(_name + ":accept");
        Received.Add(payload);
        if (ThrowOnAccept)
        {
            throw new InvalidOperationException("this panel cannot use that payload");
        }
    }

    public void Ask(string panelId, object? payload = null) => RequestNavigation(panelId, payload);
}

/// <summary>A panel that does not take payloads at all (most of them, today).</summary>
internal sealed class PlainProbeViewModel : PanelViewModelBase
{
    private readonly List<string> _log;

    public PlainProbeViewModel(AppServices services, List<string> log)
        : base(services)
    {
        _log = log;
    }

    public override string Title => "Plain";

    protected override void OnActivated() => _log.Add("C:activated");
}

/// <summary>
/// A request meets the real activation path: a <see cref="PanelCatalog"/> over three tiny probe panels, each hosted in a real
/// (offscreen) window so its view becomes visible and invisible the way a navigation frame's does.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PanelNavigationDeliveryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly List<string> _log = new();
    private readonly List<OffscreenHost> _hosts = new();
    private readonly PanelCatalog _catalog;

    public PanelNavigationDeliveryTests()
    {
        _services = TestServices.Create(_temp);
        _catalog = new PanelCatalog(
            _services,
            new[]
            {
                new PanelDescriptor("probe-a", "Probe A", "Monitor", SymbolRegular.Home24, typeof(ProbeAView), s => new ProbeViewModel(s, "A", _log)),
                new PanelDescriptor("probe-b", "Probe B", "Monitor", SymbolRegular.Home24, typeof(ProbeBView), s => new ProbeViewModel(s, "B", _log)),
                new PanelDescriptor("probe-c", "Probe C", "Monitor", SymbolRegular.Home24, typeof(ProbeCView), s => new PlainProbeViewModel(s, _log)),
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
        _temp.Dispose();
    }

    private ShellNavigation Inbox => _services.Navigation;

    /// <summary>Puts the panel on screen the way the navigation frame does: builds it through the catalog and hosts its view in a window.</summary>
    private T Show<T>(out OffscreenHost host)
        where T : FrameworkElement
    {
        var view = (T)_catalog.GetPage(typeof(T))!;
        host = new OffscreenHost(view, 100, 100);
        _hosts.Add(host);
        return view;
    }

    /// <summary>Takes it off screen again.</summary>
    private void Hide(OffscreenHost host)
    {
        _ = _hosts.Remove(host);
        host.Dispose();
        UiThread.Settle();
    }

    private static ProbeViewModel Vm(FrameworkElement view) => (ProbeViewModel)view.DataContext;

    [Fact]
    public void A_request_made_before_the_panel_exists_is_delivered_when_it_first_comes_on_screen_after_OnActivated()
    {
        UiThread.Run(() =>
        {
            var payload = new AlertsFilter(AuditSeverity.Critical);
            Inbox.Request("probe-a", payload);
            Assert.Empty(_log);

            var view = Show<ProbeAView>(out _);

            Assert.Equal(new[] { "A:activated", "A:accept" }, _log);
            Assert.Same(payload, Assert.Single(Vm(view).Received));
            Assert.Null(Inbox.Pending);
        });
    }

    [Fact]
    public void A_request_for_the_panel_already_on_screen_is_delivered_at_once_without_a_second_activation()
    {
        UiThread.Run(() =>
        {
            var view = Show<ProbeAView>(out _);
            _log.Clear();
            var payload = new AuditPreset("last-hour");

            Inbox.Request("PROBE-A", payload);

            Assert.Equal(new[] { "A:accept" }, _log);
            Assert.Same(payload, Assert.Single(Vm(view).Received));
            Assert.Null(Inbox.Pending);
        });
    }

    [Fact]
    public void A_request_is_delivered_once_not_again_when_the_panel_is_reactivated()
    {
        UiThread.Run(() =>
        {
            Inbox.Request("probe-a", new LogsPreset("errors"));
            var view = Show<ProbeAView>(out _);
            Assert.Single(Vm(view).Received);

            // The window goes to the tray and comes back: the panel deactivates and reactivates, and hears nothing new.
            _catalog.SetWindowInteractive(false);
            _catalog.SetWindowInteractive(true);

            Assert.Equal(new[] { "A:activated", "A:accept", "A:deactivated", "A:activated" }, _log);
            Assert.Single(Vm(view).Received);
        });
    }

    [Fact]
    public void A_second_request_replaces_one_that_was_never_delivered()
    {
        UiThread.Run(() =>
        {
            Inbox.Request("probe-a", new AuditPreset("first"));
            var latest = new AuditPreset("second");
            Inbox.Request("probe-b", latest);

            // A comes up first and finds nothing waiting: its request was replaced.
            var a = Show<ProbeAView>(out var hostA);
            Assert.Empty(Vm(a).Received);
            Assert.Equal("probe-b", Inbox.Pending!.PanelId);

            Hide(hostA);
            var b = Show<ProbeBView>(out _);
            Assert.Same(latest, Assert.Single(Vm(b).Received));
            Assert.Null(Inbox.Pending);
        });
    }

    [Fact]
    public void Two_requests_for_the_same_panel_deliver_only_the_later_payload()
    {
        UiThread.Run(() =>
        {
            Inbox.Request("probe-a", new AlertsFilter(AuditSeverity.Low));
            var latest = new AlertsFilter(AuditSeverity.Critical, AlertsFilter.KindBlocks);
            Inbox.Request("probe-a", latest);

            var view = Show<ProbeAView>(out _);

            Assert.Same(latest, Assert.Single(Vm(view).Received));
        });
    }

    [Fact]
    public void A_request_waits_while_another_panel_is_on_screen_and_is_not_given_to_it()
    {
        UiThread.Run(() =>
        {
            var b = Show<ProbeBView>(out var hostB);
            var payload = new AuditPreset("errors");

            Inbox.Request("probe-a", payload);

            Assert.Empty(Vm(b).Received);
            Assert.Equal("probe-a", Inbox.Pending!.PanelId);

            // The operator (or the shell) switches: the request is still there, and is delivered to its panel only.
            Hide(hostB);
            var a = Show<ProbeAView>(out _);

            Assert.Same(payload, Assert.Single(Vm(a).Received));
            Assert.Empty(Vm(b).Received);
        });
    }

    [Fact]
    public void A_request_for_a_panel_in_a_hidden_window_waits_for_the_window()
    {
        UiThread.Run(() =>
        {
            _catalog.SetWindowInteractive(false);
            var view = Show<ProbeAView>(out _);
            var payload = new LogsPreset("gateway");

            Inbox.Request("probe-a", payload);

            Assert.False(Vm(view).IsActive);
            Assert.Empty(Vm(view).Received);
            Assert.NotNull(Inbox.Pending);

            _catalog.SetWindowInteractive(true);

            Assert.True(Vm(view).IsActive);
            Assert.Same(payload, Assert.Single(Vm(view).Received));
            Assert.Null(Inbox.Pending);
        });
    }

    [Fact]
    public void A_request_without_a_payload_only_shows_the_panel_and_is_still_consumed()
    {
        UiThread.Run(() =>
        {
            Inbox.Request("probe-a");

            var view = Show<ProbeAView>(out var host);

            Assert.Empty(Vm(view).Received);
            Assert.DoesNotContain("A:accept", _log);
            Assert.Null(Inbox.Pending);

            // ...and the next visit finds nothing waiting.
            Hide(host);
            _ = Show<ProbeAView>(out _);
            Assert.Empty(Vm(view).Received);
        });
    }

    [Fact]
    public void A_panel_that_takes_no_payloads_ignores_one_and_the_request_is_consumed()
    {
        UiThread.Run(() =>
        {
            Inbox.Request("probe-c", new AlertsFilter(AuditSeverity.High));

            var view = Show<ProbeCView>(out _);

            Assert.IsType<PlainProbeViewModel>(view.DataContext);
            Assert.Equal(new[] { "C:activated" }, _log);
            Assert.Null(Inbox.Pending);
        });
    }

    [Fact]
    public void A_panel_that_throws_from_Accept_does_not_break_activation_or_the_shell()
    {
        UiThread.Run(() =>
        {
            var view = (ProbeAView)_catalog.GetPage(typeof(ProbeAView))!;
            Vm(view).ThrowOnAccept = true;
            Inbox.Request("probe-a", new AuditPreset("boom"));

            using var host = new OffscreenHost(view, 100, 100);

            Assert.True(Vm(view).IsActive);
            Assert.Single(Vm(view).Received);
            Assert.Null(Inbox.Pending);
        });
    }

    [Fact]
    public void A_request_for_a_panel_that_does_not_exist_is_dropped_not_held_forever()
    {
        UiThread.Run(() =>
        {
            Inbox.Request("no-such-panel", new AuditPreset("x"));

            Assert.Null(Inbox.Pending);

            // It did not disturb the next, real request.
            Inbox.Request("probe-a", new AuditPreset("y"));
            Assert.Equal("probe-a", Inbox.Pending!.PanelId);
        });
    }

    [Fact]
    public void The_payload_goes_to_the_panel_the_request_named_when_both_probe_panels_are_built()
    {
        UiThread.Run(() =>
        {
            var a = Show<ProbeAView>(out var hostA);
            var b = Show<ProbeBView>(out var hostB);
            Hide(hostB);
            _log.Clear();

            Inbox.Request("probe-b", new AuditPreset("for-b"));
            Assert.Empty(Vm(b).Received);

            Hide(hostA);
            _ = Show<ProbeBView>(out _);

            Assert.Empty(Vm(a).Received);
            Assert.Equal(new AuditPreset("for-b"), Assert.Single(Vm(b).Received));
        });
    }

    [Fact]
    public void A_panel_can_ask_for_another_through_its_base_class()
    {
        UiThread.Run(() =>
        {
            var a = Show<ProbeAView>(out _);
            var payload = new AlertsFilter(AuditSeverity.Critical);

            Vm(a).Ask("probe-b", payload);

            Assert.Equal("probe-b", Inbox.Pending!.PanelId);
            Assert.Same(payload, Inbox.Pending.Payload);
        });
    }

    [Fact]
    public void The_window_opens_on_the_panel_a_request_is_waiting_for_else_the_default()
    {
        UiThread.Run(() =>
        {
            Assert.Equal("probe-a", _catalog.InitialPanel.Id);

            Inbox.Request("probe-b", new AuditPreset("x"));
            Assert.Equal("probe-b", _catalog.InitialPanel.Id);

            Inbox.Request("no-such-panel");
            Assert.Equal("probe-a", _catalog.InitialPanel.Id);
        });
    }

    [Fact]
    public void The_shell_view_model_can_raise_a_request_with_or_without_a_payload()
    {
        using var shell = new MainWindowViewModel(_services);

        shell.RequestNavigation("probe-a", new AlertsFilter(AuditSeverity.Critical));
        Assert.Equal(new AlertsFilter(AuditSeverity.Critical), _services.Navigation.Pending!.Payload);

        shell.OpenPanelCommand.Execute("probe-b");
        Assert.Equal("probe-b", _services.Navigation.Pending!.PanelId);
        Assert.Null(_services.Navigation.Pending.Payload);

        // A blank parameter does nothing rather than throwing out of a binding.
        shell.OpenPanelCommand.Execute("  ");
        shell.OpenPanelCommand.Execute(null);
        Assert.Equal("probe-b", _services.Navigation.Pending.PanelId);
    }
}
