using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway.Models;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The Alerts panel's Mac-parity additions (CUST-218) on its view-model: the Kind popup (All kinds / Blocks / Audit / Scans / Egress),
/// egress decisions as rows, the severity tiles as the severity filter, the deep link's kind, the inspector's findings and target
/// history, and "Acknowledge selection". Synthetic rows from the real DDL; the panel runs on a plain STA thread.
/// </summary>
public sealed class AlertsKindTilesDetailTests : IDisposable
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-3);

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly AlertQueueDatabase _database;

    public AlertsKindTilesDetailTests()
    {
        _services = TestServices.Create(_temp);
        _database = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static string[] Ids(AlertsPanelViewModel vm) => vm.Alerts.Select(a => a.Key).ToArray();

    private static SeverityFilter Chip(AlertsPanelViewModel vm, string severity) => vm.SeverityFilters.Single(f => f.Severity == severity);

    /// <summary>Two findings (a scan finding HIGH, a guardrail block CRITICAL) and three egress decisions, only two of which deserve a row.</summary>
    private void Mixed()
    {
        _database.AddFinding("scan1", Base.AddMinutes(1), "HIGH", action: "scan-finding");
        _database.AddFinding("block1", Base.AddMinutes(2), "CRITICAL", action: "guardrail-block");
        _database.AddEgress("egress-block", Base.AddMinutes(3), "block", "passthrough", looksLikeLlm: false, target: "evil.example.test");
        _database.AddEgress("egress-llm", Base.AddMinutes(4), "allow", "passthrough", looksLikeLlm: true, target: "api.llm.test");
        _database.AddEgress("egress-quiet", Base.AddMinutes(5), "allow", "passthrough", looksLikeLlm: false);
    }

    // ------------------------------------------------------------------ egress and kinds

    [Fact]
    public void Egress_decisions_join_the_findings_when_they_were_blocked_or_look_like_an_llm_call()
    {
        Mixed();

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            // Newest first; the quiet allowed decision is not an alert.
            Assert.Equal(new[] { "audit:egress-llm", "audit:egress-block", "block1", "scan1" }, Ids(vm));
            var block = vm.Alerts.Single(a => a.Key == "audit:egress-block");
            Assert.Equal(AlertKinds.Egress, block.Kind);
            Assert.Equal("MEDIUM", block.Severity);
            Assert.Equal("egress block", block.Action);
            Assert.Equal("evil.example.test", block.TargetRef);
            Assert.Contains(block.Fields, f => f.Name == "network.decision" && f.Value == "block");
            Assert.Equal("INFO", vm.Alerts.Single(a => a.Key == "audit:egress-llm").Severity);

            // The tiles count what is loaded; INFO has no tile, so it gets its small chip.
            Assert.Equal(new[] { 1, 1, 1, 0 }, vm.Tiles.Select(t => t.Count).ToArray());
            Assert.Equal(1, vm.InfoFilter.Count);
            Assert.True(vm.ShowInfoFilter);
            Assert.Contains("· 2 egress ·", vm.SourceNote, StringComparison.Ordinal);
            Assert.StartsWith("Unacknowledged findings · 2 ·", vm.SourceNote, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(AlertKinds.All, new[] { "audit:egress-llm", "audit:egress-block", "block1", "scan1" })]
    [InlineData(AlertKinds.Blocks, new[] { "audit:egress-block", "block1" })]
    [InlineData(AlertKinds.Audit, new[] { "block1" })]
    [InlineData(AlertKinds.Scan, new[] { "scan1" })]
    [InlineData(AlertKinds.Egress, new[] { "audit:egress-llm", "audit:egress-block" })]
    public void The_kind_popup_narrows_the_list(string kind, string[] expected)
    {
        Mixed();

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            vm.KindFilter = kind;

            Assert.Equal(expected, Ids(vm));
        });
    }

    [Fact]
    public void The_kind_popup_offers_the_macs_five_choices_and_a_kind_that_hides_everything_says_so_and_clear_brings_it_back()
    {
        Mixed();

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            Assert.Equal(
                new[] { "All kinds", "Blocks", "Audit", "Scans", "Egress" },
                vm.KindChoices.Select(c => c.Label).ToArray());

            vm.KindFilter = AlertKinds.Egress;
            vm.FilterText = "no row says this";
            Assert.Empty(vm.Alerts);
            Assert.Equal("No alerts match the current filters", vm.EmptyTitle);
            Assert.Contains("kind", vm.EmptyDetail, StringComparison.Ordinal);

            vm.ClearFiltersCommand.Execute(null);

            Assert.Equal(AlertKinds.All, vm.KindFilter);
            Assert.Equal(4, vm.Alerts.Count);
        });
    }

    [Fact]
    public void A_deep_link_can_name_a_kind_and_a_kind_alone_shows_every_severity_of_it()
    {
        Mixed();

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            vm.SelectSeverityCommand.Execute(vm.Tiles[0]);
            vm.FilterText = "xx";

            vm.Accept(new AlertsFilter(Kind: AlertsFilter.KindBlocks));

            Assert.Equal(AlertKinds.Blocks, vm.KindFilter);
            Assert.Equal(string.Empty, vm.FilterText);
            Assert.All(vm.SeverityFilters, f => Assert.True(f.IsEnabled));
            Assert.Equal(new[] { "audit:egress-block", "block1" }, Ids(vm));

            vm.Accept(new AlertsFilter(AuditSeverity.Critical, "Scans"));
            Assert.Equal(AlertKinds.Scan, vm.KindFilter);
            Assert.Empty(vm.Alerts);

            vm.Accept(new AlertsFilter(Kind: AlertsFilter.KindAll));
            Assert.Equal(4, vm.Alerts.Count);
        });
    }

    // ------------------------------------------------------------------ the tiles

    [Fact]
    public void Pressing_a_tile_shows_only_that_severity_pressing_it_again_shows_all_and_another_tile_adds_or_removes_one()
    {
        Mixed();

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var critical = vm.Tiles[0];
            var high = vm.Tiles[1];
            Assert.Equal(new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW" }, vm.Tiles.Select(t => t.Severity).ToArray());
            Assert.All(vm.Tiles, t => Assert.False(t.IsPicked));
            Assert.Equal("alerts", critical.TileCaption);

            // Nothing narrowed: the tile is the Mac's "filter to this severity".
            vm.SelectSeverityCommand.Execute(critical);
            Assert.Equal(new[] { "block1" }, Ids(vm));
            Assert.True(critical.IsPicked);
            Assert.False(high.IsPicked);
            Assert.Equal("filtering", critical.TileCaption);
            Assert.Equal("hidden", high.TileCaption);

            // Another tile joins the set ...
            vm.SelectSeverityCommand.Execute(high);
            Assert.Equal(new[] { "block1", "scan1" }, Ids(vm));
            Assert.True(high.IsPicked);

            // ... and leaves it again; the last one standing, pressed, shows everything.
            vm.SelectSeverityCommand.Execute(high);
            Assert.Equal(new[] { "block1" }, Ids(vm));
            vm.SelectSeverityCommand.Execute(critical);
            Assert.Equal(4, vm.Alerts.Count);
            Assert.All(vm.SeverityFilters, f => Assert.True(f.IsEnabled));
            Assert.All(vm.Tiles, t => Assert.False(t.IsPicked));
        });
    }

    [Fact]
    public void A_severity_floor_from_a_link_is_what_the_tiles_show()
    {
        Mixed();

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            vm.Accept(new AlertsFilter(AuditSeverity.High));

            Assert.Equal(new[] { true, true, false, false }, vm.Tiles.Select(t => t.IsPicked).ToArray());
            Assert.Equal(new[] { "block1", "scan1" }, Ids(vm));
        });
    }

    // ------------------------------------------------------------------ inspector

    [Fact]
    public void Selecting_a_row_reads_its_findings_worst_first_and_the_five_newest_earlier_events_on_its_target()
    {
        _database.AddFinding("f1", Base.AddMinutes(30), "HIGH");
        _database.AddScan("scan-a", "/synthetic/f1", null, Base.AddMinutes(10), ("LOW", "Minor"), ("CRITICAL", "Exfiltration"));
        for (var i = 0; i < 7; i++)
        {
            _database.AddEvent($"h{i}", Base.AddMinutes(i), "/synthetic/f1", "hook-decision", i % 2 == 0 ? "WARNING" : "INFO");
        }

        _database.AddEvent("other", Base.AddMinutes(8), "/synthetic/else", "hook-decision");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var row = vm.Alerts.Single();

            vm.SelectedAlert = row;
            await vm.LoadDetailAsync(row);

            var detail = Assert.IsType<AlertDetail>(row.Detail);
            Assert.True(detail.IsComplete);
            Assert.Equal(new[] { "Exfiltration", "Minor" }, detail.Findings.Select(f => f.Title).ToArray());
            Assert.Equal("Critical", detail.Findings[0].SeverityKey);
            Assert.Equal("Remove the call.", detail.Findings[0].Remediation);
            Assert.Equal("main.py:12", detail.Findings[0].Location);
            Assert.True(detail.FindingsVisible);

            // Five rows, newest first, the alert's own row left out.
            Assert.Equal(AlertsPanelViewModel.HistoryRows, detail.History.Count);
            Assert.Equal(new[] { "WARNING", "INFO", "WARNING", "INFO", "WARNING" }, detail.History.Select(h => h.Severity).ToArray());
            Assert.All(detail.History, h => Assert.Equal("hook-decision", h.Action));
            Assert.True(detail.HistoryVisible);
            Assert.Contains("newest 20,000 audit events", detail.HistoryNote, StringComparison.Ordinal);

            // Once per row: picking it again gives the same, already read, detail.
            vm.SelectedAlert = null;
            vm.SelectedAlert = row;
            await vm.LoadDetailAsync(row);
            Assert.Same(detail, row.Detail);
        });
    }

    [Fact]
    public void A_row_with_nothing_to_show_leaves_both_sections_out_and_an_egress_row_never_asks_for_findings()
    {
        _database.AddEgress("e1", Base.AddMinutes(1), "block", "passthrough", looksLikeLlm: false);

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            var row = vm.Alerts.Single();
            var reads = vm.DetailReader.ReadCount;

            await vm.LoadDetailAsync(row);

            var detail = Assert.IsType<AlertDetail>(row.Detail);
            Assert.False(detail.FindingsVisible);
            Assert.False(detail.HistoryVisible);

            // The history lookup ran (the host is a target), the findings lookup did not.
            Assert.Equal(reads + 1, vm.DetailReader.ReadCount);
        });
    }

    [Fact]
    public void A_database_that_cannot_be_read_is_said_in_the_sections_and_never_thrown()
    {
        _database.AddFinding("f1", Base.AddMinutes(1), "HIGH");
        var broken = _temp.WriteFile("not-a-database.db", "this is not a SQLite file at all, just text padding it out past one page. ");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            vm.DetailReader = new AlertDetailReader(broken);
            var row = vm.Alerts.Single();

            await vm.LoadDetailAsync(row);

            var detail = Assert.IsType<AlertDetail>(row.Detail);
            Assert.Equal("The findings could not be read from the audit database.", detail.FindingsNote);
            Assert.Equal("The history could not be read from the audit database.", detail.HistoryNote);
            Assert.True(detail.FindingsVisible);
            Assert.Empty(detail.Findings);
        });
    }

    [Fact]
    public void Moving_the_selection_stops_the_lookup_that_was_running()
    {
        _database.AddFinding("f1", Base.AddMinutes(1), "HIGH");
        _database.AddFinding("f2", Base.AddMinutes(2), "HIGH");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();

            vm.SelectedAlert = vm.Alerts[0];
            vm.SelectedAlert = vm.Alerts[1];
            await vm.LoadDetailAsync(vm.Alerts[1]);

            Assert.True(vm.Alerts[1].Detail!.IsComplete);
        });
    }

    // ------------------------------------------------------------------ acknowledge selection

    [Fact]
    public void Acknowledge_selection_waits_for_a_selected_row()
    {
        _database.AddFinding("f1", Base.AddMinutes(1), "HIGH");

        StaThread.Run(async () =>
        {
            var vm = new AlertsPanelViewModel(_services);
            await vm.InitializeAsync();
            Assert.False(vm.OpenAcknowledgeSelectionCommand.CanExecute(null));

            vm.NoteSelection(new[] { vm.Alerts[0] });
            Assert.True(vm.OpenAcknowledgeSelectionCommand.CanExecute(null));

            vm.NoteSelection(Array.Empty<AlertItem>());
            Assert.False(vm.OpenAcknowledgeSelectionCommand.CanExecute(null));
        });
    }
}

