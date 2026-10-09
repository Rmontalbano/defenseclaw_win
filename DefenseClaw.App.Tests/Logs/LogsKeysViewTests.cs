using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.Tests.Logs;

/// <summary>
/// CUST-263 on the real Logs view, hosted offscreen: E and W in the list choose the Errors and Warnings+ presets, but not from a text box or a drop-down list; Home
/// and End in the list go to the first row and pause, and to the newest and follow it again; and the inspector of a judge event lists every label of the TUI's
/// detail (a PNG is written when <c>DC_RENDER_DIR</c> names a folder).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class LogsKeysViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private PanelShell? _shell;

    public LogsKeysViewTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        UiThread.Run(() =>
        {
            if (_shell?.ViewModel is PanelViewModelBase { IsActive: true } panel)
            {
                panel.SetActive(false);
            }

            _shell?.Dispose();
        });
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private (LogsPanel Panel, LogsPanelViewModel ViewModel) Open(int width = 1400, int height = 900)
    {
        LogsPanel panel = null!;
        UiThread.Run(() =>
        {
            _shell = new PanelShell(_services, width, height);
            panel = _shell.Show<LogsPanel>();
        });
        var viewModel = UiThread.Run(() => (LogsPanelViewModel)_shell!.ViewModel);
        UiThread.Run(() => viewModel.SetActive(true));

        // The stand-in NavigationView opens its pane with an animation; a measure before it lands reads a wider page.
        UiThread.WaitFor(
            () =>
            {
                _shell!.Host.Relayout();
                return _shell.PageSize.Width <= width - 225;
            },
            "navigation pane fully open");
        return (panel, viewModel);
    }

    private static bool Press(UIElement target, Key key, RoutedEvent? routed = null)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = routed ?? Keyboard.KeyDownEvent,
            Source = target,
        };
        target.RaiseEvent(press);
        return press.Handled;
    }

    private static ListBox List(LogsPanel panel) => (ListBox)panel.FindName("LogListBox");

    // ---- E and W ----

    [Fact]
    public void E_and_W_pressed_in_the_list_choose_the_presets_and_press_again_to_go_back_to_all()
    {
        var (panel, viewModel) = Open();

        UiThread.Run(() =>
        {
            viewModel.AcceptLines("Gateway", Enumerable.Range(0, 5).Select(n => LogLine.Parse($"[api] line {n}", n)).ToArray());
            var list = List(panel);

            Assert.True(Press(list, Key.E));
            Assert.Equal(LogPresets.Errors, viewModel.SelectedPreset);

            Assert.True(Press(list, Key.E));
            Assert.Equal(LogPresets.All, viewModel.SelectedPreset);

            Assert.True(Press(list, Key.W));
            Assert.Equal(LogPresets.WarningsPlus, viewModel.SelectedPreset);

            Assert.True(Press(list, Key.W));
            Assert.Equal(LogPresets.All, viewModel.SelectedPreset);

            // From anywhere in the panel, not only the list: a button, say.
            var reload = VisualTree.Find<Wpf.Ui.Controls.Button>(panel, b => AutomationProperties.GetName(b) == "Reload from disk")!;
            Assert.True(Press(reload, Key.E));
            Assert.Equal(LogPresets.Errors, viewModel.SelectedPreset);
        });
    }

    [Fact]
    public void A_letter_typed_into_the_filter_box_or_picked_in_a_drop_down_list_is_not_a_shortcut()
    {
        var (panel, viewModel) = Open();

        UiThread.Run(() =>
        {
            var preset = viewModel.SelectedPreset;
            var filter = ((DcPageToolbar)panel.FindName("PageToolbar")).SearchBox!;
            var combo = VisualTree.Find<ComboBox>(panel, c => AutomationProperties.GetName(c) == "Preset")!;

            Assert.False(Press(filter, Key.E));
            Assert.False(Press(filter, Key.W));
            Assert.False(Press(combo, Key.E));
            Assert.False(Press(combo, Key.W));

            Assert.Equal(preset, viewModel.SelectedPreset);
        });
    }

    [Fact]
    public void The_inputs_a_letter_belongs_to_are_text_boxes_password_boxes_and_combo_boxes()
    {
        UiThread.Run(() =>
        {
            Assert.True(LogsPanel.IsTextEntry(new TextBox()));
            Assert.True(LogsPanel.IsTextEntry(new PasswordBox()));
            Assert.True(LogsPanel.IsTextEntry(new ComboBox()));
            Assert.True(LogsPanel.IsTextEntry(new ComboBoxItem()));

            Assert.False(LogsPanel.IsTextEntry(null));
            Assert.False(LogsPanel.IsTextEntry(new ListBox()));
            Assert.False(LogsPanel.IsTextEntry(new ListBoxItem()));
            Assert.False(LogsPanel.IsTextEntry(new Button()));
            Assert.False(LogsPanel.IsTextEntry(new CheckBox()));
        });
    }

    [Fact]
    public void Other_keys_in_the_list_are_left_to_it()
    {
        var (panel, viewModel) = Open();

        UiThread.Run(() =>
        {
            var preset = viewModel.SelectedPreset;

            Assert.False(Press(List(panel), Key.R));
            Assert.False(Press(List(panel), Key.Q));

            Assert.Equal(preset, viewModel.SelectedPreset);
        });
    }

    // ---- Home and End ----

    [Fact]
    public void Home_in_the_list_goes_to_the_first_row_and_pauses_and_End_goes_to_the_newest_and_follows_again()
    {
        var (panel, viewModel) = Open();

        UiThread.Run(() =>
        {
            viewModel.AcceptLines("Gateway", Enumerable.Range(0, 40).Select(n => LogLine.Parse($"[api] line {n}", n)).ToArray());
            _shell!.Host.Relayout();
            var list = List(panel);

            Assert.True(Press(list, Key.Home, Keyboard.PreviewKeyDownEvent));
            Assert.Same(viewModel.DisplayedLines[0], viewModel.SelectedEntry);
            Assert.Same(viewModel.DisplayedLines[0], list.SelectedItem);
            Assert.False(viewModel.AutoScroll);
            Assert.EndsWith("· +0 since pause", viewModel.StatusLineCount, StringComparison.Ordinal);

            Assert.True(Press(list, Key.End, Keyboard.PreviewKeyDownEvent));
            Assert.Same(viewModel.DisplayedLines[^1], viewModel.SelectedEntry);
            Assert.Same(viewModel.DisplayedLines[^1], list.SelectedItem);
            Assert.True(viewModel.AutoScroll);
            Assert.DoesNotContain("since pause", viewModel.StatusLineCount, StringComparison.Ordinal);

            // Any other key is left alone.
            Assert.False(Press(list, Key.PageDown, Keyboard.PreviewKeyDownEvent));
        });
    }

    // ---- The inspector ----

    [Fact]
    public void The_inspector_of_a_file_line_is_headed_as_a_line_and_keeps_its_fields()
    {
        var (panel, viewModel) = Open();

        UiThread.Run(() =>
        {
            viewModel.AcceptLines("Gateway", Enumerable.Range(0, 3).Select(n => LogLine.Parse($"[api] line {n}", n)).ToArray());
            viewModel.SelectedEntry = viewModel.DisplayedLines[1];
            _shell!.Host.Relayout();

            var inspector = (DcInspector)panel.FindName("Inspector");
            Assert.True(inspector.IsVisible);
            Assert.Equal("Line details", inspector.Title);

            var fields = VisualTree.Find<ItemsControl>(inspector, c => AutomationProperties.GetName(c) == "Log line fields")!;
            var texts = VisualTree.Descendants<TextBlock>(fields).Select(t => t.Text).ToList();
            Assert.Contains("component", texts);
            Assert.Contains("line", texts);
        });
    }

    // ---- The inspector of a judge event ----

    [Fact]
    public void The_inspector_of_a_judge_event_with_findings_lists_every_label_of_the_TUIs_detail()
    {
        LogsInspectorFieldsTests.Fill(_services.Paths.AuditDatabasePath, add =>
        {
            LogsInspectorFieldsTests.AddJudge(add);
            add("verdict-1", 2, "guardrail.evaluation", "guardrail.evaluated", "HIGH", "act", "matched a rule", """{"defenseclaw.guardrail.decision":"block","gen_ai.request.model":"gpt-4o-mini"}""", null, null);
            add("scan-1", 3, "asset.scan", "scan.completed", "MEDIUM", "act", "scan done: 3 findings", null, null, null);
        });
        // Tall enough for the whole grid, the findings and the raw event under it, so that a picture shows them.
        var (panel, viewModel) = Open(1400, 1250);

        var load = UiThread.Run(() =>
        {
            viewModel.ActiveSource = LogsPanelViewModel.VerdictsSource;
            viewModel.SelectedPreset = LogPresets.All;
            return viewModel.LoadStructuredAsync();
        });
        UiThread.WaitFor(() => load.IsCompleted && viewModel.DisplayedLines.Count == 3, "the verdicts to load");

        UiThread.Run(() =>
        {
            var judge = viewModel.DisplayedLines.Single(l => l.Fields.Any(f => f is { Name: "ID", Value: "judge-1" }));
            viewModel.SelectedEntry = judge;
            _shell!.Host.Relayout();

            var inspector = (DcInspector)panel.FindName("Inspector");
            Assert.True(inspector.IsVisible);
            Assert.Equal("Event details", inspector.Title);

            var fields = VisualTree.Find<ItemsControl>(inspector, c => AutomationProperties.GetName(c) == "Log line fields")!;
            var texts = VisualTree.Descendants<TextBlock>(fields).Select(t => t.Text).ToList();

            // Every label of the TUI's detail for this event is a name in the grid, in the TUI's order, and its value is next to it.
            Assert.Equal(LogsInspectorFieldsTests.JudgeLabels, texts.Where(t => LogsInspectorFieldsTests.JudgeLabels.Contains(t)).ToArray());
            Assert.Contains("category=Instruction Manipulation severity=HIGH rule=JUDGE-INJ-INSTRUCT source=judge conf=0.90", texts);
            Assert.Contains("category=PII severity=MEDIUM rule=JUDGE-PII-USER source=judge", texts);
            Assert.Contains("b7ad6b7169203331", texts);
            Assert.Contains("injection", texts);
        });

        // A picture, when one is asked for, once the inspector's fade has finished.
        if (Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable) is { Length: > 0 })
        {
            Thread.Sleep(700);
        }

        UiThread.Run(() =>
        {
            _shell!.Host.Relayout();
            RenderTo.Png(_shell.Host, "cust263-logs-inspector-judge-event");
        });
    }
}
