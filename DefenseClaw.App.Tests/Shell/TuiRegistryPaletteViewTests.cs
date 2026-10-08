using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The palette overlay with the TUI registry's rows (CUST-282), on the shared UI thread: the box that takes one typed value, the note that
/// says how many entries Windows does not run, and the dashboard window swapping its CLI rows when the runtime answers. No process is
/// started: the runner is the isolated one and the runtime probe answers from the fixtures.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class TuiRegistryPaletteViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(RuntimeProbeRunner? runner = null)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: runner);
        _services.Add(services);
        return services;
    }

    private static RuntimeProbeRunner FixtureRunner(string set) => (arguments, _) =>
    {
        var file = string.Join(' ', arguments) switch
        {
            "--version-json" => "version.json",
            "--help" => "root.txt",
            "setup --help" => "setup.txt",
            "guardrail --help" => "guardrail.txt",
            "config --help" => "config.txt",
            "sandbox --help" => "sandbox.txt",
            "acp --help" => "acp.txt",
            "setup redaction --help" => "setup-redaction.txt",
            _ => null,
        };

        var path = file is null ? null : System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-" + set, file);
        return Task.FromResult(path is not null && File.Exists(path)
            ? RuntimeProbeOutput.Ok(File.ReadAllText(path))
            : RuntimeProbeOutput.Fail("exit 2"));
    };

    private static TrayIconService UnbuiltTray() =>
        (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));

    private static T Find<T>(DependencyObject root, string automationName)
        where T : DependencyObject =>
        VisualTree.Find<T>(root, e => AutomationProperties.GetName(e) == automationName)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named '{automationName}'.");

    // ------------------------------------------------------------------ the overlay

    [Fact]
    public void The_box_for_the_value_shows_for_a_row_that_takes_one_and_follows_what_is_typed()
    {
        using var services = Create();
        var tray = UnbuiltTray();

        UiThread.Run(() =>
        {
            var actions = new ShellActions(services, new PanelCatalog(services), tray, () => null);
            var catalogue = CuratedCommandCatalog.For(null);
            var rows = ShellCommandRegistry.BuildCliCommands(
                catalogue.Commands.Where(c => c.TuiName is "block skill" or "doctor" or "setup webhook add" or "keys set").ToArray(),
                actions);

            var palette = new CommandPaletteViewModel();
            palette.Load(rows, catalogue.HiddenNote, catalogue.HiddenDetail);

            var control = new CommandPaletteControl { DataContext = palette };
            using var host = new OffscreenHost(control, 760, 700);
            host.Relayout();

            // The box is named for the row it is in front of (its name follows the selection), so select the row before looking for it.
            Select(palette, "block skill");
            host.Relayout();
            var box = Find<Wpf.Ui.Controls.TextBox>(control, "Value for block skill: skill-name");
            var detail = Find<System.Windows.Controls.Border>(control, "Command detail");
            Assert.Equal(Visibility.Visible, detail.Visibility);
            Assert.Equal(Visibility.Visible, box.Visibility);
            Assert.Equal("skill-name", box.PlaceholderText);
            Assert.NotNull(VisualTree.Find<System.Windows.Controls.TextBox>(detail, t => t.Text == "defenseclaw skill block -- <skill-name>"));
            RenderTo.Png(host, "palette-registry-argument-empty");

            // What the operator types appears in the command the pane previews, character by character.
            box.Text = "pdf-tools";
            host.Relayout();
            Assert.NotNull(VisualTree.Find<System.Windows.Controls.TextBox>(detail, t => t.Text == "defenseclaw skill block -- pdf-tools"));
            RenderTo.Png(host, "palette-registry-argument-typed");

            // A row with nothing to type, one that needs more than a form, and one that needs a terminal: no box.
            foreach (var title in new[] { "doctor", "setup webhook add", "keys set" })
            {
                Select(palette, title);
                host.Relayout();
                Assert.Equal(Visibility.Visible, detail.Visibility);
                Assert.Equal(Visibility.Collapsed, box.Visibility);
            }

            RenderTo.Png(host, "palette-registry-terminal");

            // The note says how many entries are left out, and its help text says why.
            var note = VisualTree.Find<System.Windows.Controls.TextBlock>(control, t => t.Text == "21 hidden on Windows");
            Assert.NotNull(note);
            Assert.Equal(Visibility.Visible, note!.Visibility);
            Assert.Contains("Sandboxes run on Linux and macOS only.", AutomationProperties.GetHelpText(note), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_refused_value_is_explained_in_the_pane_and_a_palette_without_hidden_entries_shows_no_note()
    {
        using var services = Create();
        var tray = UnbuiltTray();

        UiThread.Run(() =>
        {
            var actions = new ShellActions(services, new PanelCatalog(services), tray, () => null);
            var rows = ShellCommandRegistry.BuildCliCommands(
                new CuratedCommandCatalog(TuiRegistryCatalogues.Extended).Commands.Where(c => c.TuiName == "guardrail mode").ToArray(),
                actions);

            var palette = new CommandPaletteViewModel();
            palette.Load(rows);

            var control = new CommandPaletteControl { DataContext = palette };
            using var host = new OffscreenHost(control, 760, 520);
            host.Relayout();

            var item = Assert.Single(palette.Results);
            item.ArgumentText = "enforce";
            Assert.True(palette.ChooseSelected());
            host.Relayout();

            var problem = VisualTree.Find<System.Windows.Controls.TextBlock>(control, t => t.Text == "Use observe or action.");
            Assert.NotNull(problem);
            Assert.Equal(Visibility.Visible, problem!.Visibility);
            RenderTo.Png(host, "palette-registry-argument-refused");

            Assert.Null(VisualTree.Find<System.Windows.Controls.TextBlock>(control, t => t.Text.EndsWith("hidden on Windows", StringComparison.Ordinal) && t.IsVisible));
        });
    }

    private static void Select(CommandPaletteViewModel palette, string title) =>
        palette.Selected = palette.Results.First(i => i.Title == title);

    // ------------------------------------------------------------------ the dashboard window

    [Fact]
    public async Task An_open_palette_swaps_its_CLI_rows_when_the_runtime_shows_it_has_the_larger_registry()
    {
        var services = Create(FixtureRunner("95159fd"));
        MainWindow? window = null;
        CommandPaletteViewModel? palette = null;

        UiThread.Run(() =>
        {
            window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            palette = (CommandPaletteViewModel)typeof(MainWindow)
                .GetField("_paletteViewModel", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;

            // The palette is asked for the way a panel's menu asks for it; the runtime has not answered, so it is the 0.8.10 registry.
            services.Navigation.RequestPalette();
            Assert.Equal(210, palette.Results.Count(i => i.IsCli));
            Assert.Equal("21 hidden on Windows", palette.HiddenNote);
            Assert.DoesNotContain(palette.Results, i => i.Title == "setup kiro");
        });

        try
        {
            _ = await services.Runtime.RefreshAsync();

            // The answer arrives on the dispatcher; the open palette is rebuilt in place.
            UiThread.WaitFor(() => palette!.Results.Count(i => i.IsCli) == 232, "the extended registry's rows");
            UiThread.Run(() =>
            {
                Assert.Contains(palette!.Results, i => i.Title == "setup kiro");
                Assert.Contains(palette.Results, i => i.Title == "guardrail mode");
                Assert.Equal("21 hidden on Windows", palette.HiddenNote);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                window!.AllowClose();
                window.Close();
            });
        }
    }

    [Fact]
    public async Task A_palette_opened_after_the_runtime_answered_reads_the_registry_of_that_moment()
    {
        var services = Create(FixtureRunner("95159fd"));
        MainWindow? window = null;
        UiThread.Run(() => window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray()));

        try
        {
            _ = await services.Runtime.RefreshAsync();

            UiThread.Run(() =>
            {
                var palette = (CommandPaletteViewModel)typeof(MainWindow)
                    .GetField("_paletteViewModel", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(window)!;

                services.Navigation.RequestPalette();

                Assert.Equal(232, palette.Results.Count(i => i.IsCli));
                Assert.Equal("21 hidden on Windows", palette.HiddenNote);
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                window!.AllowClose();
                window.Close();
            });
        }
    }

    [Fact]
    public void A_window_listens_to_the_runtime_while_it_lives_and_stops_when_it_really_closes()
    {
        var services = Create(FixtureRunner("95159fd"));

        UiThread.Run(() =>
        {
            var before = Subscribers(services.Runtime, "Changed");
            var window = new MainWindow(services, new PanelCatalog(services), UnbuiltTray());
            Assert.Equal(before + 1, Subscribers(services.Runtime, "Changed"));

            window.AllowClose();
            window.Close();
            Assert.Equal(before, Subscribers(services.Runtime, "Changed"));
        });
    }

    private static int Subscribers(object owner, string eventName)
    {
        var field = owner.GetType().GetField(eventName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{owner.GetType().Name}.{eventName} is not a field-like event any more; update this helper.");
        return (field.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
