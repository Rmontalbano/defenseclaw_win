using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Inventory;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>
/// The Inventory page's AI BOM view as a real view in the shell stand-in (<see cref="PanelShell"/>), over output the review's <c>RunStep</c> seam hands
/// back (so no process starts, and <c>aibom scan</c> is never run): the switch between the two views, the tabs, the grid and the columns it builds for
/// each tab, the detail pane, the scope chips, the empty and failed states, what a read-only installation turns off (only the two Generate buttons;
/// browsing is reading), and the layout at the window's 940 x 620 DIP minimum. The Summary and Agents tabs are also written as PNGs when the
/// <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </summary>
[Collection(UiCollection.Name)]
public class InventoryBomPanelTests
{
    private static readonly string OpenClaw = PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json");

    // ------------------------------------------------------------------ the two views

    [Fact]
    public void The_page_opens_on_the_components_and_the_ai_bom_view_takes_the_place_of_the_grid_the_detail_and_the_table_browser()
    {
        using var scene = Scene.Open(1400, 900, components: 20);

        UiThread.Run(() =>
        {
            Assert.Equal("components", scene.ViewSwitch.SelectedValue);
            Assert.Equal(Visibility.Visible, scene.ComponentsGrid.Visibility);
            Assert.Equal(Visibility.Visible, scene.ComponentsDetail.Visibility);
            Assert.Equal(Visibility.Visible, scene.TableBrowser.Visibility);
            Assert.Equal(Visibility.Collapsed, scene.BomGrid.Visibility);
            Assert.Equal(Visibility.Collapsed, scene.BomDetail.Visibility);
            Assert.False(scene.BomTabs.IsVisible);
        });

        // The segmented control is what the operator uses; it drives the view-model.
        UiThread.Run(() =>
        {
            scene.ViewSwitch.SelectedValue = "bom";
            scene.Host.Relayout();
            Assert.Equal(InventoryPanelViewModel.ViewBom, scene.ViewModel.ActiveView);
            Assert.Equal(Visibility.Collapsed, scene.ComponentsGrid.Visibility);
            Assert.Equal(Visibility.Collapsed, scene.ComponentsDetail.Visibility);
            Assert.Equal(Visibility.Collapsed, scene.TableBrowser.Visibility);
            Assert.Equal(Visibility.Visible, scene.BomGrid.Visibility);
            Assert.Equal(Visibility.Visible, scene.BomDetail.Visibility);
            Assert.True(scene.BomTabs.IsVisible);

            // Back: the components page is exactly as it was.
            scene.ViewSwitch.SelectedValue = "components";
            scene.Host.Relayout();
            Assert.Equal(Visibility.Visible, scene.ComponentsGrid.Visibility);
            Assert.Equal(Visibility.Visible, scene.TableBrowser.Visibility);
            Assert.Equal(Visibility.Collapsed, scene.BomGrid.Visibility);
            Assert.Equal(20, scene.ViewModel.ComponentsView.Cast<object>().Count());
        });
    }

