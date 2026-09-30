using System.Text.Json.Nodes;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>The settings file: what is saved, what is tolerated, and that nothing about it can break the app.</summary>
public sealed class AppearanceSettingsStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string PathIn(params string[] parts) => System.IO.Path.Combine(new[] { _temp.Path }.Concat(parts).ToArray());

    private FileAppearanceSettingsStore Store(out string path)
    {
        path = PathIn("DefenseClaw.App", "settings.json");
        return new FileAppearanceSettingsStore(path);
    }

    // ------------------------------------------------------------------ round trip

    [Theory]
    [InlineData("Default", "System")]
    [InlineData("Default", "Light")]
    [InlineData("Default", "Dark")]
    [InlineData("Linear", "System")]
    [InlineData("Linear", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Tui", "System")]
    [InlineData("Tui", "Light")]
    [InlineData("Tui", "Dark")]
    public void Every_combination_survives_a_save_and_a_load(string style, string mode)
    {
        var store = Store(out var path);
        var settings = new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode));

        store.Save(settings);

        Assert.Equal(settings, new FileAppearanceSettingsStore(path).Load());
    }

    [Fact]
    public void A_missing_file_is_the_default_look_following_the_system()
    {
        var store = Store(out var path);

        Assert.False(File.Exists(path));
        Assert.Equal(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.System), store.Load());
    }

    [Fact]
    public void The_file_is_versioned_named_and_readable_by_a_person()
    {
        var store = Store(out var path);

        store.Save(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark));

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(1, (int)root["schemaVersion"]!);
        Assert.Equal("tui", (string)root["appearance"]!["style"]!);
        Assert.Equal("dark", (string)root["appearance"]!["mode"]!);
        Assert.Equal(1, FileAppearanceSettingsStore.SchemaVersion);
    }

    [Fact]
    public void The_default_location_is_the_apps_own_folder_under_local_app_data_never_the_cli_home()
    {
        var path = FileAppearanceSettingsStore.DefaultPath;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(System.IO.Path.Combine(local, "DefenseClaw.App", "settings.json"), path);
        Assert.DoesNotContain(".defenseclaw", path, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ damaged and unexpected files

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("{")]
    [InlineData("{ \"schemaVersion\": 1, \"appearance\": { \"style\": \"lin")]
    [InlineData("not json at all")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"linear\"")]
    [InlineData("{}")]
    [InlineData("{ \"appearance\": null }")]
    [InlineData("{ \"appearance\": [1,2] }")]
    [InlineData("{ \"appearance\": \"linear\" }")]
    public void A_corrupt_or_wrongly_shaped_file_is_the_defaults_and_never_throws(string content)
    {
        var store = Store(out var path);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        Assert.Equal(AppearanceSettings.Defaults, store.Load());
    }

    [Fact]
    public void A_file_of_binary_junk_is_the_defaults()
    {
        var store = Store(out var path);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0x00, 0x01, 0x80, 0x81, 0x00, 0x00 });

        Assert.Equal(AppearanceSettings.Defaults, store.Load());
    }

    [Theory]
    [InlineData("purple", "dark", "Default", "Dark")]
    [InlineData("linear", "sepia", "Linear", "System")]
    [InlineData("", "", "Default", "System")]
    [InlineData("1", "2", "Default", "System")]
    [InlineData("-1", "99", "Default", "System")]
    [InlineData("LINEAR", "LIGHT", "Linear", "Light")]
    [InlineData("Tui", "Dark", "Tui", "Dark")]
    public void An_unknown_value_falls_back_for_that_field_alone(string style, string mode, string expectedStyle, string expectedMode)
    {
        var store = Store(out var path);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{{ \"schemaVersion\": 1, \"appearance\": {{ \"style\": \"{style}\", \"mode\": \"{mode}\" }} }}");

        Assert.Equal(
            new AppearanceSettings(Enum.Parse<AppearanceStyle>(expectedStyle), Enum.Parse<AppearanceMode>(expectedMode)),
            store.Load());
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_an_unknown_value_not_a_crash()
    {
        var store = Store(out var path);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"appearance\": { \"style\": 3, \"mode\": { \"x\": 1 } } }");

        Assert.Equal(AppearanceSettings.Defaults, store.Load());
    }

    [Fact]
    public void A_missing_field_keeps_the_other()
    {
        var store = Store(out var path);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"appearance\": { \"style\": \"tui\" } }");

        Assert.Equal(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.System), store.Load());
    }

    [Fact]
    public void Saving_over_a_corrupt_file_replaces_it_with_a_good_one()
    {
        var store = Store(out var path);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{{{ garbage");

        store.Save(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Light));

        Assert.Equal(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Light), store.Load());
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(path)));
    }

    // ------------------------------------------------------------------ a shared file

    [Fact]
    public void Saving_keeps_every_other_section_and_a_newer_schema_version_as_found()
    {
        var store = Store(out var path);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"schemaVersion\": 7, \"other\": { \"keep\": [1, 2, 3] }, \"appearance\": { \"style\": \"linear\", \"mode\": \"dark\", \"future\": true } }");

        store.Save(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Light));

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(7, (int)root["schemaVersion"]!);
        Assert.Equal(3, root["other"]!["keep"]!.AsArray().Count);
        Assert.Equal("tui", (string)root["appearance"]!["style"]!);
        Assert.Equal("light", (string)root["appearance"]!["mode"]!);
    }

    // ------------------------------------------------------------------ writes

    [Fact]
    public void A_save_leaves_no_temp_file_behind_and_creates_the_folder()
    {
        var store = Store(out var path);

        store.Save(AppearanceSettings.Defaults);
        store.Save(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        var directory = System.IO.Path.GetDirectoryName(path)!;
        Assert.Equal(new[] { path }, Directory.GetFiles(directory));
    }

    [Fact]
    public void A_save_that_cannot_happen_is_swallowed()
    {
        // The settings path is a directory: nothing can be written there, and the app must carry on regardless.
        var path = PathIn("settings.json");
        _ = Directory.CreateDirectory(path);
        var store = new FileAppearanceSettingsStore(path);

        store.Save(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        Assert.Equal(AppearanceSettings.Defaults, store.Load());
        Assert.Empty(Directory.GetFileSystemEntries(path));
    }

    [Fact]
    public void A_locked_file_is_a_failed_load_not_an_exception()
    {
        var store = Store(out var path);
        store.Save(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Dark));

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(AppearanceSettings.Defaults, store.Load());
    }
}
