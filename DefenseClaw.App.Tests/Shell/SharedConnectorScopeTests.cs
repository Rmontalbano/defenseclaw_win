using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The connector scope as the whole app uses it (CUST-223): the chip and its model, Ctrl+Shift+M, and each panel that lists
/// connector-tagged rows following the one scope (Alerts, Audit, Activity's Mutations, the Govern catalogs, AI Discovery).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SharedConnectorScopeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private AppServices Services(params string[] roster)
    {
        _services = TestServices.Create(_temp);
        _services.ConnectorScope.UpdateRoster(roster);
        return _services;
    }

    // ------------------------------------------------------------------ the chip's model

    [Fact]
    public void The_chip_shows_only_with_more_than_one_connector_and_lists_all_then_the_roster()
    {
        // On the UI thread: the model marshals a change there before it raises its properties.
        UiThread.Run(() =>
        {
            var scope = new ConnectorScope();
            var model = new ConnectorScopeViewModel(scope);
            Assert.False(model.IsVisible);

            scope.UpdateRoster(new[] { "claudecode" });
            Assert.False(model.IsVisible);

            scope.UpdateRoster(new[] { "claudecode", "codex" });
            Assert.True(model.IsVisible);
            Assert.Equal(new[] { "All connectors", "claudecode", "codex" }, model.Items.Select(i => i.Label));
            Assert.Equal("All connectors", model.Label);
            Assert.True(model.Items[0].IsSelected);
        });
    }

    [Fact]
    public void Choosing_an_entry_scopes_the_app_and_the_label_follows()
    {
        UiThread.Run(() =>
        {
            var scope = new ConnectorScope();
            scope.UpdateRoster(new[] { "claudecode", "codex" });
            var model = new ConnectorScopeViewModel(scope);

            model.SelectCommand.Execute(model.Items[2]);

            Assert.Equal("codex", scope.Current);
            Assert.Equal("codex", model.Label);
            Assert.True(model.IsScoped);
            Assert.True(model.Items[2].IsSelected);
            Assert.False(model.Items[0].IsSelected);

            model.SelectCommand.Execute(model.Items[0]);
            Assert.Null(scope.Current);
            Assert.Equal("All connectors", model.Label);
        });
    }

    [Fact]
    public void The_scope_goes_back_to_all_by_itself_when_its_connector_disappears_and_the_chip_says_so()
    {
        UiThread.Run(() =>
        {
            var scope = new ConnectorScope();
            scope.UpdateRoster(new[] { "claudecode", "codex" });
            var model = new ConnectorScopeViewModel(scope);
            model.SelectCommand.Execute(model.Items[2]);

            scope.UpdateRoster(new[] { "claudecode", "gemini" });
            Assert.Null(scope.Current);
            Assert.Equal("All connectors", model.Label);
            Assert.False(model.IsScoped);

            model.SelectCommand.Execute(model.Items[1]);
            scope.UpdateRoster(new[] { "claudecode" });
            Assert.Null(scope.Current);
            Assert.False(model.IsVisible);
        });
    }

    [Fact]
    public void The_chip_control_hides_with_one_connector_and_opens_a_menu_of_all_plus_the_connectors()
    {
        UiThread.Run(() =>
        {
            var scope = new ConnectorScope();
            scope.UpdateRoster(new[] { "claudecode" });
            var model = new ConnectorScopeViewModel(scope);
            var chip = new DcConnectorScopeChip { Model = model };
            Assert.Equal(Visibility.Collapsed, chip.Visibility);

            scope.UpdateRoster(new[] { "claudecode", "codex" });
            Assert.Equal(Visibility.Visible, chip.Visibility);
            Assert.Equal("All connectors", chip.Content);

            var menu = chip.BuildMenu();
            Assert.Equal(new[] { "All connectors", "claudecode", "codex" }, menu.Items.Cast<MenuItem>().Select(m => (string)m.Header));
            Assert.True(((MenuItem)menu.Items[0]).IsChecked);

            ((MenuItem)menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("codex", scope.Current);
            Assert.Equal("codex", chip.Content);
        });
    }

    [Fact]
    public void A_page_toolbar_carries_the_chip_only_with_more_than_one_connector()
    {
        var services = Services("claudecode");
        AuditTestDatabase.Create(Path.Combine(_temp.Path, "audit.db"), 4);

        UiThread.Run(() =>
        {
            using var shell = new PanelShell(services, 1400, 900);
            var page = shell.Show<DefenseClaw.App.Views.Panels.AuditPanel>();
            var chip = VisualTree.Find<DcConnectorScopeChip>(page);
            Assert.NotNull(chip);
            Assert.False(chip.IsVisible);

            services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex" });
            shell.Host.Relayout();
            Assert.True(chip.IsVisible);
            Assert.Equal("All connectors", chip.Content);
            Assert.True(chip.ActualWidth > 0);
            RenderTo.Png(shell.Host, "connector-chip-audit-all");

            services.ConnectorScope.Set("codex");
            shell.Host.Relayout();
            Assert.Equal("codex", chip.Content);
            RenderTo.Png(shell.Host, "connector-chip-audit-codex");
        });
    }

    // ------------------------------------------------------------------ the chord

    [Fact]
    public void Ctrl_shift_m_is_the_cycle_chord_and_no_other_chord_means_it()
    {
        Assert.True(ShellShortcuts.IsCycleConnectorChord(Key.M, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(ShellShortcuts.IsCycleConnectorChord(Key.M, ModifierKeys.Control));
        Assert.False(ShellShortcuts.IsCycleConnectorChord(Key.M, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
        Assert.False(ShellShortcuts.IsCycleConnectorChord(Key.L, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Null(ShellShortcuts.PanelIndexFor(Key.M, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.M, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void The_shortcut_list_names_the_chord_once_and_every_chord_in_it_is_unique()
    {
        var services = Services("claudecode", "codex");
        var model = ShortcutCatalog.Build(new PanelCatalog(services));

        var rows = new[] { model.Panels }.Concat(model.Others).SelectMany(s => s.Rows).ToList();
        _ = Assert.Single(rows, r => r.Keys == ShellShortcuts.CycleConnectorText);
        Assert.Equal("Ctrl+Shift+M", ShellShortcuts.CycleConnectorText);
        Assert.Equal(rows.Count, rows.Select(r => r.Keys).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_palette_has_a_cycle_entry_that_runs_the_same_step_and_is_off_with_one_connector()
    {
        var services = Services("claudecode", "codex");
        var catalog = new PanelCatalog(services);
        var scope = services.ConnectorScope;

        var tray = (TrayIconService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var actions = new ShellActions(services, catalog, tray, () => null);
        var commands = ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { }, null, connectorScope: scope);
        var cycle = Assert.Single(commands, c => c.Id == "app.cycle-connector");
        Assert.Equal(ShellShortcuts.CycleConnectorText, cycle.Shortcut);
        Assert.True(cycle.IsEnabled);

        cycle.Run();
        Assert.Equal("claudecode", scope.Current);

        scope.UpdateRoster(new[] { "claudecode" });
        var again = ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { }, null, connectorScope: scope);
        Assert.False(Assert.Single(again, c => c.Id == "app.cycle-connector").IsEnabled);
    }

    // ------------------------------------------------------------------ Audit follows the scope, both ways

    [Fact]
    public void Audit_selects_the_scoped_connector_and_a_pick_in_its_combo_scopes_the_app()
    {
        UiThread.Run(async () =>
        {
            var services = Services("claudecode", "codex");
            AuditTestDatabase.Create(Path.Combine(_temp.Path, "audit.db"), 20, i => i % 2 == 0 ? "claudecode" : "codex");
            var panel = new AuditPanelViewModel(services);
            await panel.InitializeAsync();
            panel.SetActive(true);

            services.ConnectorScope.Set("codex");
            await panel.LastLoad;
            Assert.Equal("codex", panel.SelectedConnector.Connector);
            Assert.All(panel.Rows, r => Assert.Equal("codex", r.Connector));

            services.ConnectorScope.Set(null);
            await panel.LastLoad;
            Assert.Same(ConnectorOption.All, panel.SelectedConnector);

            panel.SelectedConnector = panel.Connectors.First(c => c.Connector == "claudecode" && !c.IncludeNull);
            Assert.Equal("claudecode", services.ConnectorScope.Current);

            // "+ platform rows" is the same connector: a scope step to it keeps the refinement.
            panel.SelectedConnector = panel.Connectors.First(c => c.IncludeNull && c.Connector == "claudecode");
            Assert.Equal("claudecode", services.ConnectorScope.Current);
            Assert.True(panel.SelectedConnector.IncludeNull);

            // "platform only" belongs to no connector: the scope is left alone.
            panel.SelectedConnector = ConnectorOption.PlatformOnlyOption;
            Assert.Equal("claudecode", services.ConnectorScope.Current);
            await panel.LastLoad;
        });
    }

    [Fact]
    public void Audit_keeps_its_own_choice_when_there_is_one_connector_and_nothing_to_scope()
    {
        UiThread.Run(async () =>
        {
            var services = Services("claudecode");
            AuditTestDatabase.Create(Path.Combine(_temp.Path, "audit.db"), 6);
            var panel = new AuditPanelViewModel(services);
            await panel.InitializeAsync();
            panel.SetActive(true);

            panel.SelectedConnector = panel.Connectors[1];
            await panel.LastLoad;

            Assert.Null(services.ConnectorScope.Current);
            Assert.Equal("claudecode", panel.SelectedConnector.Connector);
        });
    }

    // ------------------------------------------------------------------ Activity's Mutations

    [Fact]
    public async Task Mutations_are_listed_under_the_scope_and_a_change_with_no_connector_is_not_in_an_agents_view()
    {
        var services = Services("claudecode", "codex");
        var database = new MutationDatabase(_temp.File("audit.db"))
            .Change("c1", connector: "claudecode")
            .Change("c2", connector: "codex")
            .Change("c3");
        var model = new ActivityMutationsViewModel(new MutationReader(database.Path), scope: services.ConnectorScope);
        await model.LoadAsync();
        Assert.Equal(3, model.Rows.Count);

        services.ConnectorScope.Set("codex");
        model.ReapplyScope();

        Assert.Equal(new[] { "c2" }, model.Rows.Select(r => r.Id));
        Assert.Equal("1 of 3 changes", model.Summary);

        services.ConnectorScope.Set(null);
        model.ReapplyScope();
        Assert.Equal(3, model.Rows.Count);
    }

    // ------------------------------------------------------------------ Alerts

    [Fact]
    public void Alerts_are_narrowed_to_the_scope_with_their_tiles_and_the_empty_state_names_it()
    {
        UiThread.Run(async () =>
        {
            var services = Services("claudecode", "codex");
            _ = new AlertQueueDatabase(services.Paths.AuditDatabasePath); // creates the schema
            Finding(services, "a1", 1, "HIGH", "claudecode");
            Finding(services, "a2", 2, "CRITICAL", "codex");
            Finding(services, "a3", 3, "HIGH", "codex");

            var panel = new AlertsPanelViewModel(services);
            await panel.InitializeAsync();
            panel.SetActive(true);
            Assert.Equal(3, panel.Alerts.Count);

            services.ConnectorScope.Set("claudecode");
            Assert.Equal(new[] { "a1" }, panel.Alerts.Select(a => a.Key));
            Assert.Equal(1, panel.SeverityFilters.Single(f => f.Severity == "HIGH").Count);
            Assert.Equal(0, panel.SeverityFilters.Single(f => f.Severity == "CRITICAL").Count);

            services.ConnectorScope.Set("codex");
            Assert.Equal(new[] { "a3", "a2" }, panel.Alerts.Select(a => a.Key));

            services.ConnectorScope.Set(null);
            Assert.Equal(3, panel.Alerts.Count);
        });
    }

    private static void Finding(AppServices services, string id, int minute, string severity, string connector)
    {
        using var connection = new SqliteConnection($"Data Source={services.Paths.AuditDatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, severity, bucket, connector, event_name)
            VALUES ($id, $timestamp, 'scan-finding', $target, 'audit_logger', $severity, 'security.finding', $connector, 'finding.observed')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$timestamp", AlertQueueDatabase.Format(DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(minute)));
        command.Parameters.AddWithValue("$target", "/synthetic/" + id);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$connector", connector);
        _ = command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ Govern

    [Fact]
    public void A_catalog_follows_the_scope_in_its_combo_and_a_pick_in_it_scopes_the_app()
    {
        UiThread.Run(() =>
        {
            var services = Services("claudecode", "codex");
            var vm = new SkillsPanelViewModel(services);
            vm.SetActive(true);

            services.ConnectorScope.Set("codex");
            Assert.Equal("codex", vm.SelectedConnector);
            Assert.Contains("codex", vm.Connectors);

            services.ConnectorScope.Set(null);
            Assert.Equal("All configured connectors", vm.SelectedConnector);

            vm.SelectedConnector = "claudecode";
            Assert.Equal("claudecode", services.ConnectorScope.Current);

            // A combo that loses its selection while its list is rebuilt is not a pick.
            vm.SelectedConnector = null;
            Assert.Equal("claudecode", services.ConnectorScope.Current);
        });
    }

    // ------------------------------------------------------------------ the base class

    [Fact]
    public void A_panel_hears_a_scope_change_only_while_it_is_active_and_catches_up_when_it_returns()
    {
        UiThread.Run(() =>
        {
            var services = Services("claudecode", "codex");
            var panel = new ProbePanel(services);

            services.ConnectorScope.Set("codex");
            Assert.Equal(0, panel.Heard);

            panel.SetActive(true);
            Assert.Equal(1, panel.Heard);

            services.ConnectorScope.Set("claudecode");
            Assert.Equal(2, panel.Heard);

            // A roster change that leaves the scope alone is not a scope change.
            services.ConnectorScope.UpdateRoster(new[] { "claudecode", "codex", "gemini" });
            Assert.Equal(2, panel.Heard);

            panel.SetActive(false);
            services.ConnectorScope.Set(null);
            Assert.Equal(2, panel.Heard);

            panel.SetActive(true);
            Assert.Equal(3, panel.Heard);
        });
    }

    private sealed class ProbePanel : PanelViewModelBase
    {
        public ProbePanel(AppServices services)
            : base(services)
        {
        }

        public override string Title => "Probe";

        public int Heard { get; private set; }

        protected override void OnConnectorScopeChanged() => Heard++;
    }
}
