using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Runtime;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// Settings in the Mac's four areas (CUST-386): which area is on screen is presentation only (never saved, nothing written when it changes), a
/// <see cref="SettingsAreaLink"/> opens the right one, the General area's appearance pickers drive the one appearance service, and every setting
/// the long page had (CUST-203/308) still goes to the same file under the same key, with no second store beside it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsAreasTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _archiveDir = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        SqlitePools.Release(_temp.Path);
        SqlitePools.Release(_archiveDir.Path);
        _temp.Dispose();
        _archiveDir.Dispose();
    }

    private AppServices Create()
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);
        return services;
    }

    private static SettingsPanelViewModel Build(AppServices services, FakePlatform? platform = null) =>
        UiThread.Run(() => new SettingsPanelViewModel(services, hooks: null, (platform ?? new FakePlatform()).Build()));

    // ------------------------------------------------------------------ the areas

    [Fact]
    public void The_page_opens_on_General_and_the_four_areas_are_the_macs_in_its_order()
    {
        var model = Build(Create());

        Assert.Equal(SettingsArea.General, model.SelectedArea);
        Assert.True(model.IsGeneralArea);
        Assert.False(model.IsMonitoringArea || model.IsNotificationsArea || model.IsConnectionArea);
        Assert.Equal(new[] { "General", "Monitoring", "Notifications", "Connection" }, Enum.GetNames<SettingsArea>());
    }

    [Fact]
    public void Switching_areas_writes_nothing_and_the_area_is_kept_while_the_page_comes_and_goes()
    {
        var services = Create();
        var model = Build(services);
        var writes = 0;
        services.Settings.Changed += (_, _) => writes++;
        var before = File.Exists(services.Settings.FilePath) ? File.ReadAllText(services.Settings.FilePath) : null;

        UiThread.Run(() =>
        {
            model.SetActive(true);
            foreach (var area in Enum.GetValues<SettingsArea>())
            {
                model.SelectedArea = area;
            }

            model.SelectedArea = SettingsArea.Notifications;
            model.SetActive(false);
            model.SetActive(true);
            model.SetActive(false);
        });

        Assert.Equal(0, writes);
        Assert.Equal(before, File.Exists(services.Settings.FilePath) ? File.ReadAllText(services.Settings.FilePath) : null);
        Assert.Equal(SettingsArea.Notifications, model.SelectedArea);
        Assert.True(model.IsNotificationsArea);
    }

    [Fact]
    public void A_link_opens_its_area_and_asks_for_its_section_and_anything_else_is_ignored()
    {
        var model = Build(Create());
        var sections = new List<string>();
        model.SectionRequested += (_, section) => sections.Add(section);

        UiThread.Run(() => model.Accept(new SettingsAreaLink(SettingsArea.Monitoring)));
        Assert.Equal(SettingsArea.Monitoring, model.SelectedArea);
        Assert.Empty(sections);

        UiThread.Run(() => model.Accept(new SettingsAreaLink(SettingsArea.Connection, SettingsAreaLink.InstallationSection)));
        Assert.Equal(SettingsArea.Connection, model.SelectedArea);
        Assert.Equal(new[] { SettingsAreaLink.InstallationSection }, sections);

        // A payload for another panel, or an area this build does not have, changes nothing.
        UiThread.Run(() => model.Accept(new AuditPreset("today")));
        UiThread.Run(() => model.Accept(new SettingsAreaLink((SettingsArea)42)));
        Assert.Equal(SettingsArea.Connection, model.SelectedArea);
        Assert.Single(sections);
    }

    [Fact]
    public void A_request_for_settings_with_a_link_lands_on_its_area_through_the_catalog()
    {
        var services = Create();
        var catalog = new PanelCatalog(services);

        UiThread.Run(() =>
        {
            var view = (DefenseClaw.App.Views.Panels.SettingsPanel)catalog.GetPage(typeof(DefenseClaw.App.Views.Panels.SettingsPanel))!;
            var model = (SettingsPanelViewModel)view.DataContext;
            using var host = new OffscreenHost(view, 980, 692);

            // Already on screen: the request is delivered at once.
            services.Navigation.Request("settings", new SettingsAreaLink(SettingsArea.Connection, SettingsAreaLink.InstallationSection));
            host.Relayout();
            Assert.Equal(SettingsArea.Connection, model.SelectedArea);

            // A request without a payload (the footer entry, Ctrl+,, the tray) leaves the area where it is.
            services.Navigation.Request("settings");
            host.Relayout();
            Assert.Equal(SettingsArea.Connection, model.SelectedArea);
        });
    }

    // ------------------------------------------------------------------ appearance

    [Fact]
    public void Without_an_appearance_service_there_is_no_appearance_card()
    {
        var model = Build(Create());

        Assert.False(model.HasAppearance);
        Assert.Null(model.AppearanceStyle);
        Assert.Null(model.AppearanceMode);

        // A pick with nothing to drive is a no-op, not a fault.
        UiThread.Run(() => model.AppearanceStyle = "Linear");
    }

    [Fact]
    public void The_appearance_pickers_show_the_services_choice_drive_it_and_follow_it()
    {
        var appearance = new FakeAppearance();
        appearance.SetStyle(AppearanceStyle.Cisco);
        appearance.SetMode(AppearanceMode.Light);
        var platform = new FakePlatform { Appearance = appearance };
        var model = Build(Create(), platform);
        var sets = appearance.Sets;

        Assert.True(model.HasAppearance);
        Assert.Equal("Cisco", model.AppearanceStyle);
        Assert.Equal("Light", model.AppearanceMode);
        Assert.Equal(new[] { "Default", "Linear", "TUI", "Cisco" }, model.AppearanceStyles.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { "System", "Light", "Dark" }, model.AppearanceModes.Select(c => c.Label).ToArray());
        Assert.Equal(sets, appearance.Sets);

        UiThread.Run(() =>
        {
            model.SetActive(true);
            model.AppearanceStyle = "Tui";
            model.AppearanceMode = "Dark";
        });

        Assert.Equal(AppearanceStyle.Tui, appearance.Style);
        Assert.Equal(AppearanceMode.Dark, appearance.Mode);

        // Changed elsewhere (the title bar flyout, Ctrl+Shift+L): the pickers follow, and hand nothing back.
        sets = appearance.Sets;
        UiThread.Run(() => appearance.ToggleLightDark());
        Assert.Equal("Light", model.AppearanceMode);
        Assert.Equal(sets + 1, appearance.Sets);

        UiThread.Run(() => model.SetActive(false));
    }

    [Fact]
    public void An_appearance_change_while_a_field_is_half_typed_leaves_the_field_alone()
    {
        var services = Create();
        var model = Build(services);

        UiThread.Run(() =>
        {
            model.SetActive(true);
            model.ArchivePathText = @"C:\Synthetic\half-typed";

            // What the appearance service writes when the operator presses Ctrl+Shift+L: the appearance section, nothing else.
            Assert.True(services.Settings.Update(s => s with { Appearance = new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark) }));

            Assert.Equal(@"C:\Synthetic\half-typed", model.ArchivePathText);
            model.SetActive(false);
        });
    }

    // ------------------------------------------------------------------ every setting, through the new surface

    /// <summary>The sections the settings file has always had; the areas add none.</summary>
    private static readonly string[] KnownSections =
    {
        AppSettingsCodec.VersionKey, "appearance", "monitoring", "notifications", "startup", "connection", "updates", "developer", "archive", "palette", "seen",
    };

    [Fact]
    public async Task Every_setting_round_trips_through_the_four_areas_into_the_same_file_under_the_same_keys()
    {
        var services = Create();
        var settingsFile = services.Settings.FilePath;
        _ = Directory.CreateDirectory(_temp.File("tools"));
        _ = Directory.CreateDirectory(_temp.File("next"));
        var cli = _temp.WriteFile(Path.Combine("tools", "defenseclaw.exe"), "stub");
        var devCli = _temp.WriteFile(Path.Combine("next", "defenseclaw.exe"), "stub");
        var devHome = _temp.File("next-home");
        var archive = Path.Combine(_archiveDir.Path, "audit-old.db");
        AuditTestDatabase.Create(archive, 3, newest: new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero), idPrefix: "arc-");

        // The real appearance service, keeping its choice where the app keeps it: the appearance section of the same file.
        using var appearance = new AppearanceFixture(backing: new FileAppearanceSettingsStore(settingsFile));
        var platform = new FakePlatform { Appearance = appearance.Service };
        var model = Build(services, platform);

        UiThread.Run(() =>
        {
            model.SetActive(true);

            model.SelectedArea = SettingsArea.General;
            model.StartWithWindows = true;
            model.CloseToTray = false;
            model.RememberLastPanel = true;
            model.AppearanceStyle = "Linear";
            model.AppearanceMode = "Dark";

            model.SelectedArea = SettingsArea.Monitoring;
            model.HealthIntervalSeconds = 23;
            model.CommitHealthInterval();
            model.IsMonitoringPaused = true;

            model.SelectedArea = SettingsArea.Notifications;
            model.NotifyCritical = false;
            model.NotifyHigh = false;
            model.NotifyGateway = false;

            model.SelectedArea = SettingsArea.Connection;
            model.AutoStartGateway = true;
            model.ApplyCliOverride(cli);
            model.DeveloperEnabled = true;
            model.SelectedRuntimeKind = model.RuntimeKinds[1];
            model.RuntimeCliPath = devCli;
            model.RuntimeHomeDirectory = devHome;
            model.RuntimeGatewayUrl = "http://127.0.0.1:18972";
            model.ApplyRuntime();
            model.ArchivePathText = archive;
        });
        await model.ApplyArchiveCommand.ExecuteAsync(null);
        UiThread.Run(() => model.SetActive(false));

        Assert.False(model.HasSaveProblem);
        Assert.False(model.HasRuntimeProblem, model.RuntimeProblem);
        Assert.False(model.HasArchiveProblem, model.ArchiveProblem);
        Assert.True(platform.AutostartOn, "Start with Windows is the Run key, through the guarded toggle, as before");

        // The file, read fresh: every value, in the section and under the key it always had.
        var json = JsonNode.Parse(File.ReadAllText(settingsFile))!.AsObject();
        Assert.All(json.Select(member => member.Key), key => Assert.Contains(key, KnownSections));
        Assert.Equal("linear", (string?)json["appearance"]!["style"]);
        Assert.Equal("dark", (string?)json["appearance"]!["mode"]);
        Assert.Equal(23, (int?)json["monitoring"]!["healthIntervalSeconds"]);
        Assert.True((bool?)json["monitoring"]!["paused"]);
        Assert.False((bool?)json["notifications"]!["critical"]);
        Assert.False((bool?)json["notifications"]!["high"]);
        Assert.False((bool?)json["notifications"]!["gateway"]);
        Assert.True((bool?)json["startup"]!["gatewayAutoStart"]);
        Assert.False((bool?)json["startup"]!["closeToTray"]);
        Assert.True((bool?)json["startup"]!["rememberLastPanel"]);
        Assert.Equal(cli, (string?)json["connection"]!["cliPathOverride"]);
        Assert.True((bool?)json["developer"]!["enabled"]);
        Assert.Equal("cli", (string?)json["developer"]!["kind"]);
        Assert.Equal(devCli, (string?)json["developer"]!["cliPath"]);
        Assert.Equal(devHome, (string?)json["developer"]!["homeDirectory"]);
        Assert.Equal("http://127.0.0.1:18972", (string?)json["developer"]!["gatewayUrl"]);
        Assert.Equal(archive, (string?)json["archive"]!["path"]);

        // No second store: the only settings file in the app's folder is the one the store owns.
        Assert.Equal(new[] { settingsFile }, Directory.GetFiles(Path.GetDirectoryName(settingsFile)!, "*.json"), StringComparer.OrdinalIgnoreCase);

        // And a page built afresh over that file shows every one of them back, on whichever area it lands.
        var reopened = AppSettingsStore.OpenFresh(settingsFile).Current;
        Assert.Equal(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark), reopened.Appearance);
        var again = Build(services, platform);
        Assert.Equal(SettingsArea.General, again.SelectedArea);
        Assert.Equal(23, again.HealthIntervalSeconds);
        Assert.True(again.IsMonitoringPaused);
        Assert.False(again.NotifyCritical || again.NotifyHigh || again.NotifyGateway);
        Assert.False(again.CloseToTray);
        Assert.True(again.RememberLastPanel);
        Assert.True(again.AutoStartGateway);
        Assert.Equal(cli, again.CliOverridePath);
        Assert.True(again.DeveloperEnabled);
        Assert.Equal(RuntimeKind.Cli, again.SelectedRuntimeKind.Kind);
        Assert.Equal(devCli, again.RuntimeCliPath);
        Assert.Equal(devHome, again.RuntimeHomeDirectory);
        Assert.Equal(archive, again.ArchivePathText);
        Assert.Equal("Linear", again.AppearanceStyle);
        Assert.Equal("Dark", again.AppearanceMode);
    }
}
