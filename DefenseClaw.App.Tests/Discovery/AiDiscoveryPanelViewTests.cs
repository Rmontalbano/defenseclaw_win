using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// What the operator sees on the AI Discovery panel once the files are read: the Products | Models switch with its counts, the cards
/// with their state pills and signal lines, the models table with only the columns the data has, the inspector beside it (and over
/// the page on a narrow panel). Synthetic data; a PNG of each state is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryPanelViewTests
{
    private static (AiDiscoveryPanel View, OffscreenHost Host) Show(DiscoveryScene scene, double width = 1280, double height = 1500)
    {
        var view = new AiDiscoveryPanel { DataContext = scene.ViewModel };
        var host = new OffscreenHost(view, width, height);
        return (view, host);
    }

    private static T Named<T>(DependencyObject root, string automationName)
        where T : DependencyObject =>
        VisualTree.Descendants<T>(root).First(candidate => AutomationProperties.GetName(candidate) == automationName);

    private static string[] VisibleHeaders(System.Windows.Controls.DataGrid grid) =>
        grid.Columns.Where(c => c.Visibility == Visibility.Visible).Select(c => c.Header?.ToString() ?? string.Empty).ToArray();

    private static void Settled(UIElement inspector)
    {
        // The pane fades in over 120 ms; a picture taken now would catch it half drawn.
        inspector.BeginAnimation(UIElement.OpacityProperty, null);
        inspector.Opacity = 1;
    }

    /// <summary>
    /// A card slides its content open, and the clock of a window nobody sees never runs the slide, so the content sits where the slide
    /// starts, above the card and clipped away. This puts it where the slide ends, so a picture shows the card as it opens.
    /// </summary>
    private static void FinishSlide(CardExpander card)
    {
        _ = card.ApplyTemplate();
        if (card.Template.FindName("ContentPresenterBorder", card) is Border { RenderTransform: TranslateTransform slide })
        {
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = 0;
        }
    }

    /// <summary>Finishes the slide of every expander on the page (the Sources card opens with the page), for a picture of the page as it really looks.</summary>
    private static void FinishSlides(AiDiscoveryPanel view, OffscreenHost host)
    {
        host.Relayout();
        foreach (var card in VisualTree.Descendants<CardExpander>(view))
        {
            FinishSlide(card);
        }

        host.Relayout();
    }

    private static bool Press(UIElement target, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = target,
        };
        target.RaiseEvent(press);
        return press.Handled;
    }

    [Fact]
    public void The_page_opens_on_products_with_both_views_counted_and_the_cards_showing_their_state()
    {
        // inventory.db holds the engine's snapshot for the SDK component, which is where a card's identity and presence bands come from.
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteInventory(
            temp.File("inventory.db"),
            DiscoveryScene.InventoryOf(DiscoveryScene.SdkSignalRow, DiscoveryScene.SdkSnapshotRow)));

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene);
            using var _ = host;

            var switcher = Named<DcSegmented>(view, "Discovery view");
            var segments = switcher.Items.OfType<DcSegment>().ToArray();
            Assert.Equal(new[] { "Products", "Models" }, segments.Select(s => s.Content?.ToString()).ToArray());
            Assert.Equal(new int?[] { 4, 3 }, segments.Select(s => s.Count).ToArray());
            Assert.Equal("products", switcher.SelectedValue);
            Assert.Equal("Products (4)", AutomationProperties.GetName(segments[0]));

            var cards = VisualTree.Descendants<CardExpander>(Named<ItemsControl>(view, "Detected AI components")).ToArray();
            Assert.Equal(4, cards.Length);

            // The first card is the one with a new process: its header carries a "new" pill, and the others (only seen) carry "seen".
            var pills = cards.Select(card => VisualTree.Descendants<DcStatePill>(card).First(p => p.IsVisible).Word).ToArray();
            Assert.Equal(new[] { "new", "seen", "seen", "seen" }, pills);

            // The models table and its filters belong to the other view.
            Assert.False(Named<System.Windows.Controls.DataGrid>(view, "Local models").IsVisible);

            // The line of counts above the list.
            var counts = Named<ItemsControl>(view, "Counts from the latest scan");
            Assert.True(counts.IsVisible);
            Assert.Equal(new[] { "11 active", "2 new", "1 changed" }, counts.Items.OfType<DiscoveryHeaderChip>().Select(c => c.Text).ToArray());

            // Open the first card and read its signals: the process line with the pid and the user, and when it was last active. It has
            // no component, so it has no identity or presence band to show.
            cards[0].IsExpanded = true;
            cards[1].IsExpanded = true;
            host.Relayout();
            FinishSlide(cards[0]);
            FinishSlide(cards[1]);
            host.Relayout();

            var agentTexts = VisualTree.Descendants<TextBlock>(cards[0]).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            Assert.Contains("runtime: pid=4242 user=example-user", string.Join('\n', agentTexts), StringComparison.Ordinal);
            Assert.Contains(agentTexts, t => t == "Last active");
            Assert.Contains(agentTexts, t => t == "Signals");
            Assert.DoesNotContain(agentTexts, t => t == "Identity");

            // The SDK card has a component with a snapshot: both bands, and its component line.
            var sdkTexts = VisualTree.Descendants<TextBlock>(cards[1]).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            Assert.Contains(sdkTexts, t => t == "Identity");
            Assert.Contains(sdkTexts, t => t == "high (86%)");
            Assert.Contains(sdkTexts, t => t == "Presence");
            Assert.Contains(sdkTexts, t => t == "low (31%)");
            Assert.Contains("component: example-sdk (pypi) version=1.4.2", string.Join('\n', sdkTexts), StringComparison.Ordinal);

            FinishSlides(view, host);
            RenderTo.Png(host, "ai-discovery-products");
        });
    }

    [Fact]
    public void The_models_view_shows_the_table_and_a_0_8_10_install_gets_only_the_columns_and_filters_its_data_can_fill()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene);
            using (host)
            {
                scene.ViewModel.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();

                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");
                Assert.True(grid.IsVisible);
                Assert.Equal(3, grid.Items.Count);
                Assert.Equal(new[] { "Model", "State", "Confidence", "Status / format", "Sources" }, VisibleHeaders(grid));

                // The cards are the other view's.
                Assert.False(Named<ItemsControl>(view, "Detected AI components").IsVisible);

                // Only the confidence picker is offered: no model names a modality, so there is no Modality picker either.
                var combos = VisualTree.Descendants<ComboBox>(view).Where(c => c.IsVisible).Select(AutomationProperties.GetName).ToArray();
                Assert.Equal(new[] { "Confidence" }, combos);

                // The columns share the width: no sideways scroll bar, and they use what there is instead of bunching at the left.
                var inner = VisualTree.Descendants<ScrollViewer>(grid).First();
                Assert.True(inner.ScrollableWidth < 1, $"the table scrolls sideways by {inner.ScrollableWidth}");
                var used = grid.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth);
                Assert.True(used >= grid.ActualWidth * 0.9, $"columns use {used} of {grid.ActualWidth}");

                // The state of a model is a pill in its row.
                var rows = VisualTree.Descendants<DataGridRow>(grid).ToArray();
                Assert.Equal("new", VisualTree.Descendants<DcStatePill>(rows[0]).Single().Word);

                FinishSlides(view, host);
                RenderTo.Png(host, "ai-discovery-models");
            }
        });
    }

    [Fact]
    public void A_payload_that_names_modalities_gets_the_modality_column_and_picker_and_lists_every_model()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.95159fd.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene);
            using (host)
            {
                scene.ViewModel.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();

                // All six models, with the one column this payload adds to what 0.8.10 shows. No owner or relevance column: a newer
                // runtime's fields are not read (CUST-310).
                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");
                Assert.Equal(6, grid.Items.Count);
                Assert.Equal(
                    new[] { "Model", "State", "Modality", "Confidence", "Status / format", "Sources" },
                    VisibleHeaders(grid));

                var combos = VisualTree.Descendants<ComboBox>(view).Where(c => c.IsVisible).Select(AutomationProperties.GetName).ToArray();
                Assert.Equal(new[] { "Modality", "Confidence" }, combos);
                Assert.DoesNotContain(VisualTree.Descendants<CheckBox>(view), c => c.IsVisible && AutomationProperties.GetName(c) == "Show all models");

                // Choosing a modality narrows the table and says so; Reset puts every model back.
                scene.ViewModel.ModalityFilter = "speech";
                host.Relayout();
                _ = Assert.Single(grid.Items);
                Assert.Equal("1 of 6", scene.ViewModel.ModelCaption);
                Assert.Contains(
                    VisualTree.Descendants<Wpf.Ui.Controls.Button>(view),
                    button => button.IsVisible && button.Content?.ToString() == "Reset filters");

                scene.ViewModel.ResetModelFiltersCommand.Execute(null);
                host.Relayout();
                Assert.Equal(6, grid.Items.Count);
            }
        });
    }

    [Fact]
    public void Selecting_a_model_opens_the_inspector_beside_the_page_and_closing_it_gives_the_width_back()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.95159fd.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene);
            using (host)
            {
                var vm = scene.ViewModel;
                vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();

                var inspector = VisualTree.Descendants<DcInspector>(view).Single();
                var page = (ScrollViewer)view.FindName("PageScroll");
                Assert.False(inspector.IsOpen);
                Assert.Equal(2, Grid.GetColumnSpan(page));

                vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "example-chat-3b-q4");
                host.Relayout();
                Settled(inspector);

                Assert.True(inspector.IsOpen);
                Assert.True(inspector.IsVisible);
                Assert.Equal(1, Grid.GetColumnSpan(page));
                Assert.True(page.IsVisible);

                var texts = VisualTree.Descendants<TextBlock>(inspector).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Contains("example-chat-3b-q4", texts);
                Assert.Contains(texts, t => t.StartsWith("2 observations", StringComparison.Ordinal));
                Assert.Contains("Modality", texts);
                Assert.Contains("Generative", texts);
                Assert.Contains("llamacpp", texts);
                Assert.Contains(texts, t => t.StartsWith("model: id=example-chat-3b-q4 status=loaded", StringComparison.Ordinal));

                // What a newer runtime adds to a model (owner, relevance, lineage) is not in the details: it is not read.
                Assert.DoesNotContain("Lineage", texts);
                Assert.DoesNotContain("Owner", texts);
                Assert.DoesNotContain("Relevance", texts);

                // The pill beside the title is the model's state.
                Assert.Equal("new", VisualTree.Descendants<DcStatePill>(inspector).First(p => p.IsVisible).Word);

                FinishSlides(view, host);
                RenderTo.Png(host, "ai-discovery-models-inspector");

                vm.ClearModelSelectionCommand.Execute(null);
                host.Relayout();
                Assert.False(inspector.IsVisible);
                Assert.Equal(2, Grid.GetColumnSpan(page));
            }
        });
    }

    [Fact]
    public void On_a_narrow_panel_the_inspector_takes_the_whole_row_and_the_page_steps_aside()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene, width: 800, height: 900);
            using (host)
            {
                var vm = scene.ViewModel;
                vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();
                Assert.True(view.IsCompact);

                var page = (ScrollViewer)view.FindName("PageScroll");
                var inspector = VisualTree.Descendants<DcInspector>(view).Single();
                vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().First();
                host.Relayout();
                Settled(inspector);

                Assert.False(page.IsVisible);
                Assert.True(inspector.IsVisible);
                Assert.Equal(2, Grid.GetColumnSpan(inspector));

                FinishSlides(view, host);
                RenderTo.Png(host, "ai-discovery-models-narrow");

                vm.ClearModelSelectionCommand.Execute(null);
                host.Relayout();
                Assert.True(page.IsVisible);
                Assert.False(inspector.IsVisible);
            }
        });
    }

    [Fact]
    public void A_model_open_in_the_inspector_stays_open_across_a_refresh_of_the_files()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);
        AiDiscoveryPanel? view = null;
        OffscreenHost? host = null;

        scene.OnUi(() =>
        {
            (view, host) = Show(scene);
            var vm = scene.ViewModel;
            vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
            host.Relayout();
            vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "example-llm-7b-q4");
            host.Relayout();
            Assert.True(VisualTree.Descendants<DcInspector>(view).Single().IsVisible);
        });

        scene.Reload();

        scene.OnUi(() =>
        {
            using (host)
            {
                host!.Relayout();
                var grid = Named<System.Windows.Controls.DataGrid>(view!, "Local models");

                Assert.Equal("example-llm-7b-q4", scene.ViewModel.SelectedModel?.ModelId);
                Assert.Same(scene.ViewModel.SelectedModel, grid.SelectedItem);
                Assert.True(VisualTree.Descendants<DcInspector>(view!).Single().IsVisible);
            }
        });
    }

    [Fact]
    public void The_wheel_over_the_table_goes_on_to_the_page_when_the_table_has_nowhere_left_to_scroll()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene, width: 1280, height: 600);
            using (host)
            {
                scene.ViewModel.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();
                var page = (ScrollViewer)view.FindName("PageScroll");
                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");
                Assert.True(page.ScrollableHeight > 0, "the page should be taller than the window for this test");
                var before = page.VerticalOffset;

                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = grid,
                };
                grid.RaiseEvent(wheel);
                host.Relayout();

                Assert.True(wheel.Handled);
                Assert.True(page.VerticalOffset > before, $"the page stayed at {page.VerticalOffset}");
            }
        });
    }

    [Fact]
    public void Escape_in_the_table_closes_the_inspector_and_with_no_model_selected_it_is_left_alone()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene);
            using (host)
            {
                var vm = scene.ViewModel;
                vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();
                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");

                Assert.False(Press(grid, Key.Escape));

                vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().First();
                host.Relayout();
                Assert.True(vm.HasModelSelection);
                Assert.True(Press(grid, Key.Escape));
                Assert.False(vm.HasModelSelection);
            }
        });
    }

    [Fact]
    public void A_filter_that_leaves_no_model_shows_the_reason_in_place_of_an_empty_table()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene);
            using (host)
            {
                var vm = scene.ViewModel;
                vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                vm.SearchText = "no-such-model";
                host.Relayout();

                Assert.False(Named<System.Windows.Controls.DataGrid>(view, "Local models").IsVisible);
                var texts = VisualTree.Descendants<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Contains("No model matches", texts);

                // The table comes back with its columns sized, not bunched, once a model passes again.
                vm.SearchText = string.Empty;
                host.Relayout();
                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");
                Assert.True(grid.IsVisible);
                Assert.True(grid.Columns.Where(c => c.Visibility == Visibility.Visible).Sum(c => c.ActualWidth) >= grid.ActualWidth * 0.9);
            }
        });
    }
}
