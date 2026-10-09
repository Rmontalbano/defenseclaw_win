using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.SetupResources;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.SetupResources;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Setup;
using InfoBar = Wpf.Ui.Controls.InfoBar;
using UiButton = Wpf.Ui.Controls.Button;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// The Setup list editor's view (CUST-270) built for real over a view-model fed by the fake CLI: the same view lays out all three lists in each look,
/// shows an empty list and a failed read as different things, keeps Enter from doing anything but showing a row, puts the review in front of Test and
/// Remove, and on a managed installation switches every control that changes something off with the installation's sentence. Set
/// <c>DC_RENDER_DIR</c> to write PNGs of it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupResourceViewTests : IDisposable
{
    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthkey", "synthtoken", "synthfrag", "synthpath-secret" };

    private readonly SetupEditorHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private void WriteDiscovery() =>
        File.WriteAllText(Path.Combine(_harness.DataDirectory, "agent_discovery.json"), FakeSetupCli.Fixture("agent-discovery.synthetic.json"));

    /// <summary>
    /// The editor of <paramref name="resource"/>, read once. The read runs on the UI thread (the view-model's collections are bound to the view the
    /// test builds next), and finishes there: it parses off the thread and comes back through the dispatcher, which is free while this awaits.
    /// </summary>
    private async Task<(SetupResourceViewModel Vm, FakeSetupCli Cli)> ReadAsync(
        SetupResource resource,
        Action<FakeSetupCli>? script = null,
        InstallationContext? installation = null,
        string prefill = "",
        string context = "")
    {
        var services = _harness.Services(installation);
        var (vm, cli) = _harness.Editor(resource, script, services, prefill, context);
        await UiThread.Run(() => vm.ReadAsync());
        return (vm, cli);
    }

    private static IReadOnlyList<string> Texts(FrameworkElement root) =>
        VisualTree.Descendants<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text).Where(t => t.Length > 0).ToArray();

    private static UiButton Labelled(FrameworkElement root, string content) =>
        VisualTree.Descendants<UiButton>(root).Single(b => b.IsVisible && Equals(b.Content, content));

    private static IReadOnlyList<string> VisibleButtons(FrameworkElement root) =>
        VisualTree.Descendants<UiButton>(root).Where(b => b.IsVisible).Select(b => b.Content?.ToString() ?? string.Empty).ToArray();

    private static IReadOnlyList<InfoBar> OpenBars(FrameworkElement root) =>
        VisualTree.Descendants<InfoBar>(root).Where(b => b.IsOpen && b.IsVisible).ToArray();

    private static DataGrid RowGrid(FrameworkElement root) => VisualTree.Descendants<DataGrid>(root).Single(g => g.Name == "RowList");

    private static DcInspector DetailsPane(FrameworkElement root) => VisualTree.Descendants<DcInspector>(root).Single();

    /// <summary>The pane fades in over 120 ms; a picture taken in the middle of that shows it half drawn.</summary>
    private static void SkipFade(DcInspector inspector) => inspector.BeginAnimation(UIElement.OpacityProperty, null);

    /// <summary>Everything the view says to a person or a screen reader: text, boxes, bars, tooltips and automation names, whether or not it is on screen right now.</summary>
    private static string Everything(FrameworkElement root)
    {
        var shown = new List<string>();
        foreach (var element in VisualTree.Descendants<FrameworkElement>(root).Append(root))
        {
            shown.Add(AutomationProperties.GetName(element));
            shown.Add(AutomationProperties.GetHelpText(element));
            switch (element)
            {
                case TextBlock text:
                    shown.Add(text.Text);
                    break;
                case TextBox box:
                    shown.Add(box.Text);
                    break;
                case InfoBar bar:
                    shown.Add(bar.Title);
                    shown.Add(bar.Message);
                    break;
                case ContentControl { Content: string content }:
                    shown.Add(content);
                    break;
            }

            if (element.ToolTip is string tip)
            {
                shown.Add(tip);
            }
        }

        return string.Join("\n", shown.Where(static s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Raises a tunnelling key press at <paramref name="target"/> the way the keyboard does; true when something handled it.</summary>
    private static bool Press(UIElement target, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = target,
        };
        target.RaiseEvent(press);
        return press.Handled;
    }

    // ---- the three lists in each look ----

    [Theory]
    [InlineData(SetupResource.Observability, "Cisco", "Light")]
    [InlineData(SetupResource.Observability, "Linear", "Dark")]
    [InlineData(SetupResource.Webhooks, "Cisco", "Light")]
    [InlineData(SetupResource.Webhooks, "Linear", "Dark")]
    [InlineData(SetupResource.TrustedPaths, "Cisco", "Light")]
    [InlineData(SetupResource.TrustedPaths, "Linear", "Dark")]
    public async Task One_view_lays_out_each_list_with_its_own_columns_a_state_per_row_and_its_own_verbs(SetupResource resource, string styleName, string modeName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));
        WriteDiscovery();
        var (vm, _) = await ReadAsync(resource);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1080, 700);

            var texts = Texts(view);
            Assert.Contains(vm.Title, texts);
            Assert.Contains(vm.Subtitle, texts);
            Assert.Contains(vm.Caption, texts);

            // The columns are the view-model's, and every row has its own state pill.
            var grid = RowGrid(view);
            Assert.Equal(vm.Columns.Select(c => c.Header), grid.Columns.Select(c => c.Header?.ToString() ?? string.Empty));
            Assert.Equal(vm.Rows.Count, VisualTree.Descendants<DataGridRow>(grid).Count());
            Assert.Equal(
                vm.Rows.Select(r => r.StateText.ToLowerInvariant()).Order(StringComparer.Ordinal),
                VisualTree.Descendants<DcStatePill>(grid).Select(p => p.Word).Order(StringComparer.Ordinal));

            // The verbs this resource has, and only those.
            var buttons = VisibleButtons(view);
            Assert.Contains("Refresh", buttons);
            Assert.Contains("Add…", buttons);
            Assert.Contains("Remove…", buttons);
            Assert.Equal(resource != SetupResource.TrustedPaths, buttons.Contains("Enable"));
            Assert.Equal(resource != SetupResource.TrustedPaths, buttons.Contains("Disable"));
            Assert.Equal(resource != SetupResource.TrustedPaths, buttons.Contains("Test…"));
            Assert.Equal(resource == SetupResource.Webhooks, buttons.Contains("Show"));

            // Nothing else is up: no review, no bar, neither the empty nor the failed picture, and the details are closed.
            Assert.DoesNotContain(VisualTree.Descendants<CommandReviewControl>(view), c => c.IsVisible);
            Assert.Empty(OpenBars(view));
            Assert.DoesNotContain(vm.EmptyTitle, texts);
            Assert.DoesNotContain(vm.FailedTitle, texts);
            Assert.False(DetailsPane(view).IsVisible);
            RenderTo.Png(host, $"cust270-{resource}-{style}-{mode}".ToLowerInvariant());
        });
    }

    [Fact]
    public async Task The_columns_follow_the_view_model_the_view_is_given()
    {
        var (observability, _) = await ReadAsync(SetupResource.Observability);
        var (trusted, _) = await ReadAsync(SetupResource.TrustedPaths);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView();
            using var host = new OffscreenHost(view, 1080, 700);
            var grid = RowGrid(view);
            Assert.Empty(grid.Columns);

            view.DataContext = observability;
            Assert.Equal(observability.Columns.Count, grid.Columns.Count);

            view.DataContext = trusted;
            Assert.Equal(trusted.Columns.Select(c => c.Header), grid.Columns.Select(c => c.Header?.ToString() ?? string.Empty));

            view.DataContext = null;
            Assert.Empty(grid.Columns);
        });
    }

    [Fact]
    public async Task A_selected_row_opens_its_details_beside_the_list_with_the_endpoint_host_only()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, _) = await ReadAsync(SetupResource.Observability);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 700);
            var inspector = DetailsPane(view);
            Assert.False(inspector.IsVisible);
            Assert.False(view.HandlesEscape);

            vm.SelectedRow = vm.Rows.Single(r => r.Key == "example-hec");
            SkipFade(inspector);
            host.Relayout();

            Assert.True(inspector.IsVisible);
            Assert.True(view.HandlesEscape);
            var texts = Texts(inspector);
            Assert.Contains("Details", texts);
            Assert.Contains("example-hec", texts);
            Assert.Contains("Redaction", texts);
            Assert.Contains("unredacted (none)", texts);
            Assert.Contains("Endpoint", texts);
            Assert.Contains("hec.example.test:8088", texts);
            Assert.DoesNotContain("/services/collector", Everything(view), StringComparison.Ordinal);
            RenderTo.Png(host, "cust270-observability-details");

            // Esc closes the details.
            Assert.True(Press(view, Key.Escape));
            host.Relayout();
            Assert.False(inspector.IsVisible);
            Assert.Null(vm.SelectedRow);
            Assert.False(Press(view, Key.Escape));
        });
    }

    [Fact]
    public async Task On_a_narrow_window_the_details_replace_the_list_and_the_verbs_stay_where_they_are()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, _) = await ReadAsync(SetupResource.Webhooks);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 720, 640); // the window's own minimum
            var listCard = (Border)view.FindName("ListCard");
            var inspector = DetailsPane(view);
            Assert.True(listCard.IsVisible);

            vm.SelectedRow = vm.Rows.Single(r => r.Key == "example-slack");
            SkipFade(inspector);
            host.Relayout();

            // Room for one of the two: the details take the whole row, the list steps aside, and what acts on the row is still on screen.
            Assert.False(listCard.IsVisible);
            Assert.True(inspector.IsVisible);
            Assert.Equal(0, Grid.GetColumn(inspector));
            Assert.Equal(2, Grid.GetColumnSpan(inspector));
            Assert.True(inspector.ActualWidth > 600, $"the details are {inspector.ActualWidth} wide");
            Assert.Contains("Remove…", VisibleButtons(view));
            Assert.True(Labelled(view, "Test…").IsEnabled);
            RenderTo.Png(host, "cust270-webhooks-narrow");

            // Esc closes the details and the list is back.
            Assert.True(Press(view, Key.Escape));
            host.Relayout();
            Assert.True(listCard.IsVisible);
            Assert.False(inspector.IsVisible);

            // Wide enough, the two sit side by side.
            host.Resize(1080, 640);
            vm.SelectedRow = vm.Rows.Single(r => r.Key == "example-slack");
            SkipFade(inspector);
            host.Relayout();
            Assert.True(listCard.IsVisible);
            Assert.True(inspector.IsVisible);
            Assert.Equal(1, Grid.GetColumn(inspector));
        });
    }

    [Theory]
    [InlineData(SetupResource.Observability)]
    [InlineData(SetupResource.Webhooks)]
    [InlineData(SetupResource.TrustedPaths)]
    public async Task No_state_pill_is_cut_by_its_column_even_with_the_details_beside_the_list_at_the_narrowest_width_that_shows_both(SetupResource resource)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, _) = await ReadAsync(resource);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 960, 700);
            vm.SelectedRow = vm.Rows.First();
            SkipFade(DetailsPane(view));
            host.Relayout();
            Assert.True(DetailsPane(view).IsVisible);
            Assert.True(RowGrid(view).IsVisible);

            var pills = VisualTree.Descendants<DcStatePill>(RowGrid(view)).ToArray();
            Assert.Equal(vm.Rows.Count, pills.Length);
            foreach (var pill in pills)
            {
                var cell = Assert.IsType<DataGridCell>(CellOf(pill));
                var available = cell.ActualWidth - cell.Padding.Left - cell.Padding.Right - cell.BorderThickness.Left - cell.BorderThickness.Right;

                // What the pill would take with all the room it wants, against what its column gives it.
                pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var needed = pill.DesiredSize.Width + pill.Margin.Left + pill.Margin.Right;
                Assert.True(needed <= available + 0.5, $"'{pill.Word}' needs {needed:0.#} DIPs and its column gives {available:0.#}");
            }
        });
    }

    private static DataGridCell? CellOf(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is DataGridCell cell)
            {
                return cell;
            }
        }

        return null;
    }

    // ---- empty and failed look different ----

    [Fact]
    public async Task An_empty_list_and_a_list_that_could_not_be_read_are_two_different_pictures()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (empty, _) = await ReadAsync(SetupResource.TrustedPaths, c => c.TrustedPaths = "[]");
        var (failed, _) = await ReadAsync(SetupResource.TrustedPaths, c => c.ReadExitCode = 1);

        UiThread.Run(() =>
        {
            var emptyView = new SetupResourceView { DataContext = empty };
            using (var host = new OffscreenHost(emptyView, 1080, 600))
            {
                var texts = Texts(emptyView);
                Assert.Contains("The allow-list is empty", texts);
                Assert.Contains(empty.EmptyDetail, texts);
                Assert.DoesNotContain("Could not read the trusted-path allow-list", texts);
                Assert.DoesNotContain("Try again", VisibleButtons(emptyView));
                Assert.False(RowGrid(emptyView).IsVisible);
                Assert.True(Labelled(emptyView, "Add…").IsEnabled); // nothing is trusted: the first folder is the next step
                RenderTo.Png(host, "cust270-trusted-empty");
            }

            var failedView = new SetupResourceView { DataContext = failed };
            using (var host = new OffscreenHost(failedView, 1080, 600))
            {
                var texts = Texts(failedView);
                Assert.Contains("Could not read the trusted-path allow-list", texts);
                Assert.Contains(failed.ErrorMessage, texts);
                Assert.Contains(failed.FailedDetail, texts);
                Assert.DoesNotContain("The allow-list is empty", texts);
                Assert.Contains("Try again", VisibleButtons(failedView));
                Assert.False(RowGrid(failedView).IsVisible);
                Assert.False(Labelled(failedView, "Remove…").IsEnabled); // an unknown list authorizes nothing
                RenderTo.Png(host, "cust270-trusted-failed");
            }
        });
    }

    [Theory]
    [InlineData(SetupResource.Observability, "No destinations are configured", "The destination list could not be read")]
    [InlineData(SetupResource.Webhooks, "No webhooks are configured", "The webhook list could not be read")]
    public async Task The_other_two_lists_tell_empty_from_failed_the_same_way(SetupResource resource, string emptyTitle, string failedTitle)
    {
        var (empty, _) = await ReadAsync(resource, c => c.Observability = c.Webhooks = "[]");
        var (failed, _) = await ReadAsync(resource, c => c.ReadExitCode = 1);

        UiThread.Run(() =>
        {
            var emptyView = new SetupResourceView { DataContext = empty };
            using (var host = new OffscreenHost(emptyView, 1080, 600))
            {
                Assert.Contains(emptyTitle, Texts(emptyView));
                Assert.DoesNotContain(failedTitle, Texts(emptyView));
            }

            var failedView = new SetupResourceView { DataContext = failed };
            using (var host = new OffscreenHost(failedView, 1080, 600))
            {
                Assert.Contains(failedTitle, Texts(failedView));
                Assert.DoesNotContain(emptyTitle, Texts(failedView));
            }
        });
    }

    [Fact]
    public async Task A_missing_cli_is_its_own_picture_and_a_list_that_is_still_being_read_says_so()
    {
        var (missing, _) = await ReadAsync(SetupResource.Webhooks, c => c.CliMissing = true);
        var (reading, _) = _harness.Editor(SetupResource.Webhooks); // never read

        UiThread.Run(() =>
        {
            var missingView = new SetupResourceView { DataContext = missing };
            using (var host = new OffscreenHost(missingView, 1080, 600))
            {
                var texts = Texts(missingView);
                Assert.Contains("The defenseclaw CLI was not found", texts);
                Assert.DoesNotContain("No webhooks are configured", texts);
                Assert.Contains("Try again", VisibleButtons(missingView));
            }

            var readingView = new SetupResourceView { DataContext = reading };
            using (var host = new OffscreenHost(readingView, 1080, 600))
            {
                var texts = Texts(readingView);
                Assert.Contains("Reading the list from the CLI…", texts);
                Assert.DoesNotContain("No webhooks are configured", texts);
                Assert.False(RowGrid(readingView).IsVisible);
            }
        });
    }

    [Fact]
    public async Task A_refresh_that_failed_keeps_the_rows_on_screen_with_a_bar_that_says_they_are_old_and_turns_every_change_off()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, cli) = await ReadAsync(SetupResource.Webhooks);
        cli.ReadExitCode = 1;
        await UiThread.Run(() => vm.ReadAsync());

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 700);
            vm.SelectedRow = vm.Rows.Single(r => r.Key == "example-slack");
            SkipFade(DetailsPane(view));
            host.Relayout();

            Assert.True(RowGrid(view).IsVisible);
            Assert.Equal(4, VisualTree.Descendants<DataGridRow>(RowGrid(view)).Count());
            var bar = Assert.Single(OpenBars(view));
            Assert.Equal("Showing an older read", bar.Title);
            Assert.Contains("Showing the last good read", bar.Message, StringComparison.Ordinal);
            Assert.False(Labelled(view, "Remove…").IsEnabled);
            Assert.False(Labelled(view, "Test…").IsEnabled);
            Assert.True(Labelled(view, "Show").IsEnabled); // a read, and the row is still there to read
            RenderTo.Png(host, "cust270-webhooks-older-read");
        });
    }

    // ---- Enter never tests ----

    [Fact]
    public async Task Enter_on_a_row_only_selects_it_and_Test_opens_a_review_that_runs_nothing_until_it_is_confirmed()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, cli) = await ReadAsync(SetupResource.Observability);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 760);
            var grid = RowGrid(view);
            vm.SelectedRow = vm.Rows.Single(r => r.Key == "example-otlp");
            host.Relayout();

            // Enter on the row and on the grid: consumed, a notice says nothing was sent, no review is up, and no command was started.
            var row = VisualTree.Descendants<DataGridRow>(grid).Single(r => ReferenceEquals(r.Item, vm.SelectedRow));
            Assert.True(Press(row, Key.Enter));
            Assert.True(Press(grid, Key.Enter));
            host.Relayout();
            Assert.False(vm.Review.IsOpen);
            Assert.Empty(cli.Applied);
            Assert.Single(cli.Ran); // the list read of the window opening, and nothing since
            Assert.Contains(OpenBars(view), b => b.Title == "Nothing was sent");

            // Test opens the review with the exact command and says it contacts a live endpoint. Still nothing has run.
            var test = Labelled(view, "Test…");
            Assert.True(test.IsEnabled);
            test.Command.Execute(null);
            host.Relayout();
            var review = Assert.Single(VisualTree.Descendants<CommandReviewControl>(view), c => c.IsVisible);
            Assert.Same(vm.Review.CommandReview, review.Review);
            Assert.Contains(
                VisualTree.Descendants<TextBox>(review).Where(b => b.IsVisible).Select(b => b.Text),
                t => t.Contains("defenseclaw setup observability test -- example-otlp", StringComparison.Ordinal));
            Assert.Contains(VisualTree.Descendants<InfoBar>(review), b => b.Title == "Contacts a live endpoint");
            Assert.Empty(cli.Applied);
            RenderTo.Png(host, "cust270-observability-test-review");

            // Esc closes the review first and runs nothing.
            Assert.True(Press(view, Key.Escape));
            host.Relayout();
            Assert.False(vm.Review.IsOpen);
            Assert.Empty(cli.Applied);

            // Confirmed from the review, the one exact command runs, once.
            test.Command.Execute(null);
            host.Relayout();
            var confirm = VisualTree.Descendants<UiButton>(review).Single(b => b.IsVisible && Equals(b.Content, "Run test"));
            Assert.True(confirm.Command.CanExecute(null));
            confirm.Command.Execute(null);
            host.Relayout();

            Assert.Equal(new[] { new[] { "setup", "observability", "test", "--", "example-otlp" } }, cli.Applied);
            Assert.True(vm.Review.IsFinished);
            Assert.Contains(OpenBars(view), b => b.Title == "Test passed");
        });
    }

    [Fact]
    public async Task Remove_and_the_delete_key_open_a_destructive_review_and_remove_nothing()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, cli) = await ReadAsync(SetupResource.Webhooks);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 760);
            var grid = RowGrid(view);
            vm.SelectedRow = vm.Rows.Single(r => r.Key == "example-hmac");
            host.Relayout();

            Assert.True(Press(grid, Key.Delete));
            host.Relayout();

            Assert.True(vm.Review.IsOpen);
            Assert.True(vm.Review.CommandReview!.IsDestructive);
            var review = Assert.Single(VisualTree.Descendants<CommandReviewControl>(view), c => c.IsVisible);
            Assert.Contains(
                VisualTree.Descendants<TextBox>(review).Where(b => b.IsVisible).Select(b => b.Text),
                t => t.Contains("defenseclaw setup webhook remove --yes -- example-hmac", StringComparison.Ordinal));
            Assert.Empty(cli.Applied);
            RenderTo.Png(host, "cust270-webhook-remove-review");

            // Cancelled, it is as if nothing had been asked.
            Assert.True(Press(view, Key.Escape));
            Assert.False(vm.Review.IsOpen);
            Assert.Empty(cli.Applied);
        });
    }

    [Fact]
    public async Task Show_reads_a_webhook_with_the_exact_command_beside_what_it_printed()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, cli) = await ReadAsync(SetupResource.Webhooks);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 760);
            vm.SelectedRow = vm.Rows.Single(r => r.Key == "example-slack");
            host.Relayout();

            var show = Labelled(view, "Show");
            Assert.True(show.IsEnabled);
            show.Command.Execute(null);
            SkipFade(DetailsPane(view));
            host.Relayout();

            var texts = Texts(DetailsPane(view));
            Assert.Contains("The command that ran", texts);
            Assert.Contains("defenseclaw setup webhook show --json -- example-slack", texts);
            Assert.Equal(new[] { "setup", "webhook", "show", "--json", "--", "example-slack" }, cli.Ran.Last());
            Assert.Empty(cli.Applied);
            RenderTo.Png(host, "cust270-webhook-show");
        });
    }

    [Fact]
    public async Task F5_reads_the_list_again()
    {
        var (vm, cli) = await ReadAsync(SetupResource.Webhooks);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1080, 700);

            Assert.True(Press(view, Key.F5));
            Assert.Equal(2, cli.Ran.Count);
        });

        // The second read finishes on its own; let it, so nothing is still running when the composition goes.
        UiThread.WaitFor(() => !vm.IsBusy, "the second read to finish");
        Assert.Equal(SetupListState.Loaded, vm.State);
    }

    [Fact]
    public async Task F5_waits_while_a_review_is_up()
    {
        var (vm, cli) = await ReadAsync(SetupResource.Webhooks);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1080, 700);
            vm.SelectedRow = vm.Rows.First();
            vm.TestCommand.Execute(null);
            Assert.True(vm.Review.IsOpen);

            // The review is a decision about the list as it was: a refresh under it would change what it is a decision about.
            _ = Press(view, Key.F5);

            Assert.Single(cli.Ran);
        });
    }

    // ---- a managed installation ----

    [Theory]
    [InlineData(SetupResource.Observability, "example-otlp")]
    [InlineData(SetupResource.Webhooks, "example-slack")]
    [InlineData(SetupResource.TrustedPaths, @"C:\Users\synthetic\.synthetic-tools\bin")]
    public async Task On_a_managed_installation_the_list_is_shown_and_every_control_that_changes_something_is_off_with_the_reason_as_its_tooltip(SetupResource resource, string key)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var (vm, cli) = await ReadAsync(resource, installation: TestInstallations.ManagedAt(_harness.DataDirectory));

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 760);
            vm.SelectedRow = vm.Rows.Single(r => r.Key == key);
            SkipFade(DetailsPane(view));
            host.Relayout();

            // The rows are there, and a bar says the list is still read while the actions are off.
            Assert.True(RowGrid(view).IsVisible);
            Assert.Equal(vm.Rows.Count, VisualTree.Descendants<DataGridRow>(RowGrid(view)).Count());
            var bar = Assert.Single(OpenBars(view));
            Assert.Equal("State-changing actions disabled - the list is still read", bar.Title);
            Assert.Equal(TestInstallations.ManagedReason, bar.Message);

            // Every control that changes something is off, and its tooltip is the installation's sentence (shown on a disabled control too).
            var offered = new[] { "Add…", "Enable", "Disable", "Test…", "Remove…" }.Where(label => VisibleButtons(view).Contains(label)).ToArray();
            Assert.Contains("Remove…", offered);
            foreach (var label in offered)
            {
                var button = Labelled(view, label);
                Assert.False(button.IsEnabled, label);
                Assert.Equal(TestInstallations.ManagedReason, button.ToolTip);
                Assert.True(ToolTipService.GetShowOnDisabled(button), label);
            }

            // Refresh and, for a webhook, Show only read: they stay on.
            Assert.True(Labelled(view, "Refresh").IsEnabled);
            if (resource == SetupResource.Webhooks)
            {
                Assert.True(Labelled(view, "Show").IsEnabled);
            }

            // The keyboard cannot open a review either: Delete is taken, and says why nothing happened.
            Assert.True(Press(RowGrid(view), Key.Delete));
            host.Relayout();
            Assert.False(vm.Review.IsOpen);
            Assert.Contains(OpenBars(view), b => b.Title == "Cannot remove" && b.Message == TestInstallations.ManagedReason);
            Assert.Empty(cli.Applied);
            RenderTo.Png(host, $"cust270-{resource}-managed".ToLowerInvariant());
        });
    }

    // ---- the trusted-folder editor's extras ----

    [Fact]
    public async Task The_trusted_folder_editor_shows_why_it_was_opened_and_the_connectors_in_folders_that_are_not_trusted_each_with_a_trust_button()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        WriteDiscovery();
        const string context = @"Setup of Claude Code stopped: its program is in C:\Users\synthetic\.local\bin, a folder that is not trusted.";
        var (vm, _) = await ReadAsync(SetupResource.TrustedPaths, prefill: @"C:\Users\synthetic\.local\bin", context: context);
        var trusted = (TrustedPathsViewModel)vm;

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 900);
            vm.SelectedRow = vm.Rows.Single(r => r.Key == @"C:\Users\Public\tools");
            SkipFade(DetailsPane(view));
            host.Relayout();

            var texts = Texts(view);
            Assert.Contains("A connector setup stopped on a folder that is not trusted", texts);
            Assert.Contains(context, texts);
            Assert.Contains(@"C:\Users\synthetic\.local\bin", texts);
            Assert.Contains("Trust this folder…", VisibleButtons(view));

            Assert.StartsWith("2 connectors' programs are in folders that are not trusted", trusted.UntrustedHeadline, StringComparison.Ordinal);
            Assert.Contains(trusted.UntrustedHeadline, texts);
            Assert.Contains("claudecode", texts);
            Assert.Contains("cursor", texts);
            Assert.Equal(2, VisibleButtons(view).Count(b => b == "Trust…"));

            // The columns the TUI has, and a folder the CLI will not honor in the Bad tone.
            Assert.Equal(new[] { "Source", "Status", "Owned", "Path" }, RowGrid(view).Columns.Select(c => c.Header?.ToString()));
            Assert.Contains(VisualTree.Descendants<DcStatePill>(RowGrid(view)), p => p.Word == "unsafe-permissions" && p.Tone == "Bad");
            RenderTo.Png(host, "cust270-trusted-context");

            // A built-in is protected: Remove is off, and says why.
            vm.SelectedRow = vm.Rows.Single(r => r.Key == @"C:\Windows\System32");
            host.Relayout();
            var remove = Labelled(view, "Remove…");
            Assert.False(remove.IsEnabled);
            Assert.Equal("Built-in defaults are protected: the CLI never removes one.", remove.ToolTip);
        });
    }

    [Fact]
    public async Task An_editor_without_extras_draws_none_of_them()
    {
        var (vm, _) = await ReadAsync(SetupResource.Observability);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1080, 700);

            Assert.DoesNotContain("Trust this folder…", VisibleButtons(view));
            Assert.DoesNotContain("Trust…", VisibleButtons(view));
            Assert.DoesNotContain("A connector setup stopped on a folder that is not trusted", Texts(view));
        });
    }

    // ---- no endpoint secret on screen ----

    [Fact]
    public async Task No_credential_of_an_address_reaches_the_screen_in_the_list_the_details_the_review_or_its_result()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        const string leaky = """
            {"name": "example-slack", "type": "slack", "enabled": true,
             "url": "https://synthuser:synthpass@hooks.example.test/services/synthpath-secret?token=synthtoken#synthfrag",
             "secret_env": "", "room_id": "", "min_severity": "HIGH", "events": [], "timeout_seconds": 10, "cooldown_seconds": null}
            """;
        var (vm, cli) = await ReadAsync(SetupResource.Webhooks, c =>
        {
            c.Webhooks = "[" + leaky + "]";
            c.WebhookShow = leaky;
            c.ApplyOutput = "  Testing webhook example-slack [slack] → https://synthuser:synthpass@hooks.example.test/services/synthpath-secret?token=synthtoken";
        });

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 900);

            void AssertClean(string where)
            {
                var shown = Everything(view);
                Assert.True(shown.Contains("hooks.example.test", StringComparison.Ordinal), where + ": the host is shown");
                foreach (var secret in Secrets)
                {
                    Assert.False(shown.Contains(secret, StringComparison.Ordinal), $"{where}: '{secret}' is on screen");
                }
            }

            AssertClean("the list");

            vm.SelectedRow = vm.Rows.Single();
            vm.ShowCommand.Execute(null);
            host.Relayout();
            AssertClean("the details after Show");

            Labelled(view, "Test…").Command.Execute(null);
            host.Relayout();
            Assert.True(vm.Review.IsOpen);
            AssertClean("the review");

            var confirm = VisualTree.Descendants<UiButton>(view).Single(b => b.IsVisible && Equals(b.Content, "Send test event"));
            confirm.Command.Execute(null);
            host.Relayout();
            Assert.True(vm.Review.IsFinished);
            Assert.Single(cli.Applied);
            AssertClean("the result of the test");
        });
    }

    // ---- bindings and names ----

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
    public async Task Every_binding_of_the_view_names_a_property_that_exists_on_what_it_is_bound_to()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        WriteDiscovery();
        var errors = new BindingErrors();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        var editors = new List<SetupResourceViewModel>();
        foreach (var resource in new[] { SetupResource.Observability, SetupResource.Webhooks, SetupResource.TrustedPaths })
        {
            var isTrusted = resource == SetupResource.TrustedPaths;
            var (vm, _) = await ReadAsync(resource, prefill: isTrusted ? @"C:\opt\bin" : string.Empty, context: isTrusted ? "A setup stopped." : string.Empty);
            editors.Add(vm);
        }

        UiThread.Run(() =>
        {
            // Refresh first: WPF reads its trace configuration once, and only a refreshed source honours a level set in code.
            System.Diagnostics.PresentationTraceSources.Refresh();
            source.Listeners.Add(errors);
            var level = source.Switch.Level;
            source.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            try
            {
                // the detector itself: a binding to nothing is reported, so an empty result below means something
                var control = new TextBlock { DataContext = editors[0] };
                control.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchThing"));

                foreach (var vm in editors)
                {
                    var view = new SetupResourceView { DataContext = vm };
                    using var host = new OffscreenHost(view, 1180, 900);

                    // a row with its details, a read, a notice, a review of each kind of change, and nothing selected again
                    vm.SelectedRow = vm.Rows.FirstOrDefault();
                    host.Relayout();
                    vm.ShowCommand.Execute(null);
                    host.Relayout();
                    vm.ActivateRowCommand.Execute(null);
                    host.Relayout();
                    if (vm.Offers(SetupVerb.Test))
                    {
                        vm.TestCommand.Execute(null);
                        host.Relayout();
                        vm.Review.DismissCommand.Execute(null);
                    }

                    vm.SelectedRow = vm.Rows.LastOrDefault(r => r.BlockedReason(SetupVerb.Remove) is null);
                    vm.RemoveCommand.Execute(null);
                    host.Relayout();
                    _ = vm.Review.ConfirmCommand.ExecuteAsync(null);
                    host.Relayout();
                    vm.Review.DismissCommand.Execute(null);
                    vm.SelectedRow = null;
                    host.Relayout();
                }
            }
            finally
            {
                source.Switch.Level = level;
                source.Listeners.Remove(errors);
            }
        });

        // WPF-UI's own templates may complain about themselves; only what mentions this window's types is ours.
        Assert.Contains(errors.Messages, m => m.Contains("NoSuchThing", StringComparison.Ordinal));
        var ours = errors.Messages
            .Where(m => !m.Contains("NoSuchThing", StringComparison.Ordinal)
                        && (m.Contains("SetupResource", StringComparison.Ordinal) || m.Contains("TrustedPaths", StringComparison.Ordinal) || m.Contains("Observability", StringComparison.Ordinal)
                            || m.Contains("Webhooks", StringComparison.Ordinal) || m.Contains("UntrustedConnector", StringComparison.Ordinal) || m.Contains("RelativeSource", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(ours);
    }

    [Theory]
    [InlineData(SetupResource.Observability)]
    [InlineData(SetupResource.Webhooks)]
    [InlineData(SetupResource.TrustedPaths)]
    public async Task Every_control_that_can_be_pressed_has_a_name_a_screen_reader_can_say(SetupResource resource)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        WriteDiscovery();
        var isTrusted = resource == SetupResource.TrustedPaths;
        var (vm, _) = await ReadAsync(resource, prefill: isTrusted ? @"C:\opt\bin" : string.Empty, context: isTrusted ? "A setup stopped." : string.Empty);

        UiThread.Run(() =>
        {
            var view = new SetupResourceView { DataContext = vm };
            using var host = new OffscreenHost(view, 1180, 900);
            vm.SelectedRow = vm.Rows.First();
            host.Relayout();

            var unnamed = VisualTree.Descendants<Control>(view)
                .Where(c => c.IsVisible && c is Button or ComboBox or TextBox or CheckBox or DataGrid)
                .Where(c => string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)) && string.IsNullOrWhiteSpace(c is ContentControl { Content: string text } ? text : null))
                .Where(c => c is not (TextBox { IsReadOnly: true }))
                .Select(c => c.GetType().Name)
                .ToArray();
            Assert.Empty(unnamed);

            // Every row is announced with its name and state.
            Assert.All(
                VisualTree.Descendants<DataGridRow>(RowGrid(view)),
                row => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(row))));
        });
    }
}
