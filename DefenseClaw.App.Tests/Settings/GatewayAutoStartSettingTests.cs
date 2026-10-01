using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// The Settings page's "Start the gateway automatically" switch (CUST-211): off by default, turning it on shows the one-time review of
/// <c>defenseclaw-gateway start</c> and writes only once that is confirmed, and declining leaves it off.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class GatewayAutoStartSettingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public GatewayAutoStartSettingTests() =>
        _services = AppServices.CreateIsolated(TestServices.IsolatedPaths(_temp.Path), claudeSettingsPath: _temp.File("claude-settings.json"));

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private SettingsPanelViewModel Build(FakePlatform platform) =>
        UiThread.Run(() => new SettingsPanelViewModel(_services, hooks: null, platform.Build()));

    private bool Saved => AppSettingsStore.OpenFresh(_services.Settings.FilePath).Current.Startup.GatewayAutoStart;

    [Fact]
    public void It_is_off_by_default()
    {
        var platform = new FakePlatform();
        var model = Build(platform);

        Assert.False(model.AutoStartGateway);
        Assert.False(_services.Settings.Current.Startup.GatewayAutoStart);
        Assert.Empty(platform.ConsentReviews);
    }

    [Fact]
    public void Turning_it_on_shows_the_review_of_the_start_command_and_saves_once_confirmed()
    {
        var platform = new FakePlatform { ConsentAnswer = true };
        var model = Build(platform);

        UiThread.Run(() => model.AutoStartGateway = true);

        var review = Assert.Single(platform.ConsentReviews);
        Assert.Equal(new[] { "start" }, Assert.Single(review.Steps).Argv);
        Assert.Equal(GatewayAutoStart.PolicyText, review.Summary);
        Assert.True(model.AutoStartGateway);
        Assert.True(_services.Settings.Current.Startup.GatewayAutoStart);
        Assert.True(Saved);
    }

    [Fact]
    public void Declining_the_review_leaves_it_off()
    {
        var platform = new FakePlatform { ConsentAnswer = false };
        var model = Build(platform);

        UiThread.Run(() => model.AutoStartGateway = true);

        Assert.Single(platform.ConsentReviews);
        Assert.False(model.AutoStartGateway);
        Assert.False(_services.Settings.Current.Startup.GatewayAutoStart);
        Assert.False(Saved);
    }

    [Fact]
    public void Turning_it_off_needs_no_review()
    {
        var platform = new FakePlatform();
        var model = Build(platform);
        UiThread.Run(() => model.AutoStartGateway = true);
        platform.ConsentReviews.Clear();

        UiThread.Run(() => model.AutoStartGateway = false);

        Assert.Empty(platform.ConsentReviews);
        Assert.False(_services.Settings.Current.Startup.GatewayAutoStart);
    }

    [Fact]
    public void A_saved_setting_is_shown_without_asking_again()
    {
        Assert.True(_services.Settings.Update(s => s with { Startup = s.Startup with { GatewayAutoStart = true } }));
        var platform = new FakePlatform();

        var model = Build(platform);

        Assert.True(model.AutoStartGateway);
        Assert.Empty(platform.ConsentReviews);
    }
}