    [Fact]
    public void Before_a_scan_the_ai_bom_view_is_an_empty_state_with_a_generate_button_that_opens_the_review()
    {
        using var scene = Scene.Open(1100, 760);

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveView = InventoryPanelViewModel.ViewBom;
            scene.Host.Relayout();

            var title = VisualTree.Descendants<TextBlock>(scene.BomGrid.Parent as DependencyObject ?? scene.BomGrid)
                .FirstOrDefault(t => t.Text == "No AI BOM yet" && t.IsVisible);
            Assert.NotNull(title);

            var generate = VisualTree.Descendants<Wpf.Ui.Controls.Button>(scene.Master)
                .Single(b => (b.Content as string) == "Generate AI BOM…");
            Assert.True(generate.IsVisible);
            Assert.False(scene.ViewModel.Review.IsOpen);

            generate.Command!.Execute(null);
            Assert.True(scene.ViewModel.Review.IsConfirming);
            Assert.Empty(scene.Ran);
        });
    }

    // ------------------------------------------------------------------ Summary and Agents

    [Fact]
    public void Rendered_summary_and_agents_tabs()
    {
        using var scene = Scene.Open(1400, 900);
        scene.Generate(OpenClaw);

        UiThread.Run(() =>
        {
            // The Summary tab: the TUI's metric / value rows, and the coverage notes where the detail goes.
            Assert.Equal("summary", scene.BomTabs.SelectedValue);
            Assert.Equal(new[] { "Metric", "Value" }, scene.BomHeaders());
            Assert.Equal("AIBOM version", scene.BomCell(0, 0));
            Assert.Equal("3", scene.BomCell(0, 1));
            Assert.Equal("Coverage notes", scene.DetailHeading());
            scene.Render("inventory-bom-summary");

            scene.BomTabs.SelectedValue = "agents";
            scene.Host.Relayout();
            Assert.Equal(new[] { "ID", "Source", "Model", "Workspace", "Default" }, scene.BomHeaders());
            Assert.Equal(3, scene.BomRowCount());
            Assert.Equal("main", scene.BomCell(0, 0));
            Assert.Equal("example/model-large", scene.BomCell(0, 2));
            Assert.Equal("yes", scene.BomCell(0, 4));

            scene.SelectRow(0);
            scene.Host.Relayout();
            Assert.Equal("AGENT: main", scene.DetailHeading());
            Assert.Equal(
                new[] { "Model", "Workspace", "Default", "Source", "Max Concurrent" },
                scene.DetailLabels("Fields of the selected AI BOM row"));
            Assert.Equal(new[] { "Bindings" }, scene.DetailLabels("Other fields the scan listed for the selected AI BOM row"));
            scene.Render("inventory-bom-agents");
        });
    }

    [Fact]
    public void The_tabs_are_named_and_counted_for_a_screen_reader_and_the_tools_tab_is_there_only_with_tools()
    {
        using var scene = Scene.Open(1100, 760);
        scene.Generate(PayloadFixtures.Read("aibom-scan.multi-connector.json"));

        UiThread.Run(() =>
        {
            Assert.Equal("AI BOM tab", AutomationProperties.GetName(scene.BomTabs));
            var tabs = scene.BomTabs.Items.Cast<DcSegment>().ToList();
            Assert.Equal(
                new[] { "Summary", "Skills (1)", "Plugins (0)", "MCPs (1)", "Agents (0)", "Tools (0)", "Models (0)", "Memory (0)" },
                tabs.Select(t => AutomationProperties.GetName(t)).ToArray());
            var tools = tabs.Single(t => (string)t.Value! == "tools");
            Assert.False(tools.IsVisible);
            Assert.False(tools.IsEnabled);
            Assert.All(tabs.Where(t => (string)t.Value! != "tools"), t => Assert.True(t.IsVisible));
        });

        using var withTools = Scene.Open(1100, 760);
        withTools.Generate(OpenClaw);
        UiThread.Run(() =>
        {
            var tools = withTools.BomTabs.Items.Cast<DcSegment>().Single(t => (string)t.Value! == "tools");
            Assert.True(tools.IsVisible);
            Assert.True(tools.IsEnabled);
            Assert.Equal("Tools (1)", AutomationProperties.GetName(tools));
        });
    }

    // ------------------------------------------------------------------ columns and cells

    [Fact]
    public void Each_tab_builds_the_tuis_columns_and_a_verdict_column_draws_state_pills()
    {
        using var scene = Scene.Open(1400, 900);
        scene.Generate(OpenClaw);

        UiThread.Run(() =>
        {
            foreach (var (tab, headers) in new[]
                     {
                         ("skills", new[] { "ID", "Verdict", "Enabled", "Severity", "Findings", "Source" }),
                         ("plugins", new[] { "Name", "Version", "Origin", "Status", "Verdict", "Findings", "Severity" }),
                         ("mcp", new[] { "ID", "Source", "Transport", "Command/URL" }),
                         ("agents", new[] { "ID", "Source", "Model", "Workspace", "Default" }),
                         ("tools", new[] { "ID", "Name", "Kind", "Source" }),
                         ("models", new[] { "ID", "Source", "Default Model", "Status" }),
                         ("memory", new[] { "ID", "Backend", "Provider", "Files", "Chunks", "Workspace" }),
                     })
            {
                scene.BomTabs.SelectedValue = tab;
                scene.Host.Relayout();
                Assert.Equal(headers, scene.BomHeaders());
            }

            // Skills: the verdict column is a pill per row that has one, in its tone.
            scene.BomTabs.SelectedValue = "skills";
            scene.Host.Relayout();
            var pills = VisualTree.Descendants<DcStatePill>(scene.BomGrid).Where(p => p.IsVisible).ToList();
            Assert.Equal(new[] { "clean", "warning" }, pills.Select(p => p.Word).ToArray());
            Assert.Equal(new[] { "Ok", "Warn" }, pills.Select(p => p.Tone).ToArray());
        });
    }

    [Fact]
    public void A_scan_of_several_connectors_leads_every_table_with_a_connector_column()
    {
        using var scene = Scene.Open(1400, 900, roster: new[] { "claudecode", "codex" });
        scene.Generate(PayloadFixtures.Read("aibom-scan.multi-connector.json"));

        UiThread.Run(() =>
        {
            scene.BomTabs.SelectedValue = "skills";
            scene.Host.Relayout();

            Assert.Equal(new[] { "Connector", "ID", "Verdict", "Enabled", "Severity", "Findings", "Source" }, scene.BomHeaders());
            Assert.Equal("claudecode", scene.BomCell(0, 0));
            Assert.Equal("pdf-tools", scene.BomCell(0, 1));
        });
    }

    [Fact]
    public void The_grid_shares_its_width_between_the_columns_and_every_column_keeps_its_floor()
    {
        using var scene = Scene.Open(1400, 900);
        scene.Generate(OpenClaw);

        UiThread.Run(() =>
        {
            scene.BomTabs.SelectedValue = "agents";
            scene.Host.Relayout();

            var viewport = VisualTree.Find<ScrollViewer>(scene.BomGrid)!.ViewportWidth;
            var widths = scene.BomHeaderWidths();
            Assert.Equal(viewport, widths.Sum(), 3.0);
            Assert.All(scene.BomGrid.Columns, column => Assert.True(column.ActualWidth >= column.MinWidth - 0.5, $"{column.Header}: {column.ActualWidth} < {column.MinWidth}"));

            // 'Default' is a flag, so it is the narrow one.
            Assert.True(widths[4] < widths[0]);
        });
    }

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void A_big_scan_realizes_only_the_rows_in_view(int width, int height)
    {
        using var scene = Scene.Open(width, height);
        var rows = string.Join(',', Enumerable.Range(1, 4000).Select(i => $"{{\"id\": \"skill-{i}\", \"source\": \"S:\\\\skills\", \"eligible\": true, \"enabled\": true, \"policy_verdict\": \"clean\"}}"));
        scene.Generate($"{{\"connector\": \"claudecode\", \"skills\": [{rows}], \"summary\": {{\"total_items\": 4000, \"skills\": {{\"count\": 4000, \"eligible\": 4000}}}}}}");

        UiThread.Run(() =>
        {
            scene.BomTabs.SelectedValue = "skills";
            scene.Host.Relayout();

            Assert.Equal(4000, scene.ViewModel.BomBrowser.Rows.Count);
            var realized = VisualTree.Descendants<DataGridRow>(scene.BomGrid).Count();
            Assert.InRange(realized, 1, 60);
            Assert.True(ScrollViewer.GetCanContentScroll(scene.BomGrid));

            // Scrolling realizes new rows and lets the old ones go.
            var scroll = VisualTree.Find<ScrollViewer>(scene.BomGrid)!;
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2);
            scene.Host.Relayout();
            Assert.InRange(VisualTree.Descendants<DataGridRow>(scene.BomGrid).Count(), 1, 60);
        });
    }

    [Fact]
    public void A_narrow_window_keeps_each_columns_floor_and_scrolls_sideways_instead_of_squeezing()
    {
        using var scene = Scene.Open(940, 620, roster: new[] { "claudecode", "codex" });
        scene.Generate(PayloadFixtures.Read("aibom-scan.95159fd.synthetic.json"));

        UiThread.Run(() =>
        {
            scene.BomTabs.SelectedValue = "plugins";
            scene.Host.Relayout();

            // Connector + seven columns need more than the ~640 DIPs a card has here.
            var scroll = VisualTree.Find<ScrollViewer>(scene.BomGrid)!;
            Assert.All(scene.BomGrid.Columns, column => Assert.True(column.ActualWidth >= column.MinWidth - 0.5, $"{column.Header}: {column.ActualWidth} < {column.MinWidth}"));
            Assert.True(scroll.ScrollableWidth > 0, "the columns do not fit, so the grid should scroll sideways");
        });
    }

    // ------------------------------------------------------------------ detail

    [Fact]
    public void Selecting_a_row_lists_the_tuis_fields_then_the_other_members_under_their_own_heading()
    {
        using var scene = Scene.Open(1400, 900);
        scene.Generate(OpenClaw);

        UiThread.Run(() =>
        {
            scene.BomTabs.SelectedValue = "memory";
            scene.Host.Relayout();

            // Nothing selected: a sentence, not an empty pane.
            Assert.Equal("Details", scene.DetailHeading());
            Assert.Contains(VisualTree.Descendants<TextBlock>(scene.BomDetail), t => t.IsVisible && t.Text == "Select a row to see its fields.");

            scene.SelectRow(0);
            scene.Host.Relayout();
            Assert.Equal("MEMORY: main", scene.DetailHeading());
            Assert.Equal(
                new[] { "Backend", "Provider", "Workspace", "DB Path", "Files", "Chunks", "FTS Available", "Vector Enabled", "Sources" },
                scene.DetailLabels("Fields of the selected AI BOM row"));
            // Nothing else was listed for this row, so there is no heading for it.
            Assert.DoesNotContain(VisualTree.Descendants<TextBlock>(scene.BomDetail), t => t.IsVisible && t.Text == "Also in this scan");

            scene.BomTabs.SelectedValue = "models";
            scene.Host.Relayout();
            scene.SelectRow(4);
            scene.Host.Relayout();
            Assert.Contains(VisualTree.Descendants<TextBlock>(scene.BomDetail), t => t.IsVisible && t.Text == "Also in this scan");
        });
    }

    [Fact]
    public void The_summary_detail_names_the_connectors_that_were_skipped_and_the_commands_that_failed()
    {
        using var scene = Scene.Open(1400, 900);
        scene.Generate(PayloadFixtures.Read("aibom-scan.partial.synthetic.json"));

        UiThread.Run(() =>
        {
            Assert.Equal("Coverage notes", scene.DetailHeading());
            var labels = scene.DetailLabels("Fields of the selected AI BOM row");
            Assert.Equal(3, labels.Count(l => l == "Skipped"));
            Assert.Contains("codex: command failed", labels);

            // ... and the bar above the tabs says so too.
            var bar = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(scene.Shell.Page!), b => b.Title == "Not everything came through");
            Assert.True(bar.IsOpen);
            Assert.Contains("entry 2", bar.Message, StringComparison.Ordinal);
        });
    }

    // ------------------------------------------------------------------ the scope chips

    [Fact]
    public void The_scope_chips_are_toggles_that_drive_the_only_argument_and_all_and_fast_set_them()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveView = InventoryPanelViewModel.ViewBom;
            scene.Host.Relayout();

            var chips = VisualTree.Descendants<ToggleButton>(scene.Chrome).Where(c => c.Content is string).ToList();
            Assert.Equal(new[] { "skills", "plugins", "mcp", "agents", "tools", "models", "memory" }, chips.Select(c => (string)c.Content).ToArray());
            Assert.All(chips, c => Assert.True(c.IsChecked));
            Assert.Equal("Scan skills", AutomationProperties.GetName(chips[0]));
            Assert.Equal(string.Empty, scene.ViewModel.BomBrowser.OnlyArgument);

            chips[3].IsChecked = false;
            chips[4].IsChecked = false;
            Assert.Equal("skills,plugins,mcp,models,memory", scene.ViewModel.BomBrowser.OnlyArgument);

            var fast = VisualTree.Descendants<Wpf.Ui.Controls.Button>(scene.Chrome).Single(b => (b.Content as string) == "Fast");
            fast.Command!.Execute(null);
            Assert.Equal("skills,plugins,mcp", scene.ViewModel.BomBrowser.OnlyArgument);
            Assert.Equal(new[] { true, true, true, false, false, false, false }, chips.Select(c => c.IsChecked == true).ToArray());

            var all = VisualTree.Descendants<Wpf.Ui.Controls.Button>(scene.Chrome).Single(b => (b.Content as string) == "All scope");
            all.Command!.Execute(null);
            Assert.All(chips, c => Assert.True(c.IsChecked));
        });
    }

    // ------------------------------------------------------------------ failed and too large

    [Fact]
    public void A_scan_that_is_too_large_has_its_sentence_in_a_bar_and_nothing_in_the_grid()
    {
        using var scene = Scene.Open(1100, 760);
        var big = "{\"connector\": \"x\", \"note\": \"" + new string('y', InventoryBomSnapshot.MaxOutputBytes + 10) + "\"}";

        scene.Generate(big);

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.BomFailed);
            var bar = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(scene.Shell.Page!), b => b.Title == "AI BOM");
            Assert.True(bar.IsOpen);
            Assert.StartsWith("Too large to display: aibom scan output is ", bar.Message, StringComparison.Ordinal);
            Assert.Equal(0, scene.BomRowCount());
            Assert.Equal("No AI BOM yet", scene.ViewModel.BomBrowser.EmptyTitle);
        });
    }

    // ------------------------------------------------------------------ the connector scope

    [Fact]
    public void The_shared_connector_scope_narrows_the_browser_and_its_count_lines()
    {
        using var scene = Scene.Open(1400, 900, roster: new[] { "claudecode", "codex" });
        scene.Generate(PayloadFixtures.Read("aibom-scan.multi-connector.json"));

        UiThread.Run(() =>
        {
            scene.ViewModel.SetActive(true);
            scene.Services.ConnectorScope.Set("codex");
            scene.Host.Relayout();

            Assert.Equal("codex", scene.ViewModel.SelectedBomConnector);
            Assert.Equal("codex", Assert.Single(scene.ViewModel.BomRows).Connector);
            Assert.StartsWith("codex · 0 items", scene.ViewModel.BomBrowser.Caption, StringComparison.Ordinal);
            Assert.Equal("Codex", scene.BomRowValue("Source"));

            scene.Services.ConnectorScope.Set(null);
            scene.Host.Relayout();
            Assert.Equal(2, scene.ViewModel.BomRows.Count);
            Assert.StartsWith("All 2 connectors", scene.BomRowValue("Source"), StringComparison.Ordinal);
            scene.ViewModel.SetActive(false);
        });
    }

    // ------------------------------------------------------------------ a read-only installation (CUST-308)

    [Fact]
    public void On_a_writable_installation_both_generate_buttons_are_on_and_say_what_they_run()
    {
        using var scene = Scene.Open(1100, 760);

        UiThread.Run(() =>
        {
            Assert.Null(scene.ViewModel.InstallationBlockedReason);

            var toolbar = scene.ToolbarGenerate;
            Assert.True(toolbar.IsEnabled);
            Assert.StartsWith("Generate AI BOM… defenseclaw aibom scan --json", (string)toolbar.ToolTip, StringComparison.Ordinal);
            Assert.Contains("the categories under Scope", (string)toolbar.ToolTip, StringComparison.Ordinal);
            Assert.True(ToolTipService.GetShowOnDisabled(toolbar));

            scene.ViewSwitch.SelectedValue = "bom";
            scene.Host.Relayout();
            var empty = scene.EmptyStateGenerate;
            Assert.True(empty.IsVisible);
            Assert.True(empty.IsEnabled);
            Assert.StartsWith("Generate AI BOM… defenseclaw aibom scan --json", (string)empty.ToolTip, StringComparison.Ordinal);
            Assert.True(ToolTipService.GetShowOnDisabled(empty));
        });
    }

    [Fact]
    public void On_a_managed_installation_both_generate_buttons_are_off_with_the_installations_reason_and_the_page_still_reads()
    {
        using var scene = Scene.Open(1100, 760, components: 12, managed: true);

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);

            // The toolbar button: off, and what a hover on it says is the installation's sentence, not the button's own description.
            var toolbar = scene.ToolbarGenerate;
            Assert.False(toolbar.IsEnabled);
            Assert.True(ToolTipService.GetShowOnDisabled(toolbar));
            Assert.Equal(TestInstallations.ManagedReason, toolbar.ToolTip);

            // The components view is a read: the rows are there, and refreshing is still offered.
            Assert.Equal(12, vm.ComponentsView.Cast<object>().Count());
            Assert.True(scene.ToolbarButton("Refresh").IsEnabled);

            // The AI BOM view has nothing to browse yet, and its own Generate gives the same single reason.
            scene.ViewSwitch.SelectedValue = "bom";
            scene.Host.Relayout();
            var empty = scene.EmptyStateGenerate;
            Assert.True(empty.IsVisible);
            Assert.False(empty.IsEnabled);
            Assert.True(ToolTipService.GetShowOnDisabled(empty));
            Assert.Equal(TestInstallations.ManagedReason, empty.ToolTip);

            // Save has nothing to save - and says nothing about the installation, which is not why it is off.
            Assert.False(scene.ToolbarSave.IsEnabled);
            Assert.NotEqual(TestInstallations.ManagedReason, scene.ToolbarSave.ToolTip as string);

            // The tabs and the scope chips are reads: they move, and the scope is kept for a scan that can be run from somewhere else.
            scene.BomTabs.SelectedValue = "agents";
            scene.Host.Relayout();
            Assert.Equal("agents", vm.BomBrowser.ActiveTab);
            vm.BomBrowser.ShowFastScopeCommand.Execute(null);
            Assert.Equal("skills,plugins,mcp", vm.BomBrowser.OnlyArgument);
        });
    }

    [Fact]
    public void A_loaded_ai_bom_stays_browsable_and_savable_when_the_installation_turns_read_only_and_only_generate_goes_off()
    {
        using var scene = Scene.Open(1400, 900);
        scene.Generate(OpenClaw);

        UiThread.Run(() =>
        {
            var vm = scene.ViewModel;
            vm.SetActive(true);
            try
            {
                Assert.True(vm.GenerateAiBomCommand.CanExecute(null));
                Assert.True(scene.ToolbarGenerate.IsEnabled);
                Assert.True(scene.ToolbarSave.IsEnabled);

                // config.yaml is edited to managed while the page is on screen.
                scene.Services.Installation.Replace(TestInstallations.ManagedAt(scene.Temp.Path));
                scene.Host.Relayout();

                // Generate is the control that changed: off, with the installation's sentence on hover.
                Assert.False(vm.GenerateAiBomCommand.CanExecute(null));
                Assert.False(scene.ToolbarGenerate.IsEnabled);
                Assert.Equal(TestInstallations.ManagedReason, scene.ToolbarGenerate.ToolTip);

                // Save JSON writes a file the operator picks, never DefenseClaw's own state.
                Assert.True(vm.CanSaveBom);
                Assert.True(scene.ToolbarSave.IsEnabled);

                // The tabs, the rows, the detail pane, the search and the scope chips are reads.
                scene.BomTabs.SelectedValue = "agents";
                scene.Host.Relayout();
                Assert.Equal(3, scene.BomRowCount());
                scene.SelectRow(0);
                Assert.Equal("AGENT: main", scene.DetailHeading());
                Assert.Equal(
                    new[] { "Model", "Workspace", "Default", "Source", "Max Concurrent" },
                    scene.DetailLabels("Fields of the selected AI BOM row"));

                vm.SearchText = "no agent is called this";
                scene.Host.Relayout();
                Assert.Equal(0, scene.BomRowCount());
                vm.SearchText = string.Empty;
                scene.Host.Relayout();
                Assert.Equal(3, scene.BomRowCount());

                vm.BomBrowser.ShowFastScopeCommand.Execute(null);
                Assert.Equal("skills,plugins,mcp", vm.BomBrowser.OnlyArgument);
                vm.BomBrowser.ShowAllScopeCommand.Execute(null);
                Assert.Equal(string.Empty, vm.BomBrowser.OnlyArgument);

                // And back: the config is fixed, and the same button is on again with its own description.
                scene.Services.Installation.Replace(TestInstallations.UserDefault());
                scene.Host.Relayout();
                Assert.True(vm.GenerateAiBomCommand.CanExecute(null));
                Assert.True(scene.ToolbarGenerate.IsEnabled);
                Assert.StartsWith("Generate AI BOM… defenseclaw aibom scan --json", (string)scene.ToolbarGenerate.ToolTip, StringComparison.Ordinal);
            }
            finally
            {
                vm.SetActive(false);
            }
        });
    }

    // ------------------------------------------------------------------ bindings

    /// <summary>Collects what WPF's binding engine says went wrong, so a typo in a binding path fails a test instead of showing a blank.</summary>
    private sealed class BindingErrors : System.Diagnostics.TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is { Length: > 0 })
            {
                Messages.Add(message);
            }
        }
    }

    [Fact]
    public void Every_binding_of_the_ai_bom_view_names_something_that_exists_through_every_tab_and_change()
    {
        var errors = new BindingErrors();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        var level = source.Switch.Level;
        UiThread.Run(() =>
        {
            // Refresh first: WPF reads its trace configuration once, and only a refreshed source honours a level set in code.
            System.Diagnostics.PresentationTraceSources.Refresh();
            source.Listeners.Add(errors);
            source.Switch.Level = System.Diagnostics.SourceLevels.Warning;

            // the detector itself: a binding to nothing is reported, so an empty result below means something
            var probe = new TextBlock { DataContext = new object() };
            probe.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchThing"));
            _ = probe.Text;
        });

        try
        {
            using var scene = Scene.Open(1400, 900, components: 10, roster: new[] { "hermes", "opencode" });
            scene.Generate(PayloadFixtures.Read("aibom-scan.95159fd.synthetic.json"));

            UiThread.Run(() =>
            {
                foreach (var tab in new[] { "skills", "plugins", "mcp", "agents", "tools", "models", "memory", "summary" })
                {
                    scene.BomTabs.SelectedValue = tab;
                    scene.Host.Relayout();
                    if (scene.ViewModel.BomBrowser.Rows.Count > 0)
                    {
                        scene.SelectRow(0);
                    }

                    scene.ViewModel.BomBrowser.StatusFilter = tab == "plugins" ? "loaded" : "all";
                    scene.ViewModel.BomBrowser.SearchText = tab == "mcp" ? "docs" : string.Empty;
                    scene.Host.Relayout();
                }

                scene.ViewModel.BomBrowser.SearchText = string.Empty;
                scene.ViewModel.BomBrowser.ScopeChips[0].IsActive = false;
                scene.ViewModel.BomBrowser.ShowFastScopeCommand.Execute(null);
                scene.ViewModel.SetActive(true);
                scene.Services.ConnectorScope.Set("opencode");
                scene.Host.Relayout();
                scene.BomTabs.SelectedValue = "agents";
                scene.Host.Relayout();
                scene.ViewSwitch.SelectedValue = "components";
                scene.Host.Relayout();
                scene.ViewModel.SetActive(false);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                source.Listeners.Remove(errors);
                source.Switch.Level = level;
            });
        }

        // The probe's own complaint is the only one.
        var complaints = errors.Messages.Where(m => !m.Contains("NoSuchThing", StringComparison.Ordinal)).ToList();
        Assert.Contains(errors.Messages, m => m.Contains("NoSuchThing", StringComparison.Ordinal));
        Assert.Empty(complaints);
    }

    // ------------------------------------------------------------------ the minimum window

    [Fact]
    public void At_the_minimum_window_the_ai_bom_page_scrolls_and_the_grid_and_the_detail_can_be_reached()
    {
        using var scene = Scene.Open(940, 620);
        scene.Generate(OpenClaw);

        UiThread.Run(() =>
        {
            scene.BomTabs.SelectedValue = "agents";
            scene.SelectRow(0);
            scene.Host.Relayout();

            // The detail drops under the grid at this width, as the components page's does.
            Assert.Equal("Narrow", scene.MasterDetail.Tag);
            Assert.True(scene.BomGrid.ActualHeight >= 280, $"the grid is {scene.BomGrid.ActualHeight} DIPs tall");

            scene.PageScroll.ScrollToEnd();
            scene.Host.Relayout();
            Assert.True(scene.IsWithinViewport(scene.DetailCard), "the detail pane is out of reach");
            scene.Render("inventory-bom-940x620-scrolled-to-end");
        });
    }

    [Fact]
    public void At_the_minimum_window_the_eight_tabs_wrap_inside_the_page_and_the_page_never_scrolls_sideways()
    {
        using var scene = Scene.Open(940, 620);
        scene.Generate(OpenClaw);

        UiThread.Run(() =>
        {
            Assert.Equal(0, scene.PageScroll.ScrollableWidth, 0.5);
            Assert.True(scene.BomTabs.ActualWidth <= scene.PageScroll.ViewportWidth + 0.5, $"the tabs are {scene.BomTabs.ActualWidth} wide on a {scene.PageScroll.ViewportWidth} page");

            // Every tab is on screen: wrapped onto a second line rather than cut off.
            var segments = scene.BomTabs.Items.Cast<DcSegment>().Where(s => s.IsVisible).ToList();
            Assert.Equal(8, segments.Count);
            Assert.All(segments, s => Assert.True(
                s.TranslatePoint(new Point(s.ActualWidth, 0), scene.BomTabs).X <= scene.BomTabs.ActualWidth + 0.5,
                $"{AutomationProperties.GetName(s)} is cut off"));
        });
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private Scene(int width, int height, int components, string[] roster, bool managed)
        {
            if (components > 0)
            {
                InventoryFixture.Create(_temp.File("inventory.db"), components);
                SqlitePools.Release(_temp.Path);
            }

            // A managed installation is read-only from the start (CUST-308); the scratch folder is the one its config.yaml is "found" in.
            Services = TestServices.Create(_temp, installation: managed ? TestInstallations.ManagedAt(_temp.Path) : null);
            if (roster.Length > 0)
            {
                Services.ConnectorScope.UpdateRoster(roster);
            }

            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(Services, width, height);
                _ = shell.Show<InventoryPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (InventoryPanelViewModel)Shell.ViewModel);
            UiThread.WaitFor(() => ViewModel.HasLoaded && !ViewModel.IsLoading, "inventory loaded");
            if (components > 0)
            {
                UiThread.WaitFor(() => ViewModel.Tables.Count > 0, "tables listed");
            }

            UiThread.Run(() => Host.Relayout());
        }

        public AppServices Services { get; }

        public PanelShell Shell { get; }

        public InventoryPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        /// <summary>Every argv the review ran, with the options it ran them with.</summary>
        public List<(IReadOnlyList<string> Argv, CliRunOptions? Options)> Ran { get; } = new();

        public static Scene Open(int width, int height, int components = 0, string[]? roster = null, bool managed = false) =>
            new(width, height, components, roster ?? Array.Empty<string>(), managed);

        /// <summary>The scratch folder the composition treats as its data directory.</summary>
        public TempDirectory Temp => _temp;

        /// <summary>The toolbar's icon button of that name (its automation name), as drawn.</summary>
        public Wpf.Ui.Controls.Button ToolbarButton(string automationName) =>
            VisualTree.Descendants<Wpf.Ui.Controls.Button>(Named<DcPageToolbar>("PageToolbar"))
                .Single(b => AutomationProperties.GetName(b) == automationName);

        /// <summary>The toolbar's Generate AI BOM button.</summary>
        public Wpf.Ui.Controls.Button ToolbarGenerate => ToolbarButton("Generate AI BOM…");

        /// <summary>The toolbar's Save AI BOM JSON button.</summary>
        public Wpf.Ui.Controls.Button ToolbarSave => ToolbarButton("Save AI BOM JSON…");

        /// <summary>The Generate AI BOM button of the AI BOM view's empty state.</summary>
        public Wpf.Ui.Controls.Button EmptyStateGenerate =>
            VisualTree.Descendants<Wpf.Ui.Controls.Button>(Master).Single(b => (b.Content as string) == "Generate AI BOM…");

        private T Named<T>(string name)
            where T : class =>
            Shell.Page!.FindName(name) as T ?? throw new InvalidOperationException($"{name} not found in the Inventory panel.");

        public ScrollViewer PageScroll => Named<ScrollViewer>("PageScroll");

        public StackPanel Chrome => Named<StackPanel>("Chrome");

        public DcSegmented ViewSwitch => Named<DcSegmented>("ViewSwitch");

        public DcSegmented BomTabs => Named<DcSegmented>("BomTabs");

        public Wpf.Ui.Controls.DataGrid ComponentsGrid => Named<Wpf.Ui.Controls.DataGrid>("ComponentsGrid");

        public Wpf.Ui.Controls.DataGrid BomGrid => Named<Wpf.Ui.Controls.DataGrid>("BomGrid");

        public Wpf.Ui.Controls.CardExpander TableBrowser => Named<Wpf.Ui.Controls.CardExpander>("TableBrowser");

        public Grid MasterDetail => Named<Grid>("MasterDetail");

        public Border DetailCard => Named<Border>("DetailCard");

        public Grid ComponentsDetail => Named<Grid>("ComponentsDetail");

        public Grid BomDetail => Named<Grid>("BomDetail");

        /// <summary>The card the grids sit in (the grid's grandparent).</summary>
        public FrameworkElement Master => (FrameworkElement)((FrameworkElement)BomGrid.Parent).Parent;

        public bool IsWithinViewport(FrameworkElement element)
        {
            var bottom = element.TranslatePoint(new Point(0, element.ActualHeight), PageScroll).Y;
            return bottom > 0 && bottom <= PageScroll.ViewportHeight + 1;
        }

        /// <summary>Opens the Generate review and confirms it; the review hands back <paramref name="stdout"/> as the command's whole output.</summary>
        public void Generate(string stdout, int exitCode = 0)
        {
            UiThread.Run(() =>
            {
                ViewModel.Review.RunStep = (_, argv, options) =>
                {
                    Ran.Add((argv, options));
                    return Task.FromResult(Done(argv, stdout, exitCode));
                };
                ViewModel.GenerateAiBomCommand.Execute(null);
            });

            UiThread.Run(() => ViewModel.Review.ConfirmCommand.ExecuteAsync(null)).GetAwaiter().GetResult();

            // The operator closes the finished dialog; the page under it is what the tests look at.
            UiThread.Run(() =>
            {
                ViewModel.Review.DismissCommand.Execute(null);
                Host.Relayout();
            });
        }

        public void Render(string name) => RenderTo.Png(Host, name);

        /// <summary>The header text of every column of the AI BOM grid, in order.</summary>
        public string[] BomHeaders() => BomGrid.Columns.Select(c => (string)c.Header).ToArray();

        /// <summary>The rendered width of each column, read from its header cell (a star column's own ActualWidth is not refreshed by layout).</summary>
        public double[] BomHeaderWidths() =>
            VisualTree.Descendants<DataGridColumnHeader>(BomGrid)
                .Where(h => h.Column is not null)
                .OrderBy(h => h.Column.DisplayIndex)
                .Select(h => h.ActualWidth)
                .ToArray();

        public int BomRowCount() => ViewModel.BomBrowser.Rows.Count;

        /// <summary>The text of a cell of the AI BOM grid as it is drawn: row and column, counted from 0.</summary>
        public string BomCell(int row, int column)
        {
            var container = (DataGridRow)BomGrid.ItemContainerGenerator.ContainerFromItem(ViewModel.BomBrowser.Rows[row]);
            var cells = VisualTree.Descendants<DataGridCell>(container).OrderBy(c => c.Column.DisplayIndex).ToList();
            var text = VisualTree.Descendants<TextBlock>(cells[column]).FirstOrDefault();
            return text?.Text ?? string.Empty;
        }

        /// <summary>The value of a Summary row, by its metric.</summary>
        public string BomRowValue(string metric) =>
            ViewModel.BomBrowser.Rows.Single(r => r.Cells[0] == metric).Cells[1];

        public void SelectRow(int index)
        {
            BomGrid.SelectedItem = ViewModel.BomBrowser.Rows[index];
            Host.Relayout();
        }

        public string DetailHeading() =>
            VisualTree.Descendants<DcCardHeader>(BomDetail).Single().Content as string ?? string.Empty;

        /// <summary>The labels listed by the named items control of the AI BOM detail pane.</summary>
        public string[] DetailLabels(string listName)
        {
            var list = VisualTree.Descendants<ItemsControl>(BomDetail).Single(i => AutomationProperties.GetName(i) == listName);
            return list.Items.Cast<InventoryKeyValueRow>().Select(r => r.Key).ToArray();
        }

        private static CliInvocation Done(IReadOnlyList<string> argv, string stdout, int exitCode)
        {
            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            foreach (var line in stdout.Split('\n'))
            {
                InvocationFactory.Append(invocation, line.TrimEnd('\r'));
            }

            InvocationFactory.Finish(invocation, exitCode);
            return invocation;
        }

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            Services.Dispose();
            SqlitePools.Release(_temp.Path);
            _temp.Dispose();
        }
    }
}
