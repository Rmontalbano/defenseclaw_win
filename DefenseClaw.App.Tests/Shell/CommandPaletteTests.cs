using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Shell;

public sealed class CommandPaletteTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public CommandPaletteTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static ShellCommand Command(
        string id,
        string title,
        string category = "App",
        string keywords = "",
        bool enabled = true,
        string? reason = null,
        string? shortcut = null,
        string description = "") => new(id, title, category, description, shortcut, keywords, enabled, reason, () => { });

    /// <summary>The palette as it opens: a Go-to entry per panel, then app actions and gateway controls.</summary>
    private IReadOnlyList<ShellCommand> RealisticCommands()
    {
        var catalog = new PanelCatalog(_services);
        var commands = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { });
        commands.Add(Command("app.refresh-panel", "Refresh current panel", keywords: "reload update data", shortcut: "F5"));
        commands.Add(Command("app.refresh-gateway", "Refresh gateway status", keywords: "poll health reload status strip"));
        commands.Add(Command("app.config-editor", "Open config editor", keywords: "config yaml settings edit configuration file"));
        commands.Add(Command("app.check-updates", "Check for updates", keywords: "update upgrade version release new"));
        commands.Add(Command("app.toggle-autostart", "Turn on Start with Windows", keywords: "autostart startup login sign in boot toggle"));
        commands.Add(Command("app.shortcuts", "Show keyboard shortcuts", keywords: "keys keyboard help hotkeys ?", shortcut: "F1"));
        commands.Add(Command("gateway.start", "Start gateway", "Gateway", "daemon sidecar service defenseclaw-gateway"));
        commands.Add(Command("gateway.stop", "Stop gateway", "Gateway", "daemon sidecar service defenseclaw-gateway"));
        commands.Add(Command("gateway.restart", "Restart gateway", "Gateway", "daemon sidecar service defenseclaw-gateway"));
        return commands;
    }

    private static string[] Titles(IEnumerable<ShellCommand> commands) => commands.Select(c => c.Title).ToArray();

    // ------------------------------------------------------------------ ranking

    [Fact]
    public void Typing_registries_puts_go_to_registries_first()
    {
        var ranked = CommandPaletteViewModel.Rank(RealisticCommands(), "registries");

        Assert.Equal("Go to Registries", ranked[0].Title);
    }

    [Theory]
    [InlineData("registries", "Go to Registries")]
    [InlineData("REGISTRIES", "Go to Registries")]
    [InlineData("  registries  ", "Go to Registries")]
    [InlineData("reg", "Go to Registries")]
    [InlineData("alerts", "Go to Alerts")]
    [InlineData("inventory", "Go to Inventory")]
    [InlineData("ai disc", "Go to AI Discovery")]
    [InlineData("setup", "Go to Setup")]
    [InlineData("config", "Open config editor")]
    [InlineData("updates", "Check for updates")]
    [InlineData("restart", "Restart gateway")]
    public void The_command_the_operator_is_after_ranks_first(string query, string expectedFirst)
    {
        var ranked = CommandPaletteViewModel.Rank(RealisticCommands(), query);

        Assert.Equal(expectedFirst, ranked[0].Title);
    }

    [Fact]
    public void An_empty_or_missing_query_returns_the_registry_order_unchanged()
    {
        var commands = RealisticCommands();

        Assert.Same(commands, CommandPaletteViewModel.Rank(commands, string.Empty));
        Assert.Same(commands, CommandPaletteViewModel.Rank(commands, null));
        Assert.Same(commands, CommandPaletteViewModel.Rank(commands, "   "));
    }

    [Fact]
    public void A_title_prefix_beats_a_word_start_beats_a_substring_beats_the_category_beats_a_keyword()
    {
        var commands = new[]
        {
            Command("k", "Zed", keywords: "alerts"),
            Command("c", "Other", category: "Alerts group"),
            Command("s", "Realerts thing"),
            Command("w", "Go to Alerts"),
            Command("p", "Alerts refresh"),
        };

        var ranked = CommandPaletteViewModel.Rank(commands, "alerts");

        Assert.Equal(new[] { "p", "w", "s", "c", "k" }, ranked.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Equal_scores_keep_the_order_the_commands_were_registered_in()
    {
        var commands = new[]
        {
            Command("a", "Go to Alerts"),
            Command("b", "Go to Audit"),
            Command("c", "Go to Activity"),
        };

        var ranked = CommandPaletteViewModel.Rank(commands, "go");

        Assert.Equal(new[] { "a", "b", "c" }, ranked.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Every_term_must_match_somewhere_and_terms_add_up()
    {
        var commands = new[]
        {
            Command("nav.alerts", "Go to Alerts", "Panel", "open navigate alerts Monitor"),
            Command("nav.audit", "Go to Audit", "Panel", "open navigate audit Monitor"),
            Command("gw", "Restart gateway", "Gateway", "daemon"),
        };

        Assert.Equal(new[] { "nav.alerts" }, CommandPaletteViewModel.Rank(commands, "go alerts").Select(c => c.Id).ToArray());
        Assert.Empty(CommandPaletteViewModel.Rank(commands, "go gateway"));
        Assert.Equal(new[] { "gw" }, CommandPaletteViewModel.Rank(commands, "gateway daemon").Select(c => c.Id).ToArray());
    }

    [Fact]
    public void A_term_matches_the_category_and_the_keywords_when_the_title_misses()
    {
        var commands = new[]
        {
            Command("a", "Refresh gateway status", "App", "poll health"),
            Command("b", "Restart gateway", "Gateway", "daemon"),
        };

        Assert.Equal(new[] { "b" }, CommandPaletteViewModel.Rank(commands, "gateway daemon").Select(c => c.Id).ToArray());
        Assert.Equal(new[] { "a" }, CommandPaletteViewModel.Rank(commands, "poll").Select(c => c.Id).ToArray());
        Assert.Equal(new[] { "a", "b" }, CommandPaletteViewModel.Rank(commands, "gateway").Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Nothing_matching_is_an_empty_result_not_an_error()
    {
        Assert.Empty(CommandPaletteViewModel.Rank(RealisticCommands(), "zzzzzz"));
    }

    [Fact]
    public void A_word_start_is_ranked_above_a_mid_word_hit()
    {
        var commands = new[]
        {
            Command("mid", "Go to Registries"),
            Command("start", "Registries first"),
        };

        Assert.Equal(new[] { "start", "mid" }, CommandPaletteViewModel.Rank(commands, "regis").Select(c => c.Id).ToArray());

        // Neither title starts a word with "istries", so both score as plain substrings and keep registry order.
        Assert.Equal(new[] { "mid", "start" }, CommandPaletteViewModel.Rank(commands, "istries").Select(c => c.Id).ToArray());
    }

    [Fact]
    public void Ranking_requires_a_command_list()
    {
        Assert.Throws<ArgumentNullException>(() => CommandPaletteViewModel.Rank(null!, "x"));
    }

    // ------------------------------------------------------------------ the palette's own state

    [Fact]
    public void Loading_shows_every_command_with_the_first_available_one_selected()
    {
        var vm = new CommandPaletteViewModel();
        var commands = new[]
        {
            Command("off", "Disabled thing", enabled: false, reason: "not now"),
            Command("a", "First"),
            Command("b", "Second"),
        };

        vm.Load(commands);

        Assert.Equal(3, vm.Results.Count);
        Assert.Equal("a", vm.Selected!.Command.Id);
        Assert.Equal("First, 2 of 3", vm.StatusLine);
    }

    [Fact]
    public void Typing_filters_the_list_and_reselects_the_first_available_match()
    {
        var vm = new CommandPaletteViewModel();
        vm.Load(RealisticCommands());

        vm.Query = "registries";

        Assert.Equal("Go to Registries", vm.Results[0].Title);
        Assert.Equal("Go to Registries", vm.Selected!.Title);
        Assert.Equal("Go to Registries, 1 of " + vm.Results.Count, vm.StatusLine);
    }

    [Fact]
    public void A_query_with_no_match_says_so()
    {
        var vm = new CommandPaletteViewModel();
        vm.Load(RealisticCommands());

        vm.Query = "zzzzzz";

        Assert.Empty(vm.Results);
        Assert.Null(vm.Selected);
        Assert.Equal("No matching commands", vm.StatusLine);
    }

    [Fact]
    public void Reloading_clears_a_previous_search()
    {
        var vm = new CommandPaletteViewModel();
        vm.Load(RealisticCommands());
        vm.Query = "registries";

        vm.Load(RealisticCommands());

        Assert.Equal(string.Empty, vm.Query);
        Assert.Equal(RealisticCommands().Count, vm.Results.Count);
    }

    [Fact]
    public void The_arrow_keys_skip_unavailable_rows_and_stop_at_the_ends()
    {
        var vm = new CommandPaletteViewModel();
        vm.Load(new[]
        {
            Command("a", "A"),
            Command("off1", "Off one", enabled: false),
            Command("off2", "Off two", enabled: false),
            Command("b", "B"),
            Command("c", "C"),
        });
        Assert.Equal("a", vm.Selected!.Command.Id);

        vm.MoveSelection(1);
        Assert.Equal("b", vm.Selected!.Command.Id);
        vm.MoveSelection(1);
        Assert.Equal("c", vm.Selected!.Command.Id);
        vm.MoveSelection(1);
        Assert.Equal("c", vm.Selected!.Command.Id);

        vm.MoveSelection(-1);
        Assert.Equal("b", vm.Selected!.Command.Id);
        vm.MoveSelection(-1);
        Assert.Equal("a", vm.Selected!.Command.Id);
        vm.MoveSelection(-1);
        Assert.Equal("a", vm.Selected!.Command.Id);

        vm.MoveSelection(3);
        Assert.Equal("c", vm.Selected!.Command.Id);
    }

    [Fact]
    public void Moving_in_an_empty_list_does_nothing()
    {
        var vm = new CommandPaletteViewModel();
        vm.Load(Array.Empty<ShellCommand>());

        vm.MoveSelection(1);
        vm.MoveSelection(-1);

        Assert.Null(vm.Selected);
        Assert.Equal("No matching commands", vm.StatusLine);
    }

    [Fact]
    public void A_list_of_only_unavailable_commands_selects_nothing_and_says_so()
    {
        var vm = new CommandPaletteViewModel();
        vm.Load(new[] { Command("a", "A", enabled: false), Command("b", "B", enabled: false) });

        Assert.Null(vm.Selected);
        Assert.Equal("2 commands, none available", vm.StatusLine);
        Assert.False(vm.ChooseSelected());
    }

    [Fact]
    public void Choosing_an_available_command_raises_chosen_then_close_and_an_unavailable_one_does_nothing()
    {
        var vm = new CommandPaletteViewModel();
        var events = new List<string>();
        vm.CommandChosen += (_, command) => events.Add("chosen:" + command.Id);
        vm.CloseRequested += (_, _) => events.Add("close");
        var off = Command("off", "Off", enabled: false, reason: "not now");
        vm.Load(new[] { off, Command("a", "A") });

        Assert.False(vm.Choose(vm.Results[0]));
        Assert.False(vm.Choose(null));
        Assert.Empty(events);

        Assert.True(vm.ChooseSelected());
        Assert.Equal(new[] { "chosen:a", "close" }, events);
    }

    [Fact]
    public void Requesting_close_raises_close_only()
    {
        var vm = new CommandPaletteViewModel();
        var events = new List<string>();
        vm.CommandChosen += (_, _) => events.Add("chosen");
        vm.CloseRequested += (_, _) => events.Add("close");

        vm.RequestClose();

        Assert.Equal(new[] { "close" }, events);
    }

    [Fact]
    public void Loading_requires_a_list()
    {
        Assert.Throws<ArgumentNullException>(() => new CommandPaletteViewModel().Load(null!));
    }

    // ------------------------------------------------------------------ what a row says

    [Fact]
    public void A_disabled_row_shows_why_in_place_of_its_description_and_says_so_to_a_screen_reader()
    {
        var item = new PaletteItem(Command("a", "Start gateway", "Gateway", enabled: false, reason: "The gateway is already running.", description: "Start it."));

        Assert.False(item.IsEnabled);
        Assert.Equal("The gateway is already running.", item.DetailLine);
        Assert.Equal("Start gateway, Gateway, unavailable. The gateway is already running.", item.AutomationName);
        Assert.Equal(item.AutomationName, item.ToString());
    }

    [Fact]
    public void An_available_row_shows_its_description_and_announces_its_shortcut()
    {
        var item = new PaletteItem(Command("a", "Go to Alerts", "Panel", shortcut: "Ctrl+2", description: "Monitor · opens the Alerts panel"));

        Assert.Equal("Monitor · opens the Alerts panel", item.DetailLine);
        Assert.True(item.HasShortcut);
        Assert.Equal("Go to Alerts, Panel, shortcut Ctrl+2", item.AutomationName);
    }

    [Fact]
    public void A_disabled_row_with_no_reason_still_has_a_detail_line()
    {
        var item = new PaletteItem(Command("a", "X", enabled: false));

        Assert.Equal("Not available right now.", item.DetailLine);
    }
}