/// <summary>The Alerts panel as a real view with the additions: the tiles, the Kind popup, the Egress rows and the inspector's sections.</summary>
[Collection(UiCollection.Name)]
public sealed class AlertsEnrichedPanelTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddMinutes(-30);

    private static IEnumerable<string> Texts(DependencyObject root) =>
        VisualTree.Descendants<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text);

    [Theory]
    [InlineData(1400, 900)]
    [InlineData(940, 620)]
    public void The_panel_shows_four_tone_tiles_the_kind_popup_the_egress_rows_and_the_inspectors_new_sections(int width, int height)
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var database = new AlertQueueDatabase(services.Paths.AuditDatabasePath);
        database.AddFinding("crit", Base.AddMinutes(1), "CRITICAL", action: "scan-finding");
        database.AddFinding("high", Base.AddMinutes(2), "HIGH");
        database.AddFinding("med", Base.AddMinutes(3), "MEDIUM", action: "guardrail-block");
        database.AddFinding("low", Base.AddMinutes(4), "LOW");
        database.AddEgress("egress-block", Base.AddMinutes(5), "block", "passthrough", looksLikeLlm: false, target: "evil.example.test");
        database.AddEgress("egress-llm", Base.AddMinutes(6), "allow", "passthrough", looksLikeLlm: true, target: "api.llm.test");
        database.AddScan("scan-a", "/synthetic/crit", null, Base, ("CRITICAL", "Environment variable dump"), ("MEDIUM", "Unpinned dependency"));
        database.AddEvent("h1", Base.AddMinutes(-9), "/synthetic/crit", "scan-finding", "WARNING");
        database.AddEvent("h2", Base.AddMinutes(-8), "/synthetic/crit", "hook-decision", "INFO");

        PanelShell? shell = null;
        try
        {
            AlertsPanel page = null!;
            AlertsPanelViewModel vm = null!;
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, width, height);
                page = shell.Show<AlertsPanel>();
                vm = (AlertsPanelViewModel)shell.ViewModel;
            });

            UiThread.WaitFor(() => vm.Alerts.Count == 6, "the queue and the egress feed to be listed");

            UiThread.Run(() =>
            {
                shell!.Host.Relayout();

                // Four tiles, each a button with the word and the number.
                var tiles = VisualTree.Descendants<Button>(page).Where(b => ReferenceEquals(b.Style, Application.Current.FindResource("DcInteractiveTile"))).ToArray();
                Assert.Equal(4, tiles.Length);
                Assert.Equal(new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW" }, tiles.Select(t => VisualTree.Descendants<TextBlock>(t).Single(x => x.Text.Length > 0 && x.Text == ((SeverityFilter)t.DataContext).Severity).Text).ToArray());
                Assert.Equal(new[] { "1", "1", "2", "1" }, tiles.Select(t => VisualTree.Descendants<TextBlock>(t).Single(x => x.FontSize > 20).Text).ToArray());

                // The Kind popup, with the Mac's words.
                var kind = VisualTree.Descendants<ComboBox>(page).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Kind");
                Assert.Equal(new[] { "All kinds", "Blocks", "Audit", "Scans", "Egress" }, kind.Items.OfType<AlertKindChoice>().Select(c => c.Label).ToArray());
                Assert.Equal(AlertKinds.All, kind.SelectedValue);

                // The table: egress rows are there, in their own Kind.
                var grid = (DataGrid)page.FindName("AlertList");
                Assert.Equal(new[] { "Time", "Severity", "Kind", "Action", "Target", "Details", "Run", "Actions" }, grid.Columns.Select(c => (string)c.Header).ToArray());
                RenderTo.Png(shell.Host, $"alerts-c3-{width}x{height}-list");

                // Pressing the MEDIUM tile: the list is the two medium rows and the tile is picked.
                tiles[2].Command.Execute(tiles[2].CommandParameter);
                shell.Host.Relayout();
                Assert.Equal(2, vm.Alerts.Count);
                RenderTo.Png(shell.Host, $"alerts-c3-{width}x{height}-medium-tile");
                tiles[2].Command.Execute(tiles[2].CommandParameter);

                // Select the scan finding: the inspector gains its Findings and History sections once they are read.
                vm.SelectedAlert = vm.Alerts.Single(a => a.Key == "crit");
            });

            UiThread.WaitFor(() => vm.SelectedAlert?.Detail is { IsComplete: true }, "the inspector's lookups to finish");

            UiThread.Run(() =>
            {
                shell!.Host.Relayout();
                var inspector = (DcInspector)page.FindName("Inspector");
                var texts = Texts(inspector).ToList();
                Assert.Contains("Findings", texts);
                Assert.Contains("Environment variable dump", texts);
                Assert.Contains("History for this target", texts);
                Assert.Contains(texts, t => t.StartsWith("Remediation: ", StringComparison.Ordinal));
                RenderTo.Png(shell.Host, $"alerts-c3-{width}x{height}-inspector");

                // An egress row has no findings section, only its attributes.
                vm.SelectedAlert = vm.Alerts.Single(a => a.Key == "audit:egress-block");
            });

            UiThread.WaitFor(() => vm.SelectedAlert?.Detail is { IsComplete: true }, "the egress row's lookups to finish");

            UiThread.Run(() =>
            {
                shell!.Host.Relayout();
                var inspector = (DcInspector)page.FindName("Inspector");
                var texts = Texts(inspector).ToList();
                Assert.DoesNotContain("Findings", texts);
                Assert.Contains("network.decision", texts);
                RenderTo.Png(shell.Host, $"alerts-c3-{width}x{height}-egress-inspector");
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
            SqliteConnection.ClearAllPools();
        }
    }
}
