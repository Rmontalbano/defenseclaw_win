using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.App.Views.ConfigEditor;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// One config.yaml draft for the whole app (CUST-385): the Setup panel's inline editor and the detached window are two views of the same
/// <see cref="ConfigEditorWindowViewModel"/> (<see cref="ConfigEditorSession"/>). An edit in one shows in the other, the draft survives leaving the
/// panel, the file is watched once, a read-only installation cannot save from either, the tray Exit still asks about it with no window open, and opening
/// the Setup page changes nothing. The masked source view is served by the test (no CLI); the views are hosted offscreen and never shown.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConfigEditorSessionTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Raw => LineEndings.Normalize(ConfigSamples.Raw);

    private static string Masked => LineEndings.Normalize(ConfigSamples.MaskedSource);

    private AppServices Services(DefenseClaw.Core.Paths.InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, Raw, installation: installation);

        // The shell's watcher hashes config.yaml on its own thread; a test that needs "the file changed" raises it (AppServices.ReloadConfig).
        services.ConfigWatcher.Dispose();
        return services;
    }

    private static ConfigEditorSession Loaded(AppServices services)
    {
        var session = ConfigEditorSession.For(services);
        session.Editor.FormSourceOverride = _ => Task.FromResult(Masked);
        _ = UiThread.Run(() => session.EnsureLoadedAsync());
        UiThread.WaitFor(() => session.Editor.HasLoadStarted && !session.Editor.IsLoading, "the draft loaded");
        return session;
    }

    private static ConfigEditorWindowViewModel ViewModelOf(ConfigEditorWindow window) =>
        (ConfigEditorWindowViewModel)typeof(ConfigEditorWindow).GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    /// <summary>How many handlers the composition's "config.yaml changed" event has that belong to <paramref name="target"/>.</summary>
    private static int WatchersOf(AppServices services, object target)
    {
        var field = typeof(AppServices).GetField(nameof(AppServices.ConfigReloaded), BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handlers = (MulticastDelegate?)field.GetValue(services);
        return handlers?.GetInvocationList().Count(d => ReferenceEquals(d.Target, target)) ?? 0;
    }

    /// <summary>The inline copy (as the Setup panel builds it) in its own offscreen host.</summary>
    private static (ConfigEditorView View, OffscreenHost Host) Inline(ConfigEditorSession session) => UiThread.Run(() =>
    {
        var view = new ConfigEditorView { IsInline = true, Session = session };
        return (view, new OffscreenHost(view, 1100, 900));
    });

    /// <summary>The detached window's content in its own offscreen host (the window is built, never shown).</summary>
    private static (ConfigEditorWindow Window, ConfigEditorView View, OffscreenHost Host) Detached(AppServices services) => UiThread.Run(() =>
    {
        var window = new ConfigEditorWindow(services);
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.DataContext = ViewModelOf(window);
        var view = VisualTree.Find<ConfigEditorView>(content)!;
        return (window, view, new OffscreenHost(content, 1100, 900));
    });

    private static void Close(ConfigEditorWindow window) => UiThread.Run(window.Close);

    [Fact]
    public void One_session_per_composition_and_the_detached_window_is_a_view_of_it()
    {
        using var services = Services();
        using var otherTemp = new TempDirectory();
        using var other = TestServices.Create(otherTemp);

        var session = ConfigEditorSession.For(services);
        Assert.Same(session, ConfigEditorSession.For(services));
        Assert.Same(session, ConfigEditorSession.TryGet(services));
        Assert.NotSame(session, ConfigEditorSession.For(other));

        var window = UiThread.Run(() => new ConfigEditorWindow(services));
        try
        {
            Assert.Same(session.Editor, UiThread.Run(() => ViewModelOf(window)));
        }
        finally
        {
            Close(window);
        }

        // Closing the window drops nothing: the draft is the session's, not the window's.
        Assert.Same(session.Editor, ConfigEditorSession.For(services).Editor);
    }

    [Fact]
    public void An_edit_in_the_inline_editor_shows_in_the_detached_window_and_the_other_way_round()
    {
        using var services = Services();
        var session = Loaded(services);
        var (inline, inlineHost) = Inline(session);
        var (window, detached, windowHost) = Detached(services);
        try
        {
            UiThread.Run(() =>
            {
                inlineHost.Relayout();
                windowHost.Relayout();
                Assert.Same(session.Editor, inline.ViewModel);
                Assert.Same(session.Editor, detached.ViewModel);
                Assert.Equal(2, session.AttachedViewCount);

                // RAW typed inline is RAW in the window.
                var edited = Raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
                inline.RawEditor.Text = edited;
                Assert.Equal(edited, session.Editor.RawText);
                Assert.Equal(edited, detached.RawEditor.Text);
                Assert.True(session.Editor.IsRawModified);

                // A FORM edit in the window (the same field objects inline) lands in both RAW editors.
                session.Editor.NotifyFormTabSelected();
                var mode = session.Editor.Sections.SelectMany(s => s.Fields).Single(f => f.Path == "guardrail.mode");
                mode.SelectedChoice = "action";
                Assert.Contains("mode: action", inline.RawEditor.Text, StringComparison.Ordinal);
                Assert.Contains("mode: action", detached.RawEditor.Text, StringComparison.Ordinal);
                Assert.Contains("model: gpt-4.1", detached.RawEditor.Text, StringComparison.Ordinal);

                // Both views show the one MODIFIED badge state and the same section list.
                windowHost.Relayout();
                inlineHost.Relayout();
                Assert.Equal(session.Editor.Sections.Count, VisualTree.Find<ListBox>(inline, l => l.Name == "SectionList")!.Items.Count);
                Assert.Equal(session.Editor.Sections.Count, VisualTree.Find<ListBox>(detached, l => l.Name == "SectionList")!.Items.Count);
            });
        }
        finally
        {
            Close(window);
            UiThread.Run(windowHost.Dispose);
            UiThread.Run(inlineHost.Dispose);
        }

        // The window went; the draft did not.
        Assert.True(session.Editor.IsRawModified);
        Assert.Contains("mode: action", session.Editor.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_views_watch_the_file_once_and_none_watches_it_for_nobody()
    {
        using var services = Services();
        var session = Loaded(services);
        Assert.Equal(0, WatchersOf(services, session.Editor));

        var (_, inlineHost) = Inline(session);
        var (window, _, windowHost) = Detached(services);
        try
        {
            Assert.True(session.Editor.IsWatchingDiskChanges);
            Assert.Equal(1, WatchersOf(services, session.Editor));
        }
        finally
        {
            Close(window);
            UiThread.Run(windowHost.Dispose);
        }

        Assert.Equal(1, WatchersOf(services, session.Editor));
        UiThread.Run(() =>
        {
            inlineHost.Dispose();
            UiThread.Settle(); // Unloaded is raised after the host lets go
        });
        Assert.Equal(0, session.AttachedViewCount);
        Assert.Equal(0, WatchersOf(services, session.Editor));
        Assert.False(session.Editor.IsWatchingDiskChanges);

        // A change made while nothing showed the draft is caught up with when a view comes back (a clean draft reloads).
        File.WriteAllText(services.Paths.ConfigFilePath, Raw.Replace("model: gpt-4o", "model: o3", StringComparison.Ordinal));
        var (_, again) = Inline(session);
        try
        {
            UiThread.WaitFor(() => session.Editor.RawText.Contains("model: o3", StringComparison.Ordinal), "the draft catches up with the file");
            Assert.Equal(1, WatchersOf(services, session.Editor));
        }
        finally
        {
            UiThread.Run(again.Dispose);
        }
    }

    [Fact]
    public void The_draft_survives_leaving_the_Setup_panel_and_coming_back_and_the_window_opens_on_it()
    {
        using var services = Services();
        var session = ConfigEditorSession.For(services);
        session.Editor.FormSourceOverride = _ => Task.FromResult(Masked);

        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1468, 875);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            UiThread.WaitFor(() => session.Editor.HasLoadStarted && !session.Editor.IsLoading, "the inline editor loaded the draft");
            var edited = Raw.Replace("api_port: 18970", "api_port: 18971", StringComparison.Ordinal);

            UiThread.Run(() =>
            {
                var inline = VisualTree.Find<ConfigEditorView>(shell.Page!)!;
                Assert.Same(session, inline.Session);
                inline.RawEditor.Text = edited;

                // Away (another panel) and back.
                _ = shell.Show<ActivityPanel>();
                Assert.Equal(0, session.AttachedViewCount);
                _ = shell.Show<SetupPanel>();
                shell.Host.Relayout();

                var back = VisualTree.Find<ConfigEditorView>(shell.Page!)!;
                Assert.Same(session.Editor, back.ViewModel);
                Assert.Equal(edited, back.RawEditor.Text);
                Assert.True(session.Editor.IsRawModified);
                Assert.Equal(1, session.AttachedViewCount);
            });

            // Coming back did not read the file over the draft, and opening the window does not either.
            var (window, detached, host) = Detached(services);
            try
            {
                UiThread.Run(() =>
                {
                    host.Relayout();
                    Assert.Equal(edited, detached.RawEditor.Text);
                    Assert.Equal(2, session.AttachedViewCount);
                });
            }
            finally
            {
                Close(window);
                UiThread.Run(host.Dispose);
            }
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("invalid")]
    public void A_read_only_installation_cannot_save_from_the_inline_editor(string kind)
    {
        using var services = Services(kind == "managed" ? TestInstallations.ManagedAt(_temp.Path) : TestInstallations.InvalidAt(_temp.Path));
        var session = Loaded(services);
        var (inline, host) = Inline(session);
        try
        {
            UiThread.Run(() =>
            {
                session.Editor.RawText = Raw.Replace("mode: observe", "mode: action", StringComparison.Ordinal);
                host.Relayout();

                Assert.True(session.Editor.HasChangesBlockedReason);
                Assert.False(session.Editor.ReviewAndSaveCommand.CanExecute(null));
                Assert.False(session.Editor.ConfirmReviewedSaveCommand.CanExecute(null));
                var save = VisualTree.Find<Wpf.Ui.Controls.Button>(inline, b => b.IsVisible && (b.Content as string) == "Save")!;
                Assert.False(save.IsEnabled);
                Assert.Equal(session.Editor.ChangesBlockedReason, save.ToolTip);

                // Revert only reads; it stays available to drop the draft.
                Assert.True(session.Editor.RevertCommand.CanExecute(null));
            });
        }
        finally
        {
            UiThread.Run(host.Dispose);
        }

        Assert.Equal(Raw, File.ReadAllText(services.Paths.ConfigFilePath));
    }

    [Fact]
    public void A_file_that_cannot_be_loaded_cannot_be_saved_and_the_card_says_why()
    {
        using var services = TestServices.Create(_temp, "llm:\n  model: x\n  provider: [unclosed\n");
        services.ConfigWatcher.Dispose();
        var session = ConfigEditorSession.For(services);
        session.Editor.FormSourceOverride = _ => Task.FromResult(string.Empty);
        _ = UiThread.Run(() => session.EnsureLoadedAsync());
        UiThread.WaitFor(() => session.Editor.HasLoadStarted && !session.Editor.IsLoading, "the draft loaded");

        var (inline, host) = Inline(session);
        try
        {
            UiThread.Run(() =>
            {
                host.Relayout();
                Assert.True(session.Editor.HasLoadProblem);
                var texts = VisualTree.Descendants<TextBlock>(inline).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Assert.Contains("config.yaml is not valid YAML", texts);
                Assert.Equal("runtime view unavailable", session.Editor.CatalogLabel);
            });
        }
        finally
        {
            UiThread.Run(host.Dispose);
        }
    }

    [Fact]
    public async Task The_tray_exit_asks_about_the_inline_draft_with_no_window_open()
    {
        using var services = Services();
        Assert.Equal(ConfigEditorExitResult.Closed, await UiThread.Run(() => ConfigEditorWindow.CloseForExitAsync(services))); // no session: nothing to ask

        var session = Loaded(services);
        Assert.Equal(ConfigEditorExitResult.Closed, await UiThread.Run(() => ConfigEditorWindow.CloseForExitAsync(services))); // clean

        var asked = 0;
        var answer = UnsavedChangesChoice.Cancel;
        session.Editor.UnsavedChangesPrompt = request =>
        {
            asked++;
            Assert.Equal(UnsavedChangesContext.Close, request.Context);
            return Task.FromResult(answer);
        };

        UiThread.Run(() => session.Editor.RawText = Raw.Replace("mode: observe", "mode: action", StringComparison.Ordinal));
        Assert.Equal(ConfigEditorExitResult.Declined, await UiThread.Run(() => ConfigEditorWindow.CloseForExitAsync(services)));
        Assert.Equal(1, asked);
        Assert.Equal(new NavigationRequest("setup", ConfigEditorFocus.Instance), services.Navigation.Pending); // the draft is put on screen to be read

        answer = UnsavedChangesChoice.Discard;
        Assert.Equal(ConfigEditorExitResult.Closed, await UiThread.Run(() => ConfigEditorWindow.CloseForExitAsync(services)));
        Assert.Equal(2, asked);

        // The window-only overload (no composition) still answers for the window alone.
        Assert.Equal(ConfigEditorExitResult.Closed, await UiThread.Run(() => ConfigEditorWindow.CloseForExitAsync()));
        Assert.Equal(Raw, File.ReadAllText(services.Paths.ConfigFilePath));
    }

    public static IEnumerable<object[]> Renders() => new[]
    {
        new object[] { 980, 692, "Light" },
        new object[] { 980, 692, "Dark" },
        new object[] { 1468, 875, "Light" },
        new object[] { 1468, 875, "Dark" },
    };

    [Theory]
    [MemberData(nameof(Renders))]
    public void The_detached_window_is_the_same_editor_search_label_sections_form_then_revert_and_save(int width, int height, string mode)
    {
        using var services = Services();
        using var fixture = new DefenseClaw.App.Tests.Appearance.AppearanceFixture(
            new DefenseClaw.App.Services.Appearance.AppearanceSettings(
                DefenseClaw.App.Services.Appearance.AppearanceStyle.Default,
                Enum.Parse<DefenseClaw.App.Services.Appearance.AppearanceMode>(mode)));
        var session = Loaded(services);
        var window = UiThread.Run(() => new ConfigEditorWindow(services));
        var host = UiThread.Run(() =>
        {
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = session.Editor;
            return new OffscreenHost(content, width, height);
        });
        try
        {
            UiThread.Run(() =>
            {
                session.Editor.RawText = Raw.Replace("model: gpt-4o", "model: gpt-4.1", StringComparison.Ordinal);
                session.Editor.NotifyFormTabSelected();
                host.Relayout();
                RenderTo.Png(host, $"cust385-window-{width}x{height}-{mode}");

                var view = VisualTree.Find<ConfigEditorView>(host.Content)!;
                var search = VisualTree.Find<Wpf.Ui.Controls.TextBox>(view, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Search all settings")!;
                var sections = VisualTree.Find<ListBox>(view, l => l.Name == "SectionList")!;
                var save = VisualTree.Find<Wpf.Ui.Controls.Button>(view, b => b.IsVisible && (b.Content as string) == "Save")!;
                Assert.True(Top(search, view) < Top(sections, view));
                Assert.True(Top(sections, view) < Top(save, view));
                Assert.True(Top(save, view) + save.ActualHeight <= height, "Save is on screen");
                Assert.Contains(VisualTree.Descendants<TextBlock>(view), t => t.IsVisible && t.Text == "MODIFIED");

                // The search: the matching settings of every section, the form's own fields.
                session.Editor.FieldSearch = "port";
                host.Relayout();
                RenderTo.Png(host, $"cust385-window-{width}x{height}-{mode}-search");
                Assert.Contains(session.Editor.SearchMatches, m => m.Field.Path == "gateway.api_port");
                Assert.False(sections.IsVisible);
                session.Editor.FieldSearch = string.Empty;
            });
        }
        finally
        {
            Close(window);
            UiThread.Run(host.Dispose);
        }
    }

    private static double Top(FrameworkElement element, FrameworkElement root) => element.TranslatePoint(new Point(0, 0), root).Y;

    [Fact]
    public void Opening_the_Setup_page_runs_nothing_that_changes_anything_scans_nothing_and_writes_no_config()
    {
        using var services = Services();

        // A defenseclaw.exe that is not a program: every command the page asks for is recorded (and fails to start), so the test sees exactly what ran.
        var bin = Path.Combine(_temp.Path, "no-such-bin");
        _ = Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "defenseclaw.exe"), "this is not a program");
        services.Paths.InvalidateExecutableCache();

        var before = File.ReadAllBytes(services.Paths.ConfigFilePath);
        var session = ConfigEditorSession.For(services);
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1468, 875);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "setup catalog read finished");
            UiThread.WaitFor(() => session.Editor.HasLoadStarted && !session.Editor.IsLoading, "the inline editor's first read finished");
            UiThread.WaitFor(() => !vm.IsGuardrailBusy && !vm.Credentials.IsLoading, "the page's reads finished");
            UiThread.Run(UiThread.Settle);

            var ran = services.Cli.Activity.Select(i => i.Argv).ToList();
            Assert.Contains(ran, argv => argv.SequenceEqual(ConfigEditorWindowViewModel.SourceArgv)); // the editor read the runtime's view
            Assert.All(ran, argv => Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(argv)));
            Assert.DoesNotContain(ran, argv => argv.Any(a => a.Contains("scan", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(before, File.ReadAllBytes(services.Paths.ConfigFilePath));
            Assert.False(services.RestartQueue.IsPending);
            Assert.False(session.Editor.IsRawModified);
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
