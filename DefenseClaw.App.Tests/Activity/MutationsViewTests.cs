using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// The Activity panel's Commands | Mutations switch and the Mutations tab as a real view: the table, the empty state, and the
/// inspector's before / after diff in the tone tints. Built in the shell stand-in over a scratch data directory whose
/// <c>audit.db</c> holds synthetic changes; rendered off-screen (PNGs only when <c>DC_RENDER_DIR</c> is set).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class MutationsViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;
    private PanelShell? _shell;

    public void Dispose()
    {
        if (_shell is not null)
        {
            UiThread.Run(_shell.Dispose);
        }

        _services?.Dispose();
        _temp.Dispose();
    }

    private MutationDatabase Database() => new(_temp.File("audit.db"));

    private ActivityPanel Open(double width = 1400, double height = 900)
    {
        _services = TestServices.Create(_temp);
        return UiThread.Run(() =>
        {
            _shell = new PanelShell(_services, width, height);
            return _shell.Show<ActivityPanel>();
        });
    }

    private ActivityPanelViewModel Model => UiThread.Run(() => (ActivityPanelViewModel)_shell!.ViewModel);

    private ActivityMutationsView View => UiThread.Run(() => (ActivityMutationsView)_shell!.Page!.FindName("MutationsView"));

    private void ShowMutations(int expectedRows)
    {
        UiThread.Run(() => Model.ActiveTab = ActivityPanelViewModel.MutationsTab);
        UiThread.WaitFor(() => UiThread.Run(() => Model.Mutations.HasLoaded && Model.Mutations.Rows.Count == expectedRows), "the changes to load");
        UiThread.Run(() => _shell!.Host.Relayout());
    }

    private static void Select(ActivityPanelViewModel model, int index) => model.Mutations.SelectedRow = model.Mutations.Rows[index];

    [Fact]
    public void Commands_is_the_default_tab_and_the_switch_swaps_the_two_views()
    {
        _ = Database();
        var page = Open();

        UiThread.Run(() =>
        {
            var switcher = (DcSegmented)page.FindName("TabSwitch");
            Assert.Equal("Commands", switcher.SelectedValue);
            Assert.Equal(new[] { "Commands", "Mutations" }, switcher.Items.OfType<DcSegment>().Select(s => s.Value));
            Assert.True(((FrameworkElement)page.FindName("Cards")).IsVisible);
            Assert.False(((FrameworkElement)page.FindName("MutationsView")).IsVisible);

            switcher.SelectedValue = "Mutations";
            _shell!.Host.Relayout();

            Assert.Equal(ActivityPanelViewModel.MutationsTab, Model.ActiveTab);
            Assert.False(((FrameworkElement)page.FindName("Cards")).IsVisible);
            Assert.True(((FrameworkElement)page.FindName("MutationsView")).IsVisible);

            switcher.SelectedValue = "Commands";
            _shell.Host.Relayout();

            Assert.True(((FrameworkElement)page.FindName("Cards")).IsVisible);
            Assert.False(((FrameworkElement)page.FindName("MutationsView")).IsVisible);
        });
    }

    [Fact]
    public void Nothing_is_read_until_the_mutations_tab_is_shown()
    {
        _ = Database().Activity("a1");
        _ = Open();

        UiThread.Run(() =>
        {
            Assert.False(Model.Mutations.HasLoaded);
            Assert.Empty(Model.Mutations.Rows);
        });

        ShowMutations(1);
    }

    [Fact]
    public void An_empty_database_shows_the_empty_state_and_no_table_rows()
    {
        _ = Database();
        _ = Open();

        ShowMutations(0);

        UiThread.Run(() =>
        {
            var texts = VisualTree.Descendants<TextBlock>(View).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("No mutations", texts);
            Assert.Contains("Gateway configuration mutations and policy changes appear here from the audit database.", texts);
            Assert.Empty(VisualTree.Descendants<DataGridRow>(View));
            _shell!.Host.Relayout();
            Render("mutations-empty");
        });
    }

    [Fact]
    public void Selecting_a_change_with_before_and_after_opens_the_inspector_with_two_tinted_columns()
    {
        _ = Database()
            .Activity("a1", reason: "tighten", before: "{\"guardrail\":{\"mode\":\"observe\"}}", after: "{\"guardrail\":{\"mode\":\"action\"}}", from: "3", to: "4")
            .Change("c1", connector: "claudecode")
            .Change("c2", "quarantine", "enforcement.action", "codex");
        _ = Open();
        ShowMutations(3);

        UiThread.Run(() =>
        {
            var inspector = (DcInspector)View.FindName("Inspector");
            Assert.False(inspector.IsOpen);
            Assert.Equal(3, VisualTree.Descendants<DataGridRow>(View).Count());

            Select(Model, 0);
            _shell!.Host.Relayout();

            Assert.True(inspector.IsOpen);
            var texts = VisualTree.Descendants<TextBlock>(inspector).Select(t => t.Text).ToList();
            Assert.Contains("policy.update", texts);
            Assert.Contains("− Before", texts);
            Assert.Contains("+ After", texts);

            var before = VisualTree.Find<Border>(inspector, b => Equals(b.Tag, "Before"))!;
            var after = VisualTree.Find<Border>(inspector, b => Equals(b.Tag, "After"))!;
            Assert.Equal(Brush(View, "DcToneCriticalSubtleBrush"), ((SolidColorBrush)before.Background).Color);
            Assert.Equal(Brush(View, "DcToneOkSubtleBrush"), ((SolidColorBrush)after.Background).Color);

            var boxes = VisualTree.Descendants<TextBox>(inspector).Select(b => b.Text).ToList();
            Assert.Contains(boxes, text => text.Contains("\"mode\": \"observe\"", StringComparison.Ordinal));
            Assert.Contains(boxes, text => text.Contains("\"mode\": \"action\"", StringComparison.Ordinal));
            Render("mutations-before-after");
        });
    }

    [Fact]
    public void A_change_that_recorded_only_a_diff_shows_a_unified_diff_on_the_tints()
    {
        _ = Database().Activity("a1", diff: "[{\"path\":\"guardrail.mode\",\"op\":\"replace\",\"before\":\"observe\",\"after\":\"action\"}]");
        _ = Open();
        ShowMutations(1);

        UiThread.Run(() =>
        {
            Select(Model, 0);
            _shell!.Host.Relayout();

            var inspector = (DcInspector)View.FindName("Inspector");
            Assert.Null(VisualTree.Find<Border>(inspector, b => Equals(b.Tag, "Before") && b.IsVisible));
            var removed = VisualTree.Find<TextBlock>(inspector, t => t.Text == "- observe")!;
            var added = VisualTree.Find<TextBlock>(inspector, t => t.Text == "+ action")!;
            Assert.Equal(Brush(View, "DcToneCriticalSubtleBrush"), ((SolidColorBrush)Parent<Border>(removed).Background).Color);
            Assert.Equal(Brush(View, "DcToneOkSubtleBrush"), ((SolidColorBrush)Parent<Border>(added).Background).Color);
            Render("mutations-unified-diff");
        });
    }

    [Fact]
    public void An_audit_row_shows_its_recorded_detail_and_the_connector_filter_narrows_the_table()
    {
        _ = Database()
            .Change("c1", connector: "claudecode", structuredJson: "{\"operation\":\"reload_applied\"}")
            .Change("c2", "quarantine", "enforcement.action", "codex");
        _ = Open();
        ShowMutations(2);

        UiThread.Run(() =>
        {
            Assert.True(Model.ShowConnectorFilter);

            Select(Model, 0);
            _shell!.Host.Relayout();
            var inspector = (DcInspector)View.FindName("Inspector");
            Assert.Contains(VisualTree.Descendants<TextBox>(inspector), b => b.Text.Contains("reload_applied", StringComparison.Ordinal));

            Model.Mutations.SelectedConnector = "codex";
            _shell.Host.Relayout();
            _ = Assert.Single(VisualTree.Descendants<DataGridRow>(View));
            Assert.False(inspector.IsOpen);
        });
    }

    [Fact]
    public void The_commands_list_is_untouched_and_the_toolbar_follows_the_tab()
    {
        _ = Database().Activity("a1");
        _ = Open();

        UiThread.Run(() =>
        {
            Assert.True(Model.IsCommandsTab);
            Assert.Equal(Model.CapacityNote, Model.CaptionText);
        });

        ShowMutations(1);

        UiThread.Run(() =>
        {
            Assert.True(Model.IsMutationsTab);
            Assert.Equal("1 change", Model.CaptionText);
            Model.ActiveTab = ActivityPanelViewModel.CommandsTab;
            Assert.Equal(Model.CapacityNote, Model.CaptionText);
        });
    }

    [Fact]
    public void A_narrow_panel_gives_the_inspector_the_whole_width()
    {
        _ = Database().Activity("a1", before: "{\"a\":1}", after: "{\"a\":2}");
        _ = Open(940, 620);
        ShowMutations(1);

        UiThread.Run(() =>
        {
            Select(Model, 0);
            _shell!.Host.Relayout();

            var card = (FrameworkElement)View.FindName("ListCard");
            Assert.False(card.IsVisible);
            Assert.True(((DcInspector)View.FindName("Inspector")).IsVisible);
            Render("mutations-compact");
        });
    }

    /// <summary>Lets the inspector's 120 ms fade-in finish (the dispatcher must run for animations to advance), then writes the PNG. UI thread only.</summary>
    private void Render(string name)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable)))
        {
            return;
        }

        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        _shell!.Host.Relayout();
        RenderTo.Png(_shell.Host, name);
    }

    private static Color Brush(FrameworkElement scope, string key) => ((SolidColorBrush)scope.FindResource(key)).Color;

    private static T Parent<T>(DependencyObject element)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null and not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return (T)current!;
    }
}
