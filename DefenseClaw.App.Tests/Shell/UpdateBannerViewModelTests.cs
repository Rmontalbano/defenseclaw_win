using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The shell view-model's update banner (CUST-206): what it shows for what <see cref="UpdateWatcher"/> knows, and what its three
/// commands do. The release check is a fake; no window opens, no browser starts, nothing reaches GitHub or the real settings file.
/// </summary>
public sealed class UpdateBannerViewModelTests : IDisposable
{
    private const string Repo = "https://github.com/cisco-ai-defense/defenseclaw/";

    private readonly TempDirectory _temp = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _temp.Dispose();
    }

    private sealed class Scene
    {
        public AppServices Services { get; set; } = null!;

        public MainWindowViewModel Shell { get; set; } = null!;

        public List<string> OpenedPages { get; } = new();

        public int Reviews { get; set; }

        /// <summary>What the next release check finds.</summary>
        public UpdateCheckResult Answer { get; set; } = NotNewer();

        public UpdateWatcher Watcher => Services.UpdateWatcher;
    }

    private static UpdateCheckResult NotNewer() =>
        new() { State = UpdateCheckState.UpToDate, InstalledVersion = "0.8.10", LatestVersion = "v0.8.10", HtmlUrl = Repo + "releases/tag/v0.8.10" };

    private static UpdateCheckResult Newer(string latest = "v0.8.11", string? installed = "0.8.10", string? html = null) =>
        new()
        {
            State = UpdateCheckState.UpdateAvailable,
            InstalledVersion = installed,
            LatestVersion = latest,
            HtmlUrl = html ?? Repo + "releases/tag/" + latest,
        };

    /// <summary>A composition whose watcher answers what the scene says, and a shell view-model whose "open" actions are recorders.</summary>
    private Scene NewScene(UpdateCheckResult? answer = null, bool checkFirst = false)
    {
        var scene = new Scene { Answer = answer ?? NotNewer() };
        scene.Services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            updateWatcherFactory: s => new UpdateWatcher(s.Settings, s.Monitor, (_, _) => Task.FromResult(scene.Answer), post: action => action()));
        _disposables.Add(scene.Services);

        if (checkFirst)
        {
            _ = scene.Services.UpdateWatcher.CheckNowAsync().GetAwaiter().GetResult();
        }

        scene.Shell = new MainWindowViewModel(scene.Services, reviewUpdate: () => scene.Reviews++, openReleasePage: scene.OpenedPages.Add);
        _disposables.Add(scene.Shell);
        return scene;
    }

    [Fact]
    public void There_is_no_banner_until_a_newer_release_is_known()
    {
        var scene = NewScene();

        Assert.False(scene.Shell.ShowUpdateBanner);
        Assert.Equal(string.Empty, scene.Shell.UpdateBannerTitle);
        Assert.False(scene.Shell.HasUpdateReleaseNotes);
        Assert.False(scene.Shell.OpenReleaseNotesCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_newer_release_shows_the_banner_with_its_version_and_what_is_installed()
    {
        var scene = NewScene(Newer("v0.8.11", installed: "0.8.10"));

        _ = await scene.Watcher.CheckNowAsync();

        Assert.True(scene.Shell.ShowUpdateBanner);
        Assert.Equal("DefenseClaw 0.8.11 is available", scene.Shell.UpdateBannerTitle);
        Assert.Contains("Installed: 0.8.10.", scene.Shell.UpdateBannerMessage, StringComparison.Ordinal);
        Assert.Contains("Updates window", scene.Shell.UpdateBannerMessage, StringComparison.Ordinal);
        Assert.Contains("review and confirm the upgrade", scene.Shell.UpdateBannerMessage, StringComparison.Ordinal);
        Assert.True(scene.Shell.HasUpdateReleaseNotes);
    }

    [Fact]
    public async Task An_unknown_installed_version_is_left_out_of_the_sentence_rather_than_printed_as_blank()
    {
        var scene = NewScene(Newer(installed: null));

        _ = await scene.Watcher.CheckNowAsync();

        Assert.True(scene.Shell.ShowUpdateBanner);
        Assert.DoesNotContain("Installed", scene.Shell.UpdateBannerMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dashboard_built_after_the_update_was_found_starts_with_the_banner_showing()
    {
        // A tray-only autostart: the watcher finds the release long before the operator opens the dashboard (CUST-176).
        var scene = NewScene(Newer(), checkFirst: true);

        Assert.True(scene.Shell.ShowUpdateBanner);
        Assert.Equal("DefenseClaw 0.8.11 is available", scene.Shell.UpdateBannerTitle);
    }

    [Fact]
    public async Task The_banner_goes_away_when_the_release_is_installed()
    {
        var scene = NewScene(Newer());
        _ = await scene.Watcher.CheckNowAsync();
        Assert.True(scene.Shell.ShowUpdateBanner);

        scene.Answer = new UpdateCheckResult { State = UpdateCheckState.UpToDate, InstalledVersion = "0.8.11", LatestVersion = "v0.8.11" };
        _ = await scene.Watcher.CheckNowAsync();

        Assert.False(scene.Shell.ShowUpdateBanner);
        Assert.False(scene.Shell.HasUpdateReleaseNotes);
    }

    [Fact]
    public async Task Review_update_opens_the_updates_window_and_does_nothing_else()
    {
        var scene = NewScene(Newer());
        _ = await scene.Watcher.CheckNowAsync();

        scene.Shell.ReviewUpdateCommand.Execute(null);

        Assert.Equal(1, scene.Reviews);
        Assert.Empty(scene.OpenedPages);
        Assert.True(scene.Shell.ShowUpdateBanner); // reviewing is not dismissing
    }

    [Fact]
    public async Task Release_notes_open_the_release_page_from_the_repository()
    {
        var scene = NewScene(Newer("v0.8.11"));
        _ = await scene.Watcher.CheckNowAsync();

        Assert.True(scene.Shell.OpenReleaseNotesCommand.CanExecute(null));
        scene.Shell.OpenReleaseNotesCommand.Execute(null);

        Assert.Equal(Repo + "releases/tag/v0.8.11", Assert.Single(scene.OpenedPages));
    }

    [Theory]
    [InlineData("https://evil.example/cisco-ai-defense/defenseclaw/releases/tag/v0.8.11")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("http://github.com/cisco-ai-defense/defenseclaw/releases/tag/v0.8.11")]
    [InlineData("https://github.com@evil.example/cisco-ai-defense/defenseclaw/releases")]
    public async Task A_release_link_that_is_not_in_the_repository_leaves_release_notes_unavailable_and_opens_nothing(string link)
    {
        var scene = NewScene(Newer(html: link));
        _ = await scene.Watcher.CheckNowAsync();

        Assert.True(scene.Shell.ShowUpdateBanner);
        Assert.False(scene.Shell.HasUpdateReleaseNotes);
        Assert.False(scene.Shell.OpenReleaseNotesCommand.CanExecute(null));

        scene.Shell.OpenReleaseNotesCommand.Execute(null);

        Assert.Empty(scene.OpenedPages);
    }

    [Fact]
    public async Task Dismissing_hides_the_banner_for_that_release_and_remembers_it()
    {
        var scene = NewScene(Newer("v0.8.11"));
        _ = await scene.Watcher.CheckNowAsync();

        scene.Shell.DismissUpdateCommand.Execute(null);

        Assert.False(scene.Shell.ShowUpdateBanner);
        Assert.Equal("0.8.11", scene.Services.Settings.Current.Updates.DismissedVersion);

        // The next check of the same release keeps it hidden ...
        _ = await scene.Watcher.CheckNowAsync();
        Assert.False(scene.Shell.ShowUpdateBanner);

        // ... and a newer release shows it again.
        scene.Answer = Newer("v0.8.12");
        _ = await scene.Watcher.CheckNowAsync();
        Assert.True(scene.Shell.ShowUpdateBanner);
        Assert.Equal("DefenseClaw 0.8.12 is available", scene.Shell.UpdateBannerTitle);
    }

    [Fact]
    public async Task A_disposed_shell_no_longer_follows_the_watcher()
    {
        var scene = NewScene(Newer());
        scene.Shell.Dispose();

        _ = await scene.Watcher.CheckNowAsync();

        Assert.False(scene.Shell.ShowUpdateBanner);
    }
}
