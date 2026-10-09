using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// What the operator sees on the Policies panel of a runtime that has the policy model (CUST-293), and that the 0.8.10 panel is still exactly
/// what it was: the views and their tables, the scope chooser, the inspector with the changes the model offers, the buttons that go off with
/// their reason, and the acknowledgement checkbox in the review. Synthetic data; a PNG is written when <c>DC_RENDER_DIR</c> names a folder.
/// <para>
/// The first read is asynchronous (the panel asks the runtime what it is, then reads the catalog off the UI thread), so a test builds the panel
/// on the UI thread, waits for the read from its own thread - leaving the UI thread free to resume the view-model - and then looks at it.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PolicyModelViewTests
{
    private sealed record Opened(PoliciesPanel View, PoliciesPanelViewModel Vm, OffscreenHost Host, ScriptedCli Cli, Task Init);

    private static Opened Start(AppServices services, string scenario, double width, double height, Action<ScriptedCli>? script)
    {
        var cli = new ScriptedCli(scenario);
        script?.Invoke(cli);
        var vm = new PoliciesPanelViewModel(services) { RunCli = cli.Run, DataFiles = new FixtureData() };
        var view = new PoliciesPanel { DataContext = vm };
        var host = new OffscreenHost(view, width, height);
        return new Opened(view, vm, host, cli, vm.InitializeAsync());
    }

    /// <summary>Builds the panel on the UI thread, waits for its first read, runs <paramref name="body"/> on the UI thread, and tears the host down.</summary>
    private static void Scenario(
        AppServices services,
        Action<PoliciesPanel, PoliciesPanelViewModel, OffscreenHost, ScriptedCli> body,
        string scenario = Fresh,
        double width = 1180,
        double height = 780,
        Action<ScriptedCli>? script = null)
    {
        var opened = UiThread.Run(() => Start(services, scenario, width, height, script));
        try
        {
            UiThread.WaitFor(() => opened.Init.IsCompleted, "policies read");
            UiThread.Run(() =>
            {
                if (opened.Vm.Model is { } model)
                {
                    model.Review.RunStep = (_, argv, _) => Task.FromResult(Result(0, argv, "ok"));
                }

                opened.Host.Relayout();
                body(opened.View, opened.Vm, opened.Host, opened.Cli);
            });
        }
        finally
        {
            UiThread.Run(opened.Host.Dispose);
        }
    }

    /// <summary>A composition over the pinned runtime, started as the app starts it at launch: the panel's first visit waits for the first probe.</summary>
    private static AppServices ServicesWithModel(TempDirectory temp)
    {
        var services = TestServices.Create(temp, runtimeProbeRunner: ProbeRunner(Pinned));
        services.Runtime.Start();
        return services;
    }

    private static PolicyModelView ModelViewOf(PoliciesPanel view) => Assert.Single(VisualTree.Descendants<PolicyModelView>(view));

    private static Wpf.Ui.Controls.Button ButtonNamed(DependencyObject root, string name) =>
        VisualTree.Descendants<Wpf.Ui.Controls.Button>(root).First(b => AutomationProperties.GetName(b) == name);

    private static void Calm(OffscreenHost host, PolicyModelView view)
    {
        // The pane fades in over 120 ms; a picture taken now would catch it half drawn.
        var inspector = VisualTree.Descendants<DcInspector>(view).Single();
        inspector.BeginAnimation(UIElement.OpacityProperty, null);
        inspector.Opacity = 1;
        host.Relayout();
    }

    private static void Show(PolicyModelViewModel model, string view) => model.SelectedNav = model.Nav.Single(n => n.View == view);

    // ------------------------------------------------------------------ the model panel

    [Fact]
    public void The_model_panel_replaces_the_table_and_shows_the_views_the_posture_scopes_and_the_changes_for_a_scope()
    {
        using var temp = new TempDirectory();
        using var services = ServicesWithModel(temp);
        Scenario(
            services,
            (view, vm, host, _) =>
            {
                Assert.True(vm.UsesModel);
                var model = vm.Model!;
                var modelView = ModelViewOf(view);

                // The 0.8.10 table is out of the way, and the toolbar is the model's.
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("ClassicHost")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("ModelHost")).Visibility);
                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(modelView));
                Assert.True(toolbar.IsVisible);
                Assert.Equal("Policies", toolbar.Title);
                Assert.Equal("● default policy · default pack · 1 of 5 opt-in packs · 26 chains (4 can block)", toolbar.Caption);

                // The runtime's six views, with their counts, one segment each.
                var nav = Assert.Single(VisualTree.Descendants<DcSegmented>(modelView), l => AutomationProperties.GetName(l) == "Policy views");
                Assert.Equal(6, nav.Items.Count);
                Assert.Equal(model.Nav[0], nav.SelectedItem);
                Assert.Equal(
                    new[] { "Posture, 3", "Opt-in packs, 1/5", "Chains, 26", "Rule families, 7", "Policies, 3", "Rule packs, 3" },
                    VisualTree.Descendants<DcSegment>(nav).Select(AutomationProperties.GetName).ToArray());
                var texts = VisualTree.Descendants<TextBlock>(nav).Select(t => t.Text).ToArray();
                Assert.Contains("Posture", texts);
                Assert.Contains("Opt-in packs", texts);
                Assert.Contains("1/5", texts);
                Assert.Contains("26", texts);
                Assert.DoesNotContain(texts, t => t.Contains("andbox", StringComparison.Ordinal));

                // One grid, built from the model's columns, with a row per scope.
                var grid = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(modelView));
                Assert.Equal(new[] { "Scope", "Mode", "Blocks at", "Alerts at", "Approval", "Rule pack", "Opt-in" }, grid.Columns.Select(c => c.Header?.ToString()).ToArray());
                Assert.Equal(3, grid.Items.Count);

                // A scope opens the inspector with its detail and the changes the model offers it.
                model.SelectedRow = model.Rows.Single(r => r.Key == "claudecode");
                host.Relayout();
                Calm(host, modelView);

                var inspector = Assert.Single(VisualTree.Descendants<DcInspector>(modelView));
                Assert.True(inspector.IsOpen);
                var headings = VisualTree.Descendants<TextBlock>(inspector).Select(t => t.Text).ToArray();
                Assert.Contains("Posture · claudecode", headings);
                Assert.Contains("Tool-call block level", headings);
                Assert.Contains("Validate a rule pack", headings);

                // The choice in force is a mark, not a button; the others are buttons, and the one that protects less says so.
                var current = Assert.Single(VisualTree.Descendants<TextBlock>(inspector), t => AutomationProperties.GetName(t) == "Tool-call block level: MEDIUM+ (current)");
                Assert.True(current.IsVisible);
                Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.Button>(inspector), b => b.IsVisible && AutomationProperties.GetName(b).EndsWith("(current)", StringComparison.Ordinal));
                var weaker = ButtonNamed(inspector, "Tool-call block level: CRITICAL, reduces protection");
                Assert.True(weaker.IsEnabled);
                Assert.Contains("Reduces protection", weaker.ToolTip?.ToString(), StringComparison.Ordinal);
                var marks = VisualTree.Descendants<DcSymbolIcon>(weaker).Count(i => i.IsVisible);
                Assert.Equal(1, marks);
                var safer = ButtonNamed(inspector, "Tool-call alert level: LOW+");
                Assert.DoesNotContain(VisualTree.Descendants<DcSymbolIcon>(safer), i => i.IsVisible);

                RenderTo.Png(host, "policies-model-posture");
            },
            Connectors);
    }

    [Fact]
    public void Every_view_is_drawn_in_the_one_grid_from_the_columns_the_model_gives()
    {
        using var temp = new TempDirectory();
        using var services = ServicesWithModel(temp);
        Scenario(
            services,
            (view, vm, host, _) =>
            {
                var model = vm.Model!;
                var modelView = ModelViewOf(view);
                var grid = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(modelView));

                foreach (var nav in model.Nav.ToList())
                {
                    Show(model, nav.View);
                    host.Relayout();

                    Assert.Equal(model.Columns.Select(c => c.Header).ToArray(), grid.Columns.Select(c => c.Header?.ToString()).ToArray());
                    Assert.Equal(model.Rows.Count, grid.Items.Count);
                    Assert.True(grid.Columns.All(c => c.ActualWidth > 0), nav.View);
                }

                // The cells wear the control their kind asks for.
                Show(model, "chains");
                host.Relayout();
                Assert.NotEmpty(VisualTree.Descendants<DcSeverityBadge>(grid));
                Assert.NotEmpty(VisualTree.Descendants<DcStatusLabel>(grid));
                RenderTo.Png(host, "policies-model-chains");

                Show(model, "policies");
                host.Relayout();
                Assert.NotEmpty(VisualTree.Descendants<DcStatePill>(grid));
                model.SelectedRow = model.Rows.Single(r => r.Key == "permissive");
                host.Relayout();
                Calm(host, modelView);
                RenderTo.Png(host, "policies-model-policies");

                // The scope chooser shows for the views that follow one scope, and not for the others.
                model.SelectedRow = null;
                Show(model, "optin");
                host.Relayout();
                var chooser = Assert.Single(VisualTree.Descendants<ComboBox>(modelView), c => AutomationProperties.GetName(c) == "Scope");
                Assert.True(chooser.IsVisible);
                Assert.Equal(new[] { "global", "claudecode", "codex" }, chooser.Items.Cast<string>().ToArray());
                model.SelectedScope = "codex";
                host.Relayout();
                RenderTo.Png(host, "policies-model-optin");

                Show(model, "posture");
                host.Relayout();
                Assert.False(chooser.IsVisible);

                Show(model, "families");
                host.Relayout();
                Assert.True(chooser.IsVisible);
                Assert.Equal("codex", chooser.SelectedItem);
                RenderTo.Png(host, "policies-model-families");
            },
            Connectors);
    }

    [Fact]
    public void A_review_that_reduces_protection_shows_the_checkbox_and_keeps_the_confirm_button_off_until_it_is_ticked()
    {
        using var temp = new TempDirectory();
        using var services = ServicesWithModel(temp);
        Scenario(
            services,
            (view, vm, host, _) =>
            {
                var model = vm.Model!;
                model.SelectedRow = model.Rows.Single(r => r.Key == "global");
                host.Relayout();

                // Preparing a mode change reads nothing and so completes at once.
                var button = ButtonNamed(ModelViewOf(view), "Mode: Log only (observe), reduces protection");
                var prepared = ((IAsyncRelayCommand<PolicyActionViewModel?>)button.Command).ExecuteAsync((PolicyActionViewModel)button.CommandParameter);
                Assert.True(prepared.IsCompleted);
                host.Relayout();

                Assert.True(model.Review.IsOpen);
                var box = VisualTree.Descendants<CheckBox>(view).Single(c => AutomationProperties.GetName(c) == "Acknowledge before running");
                Assert.True(box.IsVisible);
                var confirm = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Single(b => b.Content as string == "Switch to observe");
                Assert.False(confirm.IsEnabled);

                RenderTo.Png(host, "policies-model-review-weaker");

                box.IsChecked = true;
                host.Relayout();
                Assert.True(model.Review.IsAcknowledged);
                Assert.True(confirm.IsEnabled);
            },
            Connectors);
    }

    [Fact]
    public void A_choice_goes_off_with_the_reason_as_its_tooltip_when_the_read_was_partial_and_the_view_that_failed_says_so()
    {
        using var temp = new TempDirectory();
        using var services = ServicesWithModel(temp);
        Scenario(
            services,
            (view, vm, host, _) =>
            {
                var model = vm.Model!;
                var modelView = ModelViewOf(view);
                Assert.True(model.Trust.IsPartial);

                Show(model, "policies");
                model.SelectedRow = model.Rows.Single(r => r.Key == "strict");
                host.Relayout();
                Calm(host, modelView);

                var activate = ButtonNamed(modelView, "Activate: Activate");
                Assert.False(activate.IsEnabled);
                Assert.Contains("incomplete", activate.ToolTip?.ToString(), StringComparison.Ordinal);
                RenderTo.Png(host, "policies-model-partial");

                // The banner says what could not be read, and the packs view says it in its own words.
                var warnings = VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(modelView).Where(b => b.IsOpen).Select(b => b.Message).ToArray();
                Assert.Contains(warnings, m => m.Contains("guardrail list-packs --json", StringComparison.Ordinal));
                Show(model, "packs");
                host.Relayout();
                Assert.Contains(VisualTree.Descendants<TextBlock>(modelView), t => t.IsVisible && t.Text.StartsWith("Could not read rule packs:", StringComparison.Ordinal));
            },
            Connectors,
            script: c => c.Overrides["guardrail list-packs --json"] = (1, string.Empty, "Traceback: fixture failure"));
    }

    [Fact]
    public void On_a_narrow_panel_the_list_and_the_views_step_aside_while_a_row_is_open_and_come_back_on_escape()
    {
        using var temp = new TempDirectory();
        using var services = ServicesWithModel(temp);
        Scenario(
            services,
            (view, vm, host, _) =>
            {
                var model = vm.Model!;
                var modelView = ModelViewOf(view);
                var column = (FrameworkElement)modelView.FindName("ListColumn");
                var views = (FrameworkElement)modelView.FindName("ViewSwitch");
                var list = (FrameworkElement)modelView.FindName("ListCard");
                Assert.True(column.IsVisible);
                Assert.True(views.IsVisible);
                Assert.True(list.IsVisible);

                model.SelectedRow = model.Rows.Single(r => r.Key == "codex");
                host.Relayout();
                Assert.False(column.IsVisible);
                Assert.False(views.IsVisible);
                Assert.False(list.IsVisible);
                Assert.True(VisualTree.Descendants<DcInspector>(modelView).Single().IsVisible);

                Assert.True(vm.HandleEscape());
                host.Relayout();
                Assert.True(column.IsVisible);
                Assert.True(views.IsVisible);
                Assert.True(list.IsVisible);
            },
            Connectors,
            width: 760);
    }

    // ------------------------------------------------------------------ 0.8.10 is what it was

    [Fact]
    public void On_0810_the_panel_is_the_table_of_named_policies_with_no_trace_of_the_model()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: ProbeRunner(Installed));
        var opened = UiThread.Run(() =>
        {
            var vm = new PoliciesPanelViewModel(services)
            {
                RunCli = (argv, _) => Task.FromResult(string.Join(' ', argv) == "policy list"
                    ? Result(0, argv, PolicyFixtures.ListReport)
                    : throw new InvalidOperationException(string.Join(' ', argv))),
            };
            var view = new PoliciesPanel { DataContext = vm };
            var host = new OffscreenHost(view, 1100, 700);
            return new Opened(view, vm, host, new ScriptedCli(), vm.InitializeAsync());
        });

        try
        {
            UiThread.WaitFor(() => opened.Init.IsCompleted, "policies read");
            UiThread.Run(() =>
            {
                var (view, vm, host) = (opened.View, opened.Vm, opened.Host);
                host.Relayout();

                Assert.True(vm.UsesClassic);
                Assert.Empty(VisualTree.Descendants<PolicyModelView>(view));
                Assert.Null(((ContentControl)view.FindName("ModelHost")).Content);
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("ModelHost")).Visibility);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName("ClassicHost")).Visibility);

                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(view));
                Assert.Equal("4 policies - active: default", toolbar.Caption);
                var grid = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view));
                Assert.Equal(new[] { "Active", "Name", "Kind", "Description" }, grid.Columns.Select(c => c.Header?.ToString()).ToArray());
                Assert.Equal(4, grid.Items.Count);

                RenderTo.Png(host, "policies-0810");
            });
        }
        finally
        {
            UiThread.Run(opened.Host.Dispose);
        }
    }
}
