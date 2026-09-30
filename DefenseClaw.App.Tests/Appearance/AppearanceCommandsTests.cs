using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>An <see cref="IAppearanceControl"/> that records what it is asked to do.</summary>
internal sealed class FakeAppearance : IAppearanceControl
{
    public AppearanceStyle Style { get; set; } = AppearanceStyle.Default;

    public AppearanceMode Mode { get; set; } = AppearanceMode.System;

    public bool IsDark { get; set; } = true;

    public List<string> Calls { get; } = new();

    public event EventHandler? Changed;

    public void SetStyle(AppearanceStyle style)
    {
        Calls.Add($"style:{style}");
        Style = style;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetMode(AppearanceMode mode)
    {
        Calls.Add($"mode:{mode}");
        Mode = mode;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleLightDark()
    {
        Calls.Add("toggle");
        IsDark = !IsDark;
        Mode = IsDark ? AppearanceMode.Dark : AppearanceMode.Light;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public (System.Windows.Media.Color Window, System.Windows.Media.Color Surface, System.Windows.Media.Color Accent, System.Windows.Media.Color Text) Swatch(AppearanceStyle style) =>
        (System.Windows.Media.Colors.Black, System.Windows.Media.Colors.DimGray, System.Windows.Media.Colors.Blue, System.Windows.Media.Colors.White);
}

/// <summary>
/// The command palette's appearance commands and the light/dark shortcut: present under exactly the names the brief gives,
/// doing what they say, findable by search, and on a chord nothing else already owns.
/// </summary>
public sealed class AppearanceCommandsTests
{
    private static readonly string[] ExpectedTitles =
    {
        "Appearance: Default",
        "Appearance: Linear",
        "Appearance: TUI",
        "Toggle light/dark",
        "Mode: follow system",
    };

    private static List<ShellCommand> Build(FakeAppearance? appearance = null) =>
        ShellCommandRegistry.BuildAppearanceCommands(appearance ?? new FakeAppearance());

    // ------------------------------------------------------------------ the palette

    [Fact]
    public void The_palette_has_the_five_appearance_commands_under_the_names_in_the_brief()
    {
        var commands = Build();

        Assert.Equal(ExpectedTitles, commands.Select(c => c.Title).ToArray());
        Assert.Equal(
            new[] { "appearance.default", "appearance.linear", "appearance.tui", "appearance.toggle", "appearance.system" },
            commands.Select(c => c.Id).ToArray());
        Assert.All(commands, c => Assert.Equal(ShellCommandRegistry.AppCategory, c.Category));
    }

    [Fact]
    public void Each_command_does_exactly_what_its_title_says()
    {
        var appearance = new FakeAppearance { Style = AppearanceStyle.Tui, Mode = AppearanceMode.Dark };
        var commands = Build(appearance).ToDictionary(c => c.Title);

        commands["Appearance: Default"].Run();
        commands["Appearance: Linear"].Run();
        commands["Toggle light/dark"].Run();
        commands["Mode: follow system"].Run();

        Assert.Equal(new[] { "style:Default", "style:Linear", "toggle", "mode:System" }, appearance.Calls);
    }

    [Fact]
    public void The_style_in_use_and_the_mode_in_use_say_so_instead_of_pretending_to_do_something()
    {
        var appearance = new FakeAppearance { Style = AppearanceStyle.Linear, Mode = AppearanceMode.System };
        var commands = Build(appearance).ToDictionary(c => c.Id);

        Assert.False(commands["appearance.linear"].IsEnabled);
        Assert.Equal("The Linear style is already in use.", commands["appearance.linear"].DisabledReason);
        Assert.True(commands["appearance.default"].IsEnabled);
        Assert.True(commands["appearance.tui"].IsEnabled);

        Assert.False(commands["appearance.system"].IsEnabled);
        Assert.Equal("Already following the system.", commands["appearance.system"].DisabledReason);

        Assert.True(commands["appearance.toggle"].IsEnabled);
    }

    [Theory]
    [InlineData(true, "light")]
    [InlineData(false, "dark")]
    public void The_toggle_says_which_way_it_goes_from_where_the_app_is_now(bool isDark, string goesTo)
    {
        var command = Build(new FakeAppearance { IsDark = isDark }).Single(c => c.Id == "appearance.toggle");

        Assert.Contains($"Switch to the {goesTo} look", command.Description, StringComparison.Ordinal);
        Assert.Equal(ShellShortcuts.ToggleThemeText, command.Shortcut);
    }

    [Theory]
    [InlineData("linear", "Appearance: Linear")]
    [InlineData("tui", "Appearance: TUI")]
    [InlineData("terminal", "Appearance: TUI")]
    [InlineData("theme", "Appearance: Default")]
    [InlineData("dark", "Toggle light/dark")]
    [InlineData("light", "Toggle light/dark")]
    [InlineData("follow system", "Mode: follow system")]
    [InlineData("appearance", "Appearance: Default")]
    public void Searching_finds_them(string query, string expectedFirst)
    {
        var ranked = CommandPaletteViewModel.Rank(Build(new FakeAppearance { Style = AppearanceStyle.Default, Mode = AppearanceMode.Dark }), query);

        Assert.Equal(expectedFirst, ranked[0].Title);
    }

    [Fact]
    public void The_command_ids_are_unique_across_the_whole_palette()
    {
        var appearance = Build();
        var ids = appearance.Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ids, id => id.StartsWith("nav.", StringComparison.Ordinal) || id.StartsWith("gateway.", StringComparison.Ordinal) || id.StartsWith("app.", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the shortcut

    [Fact]
    public void The_toggle_chord_is_ctrl_shift_l_and_only_that()
    {
        Assert.Equal("Ctrl+Shift+L", ShellShortcuts.ToggleThemeText);
        Assert.True(ShellShortcuts.IsToggleThemeChord(Key.L, ModifierKeys.Control | ModifierKeys.Shift));

        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.L, ModifierKeys.Control));
        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.L, ModifierKeys.Shift));
        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.L, ModifierKeys.None));
        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.L, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.K, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.D1, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void The_toggle_chord_is_not_taken_by_any_other_shell_shortcut()
    {
        var taken = new List<string>
        {
            ShellShortcuts.PaletteText,
            ShellShortcuts.RefreshText,
            ShellShortcuts.HelpText,
            ShellShortcuts.FindText,
            ShellShortcuts.CloseText,
        };
        for (var i = 0; i < ShellShortcuts.NumberedPanels; i++)
        {
            taken.Add(ShellShortcuts.PanelChordText(i)!);
        }

        Assert.DoesNotContain(ShellShortcuts.ToggleThemeText, taken);

        // ...and it is not a panel chord by key either, whatever the modifiers.
        Assert.Null(ShellShortcuts.PanelIndexFor(Key.L, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Null(ShellShortcuts.PanelIndexFor(Key.L, ModifierKeys.Control));
    }
}
