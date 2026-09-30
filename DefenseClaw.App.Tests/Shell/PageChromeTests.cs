using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The page chrome of CUST-208: <see cref="DcPageToolbar"/> (the compact title / actions / search row that replaced the 26 px
/// title block and the text-labelled buttons) and <see cref="DcSegmented"/> (the single-select filter), as controls and as they
/// sit on the twelve panels that use them.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PageChromeTests
{
    // ------------------------------------------------------------------ the toolbar

    [Fact]
    public void The_toolbar_is_one_compact_row_with_a_20px_semibold_title_a_caption_and_the_description_as_its_tooltip()
    {
        UiThread.Run(() =>
        {
            var toolbar = Toolbar();
            using var host = new OffscreenHost(toolbar, 700, 80);

            var title = VisualTree.Descendants<TextBlock>(toolbar).Single(t => t.Text == "Skills");
            Assert.Equal(20, title.FontSize);
            Assert.Equal(FontWeights.SemiBold, title.FontWeight);
            Assert.Equal("Skills installed for each connector.", title.ToolTip);
            Assert.Equal(AutomationHeadingLevel.Level1, AutomationProperties.GetHeadingLevel(title));

            var caption = (TextBlock)toolbar.Template.FindName("CaptionText", toolbar);
            Assert.Equal("as of 12:04", caption.Text);
            Assert.Equal(Visibility.Visible, caption.Visibility);
            Assert.True(caption.FontSize >= 12, "captions stay at 12 px or more");

            // ~44-48 px with the search box (the old title, subtitle and gap took about 75).
            Assert.InRange(toolbar.ActualHeight, 44, 48);

            toolbar.Caption = string.Empty;
            host.Relayout();
            Assert.Equal(Visibility.Collapsed, caption.Visibility);
            Assert.False(toolbar.HasCaption);
        });
    }

    [Fact]
    public void A_long_caption_is_trimmed_to_what_is_left_of_the_row_and_never_pushes_the_actions_or_the_search_off()
    {
        UiThread.Run(() =>
        {
            var toolbar = Toolbar();
            toolbar.Caption = "Showing the last 200 invocations, in memory only. Each one keeps up to 2000 lines of output and nothing survives a restart.";
            using var host = new OffscreenHost(toolbar, 660, 80);

            var caption = (TextBlock)toolbar.Template.FindName("CaptionText", toolbar);
            Assert.True(IsTrimmed(caption), "a caption that cannot fit shows an ellipsis");
            Assert.True(caption.ActualWidth > 0);

            // The search box and the actions stay inside the row.
            Assert.True(toolbar.SearchBox!.TranslatePoint(new Point(toolbar.SearchBox.ActualWidth, 0), toolbar).X <= toolbar.ActualWidth + 0.5);
            var actions = (FrameworkElement)toolbar.Template.FindName("ActionsSite", toolbar);
            Assert.True(actions.TranslatePoint(new Point(actions.ActualWidth, 0), toolbar).X <= toolbar.ActualWidth + 0.5);

            // A short one fits: no ellipsis, so no second copy of it as a tooltip.
            toolbar.Caption = "as of 12:04";
            host.Relayout();
            Assert.False(IsTrimmed(caption));
        });

        static bool IsTrimmed(TextBlock block) =>
            (bool)typeof(DcPageToolbar).GetMethod("IsTrimmed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { block })!;
    }

    [Fact]
    public void The_toolbar_keeps_the_panels_tinted_glyph_tile_and_drops_it_when_there_is_no_symbol()
    {
        UiThread.Run(() =>
        {
            var toolbar = Toolbar();
            using var host = new OffscreenHost(toolbar, 700, 80);

            var tile = (FrameworkElement)toolbar.Template.FindName("IconTile", toolbar);
            var glyph = (DcSymbolIcon)toolbar.Template.FindName("Glyph", toolbar);
            Assert.Equal(Visibility.Visible, tile.Visibility);
            Assert.Equal(Wpf.Ui.Controls.SymbolRegular.PuzzlePiece24, glyph.Symbol);

            // The section's tint reaches the glyph (DcIconNav: the DcNavIcon* token of the section).
            Assert.Equal("Govern", DcIcon.GetSection(glyph));

            DcIcon.SetSymbol(toolbar, SymbolRegular.Empty);
            host.Relayout();
            Assert.Equal(Visibility.Collapsed, tile.Visibility);
        });
    }

    [Fact]
    public void Its_actions_are_32px_icon_buttons_with_a_150ms_tooltip_that_falls_back_to_the_automation_name()
    {
        UiThread.Run(() =>
        {
            var plain = IconButton("Refresh");
            var explicitTip = IconButton("Refresh", tooltip: "Refresh (F5)");
            explicitTip.SetValue(AutomationProperties.AcceleratorKeyProperty, "F5");
            var toolbar = Toolbar(actions: new[] { plain, explicitTip });
            using var host = new OffscreenHost(toolbar, 700, 80);

            foreach (var button in new[] { plain, explicitTip })
            {
                Assert.Equal(32, button.ActualWidth, 0.5);
                Assert.Equal(32, button.ActualHeight, 0.5);
                Assert.Equal(150, ToolTipService.GetInitialShowDelay(button));
                Assert.True(ToolTipService.GetShowOnDisabled(button), "a disabled action still says why through its tooltip");
                Assert.Null(button.Content);
            }

            Assert.Equal("Refresh", plain.ToolTip);
            Assert.Equal("Refresh (F5)", explicitTip.ToolTip);
            Assert.Equal("F5", AutomationProperties.GetAcceleratorKey(explicitTip));

            // The icon is drawn: a button that only has an icon still has a visible glyph.
            Assert.NotEmpty(VisualTree.Descendants<DcSymbolIcon>(plain));
        });
    }

    [Fact]
    public void The_slots_are_logical_children_and_inherit_the_panels_data_context()
    {
        UiThread.Run(() =>
        {
            var ring = new ProgressRing { Width = 16, Height = 16, IsIndeterminate = true };
            var button = IconButton("Refresh");
            _ = BindingOperations.SetBinding(button, System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(nameof(Model.Go)));
            var model = new Model();
            var toolbar = Toolbar(actions: new[] { button });
            toolbar.Leading = ring;
            toolbar.DataContext = model;
            using var host = new OffscreenHost(toolbar, 700, 80);

            // A walk of the logical tree (a busy-ring audit, an automation tool) reaches them.
            var children = LogicalTreeHelper.GetChildren(toolbar).OfType<object>().ToList();
            Assert.Contains(ring, children);
            Assert.Contains(button.Parent ?? button, children.OfType<DependencyObject>().SelectMany(c => new[] { c }.Concat(Logical(c))));

            // ... and the panel's view-model reaches the buttons in the slots (their commands bind).
            Assert.Same(model.Go, button.Command);

            // The connector slot stays out of the way while it is empty, and takes room once something is in it.
            var site = (FrameworkElement)toolbar.Template.FindName("ConnectorSite", toolbar);
            Assert.Equal(Visibility.Collapsed, site.Visibility);
            toolbar.ConnectorSlot = new TextBlock { Text = "All connectors" };
            host.Relayout();
            Assert.Equal(Visibility.Visible, site.Visibility);
        });
    }

    [Fact]
    public void The_search_box_is_optional_two_way_and_named_for_automation()
    {
        UiThread.Run(() =>
        {
            var toolbar = Toolbar(hasSearch: false);
            using var host = new OffscreenHost(toolbar, 700, 80);

            Assert.False(toolbar.SearchBox!.IsVisible);
            Assert.False(toolbar.FocusSearch());

            toolbar.HasSearch = true;
            host.Relayout();
            var box = toolbar.SearchBox!;
            Assert.True(box.IsVisible);
            Assert.Equal("Filter skills", AutomationProperties.GetName(box));
            Assert.Equal("Matches name and state.", AutomationProperties.GetHelpText(box));
            Assert.Equal("Filter skills…", ((Wpf.Ui.Controls.TextBox)box).PlaceholderText);

            // Typing reaches SearchText; setting SearchText (a reset, Esc) reaches the box.
            box.Text = "pdf";
            Assert.Equal("pdf", toolbar.SearchText);
            toolbar.SearchText = "";
            Assert.Equal("", box.Text);
        });
    }

    [Fact]
    public void In_a_real_window_FocusSearch_puts_keyboard_focus_in_the_box_and_selects_what_is_typed_there()
    {
        // The one test that takes real keyboard focus (as CommandReviewControlTests does): a focused box blinks its caret until
        // focus leaves, so the window is closed in the finally and nothing is left running on the shared UI thread.
        UiThread.Run(() =>
        {
            var toolbar = Toolbar();
            var window = new Window
            {
                Content = toolbar,
                Width = 700,
                Height = 120,
                Left = -4000,
                ShowInTaskbar = false,
                ShowActivated = true,
            };

            try
            {
                window.Show();
                window.Activate();
                Flush();
                if (!window.IsActive)
                {
                    // No interactive desktop to take keyboard focus (a locked or disconnected session).
                    return;
                }

                toolbar.SearchText = "old text";
                Assert.True(toolbar.FocusSearch());
                Flush();

                var box = toolbar.SearchBox!;
                Assert.True(box.IsKeyboardFocused);
                Assert.Equal("old text", box.SelectedText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Flush()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void A_search_delay_holds_typing_back_but_a_clear_goes_through_at_once()
    {
        DcPageToolbar toolbar = null!;
        OffscreenHost? host = null;
        try
        {
            UiThread.Run(() =>
            {
                toolbar = Toolbar(hasSearch: true);
                toolbar.SearchDelay = 150;
                host = new OffscreenHost(toolbar, 700, 80);
                toolbar.SearchBox!.Text = "abc";
                Assert.Equal("", toolbar.SearchText);
            });

            UiThread.WaitFor(() => toolbar.SearchText == "abc", "the delayed search text arrived");

            UiThread.Run(() =>
            {
                toolbar.SearchText = string.Empty;
                Assert.Equal("", toolbar.SearchBox!.Text);
            });
        }
        finally
        {
            UiThread.Run(() => host?.Dispose());
        }
    }

    [Fact]
    public void Escape_in_the_search_box_clears_it_and_is_used_up_and_with_nothing_to_clear_it_passes_on()
    {
        UiThread.Run(() =>
        {
            var toolbar = Toolbar(hasSearch: true);
            using var host = new OffscreenHost(toolbar, 700, 80);
            var box = toolbar.SearchBox!;

            box.Text = "pdf";
            Assert.True(Press(box, Key.Escape), "Esc with text clears it and is handled");
            Assert.Equal("", toolbar.SearchText);
            Assert.Equal("", box.Text);

            Assert.False(Press(box, Key.Escape), "a second Esc is the panel's (it closes the detail pane)");

            // Esc somewhere else in the toolbar does not touch the text.
            toolbar.SearchText = "keep";
            Assert.False(Press(toolbar, Key.Escape));
            Assert.Equal("keep", toolbar.SearchText);
        });
    }

    [Fact]
    public void To_UI_Automation_it_is_a_toolbar_named_after_the_panel()
    {
        UiThread.Run(() =>
        {
            var toolbar = Toolbar();
            using var host = new OffscreenHost(toolbar, 700, 80);

            var peer = UIElementAutomationPeer.CreatePeerForElement(toolbar);
            Assert.Equal(AutomationControlType.ToolBar, peer.GetAutomationControlType());
            Assert.Equal("Skills toolbar", peer.GetName());

            // Not a tab stop of its own: Tab goes straight to the first button.
            Assert.False(toolbar.IsTabStop);
            Assert.False(toolbar.Focusable);
        });
    }

    // ------------------------------------------------------------------ the segmented control

    [Fact]
    public void A_segmented_control_is_an_inset_track_with_the_chosen_segment_on_an_accent_thumb()
    {
        UiThread.Run(() =>
        {
            var segmented = Segmented(selected: 0);
            using var host = new OffscreenHost(segmented, 500, 60);

            Assert.Equal(AppColor("DcInsetBrush"), ((SolidColorBrush)TrackOf(segmented).Background).Color);

            var thumbs = Segments(segmented).Select(segment => ThumbOf(segment)).ToList();
            Assert.Equal(AppColor("DcAccentBrush"), ((SolidColorBrush)thumbs[0].Background).Color);
            Assert.Equal(AppColor("DcOnAccentBrush"), ((SolidColorBrush)Segments(segmented)[0].Foreground).Color);
            Assert.Equal(Brushes.Transparent, thumbs[1].Background);

            // Choosing another moves the thumb.
            segmented.SelectedIndex = 2;
            host.Relayout();
            Assert.Equal(Brushes.Transparent, thumbs[0].Background);
            Assert.Equal(AppColor("DcAccentBrush"), ((SolidColorBrush)thumbs[2].Background).Color);

            // Like the Mac's: neutral, not accent, while the window is not the active one.
            segmented.SetValue(DcSegmented.IsWindowActiveProperty, false);
            host.Relayout();
            Assert.Equal(AppColor("DcSelectedBrush"), ((SolidColorBrush)thumbs[2].Background).Color);
            Assert.Equal(AppColor("DcTextPrimaryBrush"), ((SolidColorBrush)Segments(segmented)[2].Foreground).Color);
        });
    }

    [Fact]
    public void A_count_shows_after_the_label_and_is_part_of_the_segments_name()
    {
        UiThread.Run(() =>
        {
            var segmented = Segmented(selected: 0);
            using var host = new OffscreenHost(segmented, 500, 60);
            var skills = Segments(segmented)[1];

            Assert.Equal("(35)", skills.CountText);
            Assert.Equal("Skills (35)", AutomationProperties.GetName(skills));
            var shown = VisualTree.Descendants<TextBlock>(skills).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            Assert.Equal(new[] { "Skills", "(35)" }, shown);

            // No count, no suffix, and the name is the label alone.
            var summary = Segments(segmented)[0];
            Assert.Equal("", summary.CountText);
            Assert.Equal("Summary", AutomationProperties.GetName(summary));
            Assert.DoesNotContain(VisualTree.Descendants<TextBlock>(summary), t => t.IsVisible && t.Text.StartsWith('('));

            skills.Count = 36;
            Assert.Equal("Skills (36)", AutomationProperties.GetName(skills));

            // A name the author gave is kept.
            var named = new DcSegment { Content = "Agents", Count = 2 };
            AutomationProperties.SetName(named, "Agents in scope");
            named.Count = 3;
            Assert.Equal("Agents in scope", AutomationProperties.GetName(named));
        });
    }

    [Fact]
    public void Left_and_right_move_the_selection_and_home_and_end_go_to_the_ends_without_wrapping_or_landing_on_a_disabled_segment()
    {
        UiThread.Run(() =>
        {
            var segmented = Segmented(selected: 0);
            using var host = new OffscreenHost(segmented, 500, 60);
            Segments(segmented)[1].IsEnabled = false;

            Assert.True(Press(segmented, Key.Right));
            Assert.Equal(2, segmented.SelectedIndex);

            Assert.True(Press(segmented, Key.Left));
            Assert.Equal(0, segmented.SelectedIndex);

            Assert.True(Press(segmented, Key.Left));
            Assert.Equal(0, segmented.SelectedIndex);

            _ = Press(segmented, Key.End);
            Assert.Equal(2, segmented.SelectedIndex);
            Assert.True(Press(segmented, Key.Right));
            Assert.Equal(2, segmented.SelectedIndex);

            _ = Press(segmented, Key.Home);
            Assert.Equal(0, segmented.SelectedIndex);

            // The whole control is one tab stop; the arrows do the rest.
            Assert.Equal(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(segmented));
        });
    }

    [Fact]
    public void To_UI_Automation_it_is_a_single_selection_list_whose_items_are_selection_items_with_their_names()
    {
        UiThread.Run(() =>
        {
            var segmented = Segmented(selected: 1);
            AutomationProperties.SetName(segmented, "Inventory view");
            using var host = new OffscreenHost(segmented, 500, 60);

            var peer = (ItemsControlAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(segmented);
            Assert.Equal("Inventory view", peer.GetName());

            var selection = (ISelectionProvider)peer.GetPattern(PatternInterface.Selection);
            Assert.False(selection.CanSelectMultiple);
            Assert.Single(selection.GetSelection());

            var items = peer.GetChildren();
            Assert.Equal(new[] { "Summary", "Skills (35)", "Plugins (1)" }, items.Select(i => i.GetName()).ToArray());
            Assert.Equal(new[] { false, true, false }, items.Select(i => ((ISelectionItemProvider)i.GetPattern(PatternInterface.SelectionItem)).IsSelected).ToArray());

            // Selecting through the pattern (a screen reader's Select) is the same as a click.
            ((ISelectionItemProvider)items[2].GetPattern(PatternInterface.SelectionItem)).Select();
            Assert.Equal(2, segmented.SelectedIndex);
        });
    }

    [Fact]
    public void SelectedValue_binds_two_way_to_a_view_model_property_and_items_can_come_from_an_ItemsSource()
    {
        UiThread.Run(() =>
        {
            var model = new Model { Source = "Gateway" };
            var segmented = new DcSegmented();
            segmented.Items.Add(new DcSegment { Value = "Gateway", Content = "gateway.log" });
            segmented.Items.Add(new DcSegment { Value = "Watchdog", Content = "watchdog.log" });
            segmented.DataContext = model;
            _ = BindingOperations.SetBinding(segmented, System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(nameof(Model.Source)) { Mode = BindingMode.TwoWay });
            using var host = new OffscreenHost(segmented, 500, 60);

            Assert.Equal(0, segmented.SelectedIndex);

            segmented.SelectedIndex = 1;
            Assert.Equal("Watchdog", model.Source);

            model.Source = "Gateway";
            Assert.Equal(0, segmented.SelectedIndex);

            // Data items are wrapped in a segment each; DisplayMemberPath / SelectedValuePath work as on any list.
            var fromData = new DcSegmented { ItemsSource = new[] { "All", "Risk", "Blocks" } };
            using var host2 = new OffscreenHost(fromData, 500, 60);
            Assert.All(Enumerable.Range(0, 3), i => Assert.IsType<DcSegment>(fromData.ItemContainerGenerator.ContainerFromIndex(i)));
            fromData.SelectedItem = "Risk";
            Assert.True(((DcSegment)fromData.ItemContainerGenerator.ContainerFromIndex(1)).IsSelected);
        });
    }

    // ------------------------------------------------------------------ the dictionary

    [Fact]
    public void Controls_xaml_is_merged_by_the_design_system_and_its_styles_resolve()
    {
        UiThread.Run(() =>
        {
            var app = Application.Current;
            foreach (var key in new object[] { "DcToolbarActions", "DcToolbarIconButton", "DcResultCaption", typeof(DcPageToolbar), typeof(DcSegmented), typeof(DcSegment) })
            {
                Assert.NotNull(app.TryFindResource(key));
            }
        });
    }

    [Fact]
    public void Controls_xaml_adds_no_token_and_hard_codes_no_colour_or_radius()
    {
        var themes = Path.Combine(AppDirectory(), "Themes");
        var controls = File.ReadAllText(Path.Combine(themes, "Controls.xaml"));

        // Every x:Key it declares is a style: the vocabulary (brushes, radii, fonts, spacing) is not extended here.
        foreach (Match key in Regex.Matches(controls, @"x:Key=""([^""]+)"""))
        {
            var start = controls.LastIndexOf('<', key.Index);
            Assert.StartsWith("<Style", controls[start..], StringComparison.Ordinal);
        }

        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{6,8}\b", controls);
        Assert.DoesNotMatch(@"<(SolidColorBrush|CornerRadius|FontFamily|Thickness|sys:Double)\b", controls);
        Assert.DoesNotMatch(@"CornerRadius=""\d", controls);

        // Everything it consumes from the design system exists there, and is reached dynamically.
        var known = File.ReadAllText(Path.Combine(themes, "DefenseClaw.xaml")) + File.ReadAllText(Path.Combine(themes, "Styles", "Default.xaml"));
        foreach (Match use in Regex.Matches(controls, @"\{DynamicResource (\w+)\}"))
        {
            Assert.Contains($"x:Key=\"{use.Groups[1].Value}\"", known, StringComparison.Ordinal);
        }

        // ... and nothing is looked up statically from the dictionary that merges this one (its keys are not loaded yet):
        // a static reference is to a key of this file.
        foreach (Match use in Regex.Matches(controls, @"\{StaticResource (\w+)\}"))
        {
            var key = use.Groups[1].Value;
            Assert.True(controls.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal), $"{key} is a static reference to a key this file does not define");
        }
    }

    // ------------------------------------------------------------------ the panels

    /// <summary>The twelve panels that open with the toolbar (every one but Overview): view, view-model, and whether the toolbar carries the filter box.</summary>
    private static readonly (string View, string ViewModel, bool Search)[] ToolbarPanels =
    {
        ("AlertsPanel", "AlertsPanelViewModel", true),
        ("LogsPanel", "LogsPanelViewModel", true),
        ("AuditPanel", "AuditPanelViewModel", true),
        ("ActivityPanel", "ActivityPanelViewModel", false),
        ("SkillsPanel", "SkillsPanelViewModel", true),
        ("McpsPanel", "McpsPanelViewModel", true),
        ("PluginsPanel", "PluginsPanelViewModel", true),
        ("ToolsPanel", "ToolsPanelViewModel", true),
        ("InventoryPanel", "InventoryPanelViewModel", true),
        ("AiDiscoveryPanel", "AiDiscoveryPanelViewModel", false),
        ("RegistriesPanel", "RegistriesPanelViewModel", false),
        ("SetupPanel", "SetupPanelViewModel", false),
    };

    /// <summary>
    /// Read from the XAML, so it costs no views and no UI thread: each of the twelve panels opens with a DcPageToolbar that
    /// names its actions (icon only, so the name and the tooltip are all the label there is), binds only what its view-model
    /// has, and keeps the filter box in the toolbar where it filters the page's main list (AI Discovery and Setup filter cards in
    /// the middle of a scrolling page, so theirs stays over them). Overview is recomposed in a later phase and keeps its page
    /// header until then.
    /// </summary>
    [Fact]
    public void Every_panel_but_Overview_declares_the_toolbar_with_named_icon_actions_and_bindings_its_view_model_has()
    {
        var panels = Path.Combine(AppDirectory(), "Views", "Panels");
        var problems = new List<string>();

        foreach (var (view, viewModelName, search) in ToolbarPanels)
        {
            var text = File.ReadAllText(Path.Combine(panels, view + ".xaml"));
            var document = XDocument.Parse(text);
            var viewModel = typeof(DefenseClaw.App.ViewModels.PanelViewModelBase).Assembly.GetType("DefenseClaw.App.ViewModels." + viewModelName)
                ?? throw new InvalidOperationException(viewModelName + " was not found");

            // No title block, no filter card.
            foreach (var old in new[] { "DcPageHeader", "DcPageTitle", "DcPageSubtitle", "{StaticResource DcToolbar}" })
            {
                if (text.Contains(old, StringComparison.Ordinal))
                {
                    problems.Add($"{view}: still uses {old}");
                }
            }

            var toolbars = document.Descendants().Where(e => e.Name.LocalName == "DcPageToolbar").ToList();
            if (toolbars.Count != 1)
            {
                problems.Add($"{view}: {toolbars.Count} DcPageToolbar elements");
                continue;
            }

            var toolbar = toolbars[0];
            string Attribute(XElement e, string name) => (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == name) ?? string.Empty;

            if (Attribute(toolbar, "Title") != "{Binding Title}" || Attribute(toolbar, "TitleToolTip") != "{Binding Description}")
            {
                problems.Add($"{view}: the title is not the view-model's Title with its Description as the tooltip");
            }

            if ((Attribute(toolbar, "HasSearch") == "True") != search)
            {
                problems.Add($"{view}: HasSearch should be {search}");
            }

            if (search && (!Attribute(toolbar, "SearchText").StartsWith("{Binding ", StringComparison.Ordinal)
                           || Attribute(toolbar, "SearchAutomationName").Length == 0
                           || Attribute(toolbar, "SearchHelpText").Length == 0
                           || Attribute(toolbar, "SearchPlaceholder").Length == 0))
            {
                problems.Add($"{view}: the search is not bound, named, described and given a placeholder");
            }

            var buttons = toolbar.Descendants().Where(e => e.Name.LocalName == "Button").ToList();
            if (buttons.Count == 0)
            {
                problems.Add($"{view}: no actions");
            }

            foreach (var button in buttons)
            {
                var name = Attribute(button, "AutomationProperties.Name");
                if (name.Length == 0 || Attribute(button, "ToolTip").Length == 0 || Attribute(button, "Content").Length != 0
                    || Attribute(button, "Icon").Length == 0 || !Attribute(button, "Command").StartsWith("{Binding ", StringComparison.Ordinal))
                {
                    problems.Add($"{view}: an action ('{name}') is not an icon with a name, a tooltip and a bound command");
                }
            }

            // Refresh is F5 wherever it is (the Logs panel never had a Refresh button; the shell's F5 re-projects its buffer).
            var refresh = buttons.SingleOrDefault(b => Attribute(b, "AutomationProperties.Name") == "Refresh");
            if (view != "LogsPanel" && (refresh is null || Attribute(refresh, "AutomationProperties.AcceleratorKey") != "F5"))
            {
                problems.Add($"{view}: no Refresh action bound to F5");
            }

            // Everything the toolbar binds is a public property of the panel's view-model (a typo would only be a binding error at run time).
            foreach (var attribute in toolbar.DescendantsAndSelf().SelectMany(e => e.Attributes()))
            {
                foreach (Match binding in Regex.Matches(attribute.Value, @"\{Binding (\w+)"))
                {
                    if (viewModel.GetProperty(binding.Groups[1].Value) is null)
                    {
                        problems.Add($"{view}: {viewModelName} has no property {binding.Groups[1].Value}");
                    }
                }
            }
        }

        Assert.Empty(problems);

        // Overview keeps the old header until it is recomposed.
        var overview = File.ReadAllText(Path.Combine(panels, "OverviewPanel.xaml"));
        Assert.Contains("DcPageHeader", overview, StringComparison.Ordinal);
        Assert.DoesNotContain("DcPageToolbar", overview, StringComparison.Ordinal);
    }

    [Fact]
    public void The_alerts_toolbar_is_live_a_compact_row_with_the_count_and_a_filter_that_reaches_the_view_model()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 940, 620);
                var page = shell.Show<AlertsPanel>();
                var viewModel = (DefenseClaw.App.ViewModels.AlertsPanelViewModel)shell.ViewModel;
                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(page));

                Assert.Equal("Alerts", toolbar.Title);
                Assert.Equal(viewModel.Description, toolbar.TitleToolTip);
                Assert.InRange(toolbar.ActualHeight, 44, 48);

                // The counts ride in the caption.
                viewModel.CountSummary = "3 of 5 alerts";
                Assert.Equal("3 of 5 alerts", toolbar.Caption);

                // The filter box is the view-model's FilterText, both ways.
                toolbar.SearchBox!.Text = "zz";
                Assert.Equal("zz", viewModel.FilterText);
                viewModel.FilterText = string.Empty;
                Assert.Equal("", toolbar.SearchBox.Text);

                // The list starts well above where it did under the 26 px title and the filter card (about 240 DIPs down at this size).
                var list = (FrameworkElement)page.FindName("ListCard");
                Assert.InRange(list.TranslatePoint(new Point(0, 0), page).Y, 100, 190);
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    [Fact]
    public void The_logs_source_is_a_segmented_control_bound_to_the_active_source_and_nothing_else_about_it_changed()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 940, 620);
                var page = shell.Show<LogsPanel>();
                var segmented = Assert.Single(VisualTree.Descendants<DcSegmented>(page));
                var viewModel = (DefenseClaw.App.ViewModels.LogsPanelViewModel)shell.ViewModel;

                Assert.Equal("Log source", AutomationProperties.GetName(segmented));
                Assert.Equal(new[] { "Gateway", "Watchdog" }, segmented.Items.OfType<DcSegment>().Select(s => (string?)s.Value).ToArray());
                Assert.Equal(new[] { "gateway.log", "watchdog.log" }, segmented.Items.OfType<DcSegment>().Select(s => (string?)s.Content).ToArray());
                Assert.Equal("Gateway", segmented.SelectedValue);

                // Picking a segment is what SelectSourceCommand always did: the view-model's ActiveSource follows ...
                segmented.SelectedIndex = 1;
                Assert.Equal("Watchdog", viewModel.ActiveSource);

                // ... and the command (still there, for the tests and any caller) moves the segment.
                viewModel.SelectSourceCommand.Execute("Gateway");
                Assert.Equal(0, segmented.SelectedIndex);

                // The filter box debounces (400 ms) before the buffer is re-projected; Esc clears it at once.
                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(page));
                Assert.Equal(400, toolbar.SearchDelay);
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    // ------------------------------------------------------------------ helpers

    private static DcPageToolbar Toolbar(IEnumerable<Wpf.Ui.Controls.Button>? actions = null, bool hasSearch = true)
    {
        var toolbar = new DcPageToolbar
        {
            Title = "Skills",
            Caption = "as of 12:04",
            TitleToolTip = "Skills installed for each connector.",
            HasSearch = hasSearch,
            VerticalAlignment = VerticalAlignment.Top,
            SearchPlaceholder = "Filter skills…",
            SearchAutomationName = "Filter skills",
            SearchHelpText = "Matches name and state.",
        };
        DcIcon.SetSymbol(toolbar, SymbolRegular.PuzzlePiece24);
        DcIcon.SetSection(toolbar, "Govern");

        var stack = new StackPanel { Style = (Style)Application.Current.FindResource("DcToolbarActions") };
        foreach (var button in actions ?? new[] { IconButton("Refresh") })
        {
            stack.Children.Add(button);
        }

        toolbar.Actions = stack;
        return toolbar;
    }

    private static Wpf.Ui.Controls.Button IconButton(string name, string? tooltip = null)
    {
        var button = new Wpf.Ui.Controls.Button { Icon = new DcSymbolIcon(SymbolRegular.ArrowClockwise24), Command = new RelayCommand(() => { }) };
        AutomationProperties.SetName(button, name);
        if (tooltip is not null)
        {
            button.ToolTip = tooltip;
        }

        return button;
    }

    private static DcSegmented Segmented(int selected)
    {
        var segmented = new DcSegmented { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        segmented.Items.Add(new DcSegment { Value = "summary", Content = "Summary" });
        segmented.Items.Add(new DcSegment { Value = "skills", Content = "Skills", Count = 35 });
        segmented.Items.Add(new DcSegment { Value = "plugins", Content = "Plugins", Count = 1 });
        segmented.SelectedIndex = selected;
        return segmented;
    }

    private static IReadOnlyList<DcSegment> Segments(DcSegmented segmented) =>
        Enumerable.Range(0, segmented.Items.Count).Select(i => (DcSegment)segmented.ItemContainerGenerator.ContainerFromIndex(i)).ToList();

    private static Border ThumbOf(DcSegment segment)
    {
        _ = segment.ApplyTemplate();
        return (Border)segment.Template.FindName("Thumb", segment);
    }

    private static Border TrackOf(DcSegmented segmented) => VisualTree.Descendants<Border>(segmented).First();

    private static Color AppColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Logical(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>A key press reaching <paramref name="target"/>; whether anything handled it.</summary>
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

    private static string AppDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new FileNotFoundException("DefenseClaw.App\\MainWindow.xaml was not found above the test output directory.");
    }

    private sealed class Model : INotifyPropertyChanged
    {
        private string _source = "Gateway";

        public Model()
        {
            Go = new RelayCommand(() => { });
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public RelayCommand Go { get; }

        public string Source
        {
            get => _source;
            set
            {
                _source = value;
                Notify();
            }
        }

        private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
