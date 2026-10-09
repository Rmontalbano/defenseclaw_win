using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// Settings → Connection → Installation (CUST-308): which DefenseClaw this run drives, what chose it, whether the app may change it, why not, and
/// the developer selector's folder. Also the two Settings controls the installation turns off - the automatic gateway start, and the sentence
/// about upgrading the runtime. The installations are synthetic (<see cref="TestInstallations"/>); the page is the real view-model over a scratch
/// directory and a stand-in for the registry, dialogs and Explorer (<see cref="FakePlatform"/>).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class InstallationSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(InstallationContext? installation = null, Func<AppServices, UpdateWatcher>? watcher = null)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path, installation),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            updateWatcherFactory: watcher);
        _services.Add(services);
        return services;
    }

    private static SettingsPanelViewModel Build(AppServices services, FakePlatform? platform = null) =>
        UiThread.Run(() => new SettingsPanelViewModel(services, hooks: null, (platform ?? new FakePlatform()).Build()));

    // ------------------------------------------------------------------ the rows

    [Fact]
    public void The_users_own_installation_says_so_and_has_no_reason()
    {
        var model = Build(Create());

        Assert.Equal("User default", model.InstallationSelectedBy);
        Assert.Equal("Unmanaged — setup changes allowed", model.InstallationAccessText);
        Assert.Equal("Ok", model.InstallationAccessTone);
        Assert.Equal(string.Empty, model.InstallationReason);
        Assert.False(model.HasInstallationReason);
        Assert.Equal("None (automatic)", model.InstallationOverride);
    }

    [Fact]
    public void A_managed_layout_is_read_only_and_names_the_profile_and_the_administrator()
    {
        var model = Build(Create(TestInstallations.Managed(_temp.Path)));

        Assert.Equal("Managed installation (Cisco Secure Client)", model.InstallationSelectedBy);
        Assert.Equal("Managed enterprise — read only", model.InstallationAccessText);
        Assert.Equal("Medium", model.InstallationAccessTone);
        Assert.Equal(TestInstallations.ManagedReason, model.InstallationReason);
        Assert.True(model.HasInstallationReason);
        Assert.Equal("None (automatic)", model.InstallationOverride);
    }

    [Fact]
    public void A_config_that_says_managed_is_read_only_whatever_chose_the_folder()
    {
        var model = Build(Create(TestInstallations.ManagedAt(_temp.Path)));

        Assert.Equal("DEFENSECLAW_HOME", model.InstallationSelectedBy);
        Assert.Equal("Managed enterprise — read only", model.InstallationAccessText);
        Assert.Equal(TestInstallations.ManagedReason, model.InstallationReason);
    }

    [Fact]
    public void An_invalid_selection_is_read_only_with_its_own_reason_and_the_strongest_tone()
    {
        var model = Build(Create(TestInstallations.Invalid()));

        Assert.Equal("DEFENSECLAW_HOME", model.InstallationSelectedBy);
        Assert.Equal("Invalid installation selection — read only", model.InstallationAccessText);
        Assert.Equal("High", model.InstallationAccessTone);
        Assert.Contains("absolute path", model.InstallationReason, StringComparison.Ordinal);
        Assert.True(model.HasInstallationReason);
    }

    [Fact]
    public void The_developer_selector_is_the_override_row_and_the_installation_stays_writable()
    {
        var model = Build(Create(TestInstallations.DeveloperRuntime()));

        Assert.Equal("Developer runtime selector (Settings → Advanced)", model.InstallationSelectedBy);
        Assert.Equal("Unmanaged — setup changes allowed", model.InstallationAccessText);
        Assert.Equal(TestInstallations.DeveloperHome, model.InstallationOverride);
        Assert.False(model.HasInstallationReason);
    }

    [Fact]
    public void The_block_is_drawn_again_when_the_installation_changes_while_the_page_is_open()
    {
        var services = Create();
        var model = Build(services);
        Assert.Equal("Unmanaged — setup changes allowed", model.InstallationAccessText);

        UiThread.Run(() =>
        {
            model.SetActive(true);
            try
            {
                services.Installation.Replace(TestInstallations.ManagedByConfigMode());

                Assert.Equal("Managed enterprise — read only", model.InstallationAccessText);
                Assert.Equal(TestInstallations.ManagedReason, model.InstallationReason);
                Assert.True(model.HasInstallationReason);
                Assert.False(model.CanChangeAutoStart);

                services.Installation.Replace(TestInstallations.UserDefault());

                Assert.Equal("Unmanaged — setup changes allowed", model.InstallationAccessText);
                Assert.False(model.HasInstallationReason);
                Assert.True(model.CanChangeAutoStart);
            }
            finally
            {
                model.SetActive(false);
            }
        });
    }

    // ------------------------------------------------------------------ the automatic gateway start

    [Fact]
    public void The_automatic_start_is_on_offer_on_a_writable_installation()
    {
        var model = Build(Create());

        Assert.True(model.CanChangeAutoStart);
        Assert.Null(model.AutoStartBlockedReason);
        Assert.Null(model.AutoStartToolTip);
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("invalid")]
    public void A_read_only_installation_turns_the_automatic_start_off_with_the_reason_and_arming_it_anyway_asks_nothing_and_saves_nothing(string which)
    {
        var context = which == "managed" ? TestInstallations.ManagedAt(_temp.Path) : TestInstallations.InvalidAt(_temp.Path);
        var services = Create(context);
        var platform = new FakePlatform { ConsentAnswer = true };
        var model = Build(services, platform);

        Assert.False(model.CanChangeAutoStart);
        Assert.Equal(context.BlockedReason, model.AutoStartBlockedReason);
        Assert.Equal(context.BlockedReason, model.AutoStartToolTip);

        UiThread.Run(() => model.AutoStartGateway = true);

        // The switch cannot be turned on from code either: no review of the start command is shown, and nothing is written.
        Assert.False(model.AutoStartGateway);
        Assert.Empty(platform.ConsentReviews);
        Assert.False(services.Settings.Current.Startup.GatewayAutoStart);
        Assert.False(AppSettingsStore.OpenFresh(services.Settings.FilePath).Current.Startup.GatewayAutoStart);
    }

    // ------------------------------------------------------------------ the update sentence

    private static Func<AppServices, UpdateWatcher> AvailableRelease() =>
        services => new UpdateWatcher(
            services.Settings,
            services.Monitor,
            (_, _) => Task.FromResult(new UpdateCheckResult
            {
                State = UpdateCheckState.UpdateAvailable,
                InstalledVersion = "0.8.10",
                LatestVersion = "v0.8.11",
                CheckedAt = DateTimeOffset.UtcNow,
            }),
            post: action => action());

    [Fact]
    public async Task On_a_writable_installation_an_available_release_is_reviewed_in_Updates()
    {
        var model = Build(Create(watcher: AvailableRelease()));

        await UiThread.Run(() => model.CheckNowCommand.ExecuteAsync(null));

        Assert.Equal("DefenseClaw 0.8.11 is available", model.UpdateStatusText);
        Assert.Contains("to review it", model.UpdateDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("not upgraded from here", model.UpdateDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_managed_installation_an_available_release_is_news_but_not_an_upgrade_from_here()
    {
        var model = Build(Create(TestInstallations.Managed(_temp.Path), AvailableRelease()));

        await UiThread.Run(() => model.CheckNowCommand.ExecuteAsync(null));

        Assert.Equal("DefenseClaw 0.8.11 is available", model.UpdateStatusText);
        Assert.Contains("to read about it", model.UpdateDetail, StringComparison.Ordinal);
        Assert.Contains("The runtime is not upgraded from here.", model.UpdateDetail, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, model.UpdateDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("to review it", model.UpdateDetail, StringComparison.Ordinal);
    }
}
