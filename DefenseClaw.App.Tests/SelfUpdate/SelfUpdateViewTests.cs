using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates.SelfUpdate;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.App.Views.Updates;
using UiButton = Wpf.Ui.Controls.Button;

namespace DefenseClaw.App.Tests.SelfUpdate;

/// <summary>The banner and the review window load (every resource they name exists) and wire their commands. Rendered offscreen; no window is shown.</summary>
[Collection(UiCollection.Name)]
public sealed class SelfUpdateViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices NewServices(AppUpdateCheck answer)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            appUpdateFactory: s =>
            {
                var store = new SelfUpdateStore(_temp.File("self.json"));
                var watcher = new AppUpdateWatcher((_, _) => Task.FromResult(answer), store, post: a => a());
                return new SelfUpdateService(watcher, store, installer: null, s.AppRestart, new AppVersion(1, 0, 0, null), string.Empty);
            });
        _disposables.Add(services);
        _ = services.AppUpdate.Watcher.CheckNowAsync(manual: false).GetAwaiter().GetResult();
        return services;
    }

    private static AppUpdateCheck Available() => new()
    {
        State = AppUpdateState.UpdateAvailable,
        Running = "1.0.0",
        Release = SelfUpdateKit.Release("1.2.0", notes: "## What's new\n- Self-update"),
        Message = "available",
    };

    [Fact]
    public void No_release_yet_is_a_neutral_status_and_never_raises_the_banner()
    {
        var services = NewServices(new AppUpdateCheck { State = AppUpdateState.NoRelease, Running = "1.0.0", Message = "No release of DefenseClaw for Windows has been published yet." });

        UiThread.Run(() =>
        {
            var vm = services.AppUpdate.ViewModel;
            vm.Refresh();
            Assert.Equal("No release yet", vm.StatusText);
            Assert.Equal("Neutral", vm.StatusTone);
            Assert.Equal("No release of DefenseClaw for Windows has been published yet.", vm.DetailText);
            Assert.False(vm.ShowBanner);
            Assert.False(vm.CanReview);
        });
    }

    [Fact]
    public void A_real_failure_still_reads_could_not_check_in_the_failure_tone()
    {
        var services = NewServices(new AppUpdateCheck { State = AppUpdateState.Failed, Message = "GitHub was unreachable: no route", LiveFailed = true });

        UiThread.Run(() =>
        {
            var vm = services.AppUpdate.ViewModel;
            vm.Refresh();
            Assert.Equal("Could not check", vm.StatusText);
            Assert.Equal("High", vm.StatusTone);
            Assert.False(vm.ShowBanner);
        });
    }

    [Fact]
    public void The_banner_offers_three_named_choices_and_collapses_when_there_is_nothing()
    {
        var services = NewServices(Available());

        UiThread.Run(() =>
        {
            var banner = new SelfUpdateBanner { DataContext = services.AppUpdate.ViewModel };
            using var host = new OffscreenHost(new StackPanel { Children = { banner } }, 1000, 140);

            var names = VisualTree.Descendants<UiButton>(banner).Select(b => System.Windows.Automation.AutomationProperties.GetName(b)).ToArray();
            Assert.Equal(["Update now", "Later", "Skip this version"], names);
            Assert.Equal("DefenseClaw for Windows 1.2.0 is available", services.AppUpdate.ViewModel.BannerTitle);
            Assert.Contains("What's new", services.AppUpdate.ViewModel.BannerMessage);

            services.AppUpdate.ViewModel.LaterCommand.Execute(null);
            Assert.False(services.AppUpdate.ViewModel.ShowBanner);
        });
    }

    [Fact]
    public void The_review_window_loads_and_follows_the_view_model()
    {
        var services = NewServices(Available());

        UiThread.Run(() =>
        {
            var window = (Window)Activator.CreateInstance(typeof(SelfUpdateWindow), BindingFlags.Instance | BindingFlags.NonPublic, null, [services.AppUpdate.ViewModel], null)!;
            try
            {
                Assert.Equal(services.AppUpdate.ViewModel, window.DataContext);
                Assert.Contains("not Authenticode-signed", services.AppUpdate.ViewModel.TrustText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Update_now_without_an_installer_fails_with_a_reason_instead_of_doing_nothing()
    {
        var services = NewServices(Available());

        var vm = services.AppUpdate.ViewModel;
        var opened = 0;
        UiThread.Run(() =>
        {
            vm.ReviewRequested += (_, _) => opened++;
            vm.UpdateNowCommand.Execute(null);
        });

        UiThread.WaitFor(() => vm.Phase == SelfUpdatePhase.Failed, "the review to report why it cannot install");

        UiThread.Run(() =>
        {
            Assert.Equal(1, opened);
            Assert.Contains("not available", vm.ErrorText);
            Assert.True(vm.HasError);
            Assert.False(vm.InstallCommand.CanExecute(null));
        });
    }
}
