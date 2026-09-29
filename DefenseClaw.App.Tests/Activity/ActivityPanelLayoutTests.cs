using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// The Activity panel as a real view: its output lists must virtualize (a transcript can be 200,000 lines), follow a
/// running command, and leave the operator alone once they scroll up. Built in the shell stand-in (<see cref="PanelShell"/>)
/// at the window's 940 x 620 DIP minimum and at 1400 x 900, laid out and rendered off-screen.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class ActivityPanelLayoutTests
{
    /// <summary>What "virtualized" means here: a screenful of rows plus the panel's cache, nowhere near the 50,000 items.</summary>
    private const int MostRealizedRows = 80;

    // ------------------------------------------------------------------ virtualization

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void A_fifty_thousand_line_transcript_realizes_only_the_lines_in_view(int width, int height)
    {
        using var scene = Scene.Open(width, height);
        var big = InvocationFactory.Create(retainFullOutput: true, "skill", "list", "--json");
        for (var i = 1; i <= 50_000; i++)
        {
            InvocationFactory.Append(big, $"{{\"name\":\"sample-skill-{i}\",\"enabled\":true}}", i % 977 == 0 ? CliStream.StandardError : CliStream.StandardOutput);
        }

        InvocationFactory.Finish(big, 0);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(big);
            var list = scene.ListFor(row);

            Assert.Equal(50_000, list.Items.Count);
            var realized = VisualTree.Descendants<ListBoxItem>(list).Count();
            Assert.InRange(realized, 1, MostRealizedRows);

            // Bounded: the list scrolls on its own instead of growing to fit 50,000 lines.
            Assert.True(list.ActualHeight <= 360.5, $"list is {list.ActualHeight} DIPs tall");

            // Recycling, so scrolling reuses those containers rather than making new ones.
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));

            scene.Render($"activity-{width}x{height}-50k");
        });
    }

    [Fact]
    public void Scrolling_through_a_long_transcript_keeps_the_realized_rows_bounded()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(retainFullOutput: true, "skill", "list", "--json");
        InvocationFactory.AppendNumbered(invocation, 50_000);
        InvocationFactory.Finish(invocation);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;

            foreach (var offset in new[] { 0.0, 12_345.0, 30_000.0, 49_990.0 })
            {
                scroll.ScrollToVerticalOffset(offset);
                scene.Host.Relayout();
                Assert.InRange(VisualTree.Descendants<ListBoxItem>(list).Count(), 1, MostRealizedRows);
            }
        });
    }

    [Fact]
    public void A_collapsed_entry_builds_no_row_elements_at_all()
    {
        using var scene = Scene.Open(940, 620);
        var invocation = InvocationFactory.Create(retainFullOutput: true, "skill", "list", "--json");
        InvocationFactory.AppendNumbered(invocation, 20_000);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation, expand: false);

            Assert.Empty(VisualTree.Descendants<ListBoxItem>(scene.Shell.Page!));
            Assert.Equal(20_000, row.Output.Count);
        });
    }

    // ------------------------------------------------------------------ following a running command

    [Fact]
    public void A_running_transcript_keeps_its_newest_line_in_view_as_output_arrives()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 500);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;

            Assert.True(row.IsFollowing);
            Assert.True(IsAtEnd(scroll), "the list should open on its newest line");

            for (var tick = 0; tick < 5; tick++)
            {
                InvocationFactory.AppendNumbered(invocation, 120, first: 501 + (tick * 120));
                row.Tick();
                scene.Host.Relayout();

                Assert.True(IsAtEnd(scroll), $"tick {tick}: offset {scroll.VerticalOffset} of {scroll.ScrollableHeight}");
                Assert.True(row.IsFollowing);
            }

            Assert.Equal("line 1100", ((ActivityOutputLine)list.Items[^1]).Text);
            Assert.Equal(0, scene.PageOffset(), 0.5);
        });
    }

    [Fact]
    public void Following_a_transcript_never_drags_the_page_to_the_bottom_of_that_entry()
    {
        using var scene = Scene.Open(940, 620);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 800);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            scene.Host.Relayout();
            InvocationFactory.AppendNumbered(invocation, 100, first: 801);
            row.Tick();
            scene.Host.Relayout();

            Assert.Equal(0, scene.PageOffset(), 0.5);
        });
    }

    [Fact]
    public void Scrolling_up_stops_following_and_new_output_does_not_move_the_view()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_000);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;
            Assert.True(row.IsFollowing);

            // The operator turns the wheel up.
            scroll.ScrollToVerticalOffset(400);
            UserScrolled(list);
            scene.Host.Relayout();

            Assert.False(row.IsFollowing);
            Assert.Equal(400, scroll.VerticalOffset);
            var firstInView = FirstVisibleText(list);

            InvocationFactory.AppendNumbered(invocation, 300, first: 1_001);
            row.Tick();
            scene.Host.Relayout();

            Assert.False(row.IsFollowing);
            Assert.Equal(400, scroll.VerticalOffset);
            Assert.Equal(firstInView, FirstVisibleText(list));
            Assert.Equal(1_300, list.Items.Count);
        });
    }

    [Fact]
    public void Scrolling_back_to_the_end_resumes_following()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_000);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;

            scroll.ScrollToVerticalOffset(100);
            UserScrolled(list);
            scene.Host.Relayout();
            Assert.False(row.IsFollowing);

            scroll.ScrollToEnd();
            UserScrolled(list);
            scene.Host.Relayout();
            Assert.True(row.IsFollowing);

            InvocationFactory.AppendNumbered(invocation, 50, first: 1_001);
            row.Tick();
            scene.Host.Relayout();
            Assert.True(IsAtEnd(scroll));
        });
    }

    [Fact]
    public void Turning_follow_on_jumps_to_the_newest_line()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_000);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;

            scroll.ScrollToVerticalOffset(100);
            UserScrolled(list);
            scene.Host.Relayout();
            Assert.False(row.IsFollowing);

            // The Follow checkbox is the same setting, reachable from the keyboard.
            var checkbox = VisualTree.Find<CheckBox>(scene.Shell.Page!, c => Equals(c.DataContext, row))!;
            Assert.Equal("Follow output", AutomationProperties.GetName(checkbox));
            checkbox.IsChecked = true;
            scene.Host.Relayout();

            Assert.True(row.IsFollowing);
            Assert.True(IsAtEnd(scroll));
        });
    }

    [Fact]
    public void The_follow_checkbox_is_offered_only_while_the_command_runs()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 10);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var checkbox = VisualTree.Find<CheckBox>(scene.Shell.Page!, c => Equals(c.DataContext, row))!;
            Assert.Equal(Visibility.Visible, checkbox.Visibility);

            InvocationFactory.Finish(invocation);
            row.Tick();
            scene.Host.Relayout();

            Assert.Equal(Visibility.Collapsed, checkbox.Visibility);
        });
    }

    // ------------------------------------------------------------------ trimming while someone is reading

    [Fact]
    public void A_trim_while_the_operator_is_reading_leaves_the_same_line_in_view()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_900);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;

            scroll.ScrollToVerticalOffset(900);
            UserScrolled(list);
            scene.Host.Relayout();
            Assert.False(row.IsFollowing);
            var reading = FirstVisibleText(list);
            Assert.Equal("line 901", reading);

            // 200 more lines cross the 2,000-line cap: the runner drops the oldest 501.
            InvocationFactory.AppendNumbered(invocation, 200, first: 1_901);
            row.Tick();
            scene.Host.Relayout();

            Assert.True(invocation.IsOutputTruncated);
            Assert.False(row.IsFollowing);
            Assert.Equal(reading, FirstVisibleText(list));
        });
    }

    [Fact]
    public void A_following_list_stays_on_its_end_through_a_trim()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_900);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;
            Assert.True(IsAtEnd(scroll));

            InvocationFactory.AppendNumbered(invocation, 1_500, first: 1_901);
            row.Tick();
            scene.Host.Relayout();

            Assert.True(invocation.IsOutputTruncated);
            Assert.True(row.IsFollowing);
            Assert.True(IsAtEnd(scroll));
            Assert.Equal("line 3400", ((ActivityOutputLine)list.Items[^1]).Text);
            Assert.True(((ActivityOutputLine)list.Items[0]).IsNotice);
        });
    }

    // ------------------------------------------------------------------ what a line looks like, and sounds like

    [Fact]
    public void Error_and_notice_lines_read_in_their_own_tones()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.Append(invocation, "plain line");
        InvocationFactory.Append(invocation, "boom", CliStream.StandardError);
        InvocationFactory.AppendNumbered(invocation, 2_100);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;
            scroll.ScrollToHome();
            scene.Host.Relayout();

            var critical = (System.Windows.Media.Brush)list.FindResource("SystemFillColorCriticalBrush");
            var caution = (System.Windows.Media.Brush)list.FindResource("SystemFillColorCautionBrush");
            var primary = (System.Windows.Media.Brush)list.FindResource("TextFillColorPrimaryBrush");

            var marker = TextOf(list, line => line.IsNotice);
            Assert.Same(caution, marker.Foreground);
            Assert.Equal(FontStyles.Italic, marker.FontStyle);

            var ordinary = TextOf(list, line => !line.IsNotice && !line.IsError);
            Assert.Same(primary, ordinary.Foreground);
            Assert.Equal(FontStyles.Normal, ordinary.FontStyle);

            // The stderr line is line 1 of the retained window only if it survived the trim; make one that did.
            InvocationFactory.Append(invocation, "late failure", CliStream.StandardError);
            row.Tick();
            scroll.ScrollToEnd();
            scene.Host.Relayout();
            var error = TextOf(list, line => line.IsError);
            Assert.Same(critical, error.Foreground);
        });
    }

    [Fact]
    public void The_list_and_its_lines_are_named_for_a_screen_reader_and_reachable_by_keyboard()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.Append(invocation, "first");
        InvocationFactory.Append(invocation, "went wrong", CliStream.StandardError);
        InvocationFactory.Append(invocation, string.Empty);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);

            Assert.Equal("Command output", AutomationProperties.GetName(list));
            Assert.Equal(SelectionMode.Extended, list.SelectionMode);

            // A ListBox is not itself a tab stop: Tab moves focus into its rows (one stop for the whole list) and the arrow keys
            // walk them. That needs focusable rows and a tab navigation mode that is not None.
            Assert.NotEqual(KeyboardNavigationMode.None, KeyboardNavigation.GetTabNavigation(list));
            Assert.All(VisualTree.Descendants<ListBoxItem>(list), item =>
            {
                Assert.True(item.Focusable);
                Assert.True(item.IsTabStop);
            });

            var names = VisualTree.Descendants<ListBoxItem>(list)
                .OrderBy(item => list.ItemContainerGenerator.IndexFromContainer(item))
                .Select(item => AutomationProperties.GetName(item))
                .ToArray();
            Assert.Equal(new[] { "first", "error: went wrong", string.Empty }, names);
        });
    }

    [Fact]
    public void Copy_is_offered_exactly_when_lines_are_selected()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 30);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);

            Assert.False(ApplicationCommands.Copy.CanExecute(null, list));

            list.SelectedItems.Add(list.Items[3]);
            list.SelectedItems.Add(list.Items[1]);
            Assert.True(ApplicationCommands.Copy.CanExecute(null, list));

            Assert.True(ApplicationCommands.SelectAll.CanExecute(null, list));
            ApplicationCommands.SelectAll.Execute(null, list);
            Assert.Equal(30, list.SelectedItems.Count);
        });
    }

    // ------------------------------------------------------------------ the page around the list

    [Fact]
    public void The_wheel_scrolls_the_page_when_the_list_has_nowhere_left_to_go()
    {
        using var scene = Scene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_000);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;
            var seenByPage = 0;
            scene.Shell.Page!.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler((_, _) => seenByPage++), handledEventsToo: true);

            // Following, so the list is at its end: a wheel notch down cannot scroll it, so it belongs to the page.
            Assert.True(IsAtEnd(scroll));
            var down = Wheel(list, -120);
            list.RaiseEvent(down);
            Assert.True(down.Handled);
            Assert.Equal(1, seenByPage);

            // In the middle the list scrolls itself and the page never hears of it.
            scroll.ScrollToVerticalOffset(300);
            scene.Host.Relayout();
            seenByPage = 0;
            var middle = Wheel(list, -120);
            list.RaiseEvent(middle);
            Assert.False(middle.Handled);
            Assert.Equal(0, seenByPage);
        });
    }

    // ------------------------------------------------------------------ helpers

    private static MouseWheelEventArgs Wheel(UIElement target, int delta) =>
        new(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = target,
        };

    /// <summary>The operator's input reaching the list (what makes the view look at where it has been left).</summary>
    private static void UserScrolled(ListBox list) => list.RaiseEvent(Wheel(list, 120));

    private static bool IsAtEnd(ScrollViewer scroll) => scroll.VerticalOffset >= scroll.ScrollableHeight - 1.0;

    /// <summary>Text of the first row whose top edge is inside the list.</summary>
    private static string FirstVisibleText(ListBox list) =>
        VisualTree.Descendants<ListBoxItem>(list)
            .Where(item => item.TranslatePoint(new Point(0, 0), list).Y >= -0.5)
            .OrderBy(item => item.TranslatePoint(new Point(0, 0), list).Y)
            .Select(item => ((ActivityOutputLine)item.DataContext).Text)
            .First();

    private static TextBlock TextOf(ListBox list, Func<ActivityOutputLine, bool> which) =>
        VisualTree.Descendants<ListBoxItem>(list)
            .Where(item => which((ActivityOutputLine)item.DataContext))
            .Select(item => VisualTree.Find<TextBlock>(item)!)
            .First();

    /// <summary>The shell stand-in with an Activity panel in it, built over a scratch data directory.</summary>
    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(int width, int height)
        {
            _services = TestServices.Create(_temp);
            Name = $"{width}x{height}";
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                _ = shell.Show<ActivityPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (ActivityPanelViewModel)Shell.ViewModel);
        }

        public string Name { get; }

        public PanelShell Shell { get; }

        public ActivityPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public static Scene Open(int width, int height) => new(width, height);

        /// <summary>Adds an entry for <paramref name="invocation"/> at the top of the list and, by default, opens its output. UI thread only.</summary>
        public ActivityRow AddRow(CliInvocation invocation, bool expand = true)
        {
            var row = new ActivityRow(invocation, _services.Cli, notify: null);
            ViewModel.Rows.Insert(0, row);
            ViewModel.IsEmpty = false;
            row.IsExpanded = expand;
            Host.Relayout();
            return row;
        }

        public ListBox ListFor(ActivityRow row) =>
            VisualTree.Find<ListBox>(Shell.Page!, list => ReferenceEquals(list.DataContext, row))
            ?? throw new InvalidOperationException("The entry's output list was not built.");

        /// <summary>How far the page itself is scrolled (the panel's outer scroll viewer).</summary>
        public double PageOffset() => VisualTree.Find<ScrollViewer>(Shell.Page!)!.VerticalOffset;

        public void Render(string name) => RenderTo.Png(Host, $"{name}");

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
