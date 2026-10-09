using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// What the operator sees on the Runtime panel (CUST-309): coverage above the findings, every plane that is not up with its whole reason as
/// text, the findings table and its inspector, the STALE banner and the buttons that go off with their reason. The view is hosted offscreen over a
/// view-model whose gateway and CLI are scripts; the data is synthetic. A PNG is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiRuntimePanelViewTests : IDisposable
{
    private const string Disabled = "ai-usage-runtime.json";
    private const string Populated = "ai-usage-runtime.populated.synthetic.json";
    private const string Degraded = "ai-usage-runtime.degraded.synthetic.json";
    private const string PlanesAb = "ai-usage-runtime.planes-ab.synthetic.json";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static string Rest(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "rest", name));

    private static string PermissionsJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "cli", "agent-discovery-runtime-permissions.windows.synthetic.json"));

    private static GatewayResult<JsonDocument> Doc(string json) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(json));

    private static CliInvocation Done(IReadOnlyList<string> argv, string output)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        InvocationFactory.Append(invocation, output);
        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    private sealed class Gateway
    {
        public Func<GatewayResult<JsonDocument>> Answer { get; set; } = () => Doc(Rest(Populated));

        public Task<GatewayResult<JsonDocument>> Read(CancellationToken _) => Task.FromResult(Answer());
    }

    private async Task<(AiRuntimePanel View, AiRuntimePanelViewModel Vm, OffscreenHost Host, Gateway Gateway)> OpenAsync(string snapshot, double width = 1100, double height = 820)
    {
        _services ??= AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _ = await _services.Runtime.RefreshAsync();

        var gateway = new Gateway { Answer = () => Doc(Rest(snapshot)) };

        // The view-model starts its reads on the UI thread, so its continuations and its collections belong to the thread that owns the bindings.
        var (vm, init) = UiThread.Run(() =>
        {
            var model = new AiRuntimePanelViewModel(_services)
            {
                ReadSnapshot = gateway.Read,
                RunPermissionsRead = (argv, _) => Task.FromResult(Done(argv, PermissionsJson())),
            };
            return (model, model.InitializeAsync());
        });
        UiThread.WaitFor(() => init.IsCompleted, "the first read");

        var (view, host) = UiThread.Run(() =>
        {
            var panel = new AiRuntimePanel { DataContext = vm };
            return (panel, new OffscreenHost(panel, width, height));
        });
        return (view, vm, host, gateway);
    }

    private static IEnumerable<string> VisibleTexts(DependencyObject root) =>
        VisualTree.Descendants<FrameworkElement>(root)
            .Where(e => e.IsVisible)
            .Select(e => e switch
            {
                // A block built from <Run>s (the plane reasons) has no Text of its own: its text is its runs'.
                TextBlock block => block.Text.Length > 0 ? block.Text : string.Concat(block.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)),
                TextBox box => box.Text,
                ContentControl { Content: string content } => content,
                _ => string.Empty,
            })
            .Where(t => t.Length > 0)
            .ToArray();

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

    private static void Settled(DcInspector inspector)
    {
        // The pane fades in over 120 ms; a picture taken now would catch it half drawn.
        inspector.BeginAnimation(UIElement.OpacityProperty, null);
        inspector.Opacity = 1;
    }

    // ------------------------------------------------------------------ populated

    [Fact]
    public async Task The_populated_panel_leads_with_coverage_lists_the_findings_and_opens_the_inspector_on_a_selection()
    {
        var (view, vm, host, _) = await OpenAsync(Populated);
        UiThread.Run(() =>
        {
            using (host)
            {
                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(view));
                Assert.Equal("Runtime", toolbar.Title);
                Assert.Equal(vm.CaptionText, toolbar.Caption);
                Assert.True(toolbar.HasSearch);

                var grid = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view));
                Assert.Equal(6, grid.Items.Count);
                Assert.Equal(
                    new[] { "Severity", "Score", "Process", "PID", "Agent", "Providers", "Inventory" },
                    grid.Columns.Select(c => c.Header?.ToString()).ToArray());

                var texts = VisibleTexts(view).ToArray();
                Assert.Contains("A · inference heartbeat: up", texts);
                Assert.Contains("B · shadow egress: up", texts);
                Assert.Contains("C · agent actions: up", texts);
                Assert.Contains("All three planes are reporting.", texts);
                Assert.Contains("Findings", texts);
                Assert.DoesNotContain(texts, t => t.StartsWith("STALE", StringComparison.Ordinal));

                // Coverage is above the findings on the page.
                var coverage = VisualTree.Descendants<TextBlock>(view).First(t => t.Text == "Coverage");
                var findings = VisualTree.Descendants<TextBlock>(view).First(t => t.Text == "Findings");
                Assert.True(coverage.TranslatePoint(default, view).Y < findings.TranslatePoint(default, view).Y);

                var inspector = Assert.Single(VisualTree.Descendants<DcInspector>(view));
                Assert.False(inspector.IsOpen);

                vm.SelectedFinding = vm.Rows[0];
                host.Relayout();

                Assert.True(inspector.IsOpen);
                var inspectorTexts = VisibleTexts(inspector).ToArray();
                Assert.Contains("python.exe", inspectorTexts);
                Assert.Contains("Command line", inspectorTexts);
                Assert.Contains("Observed sequence", inspectorTexts);
                Assert.Contains("credential_access -> identity_creation -> exfiltration", inspectorTexts);
                Assert.Contains("Inventory", inspectorTexts);
                Assert.Contains("unaccounted", inspectorTexts);
                Assert.Contains(inspectorTexts, t => t.Contains("--api-key [redacted]", StringComparison.Ordinal));
                Assert.DoesNotContain(inspectorTexts, t => t.Contains("synthetic-synthetic", StringComparison.Ordinal));

                Settled(inspector);
                RenderTo.Png(host, "cust309-runtime-populated");
            }
        });
    }

    [Fact]
    public async Task The_inspector_shows_an_unobserved_verdict_and_the_providers_with_their_ports()
    {
        var (view, vm, host, _) = await OpenAsync(Populated);
        UiThread.Run(() =>
        {
            using (host)
            {
                var inspector = Assert.Single(VisualTree.Descendants<DcInspector>(view));

                vm.SelectedFinding = vm.Rows.Single(r => r.Pid == 9912);
                host.Relayout();
                var unobserved = VisibleTexts(inspector).ToArray();
                Assert.Contains("unobserved", unobserved);
                Assert.Contains(unobserved, t => t.Contains("not evidence either way", StringComparison.Ordinal));

                vm.SelectedFinding = vm.Rows.Single(r => r.Pid == 7788);
                host.Relayout();
                var providers = VisibleTexts(inspector).ToArray();
                Assert.Contains("api.provider-a.example:443", providers);
                Assert.Contains("api.provider-a.example:8443", providers);
                Assert.Contains("frontier, dns answer (95%)", providers);
            }
        });
    }

    // ------------------------------------------------------------------ degraded, off, stale

    [Fact]
    public async Task A_degraded_panel_writes_every_plane_that_is_not_up_out_in_full_as_visible_text()
    {
        var (view, vm, host, _) = await OpenAsync(Degraded, height: 900);
        UiThread.Run(() =>
        {
            using (host)
            {
                var texts = VisibleTexts(view).ToArray();

                Assert.Contains("A · inference heartbeat: up", texts);
                Assert.Contains("B · shadow egress: partial", texts);
                Assert.Contains("C · agent actions: blind", texts);
                Assert.Contains("Partial coverage", texts);
                Assert.Contains("Partial coverage: 1 of 3 planes fully reporting.", texts);

                // Not a tooltip: the whole reason, in the page.
                Assert.Contains(texts, t => t.Contains("C · agent actions — blind: plane: Security event log unreadable: Access is denied. (the gateway needs an elevated token to read the Security channel)", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("B · shadow egress — partial: egress attribution is limited to this process's own sockets; run the gateway elevated for machine-wide coverage", StringComparison.Ordinal));
                Assert.DoesNotContain(texts, t => t.StartsWith("A · inference heartbeat —", StringComparison.Ordinal));

                // The unattributed share warns, and the badge is not the calm one.
                Assert.Contains(vm.UnattributedWarning, texts.Concat(VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(view).Select(b => b.Message)));
                Assert.DoesNotContain("All planes up", texts);

                RenderTo.Png(host, "cust309-runtime-degraded");
            }
        });
    }

    [Fact]
    public async Task No_findings_with_a_plane_off_shows_the_caveat_and_never_the_word_clean_on_its_own()
    {
        var (view, vm, host, _) = await OpenAsync(PlanesAb);
        UiThread.Run(() =>
        {
            using (host)
            {
                var texts = VisibleTexts(view).ToArray();

                Assert.Contains("No findings", texts);
                Assert.Contains(texts, t => t.StartsWith(NoFindingsCaveat, StringComparison.Ordinal));
                Assert.Contains("C · agent actions: off", texts);
                Assert.Contains(texts, t => t.Contains("not selected in ai_discovery.runtime.planes", StringComparison.Ordinal));
                Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view), g => g.IsVisible);
                foreach (var text in texts)
                {
                    Assert.DoesNotContain("clean", text.Replace(NoFindingsCaveat, string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
                }

                Assert.True(vm.ShowNoFindings);
                RenderTo.Png(host, "cust309-runtime-no-findings");
            }
        });
    }

    private const string NoFindingsCaveat = DefenseClaw.Core.AiRuntime.AiRuntimeCoverage.NoFindingsCaveat;

    [Fact]
    public async Task A_stale_snapshot_wears_the_banner_keeps_its_rows_and_has_its_buttons_off_with_the_reason_in_the_page()
    {
        var (view, vm, host, gateway) = await OpenAsync(Populated);
        gateway.Answer = () => GatewayResult<JsonDocument>.Unreachable("gateway is not listening (connection refused)");
        var refresh = UiThread.Run(() => vm.RefreshCommand.ExecuteAsync(null));
        UiThread.WaitFor(() => refresh.IsCompleted, "the failed refresh");

        UiThread.Run(() =>
        {
            using (host)
            {
                host.Relayout();
                var banner = VisualTree.Descendants<Wpf.Ui.Controls.InfoBar>(view).First(b => b.IsOpen && b.Title == "Stale");
                Assert.StartsWith("STALE", banner.Message, StringComparison.Ordinal);

                var texts = VisibleTexts(view).ToArray();
                Assert.Contains("Stale", texts);                                                              // the coverage badge
                Assert.Contains(texts, t => t.StartsWith("STALE · ", StringComparison.Ordinal));              // the toolbar caption
                Assert.Contains("A · inference heartbeat: up (last poll)", texts);                            // a stale "up" is marked, and not green
                Assert.DoesNotContain("A · inference heartbeat: up", texts);
                Assert.DoesNotContain("All planes up", texts);
                Assert.Equal(6, Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view)).Items.Count);

                var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).ToArray();
                Assert.All(buttons.Where(b => (b.Content as string) is "Poll now…" or "Change settings…" or "Disable…"), b => Assert.False(b.IsEnabled, b.Content?.ToString()));
                Assert.False(buttons.Single(b => AutomationProperties.GetName(b) == "Poll now").IsEnabled);
                Assert.True(buttons.Single(b => AutomationProperties.GetName(b) == "Refresh").IsEnabled);
                Assert.Contains(texts, t => t.Contains("is stale", StringComparison.Ordinal) && t.Contains("refresh it first", StringComparison.Ordinal));

                RenderTo.Png(host, "cust309-runtime-stale");
            }
        });
    }

    // ------------------------------------------------------------------ the other states

    [Fact]
    public async Task A_disabled_runtime_shows_its_card_and_an_enabled_enable_button()
    {
        var (view, vm, host, _) = await OpenAsync(Disabled);
        UiThread.Run(() =>
        {
            using (host)
            {
                var texts = VisibleTexts(view).ToArray();

                Assert.Contains(AiRuntimePanelViewModel.DisabledTitle, texts);
                Assert.Contains(texts, t => t.Contains("defenseclaw agent discovery runtime enable", StringComparison.Ordinal));
                Assert.DoesNotContain("Coverage", texts);
                Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view), g => g.IsVisible);

                var enable = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Where(b => (b.Content as string) == "Enable…" && b.IsVisible).ToArray();
                Assert.NotEmpty(enable);
                Assert.All(enable, b => Assert.True(b.IsEnabled));
                var poll = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Single(b => (b.Content as string) == "Poll now…");
                Assert.False(poll.IsEnabled);
                Assert.Contains("Poll now and Disable are off: The runtime planes are disabled; enable them first.", texts);

                RenderTo.Png(host, "cust309-runtime-disabled");
            }
        });
    }

    [Fact]
    public async Task A_gateway_without_the_route_shows_the_unsupported_card_and_nothing_to_poll()
    {
        var (view, vm, host, gateway) = await OpenAsync(Populated);
        gateway.Answer = () => GatewayResult<JsonDocument>.Error("gateway returned HTTP 404", 404);
        var refresh = UiThread.Run(() => vm.RefreshCommand.ExecuteAsync(null));
        UiThread.WaitFor(() => refresh.IsCompleted, "the refresh");

        UiThread.Run(() =>
        {
            using (host)
            {
                host.Relayout();
                var texts = VisibleTexts(view).ToArray();

                Assert.Contains(AiRuntimePanelViewModel.UnsupportedTitle, texts);
                Assert.Contains(AiRuntimePanelViewModel.UnsupportedDetail, texts);
                Assert.DoesNotContain("Coverage", texts);
                Assert.DoesNotContain(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view), g => g.IsVisible);
            }
        });
    }

    // ------------------------------------------------------------------ keyboard and layout

    [Fact]
    public async Task Escape_closes_the_inspector_and_ctrl_f_reaches_the_filter_box()
    {
        var (view, vm, host, _) = await OpenAsync(Populated);
        UiThread.Run(() =>
        {
            using (host)
            {
                vm.SelectedFinding = vm.Rows[1];
                host.Relayout();
                var grid = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view));

                Assert.True(Press(grid, Key.Escape));
                Assert.Null(vm.SelectedFinding);
                Assert.False(Press(grid, Key.Escape));

                Assert.True(ApplicationCommands.Find.CanExecute(null, view));
                ApplicationCommands.Find.Execute(null, view);
                var box = VisualTree.Descendants<TextBox>(view).First(b => AutomationProperties.GetName(b) == "Filter findings");
                Assert.True(box.IsKeyboardFocusWithin || box.IsFocused || Keyboard.FocusedElement is not null);
            }
        });
    }

    [Fact]
    public async Task The_grid_keeps_its_selection_and_the_inspector_stays_open_across_a_refresh_that_changes_that_finding()
    {
        var (view, vm, host, gateway) = await OpenAsync(Populated);
        var grid = UiThread.Run(() => Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view)));
        UiThread.Run(() =>
        {
            vm.SelectedFinding = vm.Rows.Single(r => r.Pid == 7788);
            host.Relayout();
            Assert.Same(vm.SelectedFinding, grid.SelectedItem);
        });

        // The same finding with another score: its row is replaced, and the selection - which is the finding's id - follows it.
        gateway.Answer = () => Doc(Rest(Populated).Replace("\"score\": 55", "\"score\": 56", StringComparison.Ordinal));
        var refresh = UiThread.Run(() => vm.RefreshCommand.ExecuteAsync(null));
        UiThread.WaitFor(() => refresh.IsCompleted, "the refresh");

        UiThread.Run(() =>
        {
            host.Relayout();

            Assert.NotNull(vm.SelectedFinding);
            Assert.Equal(7788, vm.SelectedFinding.Pid);
            Assert.Equal("56", vm.SelectedFinding.ScoreText);
            Assert.Same(vm.SelectedFinding, grid.SelectedItem);
            Assert.True(Assert.Single(VisualTree.Descendants<DcInspector>(view)).IsOpen);
        });

        // And when that finding is gone, both let go of it.
        gateway.Answer = () => Doc(Rest(PlanesAb));
        var gone = UiThread.Run(() => vm.RefreshCommand.ExecuteAsync(null));
        UiThread.WaitFor(() => gone.IsCompleted, "the second refresh");
        UiThread.Run(() =>
        {
            host.Relayout();
            Assert.Null(vm.SelectedFinding);
            Assert.False(vm.HasSelection);
            host.Dispose();
        });
    }

    [Fact]
    public async Task The_filter_box_filters_the_table()
    {
        var (view, vm, host, _) = await OpenAsync(Populated);
        UiThread.Run(() =>
        {
            using (host)
            {
                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(view));
                toolbar.SearchText = "unobserved";
                host.Relayout();

                Assert.Equal("unobserved", vm.FilterText);
                Assert.Equal(2, vm.Rows.Count);
                Assert.Equal(2, Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view)).Items.Count);

                toolbar.SearchText = "nothing-matches-this";
                host.Relayout();
                Assert.Contains("No finding matches the filter", VisibleTexts(view));
            }
        });
    }

    [Fact]
    public async Task A_narrow_panel_swaps_the_list_for_the_inspector_while_a_finding_is_selected()
    {
        var (view, vm, host, _) = await OpenAsync(Populated, width: 700, height: 760);
        UiThread.Run(() =>
        {
            using (host)
            {
                Assert.True(view.IsCompact);
                var list = (Border)view.FindName("ListCard");
                var inspector = Assert.Single(VisualTree.Descendants<DcInspector>(view));
                Assert.Equal(Visibility.Visible, list.Visibility);

                vm.SelectedFinding = vm.Rows[0];
                host.Relayout();

                Assert.Equal(Visibility.Collapsed, list.Visibility);
                Assert.True(inspector.IsVisible);
                Settled(inspector);
                RenderTo.Png(host, "cust309-runtime-compact-inspector");

                vm.SelectedFinding = null;
                host.Relayout();
                Assert.Equal(Visibility.Visible, list.Visibility);
            }
        });
    }

    [Fact]
    public async Task The_sidebar_has_no_runtime_entry_before_the_probe_answers_or_on_0_8_10_and_gets_one_when_the_runtime_has_the_planes()
    {
        async Task<(Visibility Runtime, Visibility Others)> SidebarAsync(string? runtimeSet, bool probe)
        {
            using var temp = new TempDirectory();
            using var services = AppServices.CreateIsolated(
                TestServices.IsolatedPaths(temp.Path),
                claudeSettingsPath: temp.File("claude-settings.json"),
                runtimeProbeRunner: runtimeSet is null ? null : RuntimeFixtureRunner.For(runtimeSet));
            if (probe)
            {
                _ = await services.Runtime.RefreshAsync();
            }

            var catalog = new PanelCatalog(services);
            return UiThread.Run(() =>
            {
                var items = catalog.Panels.ToDictionary(p => p.Id, p => new DcNavigationItem { Content = p.Title }, StringComparer.Ordinal);
                MainWindow.ApplyPanelGates(catalog, items);

                var others = items.Where(kv => kv.Key != "ai-runtime").Select(kv => kv.Value.Visibility).Distinct().ToArray();
                return (items["ai-runtime"].Visibility, Assert.Single(others));
            });
        }

        // Before the first probe answers: hidden, never flashed. Every other entry is there.
        Assert.Equal((Visibility.Collapsed, Visibility.Visible), await SidebarAsync(null, probe: false));
        Assert.Equal((Visibility.Collapsed, Visibility.Visible), await SidebarAsync("0.8.10", probe: true));
        Assert.Equal((Visibility.Visible, Visibility.Visible), await SidebarAsync("95159fd", probe: true));
    }

    [Fact]
    public async Task At_the_windows_minimum_size_the_long_reasons_wrap_and_the_page_does_not_scroll_sideways()
    {
        // 940 x 620 DIPs is the window's minimum; the panel host there is about 711 x 538 (see PanelShell).
        var (view, vm, host, _) = await OpenAsync(Degraded, width: 711, height: 538);
        UiThread.Run(() =>
        {
            using (host)
            {
                var scroll = (ScrollViewer)view.FindName("PageScroll");

                Assert.True(view.IsCompact);
                Assert.True(scroll.ExtentWidth <= scroll.ViewportWidth + 1, $"extent {scroll.ExtentWidth} is wider than the viewport {scroll.ViewportWidth}");
                Assert.True(scroll.ScrollableHeight > 0); // it scrolls vertically instead

                // The whole reason is on the page and wrapped inside it, not cut at the edge.
                var reason = VisualTree.Descendants<TextBlock>(view)
                    .First(t => t.IsVisible && string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)).Contains("Access is denied", StringComparison.Ordinal));
                Assert.True(reason.ActualWidth <= scroll.ViewportWidth, "the reason is wider than the page");
                Assert.True(reason.ActualHeight > 30, "the reason did not wrap");

                RenderTo.Png(host, "cust309-runtime-min-window");
            }
        });
    }

    [Fact]
    public async Task The_actions_card_carries_the_form_and_the_prerequisites_card_the_copy_only_lines()
    {
        var (view, vm, host, _) = await OpenAsync(Populated, height: 1200);
        UiThread.Run(() =>
        {
            using (host)
            {
                var texts = VisibleTexts(view).ToArray();

                Assert.Contains("Poll now…", texts);
                Assert.Contains("Change settings…", texts);
                Assert.Contains("Disable…", texts);
                Assert.Contains("Prerequisites on this PC", texts);
                Assert.Contains(AiRuntimePanelViewModel.WindowsNote, texts);
                Assert.Contains(AiRuntimePanelViewModel.CopyOnlyNote, texts);
                Assert.Contains("Copy commands", texts);
                Assert.Contains("Check again", texts);
                Assert.Contains(texts, t => t.Contains("auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("reg add HKLM\\SOFTWARE", StringComparison.Ordinal));
                Assert.Contains("Needs: elevated token AND Advanced Audit Policy", texts);

                // The commands are in a read-only box: copy-only.
                var box = VisualTree.Descendants<TextBox>(view).First(b => AutomationProperties.GetName(b).StartsWith("Commands to copy", StringComparison.Ordinal));
                Assert.True(box.IsReadOnly);

                RenderTo.Png(host, "cust309-runtime-actions-prerequisites");
            }
        });
    }
}
