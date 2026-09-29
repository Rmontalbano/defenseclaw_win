using DefenseClaw.App.Services;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The dashboard window is built on demand: nothing may build it as a side effect, only a request to show
/// it does, and the exit path must neither build it nor leave one behind. The window is a fake here (the
/// host talks to <see cref="IDashboardWindow"/>), so none of this needs a WPF Application.
/// </summary>
public class DashboardHostTests
{
    private sealed class FakeWindow : IDashboardWindow
    {
        public List<string> Calls { get; } = new();

        public bool IsVisible { get; set; }

        public void ShowAndActivate()
        {
            Calls.Add("show");
            IsVisible = true;
        }

        public void Hide()
        {
            Calls.Add("hide");
            IsVisible = false;
        }

        public void AllowClose() => Calls.Add("allow-close");

        public void Close() => Calls.Add("close");
    }

    /// <summary>A host over a counting factory, plus the windows the factory has handed out.</summary>
    private sealed class Rig
    {
        public Rig(Func<int, FakeWindow>? build = null)
        {
            Host = new DashboardHost(() =>
            {
                Built++;
                var window = build?.Invoke(Built) ?? new FakeWindow();
                Windows.Add(window);
                return window;
            });
        }

        public DashboardHost Host { get; }

        public int Built { get; private set; }

        public List<FakeWindow> Windows { get; } = new();
    }

    [Fact]
    public void Nothing_is_built_until_the_first_show()
    {
        var rig = new Rig();

        Assert.False(rig.Host.IsCreated);
        Assert.Null(rig.Host.Current);
        Assert.Equal(0, rig.Built);
    }

    [Fact]
    public void The_first_show_builds_the_window_and_brings_it_up()
    {
        var rig = new Rig();

        Assert.True(rig.Host.Show());

        Assert.Equal(1, rig.Built);
        Assert.True(rig.Host.IsCreated);
        Assert.Same(rig.Windows[0], rig.Host.Current);
        Assert.Equal(new[] { "show" }, rig.Windows[0].Calls);
    }

    [Fact]
    public void Later_shows_reuse_the_same_window()
    {
        var rig = new Rig();

        _ = rig.Host.Show();
        _ = rig.Host.Show();
        _ = rig.Host.Show();

        Assert.Equal(1, rig.Built);
        Assert.Equal(new[] { "show", "show", "show" }, rig.Windows[0].Calls);
    }

    [Fact]
    public void Hiding_never_builds_the_window()
    {
        var rig = new Rig();

        rig.Host.HideIfVisible();

        Assert.Equal(0, rig.Built);
        Assert.False(rig.Host.IsCreated);
    }

    [Fact]
    public void Hiding_hides_a_visible_window_and_leaves_a_hidden_one_alone()
    {
        var rig = new Rig();
        _ = rig.Host.Show();

        rig.Host.HideIfVisible();
        rig.Host.HideIfVisible();

        Assert.Equal(new[] { "show", "hide" }, rig.Windows[0].Calls);
        Assert.True(rig.Host.IsCreated);
    }

    [Fact]
    public void Closing_for_exit_lets_the_close_through_before_closing()
    {
        var rig = new Rig();
        _ = rig.Host.Show();

        rig.Host.CloseForExit();

        Assert.Equal(new[] { "show", "allow-close", "close" }, rig.Windows[0].Calls);
    }

    [Fact]
    public void Closing_for_exit_with_no_window_builds_nothing()
    {
        var rig = new Rig();

        rig.Host.CloseForExit();

        Assert.Equal(0, rig.Built);
        Assert.False(rig.Host.IsCreated);
    }

    [Fact]
    public void Once_exiting_a_show_is_refused_and_builds_nothing()
    {
        // A second launch signalling while the app shuts down must not conjure a window nobody will close.
        var rig = new Rig();
        rig.Host.CloseForExit();

        Assert.False(rig.Host.Show());

        Assert.Equal(0, rig.Built);
        Assert.Null(rig.Host.Current);
    }

    [Fact]
    public void Once_exiting_a_show_does_not_bring_back_a_window_that_was_closed()
    {
        var rig = new Rig();
        _ = rig.Host.Show();
        rig.Host.CloseForExit();

        Assert.False(rig.Host.Show());

        Assert.Equal(1, rig.Built);
        Assert.Equal(new[] { "show", "allow-close", "close" }, rig.Windows[0].Calls);
    }

    [Fact]
    public void A_factory_that_throws_leaves_nothing_cached_so_the_next_show_tries_again()
    {
        var rig = new Rig(attempt => attempt == 1 ? throw new InvalidOperationException("XAML fault") : new FakeWindow());

        var failure = Assert.Throws<InvalidOperationException>(() => rig.Host.Show());
        Assert.Equal("XAML fault", failure.Message);
        Assert.False(rig.Host.IsCreated);

        Assert.True(rig.Host.Show());

        Assert.Equal(2, rig.Built);
        Assert.True(rig.Host.IsCreated);
    }

    [Fact]
    public void The_dashboard_is_the_window_the_host_builds()
    {
        Assert.True(typeof(IDashboardWindow).IsAssignableFrom(typeof(MainWindow)));
    }

    [Fact]
    public void The_host_needs_a_factory()
    {
        Assert.Throws<ArgumentNullException>(() => new DashboardHost(null!));
    }
}
