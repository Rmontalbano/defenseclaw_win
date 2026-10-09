using System.Reflection;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// The Settings page's view-model (CUST-203) over a scratch data directory and a settings file of its own: every switch writes the store the
/// moment it flips, the slider writes once it is still, what is shown follows other writers (the tray flyout's Pause), the token is never
/// shown, the CLI override is validated before anything is saved, and nothing the page does can reach the real Run key, a dialog, Explorer or
/// the clipboard (<see cref="FakePlatform"/>).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsPanelViewModelTests : IDisposable
{
    private const string Secret = "tok-SECRET-0123456789-abcdef";

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

    private AppServices Create(string? configYaml = null, Func<AppServices, UpdateWatcher>? watcher = null, string? settingsPath = null, DefenseClawPaths? paths = null)
    {
        if (configYaml is not null)
        {
            _ = _temp.WriteFile("config.yaml", configYaml);
        }

        var services = AppServices.CreateIsolated(
            paths ?? TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            settingsPath: settingsPath,
            updateWatcherFactory: watcher);
        _services.Add(services);
        return services;
    }

    private static SettingsPanelViewModel Build(AppServices services, FakePlatform? platform = null, ShellHooks? hooks = null) =>
        UiThread.Run(() => new SettingsPanelViewModel(services, hooks, (platform ?? new FakePlatform()).Build()));

    private static AppSettings Saved(AppServices services) => AppSettingsStore.OpenFresh(services.Settings.FilePath).Current;

    // ------------------------------------------------------------------ the store, both ways

    [Fact]
    public void A_fresh_install_shows_the_defaults()
    {
        var model = Build(Create());

        Assert.Equal("Settings", model.Title);
        Assert.Equal(5, model.HealthIntervalSeconds);
        Assert.Equal("5 s", model.HealthIntervalText);
        Assert.False(model.IsMonitoringPaused);
        Assert.True(model.NotifyCritical);
        Assert.True(model.NotifyHigh);
        Assert.True(model.NotifyGateway);
        Assert.True(model.CloseToTray);
        Assert.False(model.RememberLastPanel);
        Assert.Null(model.CliOverridePath);
        Assert.False(model.HasSaveProblem);
        Assert.Equal(2, model.HealthIntervalMinimum);
        Assert.Equal(60, model.HealthIntervalMaximum);
    }

    [Fact]
    public void What_the_store_holds_is_what_the_page_shows()
    {
        var services = Create();
        Assert.True(services.Settings.Update(s => s with
        {
            Monitoring = s.Monitoring with { HealthIntervalSeconds = 17, Paused = true },
            Notifications = s.Notifications with { Critical = false, High = false, Gateway = false },
            Startup = s.Startup with { CloseToTray = false, RememberLastPanel = true },
        }));

        var model = Build(services);

        Assert.Equal(17, model.HealthIntervalSeconds);
        Assert.True(model.IsMonitoringPaused);
        Assert.False(model.NotifyCritical);
        Assert.False(model.NotifyHigh);
        Assert.False(model.NotifyGateway);
        Assert.False(model.CloseToTray);
        Assert.True(model.RememberLastPanel);
    }

    [Fact]
    public void Each_switch_writes_the_store_and_the_file_the_moment_it_flips()
    {
        var services = Create();
        var model = Build(services);

        model.NotifyCritical = false;
        Assert.False(services.Settings.Current.Notifications.Critical);
        Assert.False(Saved(services).Notifications.Critical);

        model.NotifyHigh = false;
        Assert.False(Saved(services).Notifications.High);

        model.NotifyGateway = false;
        Assert.False(Saved(services).Notifications.Gateway);

        model.CloseToTray = false;
        Assert.False(Saved(services).Startup.CloseToTray);

        model.RememberLastPanel = true;
        Assert.True(Saved(services).Startup.RememberLastPanel);

        // Only its own switch changes: the others are as they were.
        var saved = Saved(services);
        Assert.True(saved.Monitoring.HealthIntervalSeconds == 5 && !saved.Monitoring.Paused);
        Assert.False(model.HasSaveProblem);

        model.NotifyCritical = true;
        Assert.True(Saved(services).Notifications.Critical);
    }

    [Fact]
    public void Showing_the_store_does_not_write_it_back()
    {
        var services = Create();
        var changes = 0;
        services.Settings.Changed += (_, _) => changes++;

        var model = Build(services);
        UiThread.Run(() => model.SetActive(true));
        UiThread.Run(() => model.SetActive(false));

        Assert.Equal(0, changes);
        Assert.False(File.Exists(services.Settings.FilePath));
    }

    [Fact]
    public void The_page_follows_a_change_made_elsewhere_while_it_is_on_screen_and_stops_when_it_is_not()
    {
        var services = Create();
        var model = Build(services);
        UiThread.Run(() => model.SetActive(true));

        // The tray flyout's Pause, and a hand edit of the file, reach the page without it asking (on the UI thread, wherever they were made).
        Assert.True(services.Monitor.SetPaused(true));
        UiThread.WaitFor(() => model.IsMonitoringPaused, "the page to show the pause", timeoutMilliseconds: 5_000);

        Assert.True(services.Settings.Update(s => s with { Notifications = s.Notifications with { High = false } }));
        UiThread.WaitFor(() => !model.NotifyHigh, "the page to show the HIGH switch off", timeoutMilliseconds: 5_000);

        // Off screen it is not listening (and so not holding the store's subscriber list).
        UiThread.Run(() => model.SetActive(false));
        Assert.True(services.Monitor.SetPaused(false));
        UiThread.Run(UiThread.Settle);
        Assert.True(model.IsMonitoringPaused);

        // Back on screen it catches up at once.
        UiThread.Run(() => model.SetActive(true));
        Assert.False(model.IsMonitoringPaused);
    }

    [Fact]
    public void A_write_the_file_refuses_is_said_at_the_top_and_clears_with_the_next_good_one()
    {
        // A folder where the settings file should be: it cannot be read, so nothing can be saved.
        var blocked = _temp.File("blocked-settings.json");
        _ = Directory.CreateDirectory(blocked);
        var model = Build(Create(settingsPath: blocked));

        model.NotifyCritical = false;

        Assert.True(model.HasSaveProblem);
        Assert.Contains("Could not save", model.SaveProblem, StringComparison.Ordinal);

        // A healthy store has none.
        var healthy = Build(Create());
        healthy.NotifyCritical = false;
        Assert.False(healthy.HasSaveProblem);
    }

    // ------------------------------------------------------------------ monitoring

    [Fact]
    public void The_slider_is_saved_once_it_is_committed_clamped_to_its_range_and_the_monitor_follows()
    {
        var services = Create();
        var model = Build(services);

        model.HealthIntervalSeconds = 12;
        Assert.Equal("12 s", model.HealthIntervalText);

        // Not written per tick: the store still holds the old value until the slider has been still.
        Assert.Equal(5, services.Settings.Current.Monitoring.HealthIntervalSeconds);

        UiThread.Run(model.CommitHealthInterval);
        Assert.Equal(12, services.Settings.Current.Monitoring.HealthIntervalSeconds);
        Assert.Equal(12, Saved(services).Monitoring.HealthIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(12), services.Monitor.HealthInterval);

        model.HealthIntervalSeconds = 500;
        UiThread.Run(model.CommitHealthInterval);
        Assert.Equal(60, services.Settings.Current.Monitoring.HealthIntervalSeconds);

        model.HealthIntervalSeconds = 0.2;
        UiThread.Run(model.CommitHealthInterval);
        Assert.Equal(2, services.Settings.Current.Monitoring.HealthIntervalSeconds);

        // A half second rounds like any other value.
        model.HealthIntervalSeconds = 7.6;
        UiThread.Run(model.CommitHealthInterval);
        Assert.Equal(8, services.Settings.Current.Monitoring.HealthIntervalSeconds);
    }

    [Fact]
    public void The_slider_saves_itself_once_it_has_been_still_for_the_delay()
    {
        var services = Create();
        var model = Build(services);

        UiThread.Run(() =>
        {
            model.HealthIntervalSeconds = 9;
            model.HealthIntervalSeconds = 10;
            model.HealthIntervalSeconds = 11;
        });

        UiThread.WaitFor(() => services.Settings.Current.Monitoring.HealthIntervalSeconds == 11, "the slider's value to be saved", timeoutMilliseconds: 5_000);
    }

    [Fact]
    public void A_slider_moved_just_before_the_page_goes_away_is_saved_not_lost()
    {
        var services = Create();
        var model = Build(services);
        UiThread.Run(() => model.SetActive(true));

        UiThread.Run(() => model.HealthIntervalSeconds = 30);
        UiThread.Run(() => model.SetActive(false));

        Assert.Equal(30, services.Settings.Current.Monitoring.HealthIntervalSeconds);
    }

    [Fact]
    public void A_slider_being_dragged_is_not_pulled_back_by_someone_elses_write()
    {
        var services = Create();
        var model = Build(services);
        UiThread.Run(() => model.SetActive(true));

        UiThread.Run(() => model.HealthIntervalSeconds = 40);
        Assert.True(services.Monitor.SetPaused(true));
        UiThread.WaitFor(() => model.IsMonitoringPaused, "the page to show the pause", timeoutMilliseconds: 5_000);

        Assert.Equal(40, model.HealthIntervalSeconds);

        UiThread.Run(model.CommitHealthInterval);
        Assert.Equal(40, services.Settings.Current.Monitoring.HealthIntervalSeconds);
    }

    [Fact]
    public void The_pause_switch_is_the_monitors_own_flag_both_ways()
    {
        var services = Create();
        var model = Build(services);

        model.IsMonitoringPaused = true;
        Assert.True(services.Monitor.IsPaused);
        Assert.True(Saved(services).Monitoring.Paused);
        Assert.Contains("paused", model.PauseCaption, StringComparison.OrdinalIgnoreCase);

        model.IsMonitoringPaused = false;
        Assert.False(services.Monitor.IsPaused);
        Assert.False(Saved(services).Monitoring.Paused);
    }

    // ------------------------------------------------------------------ notifications

    [Fact]
    public async Task Reset_seen_alert_history_asks_the_tray_and_says_it_did()
    {
        var calls = 0;
        var hooks = new ShellHooks { ResetSeenAlertHistory = () => { calls++; return Task.CompletedTask; } };
        var model = Build(Create(), hooks: hooks);

        await UiThread.Run(() => model.ResetSeenAlertsCommand.ExecuteAsync(null));

        Assert.Equal(1, calls);
        Assert.Contains("reset", model.ResetSeenMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("announced again", model.ResetSeenMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_seen_alert_history_without_a_tray_says_so_and_a_failing_one_does_not_fault_the_page()
    {
        var bare = Build(Create());
        await UiThread.Run(() => bare.ResetSeenAlertsCommand.ExecuteAsync(null));
        Assert.Contains("Not available", bare.ResetSeenMessage, StringComparison.Ordinal);

        var failing = Build(Create(), hooks: new ShellHooks { ResetSeenAlertHistory = () => throw new InvalidOperationException("the tray is gone") });
        await UiThread.Run(() => failing.ResetSeenAlertsCommand.ExecuteAsync(null));
        Assert.Contains("the tray is gone", failing.ResetSeenMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_notes_are_the_macs_words()
    {
        var model = Build(Create());

        Assert.Equal("Notifications include target and severity only — never prompt or payload contents.", model.NotificationsNote);
    }

    // ------------------------------------------------------------------ startup

    [Fact]
    public async Task Start_with_windows_reads_the_run_entry_when_the_page_comes_up()
    {
        var platform = new FakePlatform { AutostartOn = true };
        var model = Build(Create(), platform);
        Assert.False(model.StartWithWindows);

        await UiThread.Run(model.RefreshMachineFactsAsync);

        Assert.True(model.StartWithWindows);
        Assert.Equal(0, platform.Toggles);
    }

    [Fact]
    public async Task Refresh_picks_up_a_start_with_windows_changed_elsewhere_without_toggling_it()
    {
        var platform = new FakePlatform();
        var model = Build(Create(), platform);
        await UiThread.Run(model.RefreshMachineFactsAsync);
        Assert.False(model.StartWithWindows);

        // The tray menu's "Start with Windows" while the page stays open: F5 shows it.
        platform.AutostartOn = true;
        await UiThread.Run(() => model.RefreshCommand.ExecuteAsync(null));

        Assert.True(model.StartWithWindows);
        Assert.Equal(0, platform.Toggles);
    }

    [Fact]
    public async Task Start_with_windows_goes_through_the_guarded_toggle_and_only_when_it_has_to()
    {
        var platform = new FakePlatform();
        var model = Build(Create(), platform);
        await UiThread.Run(model.RefreshMachineFactsAsync);

        model.StartWithWindows = true;
        Assert.Equal(1, platform.Toggles);
        Assert.True(platform.AutostartOn);
        Assert.True(model.StartWithWindows);
        Assert.False(model.HasAutostartProblem);

        // Changed behind the page's back (Task Manager): asking for what already is does nothing.
        platform.AutostartOn = false;
        model.StartWithWindows = false;
        Assert.Equal(1, platform.Toggles);
    }

    [Fact]
    public async Task A_run_key_that_refuses_leaves_the_switch_where_it_was_with_the_reason_on_the_page()
    {
        var platform = new FakePlatform { AutostartError = "Access to the registry key is denied." };
        var model = Build(Create(), platform);
        await UiThread.Run(model.RefreshMachineFactsAsync);

        model.StartWithWindows = true;

        Assert.False(model.StartWithWindows);
        Assert.True(model.HasAutostartProblem);
        Assert.Equal("Could not change Start with Windows: Access to the registry key is denied.", model.AutostartProblem);

        // The next try that goes through clears it.
        platform.AutostartError = null;
        model.StartWithWindows = true;
        Assert.True(model.StartWithWindows);
        Assert.False(model.HasAutostartProblem);
    }

    [Fact]
    public void Close_to_tray_says_what_the_close_button_will_do()
    {
        var model = Build(Create());

        Assert.Contains("hides it", model.CloseToTrayCaption, StringComparison.Ordinal);

        model.CloseToTray = false;
        Assert.Contains("exits DefenseClaw", model.CloseToTrayCaption, StringComparison.Ordinal);
        Assert.Contains("have not saved", model.CloseToTrayCaption, StringComparison.Ordinal);
        Assert.Contains("upgrade", model.CloseToTrayCaption, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ connection

    [Fact]
    public void The_endpoint_follows_config_yamls_port()
    {
        var model = Build(Create("config_version: 8\ngateway:\n  api_port: 18999\n"));

        Assert.Equal("http://127.0.0.1:18999", model.EndpointText);
    }

    [Fact]
    public void A_token_that_is_found_is_configured_hidden_and_the_value_is_nowhere_on_the_page()
    {
        _ = _temp.WriteFile(".env", $"DC_SETTINGS_TEST_TOKEN={Secret}\n");
        var services = Create("config_version: 8\ngateway:\n  token_env: DC_SETTINGS_TEST_TOKEN\n");
        Assert.True(services.Token.Found);

        var model = Build(services);

        Assert.Equal("configured (hidden)", model.TokenStatus);
        Assert.True(model.TokenFound);
        Assert.Equal("Ok", model.TokenTone);
        Assert.Contains("DC_SETTINGS_TEST_TOKEN", model.TokenDetail, StringComparison.Ordinal);
        Assert.Contains(".env", model.TokenDetail, StringComparison.Ordinal);

        // Not in any string the page can show, however it is reached.
        foreach (var text in EveryString(model))
        {
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_token_that_is_not_found_says_so_and_names_the_variable()
    {
        var model = Build(Create("config_version: 8\ngateway:\n  token_env: DC_SETTINGS_TEST_NO_SUCH_VARIABLE\n"));

        Assert.Equal("not found", model.TokenStatus);
        Assert.False(model.TokenFound);
        Assert.Equal("High", model.TokenTone);
        Assert.Contains("DC_SETTINGS_TEST_NO_SUCH_VARIABLE", model.TokenDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_five_files_are_listed_where_the_app_resolved_them_with_the_data_directorys_source()
    {
        var services = Create();
        var model = Build(services);

        Assert.Equal(
            new[] { "Config file", "Data directory", ".env file", "Audit database", "Gateway log", "Python runtime" },
            model.Files.Select(f => f.Label).ToArray());
        Assert.Equal(
            new[] { services.Paths.ConfigFilePath, services.Paths.DataDirectory, services.Paths.EnvFilePath, services.Paths.AuditDatabasePath, services.Paths.GatewayLogPath, services.Paths.PythonInterpreterPath },
            model.Files.Select(f => f.FullPath).ToArray());

        var data = model.Files[1];
        Assert.True(data.IsDirectory);
        Assert.Equal(services.Paths.DataDirectoryOrigin.Description, data.Note);
        Assert.Contains(data.Note!, data.DetailText, StringComparison.Ordinal);

        Assert.Equal("Copy the Config file path", model.Files[0].CopyAutomationName);
        Assert.Equal("Show the Config file in Explorer", model.Files[0].OpenFolderAutomationName);
        Assert.Equal("Open the Data directory in Explorer", data.OpenFolderAutomationName);
        Assert.Equal($"Config file: {services.Paths.ConfigFilePath}", model.Files[0].ToString());
    }

    [Fact]
    public async Task A_file_that_is_not_there_says_so_once_the_page_has_looked()
    {
        var services = Create("config_version: 8\n");
        var model = Build(services);

        await UiThread.Run(model.RefreshMachineFactsAsync);

        Assert.True(model.Files[0].Exists);
        Assert.True(model.Files[1].Exists);
        Assert.False(model.Files[3].Exists);
        Assert.Contains("Not found on disk", model.Files[3].DetailText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, model.Files[0].DetailText);
    }

    [Fact]
    public async Task The_python_runtime_row_is_the_setup_layouts_runtime_python()
    {
        var bin = Path.Combine(_temp.Path, "Programs", "DefenseClaw", "bin");
        var python = Path.Combine(_temp.Path, "Programs", "DefenseClaw", "runtime", "python", "python.exe");
        TouchFile(python);
        var services = Create(paths: new DefenseClawPaths(dataDirectory: _temp.Path, binDirectory: bin, searchPath: Array.Empty<string>()));
        var model = Build(services);

        await UiThread.Run(model.RefreshMachineFactsAsync);

        var row = model.Files.Single(f => f.Label == "Python runtime");
        Assert.Equal(python, row.FullPath);
        Assert.False(row.IsDirectory);
        Assert.True(row.Exists);
        Assert.Equal("From the Setup install's runtime folder.", row.Note);
        Assert.DoesNotContain("Not found", row.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_python_runtime_row_is_the_installer_scripts_venv_when_there_is_no_setup_runtime()
    {
        var venv = Path.Combine(_temp.Path, ".venv", "Scripts", "python.exe");
        TouchFile(venv);
        var services = Create(paths: new DefenseClawPaths(dataDirectory: _temp.Path, binDirectory: Path.Combine(_temp.Path, "no-such-bin"), searchPath: Array.Empty<string>()));
        var model = Build(services);

        await UiThread.Run(model.RefreshMachineFactsAsync);

        var row = model.Files.Single(f => f.Label == "Python runtime");
        Assert.Equal(venv, row.FullPath);
        Assert.True(row.Exists);
        Assert.Equal("From the installer script's .venv under the data directory.", row.Note);
    }

    [Fact]
    public async Task The_python_runtime_row_says_not_found_when_neither_layout_has_one()
    {
        var services = Create(paths: new DefenseClawPaths(dataDirectory: _temp.Path, binDirectory: Path.Combine(_temp.Path, "no-such-bin"), searchPath: Array.Empty<string>()));
        var model = Build(services);

        await UiThread.Run(model.RefreshMachineFactsAsync);

        var row = model.Files.Single(f => f.Label == "Python runtime");
        Assert.Equal(Path.Combine(_temp.Path, "runtime", "python", "python.exe"), row.FullPath);
        Assert.False(row.Exists);
        Assert.Contains("Not found on disk", row.DetailText, StringComparison.Ordinal);
    }

    private static void TouchFile(string path)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    [Fact]
    public void Copy_puts_the_path_on_the_clipboard_and_says_so_and_a_busy_clipboard_is_reported()
    {
        var platform = new FakePlatform();
        var services = Create();
        var model = Build(services, platform);

        model.Files[0].CopyCommand.Execute(null);
        Assert.Equal(services.Paths.ConfigFilePath, platform.Copied);
        Assert.Equal("Copied the Config file path.", model.ConnectionMessage);

        platform.ClipboardBusy = true;
        model.Files[2].CopyCommand.Execute(null);
        Assert.Contains("another program is holding the clipboard", model.ConnectionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_folder_selects_a_file_that_exists_and_opens_the_folder_of_one_that_does_not()
    {
        var platform = new FakePlatform();
        var services = Create("config_version: 8\n");
        var model = Build(services, platform);

        // The config file is there: Explorer shows it selected.
        await UiThread.Run(() => model.Files[0].OpenFolderCommand.ExecuteAsync(null));
        Assert.Equal((services.Paths.ConfigFilePath, true), platform.Revealed[^1]);

        // The audit database is not, but its folder is: the folder.
        await UiThread.Run(() => model.Files[3].OpenFolderCommand.ExecuteAsync(null));
        Assert.Equal((services.Paths.DataDirectory, false), platform.Revealed[^1]);

        // The data directory opens as itself.
        await UiThread.Run(() => model.Files[1].OpenFolderCommand.ExecuteAsync(null));
        Assert.Equal((services.Paths.DataDirectory, false), platform.Revealed[^1]);
        Assert.Equal(3, platform.Revealed.Count);
    }

    [Fact]
    public async Task Open_folder_for_a_path_that_is_nowhere_opens_nothing_and_says_so()
    {
        var platform = new FakePlatform();
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.File("no-such-data-directory")),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            settingsPath: _temp.File("nowhere-settings.json"));
        _services.Add(services);
        var model = Build(services, platform);

        // Neither the audit database nor the folder it would be in.
        await UiThread.Run(() => model.Files[3].OpenFolderCommand.ExecuteAsync(null));

        Assert.Empty(platform.Revealed);
        Assert.Contains("does not exist", model.ConnectionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void What_explorer_is_handed_is_a_file_to_select_or_a_folder_never_a_file_to_open()
    {
        var folder = _temp.Path;
        var file = _temp.WriteFile("there.txt", "x");

        Assert.Equal((file, true), SettingsPanelViewModel.ResolveRevealTarget(file, isDirectory: false));
        Assert.Equal((folder, false), SettingsPanelViewModel.ResolveRevealTarget(_temp.File("missing.txt"), isDirectory: false));
        Assert.Equal((folder, false), SettingsPanelViewModel.ResolveRevealTarget(folder, isDirectory: true));

        // A directory flagged path that is really a file is not handed to Explorer: it would run it.
        Assert.Equal(((string?)null, false), SettingsPanelViewModel.ResolveRevealTarget(file, isDirectory: true));
        Assert.Equal(((string?)null, false), SettingsPanelViewModel.ResolveRevealTarget(Path.Combine(_temp.File("no-such-folder"), "x.txt"), isDirectory: false));
    }

    [Fact]
    public async Task Reload_config_reads_it_again_and_shows_the_new_endpoint()
    {
        var services = Create("config_version: 8\ngateway:\n  api_port: 18991\n");
        var model = Build(services);
        Assert.Equal("http://127.0.0.1:18991", model.EndpointText);

        _ = _temp.WriteFile("config.yaml", "config_version: 8\ngateway:\n  api_port: 18992\n");
        await UiThread.Run(() => model.ReloadConfigCommand.ExecuteAsync(null));

        Assert.Equal("http://127.0.0.1:18992", model.EndpointText);
        Assert.Equal("config.yaml and .env re-read.", model.ConnectionMessage);
        Assert.False(model.IsReloading);
    }

    [Fact]
    public async Task Reload_config_that_cannot_be_parsed_keeps_the_last_good_one_and_says_why()
    {
        var services = Create("config_version: 8\ngateway:\n  api_port: 18991\n");
        var model = Build(services);

        _ = _temp.WriteFile("config.yaml", "gateway: [unterminated\n  api_port: : :\n");
        await UiThread.Run(() => model.ReloadConfigCommand.ExecuteAsync(null));

        Assert.Equal("http://127.0.0.1:18991", model.EndpointText);
        Assert.StartsWith("config.yaml could not be applied; the last good configuration is still in use.", model.ConnectionMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the CLI override

    private string MakeFile(string relative)
    {
        var path = _temp.File(relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not really a program");
        return path;
    }

    [Fact]
    public async Task With_nothing_chosen_the_cli_is_looked_up_and_a_missing_one_says_how_to_fix_it()
    {
        var model = Build(Create());
        Assert.False(model.HasCliOverride);
        Assert.Equal("Automatic: PATH, then the install directory", model.CliOverrideDisplay);

        await UiThread.Run(model.RefreshMachineFactsAsync);

        // The isolated paths have an empty PATH and no install directory.
        Assert.False(model.CliFound);
        Assert.StartsWith("Not found", model.CliResolvedPath, StringComparison.Ordinal);
        Assert.Contains("choose defenseclaw.exe", model.CliSourceNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chosen_cli_is_validated_saved_handed_to_the_paths_and_shown_as_the_one_in_use()
    {
        var services = Create();
        var model = Build(services);
        var exe = MakeFile(@"tools\defenseclaw.exe");

        UiThread.Run(() => model.ApplyCliOverride(exe));
        await model.LastCliRefresh;
        await UiThread.Run(model.RefreshMachineFactsAsync);

        Assert.Null(model.CliOverrideProblem);
        Assert.Equal(exe, model.CliOverridePath);
        Assert.True(model.HasCliOverride);
        Assert.Equal(exe, Saved(services).Connection.CliPathOverride);

        // Every lookup of the CLI in the app now answers with it: what runs, and what every review shows.
        Assert.Equal(exe, services.Paths.CliPathOverride);
        Assert.Equal(exe, services.Paths.CliPath);
        Assert.Equal(exe, await services.Paths.FindExecutableAsync("defenseclaw"));

        Assert.True(model.CliFound);
        Assert.Equal(exe, model.CliResolvedPath);
        Assert.Contains("The file you chose", model.CliSourceNote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrong-name", "must be named defenseclaw.exe")]
    [InlineData("missing", "does not exist")]
    [InlineData("relative", "full path")]
    public void A_file_that_fails_validation_is_refused_with_the_reason_and_nothing_is_saved(string kind, string reason)
    {
        var services = Create();
        var model = Build(services);
        var path = kind switch
        {
            "wrong-name" => MakeFile(@"tools\notepad.exe"),
            "missing" => _temp.File(@"tools\no-such-dir\defenseclaw.exe"),
            _ => "defenseclaw.exe",
        };

        UiThread.Run(() => model.ApplyCliOverride(path));

        Assert.NotNull(model.CliOverrideProblem);
        Assert.Contains(reason, model.CliOverrideProblem, StringComparison.Ordinal);
        Assert.True(model.HasCliOverrideProblem);
        Assert.Null(model.CliOverridePath);
        Assert.Null(services.Settings.Current.Connection.CliPathOverride);
        Assert.Null(services.Paths.CliPathOverride);
    }

    [Fact]
    public void A_good_choice_after_a_refusal_clears_the_problem_and_use_automatic_puts_the_lookup_back()
    {
        var services = Create();
        var model = Build(services);
        var exe = MakeFile(@"tools\defenseclaw.exe");

        UiThread.Run(() => model.ApplyCliOverride(MakeFile(@"tools\other.exe")));
        Assert.True(model.HasCliOverrideProblem);

        UiThread.Run(() => model.ApplyCliOverride(exe));
        Assert.False(model.HasCliOverrideProblem);
        Assert.True(model.ClearCliOverrideCommand.CanExecute(null));

        UiThread.Run(() => model.ClearCliOverrideCommand.Execute(null));
        Assert.Null(model.CliOverridePath);
        Assert.Null(services.Settings.Current.Connection.CliPathOverride);
        Assert.Null(services.Paths.CliPathOverride);
        Assert.False(model.ClearCliOverrideCommand.CanExecute(null));
    }

    [Fact]
    public void Browse_offers_the_file_picker_and_applies_what_it_returns_and_cancel_changes_nothing()
    {
        var exe = MakeFile(@"tools\defenseclaw.exe");
        var platform = new FakePlatform();
        var services = Create();
        var model = Build(services, platform);

        // Cancelled.
        UiThread.Run(() => model.BrowseCliExecutableCommand.Execute(null));
        Assert.Equal(services.Paths.BinDirectory, platform.PickStart);
        Assert.Null(model.CliOverridePath);

        // Chosen.
        platform.Picked = exe;
        UiThread.Run(() => model.BrowseCliExecutableCommand.Execute(null));
        Assert.Equal(exe, model.CliOverridePath);

        // The picker starts where the current choice is.
        platform.Picked = null;
        UiThread.Run(() => model.BrowseCliExecutableCommand.Execute(null));
        Assert.Equal(Path.GetDirectoryName(exe), platform.PickStart);
        Assert.Equal(exe, model.CliOverridePath);
    }

    [Fact]
    public async Task A_chosen_cli_whose_file_has_gone_is_not_used_and_the_page_says_the_lookup_is()
    {
        var services = Create();
        var model = Build(services);
        var exe = MakeFile(@"tools\defenseclaw.exe");
        UiThread.Run(() => model.ApplyCliOverride(exe));
        await model.LastCliRefresh;
        File.Delete(exe);

        await UiThread.Run(model.RefreshMachineFactsAsync);

        Assert.Equal(exe, model.CliOverridePath);
        Assert.False(model.CliFound);
        Assert.Contains("missing", model.CliSourceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_choice_reaches_the_paths_when_the_app_starts()
    {
        var exe = MakeFile(@"tools\defenseclaw.exe");
        var first = Create();
        Assert.True(first.Settings.Update(s => s with { Connection = s.Connection with { CliPathOverride = exe } }));

        // "The app was restarted": a new composition over the same settings file.
        var second = Create();

        Assert.Equal(exe, second.Paths.CliPathOverride);
        Assert.Equal(exe, second.Paths.CliPath);
    }

    [Fact]
    public async Task Copying_and_showing_the_cli_work_on_what_is_in_use()
    {
        var platform = new FakePlatform();
        var services = Create();
        var model = Build(services, platform);
        var exe = MakeFile(@"tools\defenseclaw.exe");
        UiThread.Run(() => model.ApplyCliOverride(exe));
        await model.LastCliRefresh;
        await UiThread.Run(model.RefreshMachineFactsAsync);

        model.CopyCliPathCommand.Execute(null);
        Assert.Equal(exe, platform.Copied);

        await UiThread.Run(() => model.OpenCliFolderCommand.ExecuteAsync(null));
        Assert.Equal((exe, true), platform.Revealed[^1]);
    }

    // ------------------------------------------------------------------ updates

    private static Func<AppServices, UpdateWatcher> WatcherReturning(UpdateCheckResult result, List<bool>? forced = null) =>
        services => new UpdateWatcher(
            services.Settings,
            services.Monitor,
            (force, _) =>
            {
                forced?.Add(force);
                return Task.FromResult(result);
            },
            post: action => action());

    [Fact]
    public void Before_any_check_the_page_says_nothing_has_been_checked()
    {
        var model = Build(Create());

        Assert.Equal("Not detected", model.RuntimeVersionText);
        Assert.Equal("Not checked yet", model.UpdateStatusText);
        Assert.Equal("Neutral", model.UpdateStatusTone);
        Assert.Equal("Never", model.LastCheckedText);
        Assert.True(model.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task Check_now_asks_the_watcher_live_and_shows_an_available_release_and_when_it_looked()
    {
        var forced = new List<bool>();
        var services = Create(watcher: WatcherReturning(
            new UpdateCheckResult
            {
                State = UpdateCheckState.UpdateAvailable,
                InstalledVersion = "0.8.10",
                LatestVersion = "v0.8.11",
                CheckedAt = DateTimeOffset.UtcNow,
            },
            forced));
        var model = Build(services);

        await UiThread.Run(() => model.CheckNowCommand.ExecuteAsync(null));

        // An explicit check skips the 24 h cache; nothing automatic does.
        Assert.Equal(new[] { true }, forced);
        Assert.Equal("DefenseClaw 0.8.11 is available", model.UpdateStatusText);
        Assert.Equal("Medium", model.UpdateStatusTone);
        Assert.Contains("Open Updates", model.UpdateDetail, StringComparison.Ordinal);
        Assert.NotEqual("Never", model.LastCheckedText);
        Assert.Contains("just now", model.LastCheckedText, StringComparison.Ordinal);
        Assert.False(model.IsCheckingUpdates);
        Assert.True(model.CheckNowCommand.CanExecute(null));

        // The watcher is the one the banner follows: it agrees.
        Assert.True(services.UpdateWatcher.ShowBanner);
    }

    [Fact]
    public async Task Check_now_that_finds_nothing_newer_says_up_to_date()
    {
        var services = Create(watcher: WatcherReturning(
            new UpdateCheckResult { State = UpdateCheckState.UpToDate, InstalledVersion = "0.8.10", LatestVersion = "v0.8.10", CheckedAt = DateTimeOffset.UtcNow }));
        var model = Build(services);

        await UiThread.Run(() => model.CheckNowCommand.ExecuteAsync(null));

        Assert.Equal("Up to date", model.UpdateStatusText);
        Assert.Equal("Ok", model.UpdateStatusTone);
        Assert.Equal(string.Empty, model.UpdateDetail);
    }

    [Fact]
    public async Task A_check_that_fails_says_could_not_check_and_why()
    {
        var services = Create(watcher: WatcherReturning(
            new UpdateCheckResult
            {
                State = UpdateCheckState.CheckFailed,
                ErrorMessage = "GitHub could not be reached.",
                CheckedAt = DateTimeOffset.UtcNow,
            }));
        var model = Build(services);

        await UiThread.Run(() => model.CheckNowCommand.ExecuteAsync(null));

        Assert.Equal("Could not check", model.UpdateStatusText);
        Assert.Equal("High", model.UpdateStatusTone);
        Assert.Contains("GitHub could not be reached.", model.UpdateDetail, StringComparison.Ordinal);
        Assert.Equal("Never", model.LastCheckedText);
    }

    [Fact]
    public void Open_updates_opens_the_updates_window()
    {
        var platform = new FakePlatform();
        var services = Create();
        var model = Build(services, platform);

        model.OpenUpdatesCommand.Execute(null);

        Assert.Equal(1, platform.UpdatesOpened);
        Assert.Same(services, platform.UpdatesOpenedWith);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(120, "2m ago")]
    public void The_last_check_reads_as_a_time_and_how_long_ago(int secondsAgo, string relative)
    {
        var now = new DateTimeOffset(2026, 9, 30, 14, 30, 0, TimeSpan.Zero);

        var text = SettingsPanelViewModel.FormatLastChecked(now.AddSeconds(-secondsAgo), now);

        Assert.EndsWith($"({relative})", text, StringComparison.Ordinal);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} \(", text);
    }

    // ------------------------------------------------------------------ never a secret

    /// <summary>Every string the page can show: each public string property of the view-model and of each file row, and their <c>ToString</c>.</summary>
    private static IEnumerable<string> EveryString(SettingsPanelViewModel model)
    {
        foreach (var owner in new object[] { model }.Concat(model.Files))
        {
            yield return owner.ToString() ?? string.Empty;

            foreach (var property in owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0 && property.GetValue(owner) is string text)
                {
                    yield return text;
                }
            }
        }
    }
}

/// <summary>
/// Stands in for the Run key, the file picker, the clipboard, Explorer and the Updates window, and records what the page asked of each.
/// </summary>
internal sealed class FakePlatform
{
    public bool AutostartOn { get; set; }

    /// <summary>When set, a toggle is refused with this reason, as a policy-locked Run key refuses it.</summary>
    public string? AutostartError { get; set; }

    public int Toggles { get; private set; }

    /// <summary>What the picker returns; null is "cancelled".</summary>
    public string? Picked { get; set; }

    public string? PickStart { get; private set; }

    /// <summary>What the folder picker returns; null is "cancelled".</summary>
    public string? PickedFolder { get; set; }

    /// <summary>What the archive picker returns; null is "cancelled".</summary>
    public string? PickedArchive { get; set; }

    public string? ArchivePickStart { get; private set; }

    public string? Copied { get; private set; }

    public bool ClipboardBusy { get; set; }

    public List<(string Path, bool Select)> Revealed { get; } = new();

    /// <summary>What the operator answers to the one-time review of the automatic gateway start; every review shown is kept.</summary>
    public bool ConsentAnswer { get; set; } = true;

    public List<CommandReview> ConsentReviews { get; } = new();

    public int UpdatesOpened { get; private set; }

    public AppServices? UpdatesOpenedWith { get; private set; }

    public SettingsPlatform Build() => new()
    {
        IsAutostartEnabled = () => AutostartOn,
        ToggleAutostart = () =>
        {
            Toggles++;
            if (AutostartError is { } error)
            {
                return new AutostartToggleResult(AutostartOn, error);
            }

            AutostartOn = !AutostartOn;
            return new AutostartToggleResult(AutostartOn, null);
        },
        PickCliExecutable = start =>
        {
            PickStart = start;
            return Picked;
        },
        PickFolder = (_, _) => PickedFolder,
        PickArchiveDatabase = start =>
        {
            ArchivePickStart = start;
            return PickedArchive;
        },
        CopyText = text =>
        {
            if (ClipboardBusy)
            {
                throw new System.Runtime.InteropServices.ExternalException("CLIPBRD_E_CANT_OPEN");
            }

            Copied = text;
        },
        Reveal = (path, select) =>
        {
            Revealed.Add((path, select));
            return true;
        },
        ConfirmGatewayAutoStart = review =>
        {
            ConsentReviews.Add(review);
            return ConsentAnswer;
        },
        OpenUpdates = services =>
        {
            UpdatesOpened++;
            UpdatesOpenedWith = services;
        },
    };
}
