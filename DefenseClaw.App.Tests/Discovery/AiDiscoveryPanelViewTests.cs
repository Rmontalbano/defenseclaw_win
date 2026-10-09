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
    public void A_payload_that_classifies_its_models_gets_the_owner_modality_and_relevance_columns_the_pickers_and_the_switch()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.95159fd.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene);
            using (host)
            {
                scene.ViewModel.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();

                // The recommended models, with the three columns this payload adds to what 0.8.10 shows.
                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");
                Assert.Equal(3, grid.Items.Count);
                Assert.Equal(
                    new[] { "Model", "State", "Owners", "Modality", "Relevance", "Confidence", "Status / format", "Sources" },
                    VisibleHeaders(grid));

                var combos = VisualTree.Descendants<ComboBox>(view).Where(c => c.IsVisible).Select(AutomationProperties.GetName).ToArray();
                Assert.Equal(new[] { "Modality", "Relevance", "Confidence" }, combos);

                // The switch is offered, off, and the note says what the recommended view kept off the list.
                var showAll = Assert.Single(VisualTree.Descendants<CheckBox>(view), c => c.IsVisible && AutomationProperties.GetName(c) == "Show all models");
                Assert.False(showAll.IsChecked);
                Assert.Contains(
                    VisualTree.Descendants<TextBlock>(view),
                    t => t.IsVisible && t.Text == "3 models hidden by the recommended view. Turn on Show all models to list them.");

                // The line of counts is the TUI's, from the signals the file lists; this file's gateway has not been asked.
                Assert.DoesNotContain(Named<ItemsControl>(view, "Counts from the latest scan").Items.OfType<DiscoveryHeaderChip>(), c => c.IsDiagnostic);

                // Turning it on lists every model and takes the note away.
                showAll.IsChecked = true;
                host.Relayout();
                Assert.Equal(6, grid.Items.Count);
                Assert.True(scene.ViewModel.ShowAllModels);
                Assert.DoesNotContain(VisualTree.Descendants<TextBlock>(view), t => t.IsVisible && t.Text.Contains("hidden by the recommended view", StringComparison.Ordinal));

                // Choosing a modality narrows the table and says so; Reset puts the recommended view back.
                scene.ViewModel.ModalityFilter = "speech";
                host.Relayout();
                _ = Assert.Single(grid.Items);
                Assert.Equal("1 of 6", scene.ViewModel.ModelCaption);
                Assert.Contains(
                    VisualTree.Descendants<Wpf.Ui.Controls.Button>(view),
                    button => button.IsVisible && button.Content?.ToString() == "Reset filters");

                scene.ViewModel.ResetModelFiltersCommand.Execute(null);
                host.Relayout();
                Assert.Equal(3, grid.Items.Count);
                Assert.False(showAll.IsChecked);
            }
        });
    }

    [Fact]
    public void A_gateway_report_that_classifies_the_models_reaches_the_table_the_line_of_counts_and_the_inspectors_provenance()
    {
        // The files of a runtime that does not send owner, relevance, confidence or lineage; the gateway's report has them.
        using var scene = DiscoveryScene.Open(
            null,
            DiscoveryScene.DiscoveryOn,
            temp => temp.WriteFile("ai_discovery_state.json", DiscoveryData.StateFileOf(DiscoveryData.Rest(DiscoveryData.ReportPopulated), withNewerModelFields: false)));

        scene.OnUi(() =>
        {
            var (view, host) = Show(scene, width: 1500, height: 1500);
            using (host)
            {
                var vm = scene.ViewModel;
                vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                host.Relayout();

                // Before the report: the 0.8.10 table, every model, no switch.
                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");
                Assert.Equal(9, grid.Items.Count);
                Assert.Equal(new[] { "Model", "State", "Modality", "Confidence", "Status / format", "Sources" }, VisibleHeaders(grid));
                Assert.DoesNotContain(VisualTree.Descendants<CheckBox>(view), c => c.IsVisible && AutomationProperties.GetName(c) == "Show all models");

                vm.ApplyUsage(AiUsageReader.ParseText(DiscoveryData.Rest(DiscoveryData.ReportPopulated)));
                host.Relayout();

                // After it: the recommended models, the columns and pickers the report's members make possible, the switch and its note.
                Assert.Equal(4, grid.Items.Count);
                Assert.Equal(
                    new[] { "Model", "State", "Owners", "Modality", "Relevance", "Confidence", "Status / format", "Sources" },
                    VisibleHeaders(grid));
                var combos = VisualTree.Descendants<ComboBox>(view).Where(c => c.IsVisible).Select(AutomationProperties.GetName).ToArray();
                Assert.Equal(new[] { "Modality", "Relevance", "Confidence" }, combos);
                Assert.Single(VisualTree.Descendants<CheckBox>(view), c => c.IsVisible && AutomationProperties.GetName(c) == "Show all models");
                Assert.Contains(
                    VisualTree.Descendants<TextBlock>(view),
                    t => t.IsVisible && t.Text == "5 models hidden by the recommended view. Turn on Show all models to list them.");

                // The TUI's last header part, after the counts, with what it means for a tooltip.
                var chips = Named<ItemsControl>(view, "Counts from the latest scan").Items.OfType<DiscoveryHeaderChip>().ToArray();
                Assert.Equal("model-lookup=offline", chips[^1].Text);
                Assert.Equal(new[] { "12 active", "3 new", "model-lookup=offline" }, chips.Select(c => c.Text).ToArray());

                // The columns share the width: no sideways scroll bar.
                var inner = VisualTree.Descendants<ScrollViewer>(grid).First();
                Assert.True(inner.ScrollableWidth < 1, $"the table scrolls sideways by {inner.ScrollableWidth}");

                // A row says who owns the model, how central it is, how sure the scanner is, and where the signals came from.
                var rows = VisualTree.Descendants<DataGridRow>(grid).ToArray();
                var chatTexts = VisualTree.Descendants<TextBlock>(rows[0]).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Contains("Example-Chat-3B-Q4", chatTexts);
                Assert.Contains("Example Notes", chatTexts);
                Assert.Contains("Primary", chatTexts);
                Assert.Contains("95%", chatTexts);
                Assert.Contains("model_file, model_runtime", chatTexts);
                var listedTexts = VisualTree.Descendants<TextBlock>(rows.Single(r => r.Item is DiscoveryModelRow { ModelId: "llama-lite-8b" })).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Contains("API", listedTexts);
                Assert.Contains("—", listedTexts);

                FinishSlides(view, host);
                RenderTo.Png(host, "ai-discovery-models-classified");

                // The inspector gains the lineage under a heading of its own, and the closed page gives the width back.
                var inspector = VisualTree.Descendants<DcInspector>(view).Single();
                vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "Example-Chat-3B-Q4");
                host.Relayout();
                Settled(inspector);
                var texts = VisualTree.Descendants<TextBlock>(inspector).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                Assert.Contains("Provenance", texts);
                Assert.Contains("Publisher", texts);
                Assert.Contains("Example Labs", texts);
                Assert.Contains("United States (US)", texts);
                Assert.Contains("catalog_exact", texts);
                Assert.Contains("Discovery confidence", texts);
                Assert.Contains("95%", texts);
                Assert.Contains("Owners", texts);

                FinishSlides(view, host);
                RenderTo.Png(host, "ai-discovery-models-classified-inspector");

                // A model whose runtime gave no lineage has no Provenance heading to show.
                vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "speech-tiny");
                host.Relayout();
                Assert.Contains("Provenance", VisualTree.Descendants<TextBlock>(inspector).Where(t => t.IsVisible).Select(t => t.Text));
                vm.ShowAllModels = true;
                vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "browser-spellcheck");
                host.Relayout();
                Assert.DoesNotContain("Provenance", VisualTree.Descendants<TextBlock>(inspector).Where(t => t.IsVisible).Select(t => t.Text));
            }
        });
    }

    [Fact]
    public void A_0_8_10_gateway_that_answers_leaves_the_models_view_as_it_was_with_nothing_added_to_the_table_or_the_filters()
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

                vm.ApplyUsage(AiUsageReader.ParseText(DiscoveryData.ReportOf0810(DiscoveryData.State0810())));
                host.Relayout();

                var grid = Named<System.Windows.Controls.DataGrid>(view, "Local models");
                Assert.Equal(3, grid.Items.Count);
                Assert.Equal(new[] { "Model", "State", "Confidence", "Status / format", "Sources" }, VisibleHeaders(grid));
                var combos = VisualTree.Descendants<ComboBox>(view).Where(c => c.IsVisible).Select(AutomationProperties.GetName).ToArray();
                Assert.Equal(new[] { "Confidence" }, combos);
                Assert.DoesNotContain(VisualTree.Descendants<CheckBox>(view), c => c.IsVisible && AutomationProperties.GetName(c) == "Show all models");
                Assert.DoesNotContain(VisualTree.Descendants<TextBlock>(view), t => t.IsVisible && t.Text.Contains("hidden by the recommended view", StringComparison.Ordinal));

                // The line of counts is the one it was: counts only, no diagnostic the 0.8.10 gateway does not send.
                var chips = Named<ItemsControl>(view, "Counts from the latest scan").Items.OfType<DiscoveryHeaderChip>().ToArray();
                Assert.Equal(new[] { "11 active", "2 new", "1 changed" }, chips.Select(c => c.Text).ToArray());

                // The confidence of a model is still what the scanner's strongest match said, worded as such.
                var rows = VisualTree.Descendants<DataGridRow>(grid).ToArray();
                Assert.Contains("90% signal", VisualTree.Descendants<TextBlock>(rows[0]).Where(t => t.IsVisible).Select(t => t.Text));
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

                // What a newer runtime adds to a model is in its details (CUST-310): the owner, the relevance, the discovery confidence, and
                // the lineage under a heading of its own.
                Assert.Contains("Owners", texts);
                Assert.Contains("Example Notes", texts);
                Assert.Contains("Relevance", texts);
                Assert.Contains("Primary", texts);
                Assert.Contains("Discovery confidence", texts);
                Assert.Contains("93%", texts);
                Assert.Contains("Provenance", texts);
                Assert.Contains("Publisher", texts);
                Assert.Contains("Example Labs", texts);
                Assert.Contains("United States (US)", texts);
                Assert.Contains("Root model", texts);
                Assert.Contains("example-labs/chat-3b", texts);
                Assert.Contains("quantized · Q4_K_M", texts);
                Assert.Contains("catalog_exact", texts);
                Assert.Contains("Lineage confidence", texts);

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
