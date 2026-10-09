using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The palette's recents (CUST-264): the last five commands it ran are remembered by id in <c>settings.json</c>, listed first while the search
/// is empty, and still there after a restart; and the ranking's new rule, the first letters of a title's words ("rg" for "Restart gateway").
/// Everything runs on the palette's view-model and scratch settings files; no window is built.
/// </summary>
public sealed class PaletteRecentsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("DefenseClaw.App.settings.json");

    /// <summary>A store over the scratch file that shares nothing with another: what stands for "the app was started again".</summary>
    private AppSettingsStore Store() => AppSettingsStore.OpenFresh(SettingsPath);

    private static ShellCommand Command(string id, string title, bool enabled = true, string category = "App") =>
        new(id, title, category, string.Empty, null, string.Empty, enabled, enabled ? null : "not now", () => { });

    private static List<ShellCommand> Rows(params string[] ids) => ids.Select(id => Command(id, "Command " + id)).ToList();

    private static string[] Ids(CommandPaletteViewModel vm) => vm.Results.Select(r => r.Command.Id).ToArray();

    private CommandPaletteViewModel Palette(AppSettingsStore? store, IReadOnlyList<ShellCommand> rows)
    {
        var vm = new CommandPaletteViewModel { RecentsStore = store };
        vm.Load(rows);
        return vm;
    }

    private static void Choose(CommandPaletteViewModel vm, string id) =>
        Assert.True(vm.Choose(vm.Results.First(r => r.Command.Id == id)), id);

    // ------------------------------------------------------------------ the list of ids

    [Fact]
    public void A_command_joins_the_front_moves_there_when_it_is_in_already_and_the_oldest_falls_off_after_five()
    {
        IReadOnlyList<string> recent = Array.Empty<string>();
        foreach (var id in new[] { "a", "b", "c", "d", "e" })
        {
            recent = PaletteRecents.Push(recent, id);
        }

        Assert.Equal(new[] { "e", "d", "c", "b", "a" }, recent);

        recent = PaletteRecents.Push(recent, "c");
        Assert.Equal(new[] { "c", "e", "d", "b", "a" }, recent);

        recent = PaletteRecents.Push(recent, "f");
        Assert.Equal(new[] { "f", "c", "e", "d", "b" }, recent);
        Assert.Equal(PaletteSettings.MaxRecent, recent.Count);
        Assert.Equal(5, PaletteSettings.MaxRecent);
    }

    [Fact]
    public void A_blank_id_changes_nothing_and_ids_are_compared_exactly()
    {
        var recent = new[] { "cli.doctor" };

        Assert.Same(recent, PaletteRecents.Push(recent, "  "));
        Assert.Same(recent, PaletteRecents.Push(recent, string.Empty));
        Assert.Equal(new[] { "cli.Doctor", "cli.doctor" }, PaletteRecents.Push(recent, "cli.Doctor"));
    }

    [Fact]
    public void The_remembered_rows_move_to_the_front_in_the_order_they_were_run_and_the_rest_keep_theirs()
    {
        var rows = Rows("a", "b", "c", "d", "e");

        var ordered = PaletteRecents.Promote(rows, new[] { "d", "b" }, out var promoted);

        Assert.Equal(new[] { "d", "b", "a", "c", "e" }, ordered.Select(r => r.Id));
        Assert.Equal(2, promoted);
    }

    [Fact]
    public void A_remembered_id_with_no_row_is_left_out_and_a_row_that_cannot_be_chosen_now_is_still_a_recent_one()
    {
        var rows = new List<ShellCommand> { Command("a", "A"), Command("off", "Off", enabled: false), Command("c", "C") };

        var ordered = PaletteRecents.Promote(rows, new[] { "gone", "off", "c" }, out var promoted);

        // "off" leads with the recents, still off: the palette says why in place of its description rather than leaving the operator to look for it.
        Assert.Equal(new[] { "off", "c", "a" }, ordered.Select(r => r.Id));
        Assert.Equal(2, promoted);
        Assert.False(ordered[0].IsEnabled);
        Assert.Equal("not now", ordered[0].DisabledReason);
    }

    [Fact]
    public void Nothing_remembered_leaves_the_list_as_it_is()
    {
        var rows = Rows("a", "b");

        Assert.Same(rows, PaletteRecents.Promote(rows, Array.Empty<string>(), out var none));
        Assert.Equal(0, none);
        Assert.Same(rows, PaletteRecents.Promote(rows, new[] { "unknown" }, out var unknown));
        Assert.Equal(0, unknown);
    }

    // ------------------------------------------------------------------ the palette

    [Fact]
    public void An_empty_search_lists_what_was_run_last_first_and_a_typed_one_does_not()
    {
        var store = Store();
        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "gateway.restart", "nav.alerts" } } });
        var rows = new List<ShellCommand>
        {
            Command("nav.overview", "Go to Overview"),
            Command("nav.alerts", "Go to Alerts"),
            Command("gateway.restart", "Restart gateway", category: "Gateway"),
            Command("app.shortcuts", "Show keyboard shortcuts"),
        };

        var vm = Palette(store, rows);

        Assert.Equal(new[] { "gateway.restart", "nav.alerts", "nav.overview", "app.shortcuts" }, Ids(vm));
        Assert.Equal(new[] { true, true, false, false }, vm.Results.Select(r => r.IsRecent));
        Assert.Equal("gateway.restart", vm.Selected!.Command.Id);
        Assert.Equal("4 of 4 commands", vm.CountLine);

        // Typing ranks by what matches, and nothing is marked recent.
        vm.Query = "go to";
        Assert.Equal(new[] { "nav.overview", "nav.alerts" }, Ids(vm));
        Assert.All(vm.Results, r => Assert.False(r.IsRecent));

        // Clearing it brings them back.
        vm.Query = string.Empty;
        Assert.Equal("gateway.restart", vm.Results[0].Command.Id);
        Assert.True(vm.Results[0].IsRecent);
    }

    [Fact]
    public void A_recent_row_says_so_to_a_screen_reader_and_in_its_chip()
    {
        var item = new PaletteItem(Command("nav.alerts", "Go to Alerts", category: "Panel"), isRecent: true);

        Assert.True(item.IsRecent);
        Assert.Equal("Go to Alerts, Panel, recent", item.AutomationName);
        Assert.Equal("Go to Alerts, Panel", new PaletteItem(Command("nav.alerts", "Go to Alerts", category: "Panel")).AutomationName);
    }

    [Fact]
    public void A_palette_with_no_settings_lists_the_rows_in_registry_order_as_before()
    {
        var rows = Rows("a", "b", "c");

        var vm = Palette(null, rows);
        Choose(vm, "c");

        Assert.Equal(new[] { "a", "b", "c" }, Ids(Palette(null, rows)));
    }

    [Fact]
    public void Choosing_a_command_remembers_it_newest_first_and_only_five()
    {
        var store = Store();
        var rows = Rows("a", "b", "c", "d", "e", "f", "g");
        var vm = Palette(store, rows);

        foreach (var id in new[] { "a", "b", "c", "d", "e", "f" })
        {
            Choose(vm, id);
        }

        Assert.Equal(new[] { "f", "e", "d", "c", "b" }, store.Current.Palette.RecentCommandIds);

        Choose(vm, "c");
        Assert.Equal(new[] { "c", "f", "e", "d", "b" }, store.Current.Palette.RecentCommandIds);

        // And the next time the palette opens, those five lead.
        vm.Load(rows);
        Assert.Equal(new[] { "c", "f", "e", "d", "b", "a", "g" }, Ids(vm));
    }

    [Fact]
    public void The_recents_survive_a_restart()
    {
        var first = Store();
        var rows = Rows("a", "b", "c", "d");
        var vm = Palette(first, rows);
        Choose(vm, "b");
        Choose(vm, "d");

        // The app starts again: a new store reads the file the old one wrote, and a new palette lists them first.
        var restarted = Store();
        Assert.Equal(new[] { "d", "b" }, restarted.Current.Palette.RecentCommandIds);
        Assert.Equal(new[] { "d", "b", "a", "c" }, Ids(Palette(restarted, rows)));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal(new[] { "d", "b" }, json["palette"]!["recent"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void A_command_that_cannot_be_chosen_is_not_remembered_and_a_remembered_one_that_cannot_be_chosen_now_is_kept_and_listed_with_its_reason()
    {
        var store = Store();
        var rows = new List<ShellCommand> { Command("a", "A"), Command("gateway.start", "Start gateway", enabled: false), Command("c", "C") };
        var vm = Palette(store, rows);

        Assert.False(vm.Choose(vm.Results[1]));
        Assert.Empty(store.Current.Palette.RecentCommandIds);

        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "gateway.start", "c" } } });
        vm.Load(rows);

        // It leads with the other recent, greyed out, and says why in place of its description; Enter still goes to the first row that can be chosen.
        Assert.Equal(new[] { "gateway.start", "c", "a" }, Ids(vm));
        Assert.Equal(new[] { true, true, false }, vm.Results.Select(r => r.IsRecent));
        Assert.False(vm.Results[0].IsEnabled);
        Assert.Equal("not now", vm.Results[0].DetailLine);
        Assert.Equal("Start gateway, App, recent, unavailable. not now", vm.Results[0].AutomationName);
        Assert.Equal("c", vm.Selected!.Command.Id);
        Assert.Equal(new[] { "gateway.start", "c" }, store.Current.Palette.RecentCommandIds);
    }

    [Fact]
    public void Re_run_last_and_cancel_running_are_never_remembered_so_they_cannot_crowd_out_what_the_operator_ran()
    {
        var store = Store();
        var rows = new List<ShellCommand>
        {
            Command("nav.alerts", "Go to Alerts"),
            Command(ShellCommandRegistry.RerunLastId, "Re-run last command"),
            Command(ShellCommandRegistry.CancelRunningId, "Cancel running command"),
        };
        var vm = Palette(store, rows);

        Choose(vm, "nav.alerts");
        Choose(vm, ShellCommandRegistry.RerunLastId);
        Choose(vm, ShellCommandRegistry.CancelRunningId);

        Assert.Equal(new[] { "nav.alerts" }, store.Current.Palette.RecentCommandIds);
    }

    [Fact]
    public void A_row_that_asks_for_a_value_is_remembered_when_it_runs_not_when_it_asks()
    {
        var store = Store();
        var h = new CuratedCommand(new[] { "skill", "block" }, "Enforce", "Block a skill", string.Empty, new[] { "<skill-name>" }, TuiName: "block skill", ArgumentHint: "<skill-name>");
        var row = new ShellCommand(h.Id, h.Title, "Enforce", h.Summary, null, string.Empty, true, null, () => { }, h, () => { }, _ => { }, _ => { });
        var vm = Palette(store, new[] { row });

        // The first Enter asks for the value; nothing ran.
        Assert.True(vm.ChooseSelected());
        Assert.Empty(store.Current.Palette.RecentCommandIds);

        vm.Results[0].ArgumentText = "pdf-tools";
        Assert.True(vm.ChooseSelected());
        Assert.Equal(new[] { "cli.block.skill" }, store.Current.Palette.RecentCommandIds);
    }

    [Fact]
    public void Swapping_the_rows_under_an_open_palette_keeps_the_recents_first()
    {
        var store = Store();
        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "c" } } });
        var vm = Palette(store, Rows("a", "b", "c"));
        Assert.Equal("c", vm.Results[0].Command.Id);

        vm.Reload(Rows("a", "b", "c", "d"));

        Assert.Equal(new[] { "c", "a", "b", "d" }, Ids(vm));
        Assert.True(vm.Results[0].IsRecent);
    }

    // ------------------------------------------------------------------ the settings section

    [Fact]
    public void The_section_is_empty_by_default_and_a_fresh_install_writes_nothing_for_it()
    {
        var store = Store();

        Assert.Empty(store.Current.Palette.RecentCommandIds);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void A_hand_edited_list_is_cleaned_on_the_way_in()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(
            SettingsPath,
            """
            { "palette": { "recent": [ "a", "b", "a", "", "  ", 7, null, { "x": 1 }, "c", "d", "e", "f", "g" ] } }
            """);

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Store().Current.Palette.RecentCommandIds);
    }

    [Theory]
    [InlineData("""{ "palette": { "recent": "a" } }""")]
    [InlineData("""{ "palette": { "recent": 3 } }""")]
    [InlineData("""{ "palette": { "recent": null } }""")]
    [InlineData("""{ "palette": [ "a" ] }""")]
    [InlineData("""{ "palette": 7 }""")]
    [InlineData("""{ "palette": { } }""")]
    public void Anything_else_in_its_place_reads_as_none(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);

        Assert.Empty(Store().Current.Palette.RecentCommandIds);
    }

    [Fact]
    public void An_id_too_long_to_be_one_is_dropped()
    {
        var tooLong = new string('x', PaletteSettings.MaxIdLength + 1);

        var settings = new PaletteSettings { RecentCommandIds = new[] { tooLong, "cli.doctor", new string('y', PaletteSettings.MaxIdLength) } };

        Assert.Equal(new[] { "cli.doctor", new string('y', PaletteSettings.MaxIdLength) }, settings.RecentCommandIds);
    }

    [Fact]
    public void Writing_the_same_list_again_is_not_a_change_and_other_members_of_the_section_survive_a_write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "palette": { "recent": [ "a" ], "pinned": [ "z" ] }, "somethingNew": { "keep": true } }""");
        var store = Store();
        var changes = 0;
        store.Changed += (_, _) => changes++;

        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "a" } } });
        Assert.Equal(0, changes);

        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "b", "a" } } });
        Assert.Equal(1, changes);

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal(new[] { "b", "a" }, json["palette"]!["recent"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("z", (string?)json["palette"]!["pinned"]![0]);
        Assert.True((bool?)json["somethingNew"]!["keep"]);
    }

    [Fact]
    public void Only_the_palette_section_is_named_as_changed()
    {
        var store = Store();
        AppSettingsChangedEventArgs? seen = null;
        store.Changed += (_, e) => seen = e;

        _ = store.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "a" } } });

        Assert.Equal(AppSettingsSections.Palette, seen!.Sections);
        Assert.True(seen.Affects(AppSettingsSections.Palette));
        Assert.True(AppSettingsSections.All.HasFlag(AppSettingsSections.Palette));
    }

    // ------------------------------------------------------------------ initials

    [Theory]
    [InlineData("Restart gateway status", "Rgs")]
    [InlineData("Go to Alerts", "GtA")]
    [InlineData("scan skill --all", "ssa")]
    [InlineData("setup local-observability up", "slou")]
    [InlineData("Turn on Start with Windows", "ToSwW")]
    [InlineData("  spaced   out  ", "so")]
    [InlineData("2fa login", "2l")]
    [InlineData("", "")]
    [InlineData("--", "")]
    public void The_initials_of_a_title_are_the_first_letter_of_each_of_its_words(string title, string expected) =>
        Assert.Equal(expected, CommandPaletteViewModel.Initials(title));

    [Theory]
    [InlineData("Re-run last command", "Rlc")]
    [InlineData("setup local-observability up", "slu")]
    [InlineData("scan skill --all", "ssa")]
    [InlineData("Restart gateway status", "Rgs")]
    [InlineData("Go to Alerts", "GtA")]
    [InlineData("  spaced   out  ", "so")]
    [InlineData("--", "")]
    [InlineData("", "")]
    public void With_a_hyphenated_word_counted_once_the_initials_are_these(string title, string expected) =>
        Assert.Equal(expected, CommandPaletteViewModel.SpacedInitials(title));

    private static ShellCommand[] Titles(params string[] titles) =>
        titles.Select((title, i) => Command("c" + i, title)).ToArray();

    [Fact]
    public void A_hyphenated_word_can_be_typed_as_one_initial_or_as_its_parts()
    {
        var commands = Titles("Re-run last command", "setup local-observability up", "Open config editor");

        Assert.Equal(new[] { "Re-run last command" }, CommandPaletteViewModel.Rank(commands, "rlc").Select(c => c.Title));
        Assert.Equal(new[] { "Re-run last command" }, CommandPaletteViewModel.Rank(commands, "rrlc").Select(c => c.Title));
        Assert.Equal(new[] { "setup local-observability up" }, CommandPaletteViewModel.Rank(commands, "slu").Select(c => c.Title));
        Assert.Equal(new[] { "setup local-observability up" }, CommandPaletteViewModel.Rank(commands, "slou").Select(c => c.Title));
    }

    [Fact]
    public void The_first_letters_of_the_words_find_the_row_without_typing_them_out()
    {
        var commands = Titles("Restart gateway", "Refresh gateway status", "Go to Alerts", "Go to Audit", "Open config editor", "Move reports");

        Assert.Equal(new[] { "Restart gateway", "Refresh gateway status" }, CommandPaletteViewModel.Rank(commands, "rg").Select(c => c.Title));
        Assert.Equal(new[] { "Refresh gateway status" }, CommandPaletteViewModel.Rank(commands, "rgs").Select(c => c.Title));
        Assert.Equal(new[] { "Go to Alerts", "Go to Audit" }, CommandPaletteViewModel.Rank(commands, "gta").Select(c => c.Title));
        Assert.Equal(new[] { "Open config editor" }, CommandPaletteViewModel.Rank(commands, "OCE").Select(c => c.Title));
        Assert.Equal(new[] { "Move reports" }, CommandPaletteViewModel.Rank(commands, "mr").Select(c => c.Title));
    }

    [Fact]
    public void Initials_rank_below_the_start_of_the_title_and_of_a_word_and_above_a_hit_in_the_middle_of_one()
    {
        var commands = Titles("Arg parser", "Restart gateway", "Rg tool", "Big rg thing");

        // "Rg tool" starts with it (100), "Big rg thing" starts a word with it (80), "Restart gateway" has it as its initials (70), "Arg parser" only inside a word (60).
        Assert.Equal(
            new[] { "Rg tool", "Big rg thing", "Restart gateway", "Arg parser" },
            CommandPaletteViewModel.Rank(commands, "rg").Select(c => c.Title));
    }

    [Fact]
    public void Initials_are_for_the_front_of_the_title_and_for_two_letters_or_more()
    {
        var commands = Titles("Restart gateway status", "Gateway status");

        // "gs" is the initials of "Gateway status" but only the middle of the other title's.
        Assert.Equal(new[] { "Gateway status" }, CommandPaletteViewModel.Rank(commands, "gs").Select(c => c.Title));

        // One letter is the start of a word, as before.
        Assert.Equal(new[] { "Gateway status", "Restart gateway status" }, CommandPaletteViewModel.Rank(commands, "g").Select(c => c.Title));
    }

    [Fact]
    public void Every_term_still_has_to_match_and_initials_can_be_one_of_them()
    {
        var commands = Titles("Restart gateway", "Refresh gateway status", "Go to Alerts");

        Assert.Equal(new[] { "Refresh gateway status" }, CommandPaletteViewModel.Rank(commands, "rg status").Select(c => c.Title));
        Assert.Empty(CommandPaletteViewModel.Rank(commands, "rg alerts"));
    }

    [Fact]
    public void A_cli_row_matches_on_its_initials_too_a_little_lower_than_a_panel_of_the_same_match()
    {
        var cli = new CuratedCommand(new[] { "setup", "local-observability", "up" }, "Setup", "Start the stack", string.Empty, Array.Empty<string>(), TuiName: "setup local-observability up");
        var row = new ShellCommand(cli.Id, cli.Title, "Setup", cli.Summary, null, string.Empty, true, null, () => { }, cli);
        var panel = Command("nav.x", "Show local observability untouched");

        // Both have the initials "slou"; the CLI row is worth CliPenalty less.
        Assert.Equal(new[] { "nav.x", cli.Id }, CommandPaletteViewModel.Rank(new[] { row, panel }, "slou").Select(c => c.Id));
    }
}
