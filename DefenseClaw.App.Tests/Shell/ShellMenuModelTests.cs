using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The main window's menus (CUST-384; the Mac's <c>.commands</c> in <c>App/DefenseClawApp.swift</c>) as data: every row an existing registry command
/// looked up by id, never a handler of its own; the Go menu the sidebar itself; every enabled state and reason the palette's; Windows chords and
/// access keys, none new; and building them runs nothing. The actions are the palette tests' isolated ones (no CLI on the runner's PATH, the
/// clipboard, toast, About and update check are recorders). Synthetic data only.
/// </summary>
public sealed class ShellMenuModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _owned = new();
    private readonly FakeAppearance _appearance = new() { Style = AppearanceStyle.Linear, Mode = AppearanceMode.System };
    private readonly List<string> _window = new();
    private readonly List<string> _toasts = new();
    private readonly List<string> _clipboard = new();
    private readonly List<string> _about = new();
    private readonly List<CliInvocation> _activity = new();
    private readonly List<string> _navigated = new();
    private int _appChecks;

    public ShellMenuModelTests()
    {
        (Services, Catalog, Actions) = Compose();
    }

    private AppServices Services { get; set; }

    private PanelCatalog Catalog { get; set; }

    private ShellActions Actions { get; set; }

    public void Dispose()
    {
        foreach (var services in _owned)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private (AppServices, PanelCatalog, ShellActions) Compose(InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _owned.Add(services);
        var catalog = new PanelCatalog(services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var actions = new ShellActions(services, catalog, tray, () => null)
        {
            Toast = (title, message) => _toasts.Add(title + ": " + message),
            ClipboardWriter = _clipboard.Add,
            ActivitySource = () => _activity.ToArray(),
            AppUpdateChecker = () =>
            {
                _appChecks++;
                return Task.CompletedTask;
            },
            AboutPresenter = (title, text) => _about.Add(title + "\n" + text),
        };
        return (services, catalog, actions);
    }

    private ShellWindowCommands Window(bool sidebarOpen = true) => new(
        OpenPalette: () => _window.Add("palette"),
        CloseToTray: () => _window.Add("tray"),
        Exit: () => _window.Add("exit"),
        IsSidebarOpen: () => sidebarOpen,
        ToggleSidebar: () => _window.Add("sidebar"));

    /// <summary>The palette's rows, as the window builds them for the palette (without the CLI registry, which the menus do not list).</summary>
    private List<ShellCommand> Palette(PanelCatalog? catalog = null) =>
        ShellCommandRegistry.Build(catalog ?? Catalog, Actions, p => _navigated.Add(p.Id), () => _window.Add("shortcuts"), _appearance, curated: null, Services.ConnectorScope).ToList();

    /// <summary>The rows the menus are made of: the palette's, then the menu bar's own - what <c>MainWindow.BuildMenus</c> builds.</summary>
    private List<ShellCommand> Registry(PanelCatalog? catalog = null)
    {
        var commands = Palette(catalog);
        commands.AddRange(ShellCommandRegistry.BuildMenuOnlyCommands(Actions, Window()));
        return commands;
    }

    private IReadOnlyList<ShellMenu> Menus(List<ShellCommand>? commands = null, PanelCatalog? catalog = null) =>
        ShellMenuModel.Build(commands ?? Registry(catalog), catalog ?? Catalog, id => ShellMenuModel.CheckedState(id, _appearance, sidebarOpen: true));

    private static ShellMenu Menu(IReadOnlyList<ShellMenu> menus, string key) => menus.Single(m => m.Key == key);

    private static List<string> Ids(ShellMenu menu) => ShellMenuModel.Rows(menu.Entries).Select(r => r.Id).ToList();

    private static ShellMenuCommand RowOf(IReadOnlyList<ShellMenu> menus, string id) =>
        menus.SelectMany(m => ShellMenuModel.Rows(m.Entries)).First(r => r.Id == id);

    private static CliInvocation Ran(string output)
    {
        var invocation = InvocationFactory.Create(argv: new[] { "doctor" });
        if (output.Length > 0)
        {
            InvocationFactory.Append(invocation, output);
        }

        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    // ------------------------------------------------------------------ every row is a registry command

    [Fact]
    public void The_menus_are_the_Macs_command_menus_as_Windows_menus_each_row_a_registry_command_by_id()
    {
        var commands = Registry();
        var menus = Menus(commands);

        Assert.Equal(new[] { "File", "View", "Monitor", "Commands", "Go", "Help" }, menus.Select(m => m.Label));

        Assert.Equal(
            new[] { ShellCommandRegistry.AboutId, "nav.settings", ShellCommandRegistry.AppUpdatesId, ShellCommandRegistry.RuntimeUpdatesId, ShellCommandRegistry.CloseToTrayId, ShellCommandRegistry.ExitId },
            Ids(Menu(menus, ShellMenuModel.FileKey)));
        Assert.Equal(
            new[] { "app.refresh-panel", "app.refresh-gateway", "appearance.toggle" }
                .Concat(AppearanceCatalog.Styles.Select(ShellMenuModel.StyleId))
                .Concat(new[] { "appearance.system", ShellCommandRegistry.SidebarId }),
            Ids(Menu(menus, ShellMenuModel.ViewKey)));
        Assert.Equal(new[] { "app.run-doctor", "app.scan-ai", "app.cycle-connector" }, Ids(Menu(menus, ShellMenuModel.MonitorKey)));
        Assert.Equal(
            new[] { ShellCommandRegistry.PaletteId, "app.copy-last-output", "app.export-last-output", "app.diagnose" },
            Ids(Menu(menus, ShellMenuModel.CommandsKey)));
        Assert.Equal(new[] { "app.docs", "app.shortcuts" }, Ids(Menu(menus, ShellMenuModel.HelpKey)));

        // Every row is one of the registry's own objects: the menu shares its handler, its enabled state and its reason; it has none of its own.
        foreach (var row in menus.SelectMany(m => ShellMenuModel.Rows(m.Entries)))
        {
            Assert.Contains(commands, c => ReferenceEquals(c, row.Command));
        }
    }

    [Fact]
    public void No_handler_is_orphaned_or_duplicated()
    {
        var palette = Palette();
        var menuOnly = ShellCommandRegistry.BuildMenuOnlyCommands(Actions, Window());
        var all = palette.Concat(menuOnly).ToList();

        // One row per id across the palette and the menu bar's own: nothing the palette has is made a second time for the menus.
        Assert.Equal(all.Count, all.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            new[] { ShellCommandRegistry.PaletteId, ShellCommandRegistry.AboutId, ShellCommandRegistry.AppUpdatesId, ShellCommandRegistry.CloseToTrayId, ShellCommandRegistry.ExitId, ShellCommandRegistry.SidebarId },
            menuOnly.Select(c => c.Id));
        Assert.Contains(palette, c => c.Id == ShellCommandRegistry.RuntimeUpdatesId);

        // Every menu-only row is in a menu (none built for nothing), and within a menu no row is there twice. The one row in two menus is Settings: File's
        // Settings (Ctrl+,) and the Go menu's footer entry are the palette's one "Go to Settings".
        var menus = Menus(all);
        var rows = menus.SelectMany(m => ShellMenuModel.Rows(m.Entries).Select(r => (Menu: m.Key, r.Id))).ToList();
        Assert.All(menuOnly, c => Assert.Contains(rows, r => r.Id == c.Id));
        Assert.All(menus, m => Assert.Equal(Ids(m).Count, Ids(m).Distinct(StringComparer.Ordinal).Count()));
        Assert.Equal(new[] { "nav.settings" }, rows.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void A_row_whose_command_is_not_offered_is_left_out_as_the_palette_leaves_it_out()
    {
        // No appearance service and no connector scope: the palette has no look rows and no "Cycle connector scope", and neither do the menus. A
        // separator with nothing left to separate goes too.
        var commands = ShellCommandRegistry.Build(Catalog, Actions, _ => { }, () => { }).ToList();
        commands.AddRange(ShellCommandRegistry.BuildMenuOnlyCommands(Actions, Window()));
        var menus = ShellMenuModel.Build(commands, Catalog);

        var view = Menu(menus, ShellMenuModel.ViewKey);
        Assert.Equal(new[] { "app.refresh-panel", "app.refresh-gateway", ShellCommandRegistry.SidebarId }, Ids(view));
        Assert.DoesNotContain(view.Entries, e => e is ShellSubmenu);
        Assert.Equal(1, view.Entries.Count(e => e is ShellMenuSeparator));
        Assert.Equal(new[] { "app.run-doctor", "app.scan-ai" }, Ids(Menu(menus, ShellMenuModel.MonitorKey)));
    }

    // ------------------------------------------------------------------ the Go menu is the sidebar

    [Fact]
    public void The_Go_menu_is_the_sidebar_group_by_group_then_its_footer()
    {
        var go = Menu(Menus(), ShellMenuModel.GoKey);

        var headings = go.Entries.OfType<ShellMenuSeparator>().Select(s => s.Heading).OfType<string>().ToList();
        Assert.Equal(PanelCatalog.Groups, headings);

        Assert.Equal(
            Catalog.SidebarOrder.Concat(Catalog.FooterPanels).Select(p => "nav." + p.Id),
            Ids(go));
        Assert.Equal(Catalog.SidebarOrder.Concat(Catalog.FooterPanels).Select(p => p.Title), ShellMenuModel.Rows(go.Entries).Select(r => r.Label));

        // Each row sits under its own group's heading; the footer (Settings) under a plain separator after the last group.
        string? group = null;
        foreach (var entry in go.Entries)
        {
            switch (entry)
            {
                case ShellMenuSeparator separator:
                    group = separator.Heading ?? PanelCatalog.FooterGroup;
                    break;
                case ShellMenuCommand row:
                    Assert.Equal(Catalog.ById(row.Id["nav.".Length..])!.Group, group);
                    break;
            }
        }
    }

    [Fact]
    public void A_sidebar_that_changes_changes_the_Go_menu_with_it()
    {
        // The shipped catalog (CUST-383): Govern ends Tools, Policies, Sandboxes, and the Go menu shows it in that order.
        var shipped = Menu(Menus(), ShellMenuModel.GoKey);
        Assert.Equal(new[] { "nav.skills", "nav.mcps", "nav.plugins", "nav.tools", "nav.policies", "nav.sandboxes" }, GroupIds(shipped, "Govern"));

        // Another change to the catalog (Agents moved up into Monitor): the Go menu is built from the catalog, so it follows.
        var panels = Catalog.Panels.ToList();
        var agents = panels.Single(p => p.Id == "agents");
        panels.Remove(agents);
        panels.Insert(panels.FindIndex(p => p.Id == "overview") + 1, agents with { Group = "Monitor" });
        var moved = new PanelCatalog(Services, panels);

        var go = Menu(Menus(catalog: moved), ShellMenuModel.GoKey);

        Assert.Equal(moved.SidebarOrder.Concat(moved.FooterPanels).Select(p => "nav." + p.Id), Ids(go));
        Assert.Equal("nav.agents", GroupIds(go, "Monitor").ElementAt(1));
        Assert.DoesNotContain("nav.agents", GroupIds(go, "Discover"));
    }

    private static IEnumerable<string> GroupIds(ShellMenu menu, string heading) =>
        menu.Entries
            .SkipWhile(e => e is not ShellMenuSeparator s || s.Heading != heading)
            .Skip(1)
            .TakeWhile(e => e is ShellMenuCommand)
            .Cast<ShellMenuCommand>()
            .Select(r => r.Id)
            .ToList();

    [Fact]
    public void Go_rows_show_the_chord_and_the_disabled_reason_of_the_palettes_Go_to_rows()
    {
        var go = ShellMenuModel.Rows(Menu(Menus(), ShellMenuModel.GoKey).Entries).ToList();
        var goTo = ShellCommandRegistry.BuildPanelCommands(Catalog, _ => { });

        Assert.Equal(goTo.Select(c => c.Id), go.Select(r => r.Id));
        foreach (var row in go)
        {
            var palette = goTo.Single(c => c.Id == row.Id);
            Assert.Equal(palette.Shortcut, row.Gesture);
            Assert.Equal(palette.IsEnabled, row.IsEnabled);
            Assert.Equal(palette.DisabledReason, row.Command.DisabledReason);
        }

        // A runtime that has not answered offers neither Runtime nor Agents: greyed out with the palette's sentence, and no chord (it would do nothing).
        foreach (var id in new[] { "ai-runtime", "agents" })
        {
            var row = go.Single(r => r.Id == "nav." + id);
            Assert.False(row.IsEnabled);
            Assert.Null(row.Gesture);
            Assert.Equal(Catalog.Gate(Catalog.ById(id)!).Reason, row.Detail);
        }

        Assert.Equal("Ctrl+1", go.Single(r => r.Id == "nav.overview").Gesture);
        Assert.Equal("Ctrl+,", go.Single(r => r.Id == "nav.settings").Gesture);
    }

    // ------------------------------------------------------------------ enabled states agree with the palette

    /// <summary>Every menu row that the palette also lists is enabled, disabled and explained exactly as the palette's row is, with the same chord.</summary>
    private void AssertAgreesWithThePalette()
    {
        var palette = Palette();
        foreach (var row in Menus().SelectMany(m => ShellMenuModel.Rows(m.Entries)))
        {
            if (palette.FirstOrDefault(c => c.Id == row.Id) is not { } twin)
            {
                continue;
            }

            Assert.Equal(twin.IsEnabled, row.IsEnabled);
            Assert.Equal(twin.DisabledReason, row.Command.DisabledReason);
            Assert.Equal(twin.Shortcut, row.Gesture);
            Assert.Equal(twin.Description, row.Command.Description);
        }
    }

    [Fact]
    public void With_no_command_output_copy_and_export_are_off_and_say_why_in_both()
    {
        var menus = Menus();
        var copy = RowOf(menus, "app.copy-last-output");
        var export = RowOf(menus, "app.export-last-output");

        Assert.False(copy.IsEnabled);
        Assert.Equal("There is no command output to copy yet.", copy.Detail);
        Assert.False(export.IsEnabled);
        Assert.Equal("There is no command output to export yet.", export.Detail);
        AssertAgreesWithThePalette();

        // A run with no output: it can be exported (its command and outcome), not copied.
        _activity.Add(Ran(string.Empty));
        Assert.False(RowOf(Menus(), "app.copy-last-output").IsEnabled);
        Assert.True(RowOf(Menus(), "app.export-last-output").IsEnabled);
        AssertAgreesWithThePalette();

        _activity.Insert(0, Ran("synthetic output"));
        Assert.True(RowOf(Menus(), "app.copy-last-output").IsEnabled);
        AssertAgreesWithThePalette();

        Assert.Empty(_clipboard);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task While_a_diagnosis_runs_diagnose_is_off_in_both_and_back_on_after()
    {
        // The first toast of a diagnosis is said while it is running: read the rows there.
        ShellMenuCommand? during = null;
        string? paletteReason = null;
        Actions.Toast = (title, message) =>
        {
            _toasts.Add(title + ": " + message);
            if (during is null)
            {
                during = RowOf(Menus(), "app.diagnose");
                paletteReason = Palette().Single(c => c.Id == "app.diagnose").DisabledReason;
            }
        };

        Assert.True(RowOf(Menus(), "app.diagnose").IsEnabled);

        // The isolated runner has no CLI, so the run ends at once with "could not run" - nothing is started.
        await Actions.DiagnoseInBackgroundAsync();

        Assert.NotNull(during);
        Assert.False(during!.IsEnabled);
        Assert.Equal("A diagnosis is already running.", during.Detail);
        Assert.Equal(during.Detail, paletteReason);
        Assert.True(RowOf(Menus(), "app.diagnose").IsEnabled);
    }

    [Fact]
    public void On_a_managed_installation_the_rows_that_change_it_are_off_with_the_installations_sentence()
    {
        (Services, Catalog, Actions) = Compose(TestInstallations.Managed(_temp.Path));

        var scan = RowOf(Menus(), "app.scan-ai");
        Assert.False(scan.IsEnabled);
        Assert.Equal(TestInstallations.ManagedReason, scan.Detail);
        AssertAgreesWithThePalette();

        // The reads, the app's own update check and the window's own rows are not held back.
        foreach (var id in new[] { "app.run-doctor", "app.diagnose", ShellCommandRegistry.RuntimeUpdatesId, ShellCommandRegistry.AppUpdatesId, ShellCommandRegistry.ExitId, "nav.settings" })
        {
            Assert.True(RowOf(Menus(), id).IsEnabled, id);
        }
    }

    [Fact]
    public void Cycle_connector_filter_is_off_with_the_palettes_reason_when_there_is_nothing_to_cycle()
    {
        var cycle = RowOf(Menus(), "app.cycle-connector");

        Assert.False(cycle.IsEnabled);
        Assert.Equal("There is only one connector, so there is nothing to choose between.", cycle.Detail);
        Assert.Equal(ShellShortcuts.CycleConnectorText, cycle.Gesture);
        AssertAgreesWithThePalette();
    }

    [Fact]
    public void The_app_update_check_is_off_while_one_runs()
    {
        Assert.True(RowOf(Menus(), ShellCommandRegistry.AppUpdatesId).IsEnabled);

        Services.AppUpdate.ViewModel.IsChecking = true;
        var row = RowOf(Menus(), ShellCommandRegistry.AppUpdatesId);
        Assert.False(row.IsEnabled);
        Assert.Equal("Already checking for app updates.", row.Detail);

        Services.AppUpdate.ViewModel.IsChecking = false;
        Assert.True(RowOf(Menus(), ShellCommandRegistry.AppUpdatesId).IsEnabled);
    }

    [Fact]
    public void The_two_update_rows_each_name_their_product()
    {
        var file = ShellMenuModel.Rows(Menu(Menus(), ShellMenuModel.FileKey).Entries).ToList();

        var app = file.Single(r => r.Id == ShellCommandRegistry.AppUpdatesId);
        var runtime = file.Single(r => r.Id == ShellCommandRegistry.RuntimeUpdatesId);
        Assert.Equal("Check for app updates…", app.Label);
        Assert.Equal("Check for runtime updates…", runtime.Label);
        Assert.Contains("DefenseClaw for Windows (this app)", app.Detail, StringComparison.Ordinal);
        Assert.Contains("DefenseClaw release", runtime.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ building runs nothing; choosing runs the registry's handler

    [Fact]
    public void Building_and_rebuilding_the_menus_runs_nothing()
    {
        for (var i = 0; i < 3; i++)
        {
            _ = Menus();
        }

        Assert.Empty(_window);
        Assert.Empty(_navigated);
        Assert.Empty(_toasts);
        Assert.Empty(_clipboard);
        Assert.Empty(_about);
        Assert.Equal(0, _appChecks);
        Assert.Null(Services.Navigation.Pending);
        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public void Choosing_a_row_runs_the_registry_handler_and_only_it()
    {
        var menus = Menus();
        void Choose(string id) => RowOf(menus, id).Command.Run();

        Choose(ShellCommandRegistry.PaletteId);
        Choose(ShellCommandRegistry.CloseToTrayId);
        Choose(ShellCommandRegistry.ExitId);
        Choose(ShellCommandRegistry.SidebarId);
        Assert.Equal(new[] { "palette", "tray", "exit", "sidebar" }, _window);

        Choose(ShellCommandRegistry.AppUpdatesId);
        Assert.Equal(1, _appChecks);

        Choose(ShellCommandRegistry.AboutId);
        var about = Assert.Single(_about);
        Assert.StartsWith(ShellActions.AboutTitle + "\n", about, StringComparison.Ordinal);

        Choose("nav.alerts");
        Assert.Equal(new[] { "alerts" }, _navigated);

        Choose("app.shortcuts");
        Assert.Equal("shortcuts", _window[^1]);

        Choose("app.run-doctor");
        Assert.Equal(new NavigationRequest("overview", new OverviewFocus(OverviewFocus.DoctorSection)), Services.Navigation.Pending);

        Choose("appearance.toggle");
        Assert.Equal("toggle", _appearance.Calls[^1]);

        Assert.Empty(Services.Cli.Activity);
    }

    [Fact]
    public void About_says_what_the_app_is_and_which_runtime_it_found_and_runs_nothing()
    {
        var text = Actions.AboutText;

        Assert.StartsWith("DefenseClaw for Windows ", text, StringComparison.Ordinal);
        Assert.Contains("Not an official Cisco product.", text, StringComparison.Ordinal);
        Assert.Contains("Runtime: " + Services.Runtime.Current.UnknownReason, text, StringComparison.Ordinal);
        Assert.Contains("Docs: " + ShellActions.DocsUrl, text, StringComparison.Ordinal);
        Assert.Contains("Apache License, Version 2.0", text, StringComparison.Ordinal);
        Assert.Empty(Services.Cli.Activity);
    }

    // ------------------------------------------------------------------ Windows keys

    [Fact]
    public void Every_chord_shown_is_one_the_shell_already_has_and_none_is_a_text_editing_or_Alt_chord()
    {
        var known = new HashSet<string>(ShellShortcuts.ShellChords, StringComparer.Ordinal)
        {
            ShellShortcuts.ToggleThemeText,
            ShellShortcuts.CycleConnectorText,
        };
        for (var i = 0; i < ShellShortcuts.NumberedPanels; i++)
        {
            _ = known.Add(ShellShortcuts.PanelChordText(i)!);
        }

        var editing = new[] { "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+A", "Ctrl+S", "Ctrl+M" };
        foreach (var row in Menus().SelectMany(m => ShellMenuModel.Rows(m.Entries)))
        {
            if (row.Gesture is not { } gesture)
            {
                continue;
            }

            Assert.Contains(gesture, known);
            Assert.DoesNotContain(gesture, editing);
            Assert.DoesNotContain("Alt", gesture, StringComparison.Ordinal);
            Assert.DoesNotContain("⌘", gesture, StringComparison.Ordinal);
        }

        Assert.Equal("Ctrl+K", RowOf(Menus(), ShellCommandRegistry.PaletteId).Gesture);
        Assert.Equal("F5", RowOf(Menus(), "app.refresh-panel").Gesture);
        Assert.Equal("Ctrl+,", RowOf(Menus(), "nav.settings").Gesture);
    }

    [Fact]
    public void The_shells_chords_are_unchanged()
    {
        Assert.Equal(
            new[] { "Ctrl+K", "F5", "Ctrl+R", "F1", "Ctrl+F", "Esc", "Ctrl+Shift+L", "Ctrl+,", "Ctrl+Shift+H", "Ctrl+Shift+A", "Ctrl+Shift+D", "Ctrl+Shift+Y", "Ctrl+Shift+E" },
            ShellShortcuts.ShellChords);
        Assert.Equal("Ctrl+Shift+M", ShellShortcuts.CycleConnectorText);
    }

    [Fact]
    public void Every_menu_and_row_outside_Go_has_an_access_key_and_none_repeats_within_a_menu()
    {
        var menus = Menus();
        var tops = menus.Select(m => ShellMenuModel.AccessKeyOf(m.Header)).ToList();
        Assert.Equal(new char?[] { 'F', 'V', 'M', 'C', 'G', 'H' }, tops);

        void Check(string where, IReadOnlyList<ShellMenuEntry> entries, bool keyed)
        {
            var keys = new List<char>();
            foreach (var entry in entries)
            {
                var header = entry switch
                {
                    ShellMenuCommand row => row.Header,
                    ShellSubmenu submenu => submenu.Header,
                    _ => null,
                };
                if (header is null)
                {
                    continue;
                }

                if (ShellMenuModel.AccessKeyOf(header) is { } key)
                {
                    keys.Add(key);
                }
                else
                {
                    Assert.False(keyed, $"'{header}' in {where} has no access key");
                }

                if (entry is ShellSubmenu nested)
                {
                    Check(nested.Key, nested.Entries, keyed: true);
                }
            }

            Assert.Equal(keys.Count, keys.Distinct().Count());
        }

        foreach (var menu in menus)
        {
            // The Go menu is a long generated list reached by its Ctrl chords and the arrow keys, as the Mac's is: no access keys.
            Check(menu.Key, menu.Entries, keyed: menu.Key != ShellMenuModel.GoKey);
        }

        Assert.Equal("Exit", RowOf(menus, ShellCommandRegistry.ExitId).Label);
        Assert.Equal('X', ShellMenuModel.AccessKeyOf(RowOf(menus, ShellCommandRegistry.ExitId).Header));
    }

    // ------------------------------------------------------------------ the parts

    [Fact]
    public void Check_marks_are_the_style_in_use_follow_system_and_the_sidebar()
    {
        var menus = Menus();

        Assert.True(RowOf(menus, ShellMenuModel.StyleId(AppearanceStyle.Linear)).IsChecked);
        Assert.False(RowOf(menus, ShellMenuModel.StyleId(AppearanceStyle.Default)).IsChecked);
        Assert.True(RowOf(menus, "appearance.system").IsChecked);
        Assert.True(RowOf(menus, ShellCommandRegistry.SidebarId).IsChecked);
        Assert.Null(RowOf(menus, "appearance.toggle").IsChecked);
        Assert.Null(RowOf(menus, ShellCommandRegistry.ExitId).IsChecked);

        // The rows the check marks stand for: the style in use and "follow system" are off in the palette too (already chosen).
        Assert.False(RowOf(menus, ShellMenuModel.StyleId(AppearanceStyle.Linear)).IsEnabled);
        Assert.False(RowOf(menus, "appearance.system").IsEnabled);

        Assert.False(ShellMenuModel.CheckedState(ShellCommandRegistry.SidebarId, null, sidebarOpen: false));
        Assert.Null(ShellMenuModel.CheckedState("appearance.system", null, sidebarOpen: true));

        // Every style's id is one the registry builds.
        var looks = ShellCommandRegistry.BuildAppearanceCommands(_appearance).Select(c => c.Id).ToList();
        Assert.All(AppearanceCatalog.Styles, style => Assert.Contains(ShellMenuModel.StyleId(style), looks));
    }

    [Theory]
    [InlineData("E_xit", "Exit", 'X')]
    [InlineData("_File", "File", 'F')]
    [InlineData("a__b", "a_b", null)]
    [InlineData("plain", "plain", null)]
    [InlineData("snake__case _Here", "snake_case Here", 'H')]
    public void Access_key_markers_are_read_as_WPF_reads_them(string header, string label, char? key)
    {
        Assert.Equal(label, ShellMenuModel.WithoutAccessKey(header));
        Assert.Equal(key, ShellMenuModel.AccessKeyOf(header));
    }

    [Fact]
    public void Separators_never_lead_trail_double_or_head_an_empty_group()
    {
        var row = new ShellMenuCommand(new ShellCommand("x", "X", "App", string.Empty, null, string.Empty, true, null, () => { }), "_X");
        var tidy = ShellMenuModel.Tidy(new List<ShellMenuEntry>
        {
            new ShellMenuSeparator(),
            row,
            new ShellMenuSeparator(),
            new ShellMenuSeparator(),
            row,
            new ShellMenuSeparator(),
            new ShellMenuSeparator("Empty"),
            new ShellMenuSeparator("Group"),
            row,
            new ShellMenuSeparator(),
        });

        Assert.Equal(
            new ShellMenuEntry[] { row, new ShellMenuSeparator(), row, new ShellMenuSeparator("Group"), row },
            tidy);
    }
}
