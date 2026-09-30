using System.Text.Json.Nodes;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// The app's settings file as a whole: every section's round trip, what an older file migrates to, what a damaged, unknown or
/// newer file does, and that concurrent writers lose nothing. Every test works on a scratch file (see <see cref="TempDirectory"/>);
/// none can reach the real <c>%LOCALAPPDATA%\DefenseClaw.App\settings.json</c>.
/// </summary>
public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string NewPath() => System.IO.Path.Combine(_temp.Path, "DefenseClaw.App", Guid.NewGuid().ToString("N") + ".json");

    private static AppSettingsStore Fresh(string path) => AppSettingsStore.OpenFresh(path);

    private static void WriteFile(string path, string content)
    {
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static AppSettings Everything() => new(
        new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark),
        new MonitoringSettings { HealthIntervalSeconds = 17, Paused = true },
        new NotificationSettings { Critical = false, High = false, Gateway = false, HighWaterUnixNano = 1_780_000_000_123_456_789 },
        new StartupSettings { GatewayAutoStart = true, CloseToTray = false, RememberLastPanel = true, LastPanelId = "ai-discovery" },
        new ConnectionSettings { CliPathOverride = @"C:\Tools\DefenseClaw\defenseclaw.exe" },
        new UpdateSettings { LastCheckUnix = 1_790_000_000, DismissedVersion = "0.9.1" });

    // ------------------------------------------------------------------ defaults

    [Fact]
    public void A_fresh_install_gets_the_specified_defaults_and_no_file_is_created_by_reading()
    {
        var path = NewPath();
        var settings = Fresh(path).Current;

        Assert.Equal(5, settings.Monitoring.HealthIntervalSeconds);
        Assert.False(settings.Monitoring.Paused);
        Assert.True(settings.Notifications.Critical);
        Assert.True(settings.Notifications.High);
        Assert.True(settings.Notifications.Gateway);
        Assert.Equal(0, settings.Notifications.HighWaterUnixNano);
        Assert.False(settings.Startup.GatewayAutoStart);
        Assert.True(settings.Startup.CloseToTray);
        Assert.False(settings.Startup.RememberLastPanel);
        Assert.Null(settings.Startup.LastPanelId);
        Assert.Null(settings.Connection.CliPathOverride);
        Assert.Equal(0, settings.Updates.LastCheckUnix);
        Assert.Null(settings.Updates.DismissedVersion);
        Assert.Equal(AppearanceSettings.Defaults, settings.Appearance);
        Assert.Equal(AppSettings.Defaults, settings);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_monitoring_interval_is_two_to_sixty_seconds_and_clamps_rather_than_fails()
    {
        Assert.Equal(2, MonitoringSettings.MinHealthIntervalSeconds);
        Assert.Equal(60, MonitoringSettings.MaxHealthIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(5), new MonitoringSettings().HealthInterval);

        Assert.Equal(2, new MonitoringSettings { HealthIntervalSeconds = 1 }.HealthIntervalSeconds);
        Assert.Equal(2, new MonitoringSettings { HealthIntervalSeconds = 0 }.HealthIntervalSeconds);
        Assert.Equal(2, new MonitoringSettings { HealthIntervalSeconds = -30 }.HealthIntervalSeconds);
        Assert.Equal(2, new MonitoringSettings { HealthIntervalSeconds = 2 }.HealthIntervalSeconds);
        Assert.Equal(60, new MonitoringSettings { HealthIntervalSeconds = 60 }.HealthIntervalSeconds);
        Assert.Equal(60, new MonitoringSettings { HealthIntervalSeconds = 61 }.HealthIntervalSeconds);
        Assert.Equal(60, new MonitoringSettings { HealthIntervalSeconds = int.MaxValue }.HealthIntervalSeconds);
        Assert.Equal(2, (new MonitoringSettings() with { HealthIntervalSeconds = 1 }).HealthIntervalSeconds);
    }

    // ------------------------------------------------------------------ round trips

    [Fact]
    public void Every_section_survives_a_save_and_a_reload_in_a_new_process()
    {
        var path = NewPath();
        var everything = Everything();

        Assert.True(Fresh(path).Update(_ => everything));

        Assert.Equal(everything, Fresh(path).Load());
        Assert.Equal(everything, Fresh(path).Current);
    }

    [Fact]
    public void The_monitoring_section_round_trips_alone()
    {
        var path = NewPath();
        var changed = new MonitoringSettings { HealthIntervalSeconds = 30, Paused = true };

        _ = Fresh(path).Update(s => s with { Monitoring = changed });

        var reloaded = Fresh(path).Current;
        Assert.Equal(changed, reloaded.Monitoring);
        Assert.Equal(AppSettings.Defaults with { Monitoring = changed }, reloaded);
    }

    [Fact]
    public void The_notifications_section_round_trips_alone_including_the_nanosecond_high_water_mark_exactly()
    {
        var path = NewPath();
        var changed = new NotificationSettings { Critical = false, High = true, Gateway = false, HighWaterUnixNano = long.MaxValue - 1 };

        _ = Fresh(path).Update(s => s with { Notifications = changed });

        Assert.Equal(changed, Fresh(path).Current.Notifications);
        Assert.Equal(long.MaxValue - 1, (long)ReadJson(path)["notifications"]!["highWaterUnixNano"]!);
    }

    [Fact]
    public void The_startup_section_round_trips_alone()
    {
        var path = NewPath();
        var changed = new StartupSettings { GatewayAutoStart = true, CloseToTray = false, RememberLastPanel = true, LastPanelId = "audit" };

        _ = Fresh(path).Update(s => s with { Startup = changed });

        Assert.Equal(changed, Fresh(path).Current.Startup);
    }

    [Fact]
    public void The_connection_section_round_trips_alone_and_a_path_reads_as_typed()
    {
        var path = NewPath();
        var cli = @"C:\Program Files\DefenseClaw & Co\bin\defenseclaw.exe";

        _ = Fresh(path).Update(s => s with { Connection = new ConnectionSettings { CliPathOverride = cli } });

        Assert.Equal(cli, Fresh(path).Current.Connection.CliPathOverride);

        // A person may open the file: the ampersand is not written as \u0026.
        Assert.Contains("DefenseClaw & Co", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void The_updates_section_round_trips_alone()
    {
        var path = NewPath();
        var changed = new UpdateSettings { LastCheckUnix = 1_790_000_123, DismissedVersion = "0.9.1-rc.2" };

        _ = Fresh(path).Update(s => s with { Updates = changed });

        Assert.Equal(changed, Fresh(path).Current.Updates);
    }

    [Theory]
    [InlineData("Default", "System")]
    [InlineData("Linear", "Light")]
    [InlineData("Tui", "Dark")]
    public void The_appearance_section_round_trips_alone_under_its_lowercase_names(string styleName, string modeName)
    {
        var path = NewPath();
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        var changed = new AppearanceSettings(style, mode);

        _ = Fresh(path).Update(s => s with { Appearance = changed });

        Assert.Equal(changed, Fresh(path).Current.Appearance);
        if (changed != AppearanceSettings.Defaults)
        {
            Assert.Equal(style.ToString().ToLowerInvariant(), (string)ReadJson(path)["appearance"]!["style"]!);
            Assert.Equal(mode.ToString().ToLowerInvariant(), (string)ReadJson(path)["appearance"]!["mode"]!);
        }
    }

    [Fact]
    public void The_file_is_versioned_first_named_in_camel_case_and_readable_by_a_person()
    {
        var path = NewPath();

        _ = Fresh(path).Update(_ => Everything());

        var root = ReadJson(path);
        Assert.Equal("schemaVersion", root.First().Key);
        Assert.Equal(1, (int)root["schemaVersion"]!);
        Assert.Equal(17, (int)root["monitoring"]!["healthIntervalSeconds"]!);
        Assert.True((bool)root["monitoring"]!["paused"]!);
        Assert.False((bool)root["notifications"]!["critical"]!);
        Assert.True((bool)root["startup"]!["gatewayAutoStart"]!);
        Assert.Equal("ai-discovery", (string)root["startup"]!["lastPanelId"]!);
        Assert.Equal(@"C:\Tools\DefenseClaw\defenseclaw.exe", (string)root["connection"]!["cliPathOverride"]!);
        Assert.Equal("0.9.1", (string)root["updates"]!["dismissedVersion"]!);
        Assert.Contains("\n", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(1, AppSettingsStore.SchemaVersion);
    }

    [Fact]
    public void A_null_value_removes_its_member_instead_of_writing_null()
    {
        var path = NewPath();
        var store = Fresh(path);
        _ = store.Update(s => s with { Startup = new StartupSettings { LastPanelId = "audit" } });
        Assert.Equal("audit", (string)ReadJson(path)["startup"]!["lastPanelId"]!);

        _ = store.Update(s => s with { Startup = s.Startup with { LastPanelId = null } });

        Assert.False(ReadJson(path)["startup"]!.AsObject().ContainsKey("lastPanelId"));
        Assert.Null(Fresh(path).Current.Startup.LastPanelId);
    }

    [Fact]
    public void Only_the_sections_that_changed_are_written_so_a_changed_default_still_reaches_everyone_else()
    {
        var path = NewPath();

        _ = Fresh(path).Update(s => s with { Monitoring = s.Monitoring with { Paused = true } });

        var root = ReadJson(path);
        Assert.Equal(new[] { "schemaVersion", "monitoring" }, root.Select(member => member.Key).ToArray());
    }

    [Fact]
    public void An_update_that_changes_nothing_writes_nothing_and_raises_nothing()
    {
        var path = NewPath();
        var store = Fresh(path);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        Assert.True(store.Update(s => s));
        Assert.True(store.Update(s => s with { Monitoring = new MonitoringSettings() }));

        Assert.False(File.Exists(path));
        Assert.Equal(0, raised);
    }

    // ------------------------------------------------------------------ migration from today's (appearance-only) file

    [Fact]
    public void Todays_appearance_only_file_reads_as_appearance_plus_defaults()
    {
        var path = NewPath();
        WriteFile(path, """{ "schemaVersion": 1, "appearance": { "style": "linear", "mode": "dark" } }""");

        var settings = Fresh(path).Load();

        Assert.Equal(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark), settings.Appearance);
        Assert.Equal(AppSettings.Defaults with { Appearance = settings.Appearance }, settings);
    }

    [Fact]
    public void A_new_section_is_added_beside_todays_file_without_touching_its_appearance_or_version()
    {
        var path = NewPath();
        WriteFile(path, """{ "schemaVersion": 1, "appearance": { "style": "tui", "mode": "light" } }""");

        _ = Fresh(path).Update(s => s with { Monitoring = s.Monitoring with { HealthIntervalSeconds = 20 } });

        var root = ReadJson(path);
        Assert.Equal(1, (int)root["schemaVersion"]!);
        Assert.Equal("tui", (string)root["appearance"]!["style"]!);
        Assert.Equal("light", (string)root["appearance"]!["mode"]!);
        Assert.Equal(20, (int)root["monitoring"]!["healthIntervalSeconds"]!);
        Assert.Equal(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Light), Fresh(path).Current.Appearance);
    }

    [Fact]
    public void The_appearance_store_and_the_settings_store_are_one_file_with_one_writer()
    {
        var path = NewPath();
        var appearance = new FileAppearanceSettingsStore(path);
        var settings = AppSettingsStore.ForPath(path);

        appearance.Save(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));
        Assert.Equal(AppearanceStyle.Linear, settings.Current.Appearance.Style);

        _ = settings.Update(s => s with
        {
            Appearance = new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Light),
            Startup = new StartupSettings { RememberLastPanel = true },
        });
        Assert.Equal(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Light), appearance.Load());

        // Saving the look afterwards keeps what the settings store put in the other section.
        appearance.Save(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.System));
        var root = ReadJson(path);
        Assert.True((bool)root["startup"]!["rememberLastPanel"]!);
        Assert.Equal("default", (string)root["appearance"]!["style"]!);
    }

    // ------------------------------------------------------------------ forgiving reads

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("{")]
    [InlineData("""{ "schemaVersion": 1, "monitoring": { "healthIntervalSeconds": 3""")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"monitoring\"")]
    [InlineData("{}")]
    [InlineData("""{ "monitoring": 5, "notifications": [], "startup": "yes", "connection": null, "updates": true, "appearance": 1 }""")]
    [InlineData("""{ "a": 1, "a": 2 }""")]
    [InlineData("""{ "monitoring": { "paused": true, "paused": false } }""")]
    public void A_corrupt_or_wrongly_shaped_file_is_the_defaults_and_never_throws(string content)
    {
        var path = NewPath();
        WriteFile(path, content);

        Assert.Equal(AppSettings.Defaults, Fresh(path).Load());
        Assert.Equal(AppSettings.Defaults, Fresh(path).Current);
    }

    [Fact]
    public void A_file_of_binary_junk_is_the_defaults()
    {
        var path = NewPath();
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80, 0x81, 0x00, 0x00 });

        Assert.Equal(AppSettings.Defaults, Fresh(path).Load());
    }

    [Fact]
    public void A_file_far_bigger_than_any_settings_file_is_treated_as_damaged_not_loaded()
    {
        var path = NewPath();
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
        {
            stream.SetLength(6 * 1024 * 1024);
        }

        Assert.Equal(AppSettings.Defaults, Fresh(path).Load());
    }

    [Theory]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": 1 } }""", 2)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": 0 } }""", 2)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": -7 } }""", 2)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": 9000 } }""", 60)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": 12 } }""", 12)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": 12.0 } }""", 12)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": 12.6 } }""", 13)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": "12" } }""", 5)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": null } }""", 5)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": true } }""", 5)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": [3] } }""", 5)]
    [InlineData("""{ "monitoring": { } }""", 5)]
    [InlineData("""{ "monitoring": { "healthIntervalSeconds": 1e30 } }""", 60)]
    public void A_bad_interval_clamps_or_defaults_and_never_reaches_a_timer_out_of_range(string content, int expected)
    {
        var path = NewPath();
        WriteFile(path, content);

        Assert.Equal(expected, Fresh(path).Current.Monitoring.HealthIntervalSeconds);
    }

    [Fact]
    public void A_bad_field_falls_back_for_that_field_alone()
    {
        var path = NewPath();
        WriteFile(path, """
            {
              "monitoring": { "healthIntervalSeconds": 11, "paused": "yes" },
              "notifications": { "critical": 0, "high": false, "gateway": null, "highWaterUnixNano": "soon" },
              "startup": { "gatewayAutoStart": true, "closeToTray": "no", "rememberLastPanel": [], "lastPanelId": 5 },
              "connection": { "cliPathOverride": { "x": 1 } },
              "updates": { "lastCheckUnix": -400, "dismissedVersion": "  " },
              "appearance": { "style": "purple", "mode": "dark" }
            }
            """);

        var settings = Fresh(path).Current;

        Assert.Equal(11, settings.Monitoring.HealthIntervalSeconds);
        Assert.False(settings.Monitoring.Paused);
        Assert.True(settings.Notifications.Critical);
        Assert.False(settings.Notifications.High);
        Assert.True(settings.Notifications.Gateway);
        Assert.Equal(0, settings.Notifications.HighWaterUnixNano);
        Assert.True(settings.Startup.GatewayAutoStart);
        Assert.True(settings.Startup.CloseToTray);
        Assert.False(settings.Startup.RememberLastPanel);
        Assert.Null(settings.Startup.LastPanelId);
        Assert.Null(settings.Connection.CliPathOverride);
        Assert.Equal(0, settings.Updates.LastCheckUnix);
        Assert.Null(settings.Updates.DismissedVersion);
        Assert.Equal(AppearanceStyle.Default, settings.Appearance.Style);
        Assert.Equal(AppearanceMode.Dark, settings.Appearance.Mode);
    }

    [Fact]
    public void A_path_override_is_trimmed_and_a_blank_one_is_no_override()
    {
        var path = NewPath();
        WriteFile(path, """{ "connection": { "cliPathOverride": "  C:\\bin\\defenseclaw.exe  " } }""");
        Assert.Equal(@"C:\bin\defenseclaw.exe", Fresh(path).Current.Connection.CliPathOverride);

        Assert.Null(new ConnectionSettings { CliPathOverride = "   " }.CliPathOverride);
        Assert.Null(new ConnectionSettings { CliPathOverride = string.Empty }.CliPathOverride);
        Assert.Null(new ConnectionSettings { CliPathOverride = null }.CliPathOverride);
    }

    [Fact]
    public void Comments_trailing_commas_and_a_byte_order_mark_are_tolerated_on_read()
    {
        var path = NewPath();
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            """
            // hand edited
            {
              "schemaVersion": 1, /* keep */
              "monitoring": { "paused": true, },
            }
            """,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.True(Fresh(path).Current.Monitoring.Paused);
    }

    [Fact]
    public void A_negative_high_water_mark_or_check_time_reads_as_zero()
    {
        var path = NewPath();
        WriteFile(path, """{ "notifications": { "highWaterUnixNano": -5 }, "updates": { "lastCheckUnix": -1 } }""");

        var settings = Fresh(path).Current;

        Assert.Equal(0, settings.Notifications.HighWaterUnixNano);
        Assert.Equal(0, settings.Updates.LastCheckUnix);
    }

    // ------------------------------------------------------------------ unknown and newer content

    [Fact]
    public void Unknown_sections_unknown_members_and_a_newer_schema_version_survive_every_save()
    {
        var path = NewPath();
        WriteFile(path, """
            {
              "schemaVersion": 7,
              "futureSection": { "keep": [1, 2, 3], "nested": { "x": null } },
              "monitoring": { "healthIntervalSeconds": 9, "paused": false, "pollStrategy": "adaptive" },
              "appearance": { "style": "linear", "mode": "dark", "density": "compact" }
            }
            """);

        var store = Fresh(path);
        Assert.Equal(9, store.Current.Monitoring.HealthIntervalSeconds);

        _ = store.Update(s => s with
        {
            Monitoring = s.Monitoring with { Paused = true },
            Appearance = new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Light),
            Startup = new StartupSettings { RememberLastPanel = true },
        });

        var root = ReadJson(path);
        Assert.Equal(7, (int)root["schemaVersion"]!);
        Assert.Equal(3, root["futureSection"]!["keep"]!.AsArray().Count);
        Assert.Null(root["futureSection"]!["nested"]!["x"]);
        Assert.Equal("adaptive", (string)root["monitoring"]!["pollStrategy"]!);
        Assert.True((bool)root["monitoring"]!["paused"]!);
        Assert.Equal(9, (int)root["monitoring"]!["healthIntervalSeconds"]!);
        Assert.Equal("compact", (string)root["appearance"]!["density"]!);
        Assert.Equal("tui", (string)root["appearance"]!["style"]!);
        Assert.True((bool)root["startup"]!["rememberLastPanel"]!);
    }

    [Fact]
    public void A_missing_or_unusable_schema_version_is_replaced_by_this_builds()
    {
        var path = NewPath();
        WriteFile(path, """{ "schemaVersion": "two", "monitoring": { "paused": true } }""");

        _ = Fresh(path).Update(s => s with { Startup = new StartupSettings { GatewayAutoStart = true } });

        Assert.Equal(1, (int)ReadJson(path)["schemaVersion"]!);

        var missing = NewPath();
        WriteFile(missing, """{ "monitoring": { "paused": true } }""");
        _ = Fresh(missing).Update(s => s with { Startup = new StartupSettings { GatewayAutoStart = true } });

        Assert.Equal("schemaVersion", ReadJson(missing).First().Key);
        Assert.Equal(1, (int)ReadJson(missing)["schemaVersion"]!);
    }

    [Fact]
    public void A_section_that_is_not_an_object_is_replaced_when_that_section_is_written()
    {
        var path = NewPath();
        WriteFile(path, """{ "monitoring": 5, "startup": { "closeToTray": false } }""");

        _ = Fresh(path).Update(s => s with { Monitoring = s.Monitoring with { Paused = true } });

        var root = ReadJson(path);
        Assert.True((bool)root["monitoring"]!["paused"]!);
        Assert.False((bool)root["startup"]!["closeToTray"]!);
    }

    // ------------------------------------------------------------------ damaged file, then a save

    [Fact]
    public void Saving_over_a_corrupt_file_replaces_it_with_a_good_one()
    {
        var path = NewPath();
        WriteFile(path, "{{{ garbage");

        Assert.True(Fresh(path).Update(s => s with { Startup = new StartupSettings { GatewayAutoStart = true } }));

        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path)));
        Assert.True(Fresh(path).Current.Startup.GatewayAutoStart);
    }

    [Fact]
    public void A_duplicate_member_name_is_a_damaged_file_that_a_save_replaces_not_an_exception()
    {
        // The JSON parser builds a section's member table lazily, so this only fails when the section is first read.
        var path = NewPath();
        WriteFile(path, """{ "monitoring": { "paused": true, "paused": false }, "startup": { "closeToTray": false } }""");

        Assert.Equal(AppSettings.Defaults, Fresh(path).Load());
        Assert.True(Fresh(path).Update(s => s with { Notifications = new NotificationSettings { Gateway = false } }));

        Assert.False(Fresh(path).Current.Notifications.Gateway);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path)));
    }

    [Fact]
    public void A_file_that_goes_bad_while_the_app_runs_is_rebuilt_with_everything_that_is_not_a_default()
    {
        var path = NewPath();
        var store = Fresh(path);
        _ = store.Update(s => s with { Monitoring = new MonitoringSettings { HealthIntervalSeconds = 40 } });
        _ = store.Update(s => s with { Notifications = new NotificationSettings { Gateway = false } });

        File.WriteAllText(path, "<<corrupted by something else>>");
        _ = store.Update(s => s with { Startup = new StartupSettings { GatewayAutoStart = true } });

        var reloaded = Fresh(path).Current;
        Assert.Equal(40, reloaded.Monitoring.HealthIntervalSeconds);
        Assert.False(reloaded.Notifications.Gateway);
        Assert.True(reloaded.Startup.GatewayAutoStart);
    }

    [Fact]
    public void A_glitch_on_disk_does_not_reset_the_running_apps_settings()
    {
        var path = NewPath();
        var store = Fresh(path);
        _ = store.Update(s => s with { Monitoring = new MonitoringSettings { Paused = true } });

        File.WriteAllText(path, "");

        Assert.Equal(AppSettings.Defaults, store.Load());          // what a new process would see
        Assert.True(store.Current.Monitoring.Paused);              // what this one keeps
    }

    [Fact]
    public void A_file_deleted_while_the_app_runs_reads_as_the_defaults_on_the_next_load_and_is_recreated_by_the_next_save()
    {
        var path = NewPath();
        var store = Fresh(path);
        _ = store.Update(s => s with { Monitoring = new MonitoringSettings { Paused = true } });

        File.Delete(path);
        Assert.False(store.Load().Monitoring.Paused);

        _ = store.Update(s => s with { Startup = new StartupSettings { GatewayAutoStart = true } });
        Assert.True(Fresh(path).Current.Startup.GatewayAutoStart);
    }

    // ------------------------------------------------------------------ the Changed event

    [Fact]
    public void Changed_carries_before_after_and_which_sections_differ()
    {
        var store = Fresh(NewPath());
        var seen = new List<AppSettingsChangedEventArgs>();
        store.Changed += (_, e) => seen.Add(e);

        _ = store.Update(s => s with
        {
            Monitoring = s.Monitoring with { Paused = true },
            Updates = new UpdateSettings { LastCheckUnix = 5 },
        });

        var e = Assert.Single(seen);
        Assert.Equal(AppSettings.Defaults, e.Previous);
        Assert.True(e.Current.Monitoring.Paused);
        Assert.Equal(AppSettingsSections.Monitoring | AppSettingsSections.Updates, e.Sections);
        Assert.True(e.Affects(AppSettingsSections.Monitoring));
        Assert.True(e.Affects(AppSettingsSections.Updates));
        Assert.False(e.Affects(AppSettingsSections.Startup));
        Assert.Same(store.Current, e.Current);
    }

    [Fact]
    public void Changed_names_every_section()
    {
        var store = Fresh(NewPath());
        AppSettingsChangedEventArgs? seen = null;
        store.Changed += (_, e) => seen = e;

        _ = store.Update(_ => Everything());

        Assert.Equal(AppSettingsSections.All, seen!.Sections);
    }

    [Fact]
    public void A_throwing_subscriber_neither_fails_the_update_nor_silences_the_others()
    {
        var store = Fresh(NewPath());
        var second = 0;
        store.Changed += (_, _) => throw new InvalidOperationException("a subscriber that always throws");
        store.Changed += (_, _) => second++;

        Assert.True(store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } }));

        Assert.Equal(1, second);
        Assert.True(store.Current.Monitoring.Paused);
    }

    [Fact]
    public async Task A_subscriber_may_read_and_even_update_the_store_from_inside_Changed_without_deadlock()
    {
        var store = Fresh(NewPath());
        var nested = false;
        store.Changed += (_, e) =>
        {
            Assert.Equal(e.Current.Monitoring.Paused, store.Current.Monitoring.Paused);
            if (e.Affects(AppSettingsSections.Monitoring) && !nested)
            {
                nested = true;
                _ = store.Update(s => s with { Startup = new StartupSettings { GatewayAutoStart = true } });
            }
        };

        var done = Task.Run(() => store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } }));

        _ = await done.WaitAsync(TimeSpan.FromSeconds(20));   // a TimeoutException here means Update deadlocked on a subscriber that called back in
        Assert.True(store.Current.Monitoring.Paused);
        Assert.True(store.Current.Startup.GatewayAutoStart);
    }

    [Fact]
    public void Load_announces_an_edit_made_by_hand_but_not_the_first_read()
    {
        var path = NewPath();
        WriteFile(path, """{ "monitoring": { "healthIntervalSeconds": 10 } }""");
        var store = Fresh(path);
        var seen = new List<AppSettingsChangedEventArgs>();
        store.Changed += (_, e) => seen.Add(e);

        _ = store.Current;
        _ = store.Load();
        Assert.Empty(seen);

        WriteFile(path, """{ "monitoring": { "healthIntervalSeconds": 45 } }""");
        var reloaded = store.Load();

        Assert.Equal(45, reloaded.Monitoring.HealthIntervalSeconds);
        Assert.Equal(45, store.Current.Monitoring.HealthIntervalSeconds);
        var e = Assert.Single(seen);
        Assert.Equal(AppSettingsSections.Monitoring, e.Sections);
    }

    // ------------------------------------------------------------------ atomic, tolerant writes

    [Fact]
    public void A_save_leaves_no_temp_file_behind_and_creates_the_folder()
    {
        var path = NewPath();
        var store = Fresh(path);

        _ = store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } });
        _ = store.Update(s => s with { Monitoring = s.Monitoring with { HealthIntervalSeconds = 30 } });

        Assert.Equal(new[] { path }, Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path) + "*"));
    }

    [Fact]
    public void A_save_that_cannot_happen_is_reported_and_swallowed_and_the_path_is_left_alone()
    {
        // The settings path is a directory: nothing can be written there, and the app must carry on regardless.
        var path = System.IO.Path.Combine(_temp.Path, "settings.json");
        _ = Directory.CreateDirectory(path);
        var store = Fresh(path);

        Assert.False(store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } }));

        Assert.True(store.Current.Monitoring.Paused);
        Assert.Empty(Directory.GetFileSystemEntries(path));
        Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(_temp.Path));
    }

    [Fact]
    public void A_file_nobody_can_replace_keeps_its_old_content_loses_no_temp_file_and_the_change_goes_out_with_the_next_save()
    {
        var path = NewPath();
        var store = Fresh(path);
        Assert.True(store.Update(s => s with { Startup = new StartupSettings { GatewayAutoStart = true } }));
        var before = File.ReadAllText(path);

        // Open for reading without FileShare.Delete: readable, but it cannot be replaced.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } }));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!));
        }

        Assert.True(store.Current.Monitoring.Paused);

        Assert.True(store.Update(s => s with { Updates = new UpdateSettings { LastCheckUnix = 9 } }));

        var reloaded = Fresh(path).Current;
        Assert.True(reloaded.Monitoring.Paused);
        Assert.Equal(9, reloaded.Updates.LastCheckUnix);
        Assert.True(reloaded.Startup.GatewayAutoStart);
    }

    [Fact]
    public void A_locked_file_is_a_failed_read_not_an_exception_and_is_not_remembered_as_the_defaults()
    {
        var path = NewPath();
        WriteFile(path, """{ "monitoring": { "healthIntervalSeconds": 30 } }""");
        var store = Fresh(path);
        var raised = 0;
        store.Changed += (_, _) => raised++;

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(AppSettings.Defaults, store.Load());
            Assert.Equal(AppSettings.Defaults, store.Current);
            Assert.False(store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } }));
        }

        // Once the file can be read, what it says is what the store says; the locked moment left nothing behind.
        Assert.Equal(30, store.Current.Monitoring.HealthIntervalSeconds);
        Assert.False(store.Current.Monitoring.Paused);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_change_function_that_returns_null_or_throws_leaves_the_store_usable_and_unchanged()
    {
        var store = Fresh(NewPath());

        _ = Assert.Throws<InvalidOperationException>(() => store.Update(_ => null!));
        _ = Assert.Throws<ArgumentNullException>(() => store.Update(null!));
        _ = Assert.Throws<FormatException>(() => store.Update(_ => throw new FormatException("a bug in the caller")));

        Assert.Equal(AppSettings.Defaults, store.Current);
        Assert.True(store.Update(s => s with { Monitoring = s.Monitoring with { Paused = true } }));
        Assert.True(store.Current.Monitoring.Paused);
    }

    // ------------------------------------------------------------------ one store per file

    [Fact]
    public void ForPath_hands_every_caller_the_same_store_however_the_path_is_spelled()
    {
        var path = NewPath();
        var direct = AppSettingsStore.ForPath(path);
        var dotted = AppSettingsStore.ForPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "..", "DefenseClaw.App", System.IO.Path.GetFileName(path)));
        var shouted = AppSettingsStore.ForPath(path.ToUpperInvariant());

        Assert.Same(direct, dotted);
        Assert.Same(direct, shouted);
        Assert.NotSame(direct, AppSettingsStore.ForPath(NewPath()));
        Assert.Equal(System.IO.Path.GetFullPath(path), direct.FilePath);
    }

    [Fact]
    public void The_default_location_is_the_apps_own_folder_under_local_app_data_never_the_cli_home()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(System.IO.Path.Combine(local, "DefenseClaw.App", "settings.json"), AppSettingsStore.DefaultPath);
        Assert.DoesNotContain(".defenseclaw", AppSettingsStore.DefaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AppSettingsStore.DefaultPath, FileAppearanceSettingsStore.DefaultPath);
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task Concurrent_updates_each_take_effect_exactly_once()
    {
        var path = NewPath();
        var store = Fresh(path);
        var raised = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref raised);

        const int threads = 6;
        const int perThread = 25;
        var barrier = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (var i = 0; i < perThread; i++)
            {
                Assert.True(store.Update(s => s with
                {
                    Notifications = s.Notifications with { HighWaterUnixNano = s.Notifications.HighWaterUnixNano + 1 },
                }));
            }
        })).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        // Read-modify-write is atomic: no increment was lost, in memory or on disk.
        Assert.Equal(threads * perThread, store.Current.Notifications.HighWaterUnixNano);
        Assert.Equal(threads * perThread, raised);
        Assert.Equal(threads * perThread, Fresh(path).Current.Notifications.HighWaterUnixNano);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path)));
        Assert.Equal(new[] { path }, Directory.GetFiles(System.IO.Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task Concurrent_updates_of_different_sections_all_reach_the_file()
    {
        var path = NewPath();
        var store = Fresh(path);
        var barrier = new Barrier(5);

        var workers = new[]
        {
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 1; i <= 25; i++) { var n = i; _ = store.Update(s => s with { Monitoring = new MonitoringSettings { HealthIntervalSeconds = 2 + n } }); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 1; i <= 25; i++) { var n = i; _ = store.Update(s => s with { Notifications = s.Notifications with { HighWaterUnixNano = n } }); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 1; i <= 25; i++) { var n = i; _ = store.Update(s => s with { Startup = new StartupSettings { LastPanelId = "panel-" + n } }); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 1; i <= 25; i++) { var n = i; _ = store.Update(s => s with { Updates = new UpdateSettings { LastCheckUnix = n } }); } }),
            Task.Run(() =>
            {
                barrier.SignalAndWait();

                // A reader racing the writers sees whole, valid settings every time.
                for (var i = 0; i < 200; i++)
                {
                    var seen = store.Current;
                    Assert.InRange(seen.Monitoring.HealthIntervalSeconds, 2, 60);
                    _ = store.Load();
                }
            }),
        };

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));

        var reloaded = Fresh(path).Current;
        Assert.Equal(27, reloaded.Monitoring.HealthIntervalSeconds);
        Assert.Equal(25, reloaded.Notifications.HighWaterUnixNano);
        Assert.Equal("panel-25", reloaded.Startup.LastPanelId);
        Assert.Equal(25, reloaded.Updates.LastCheckUnix);
        Assert.Equal(store.Current, reloaded);
    }

    [Fact]
    public async Task Two_appearance_stores_on_one_path_saving_at_once_leave_a_valid_file()
    {
        var path = NewPath();
        var a = new FileAppearanceSettingsStore(path);
        var b = new FileAppearanceSettingsStore(path);
        var barrier = new Barrier(2);

        var workers = new[]
        {
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 30; i++) { a.Save(new AppearanceSettings(AppearanceStyle.Linear, i % 2 == 0 ? AppearanceMode.Dark : AppearanceMode.Light)); } }),
            Task.Run(() => { barrier.SignalAndWait(); for (var i = 0; i < 30; i++) { b.Save(new AppearanceSettings(AppearanceStyle.Tui, i % 2 == 0 ? AppearanceMode.Light : AppearanceMode.Dark)); } }),
        };

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path)));
        Assert.Equal(a.Load(), b.Load());
    }
}
