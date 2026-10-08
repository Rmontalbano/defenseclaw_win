using System.Windows;
using System.Windows.Automation;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.App.Views.Redaction;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Redaction;

/// <summary>
/// The ways into the redaction window (CUST-295): Setup's tile and Logs' button. Both follow the runtime check and nothing else: shown while
/// the connected runtime has <c>setup redaction</c>, absent (not just disabled) on 0.8.10 and while the runtime has not been probed, and
/// following a runtime that is learned or upgraded while the panel is on screen. The help text comes from the same fixtures the Core suite
/// uses; no process is started.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RedactionEntryPointTests : IDisposable
{
    /// <summary>A generous wait for a condition on a CI runner many times slower than this machine.</summary>
    private const int SlowRunner = 120_000;

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

    /// <summary>A runner that answers the runtime probes from the fixture set named by <paramref name="set"/>, which a test can change.</summary>
    private sealed class Runtime(string set)
    {
        public string Set { get; set; } = set;

        public Task<RuntimeProbeOutput> Answer(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
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

            var path = file is null ? null : System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-" + Set, file);
            return Task.FromResult(path is not null && File.Exists(path) ? RuntimeProbeOutput.Ok(File.ReadAllText(path)) : RuntimeProbeOutput.Fail("exit 2"));
        }
    }

    private AppServices Create(Runtime? runtime)
    {
        var services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: runtime is null ? null : runtime.Answer);
        _services.Add(services);
        return services;
    }

    private static async Task Probe(AppServices services) => _ = await services.Runtime.RefreshAsync(force: true);

    private static System.Windows.Controls.Button[] RedactionButtons(FrameworkElement page) =>
        VisualTree.Descendants<System.Windows.Controls.Button>(page)
            .Where(b => b.IsVisible && AutomationProperties.GetName(b) == "Redaction policy")
            .ToArray();

    // ------------------------------------------------------------------ the gate

    [Fact]
    public async Task The_window_opens_only_on_a_runtime_that_has_the_editor()
    {
        var old = Create(new Runtime("0.8.10"));
        await Probe(old);
        Assert.False(RedactionWindow.CanOpen(old));
        Assert.False(UiThread.Run(() => RedactionWindow.Open(old))); // refuses before a window is made
        Assert.Null(RedactionWindow.Current);

        var unknown = Create(runtime: null);
        await Probe(unknown);
        Assert.False(RedactionWindow.CanOpen(unknown)); // a probe that failed is not "empty": it fails closed

        var pinned = Create(new Runtime("95159fd"));
        await Probe(pinned);
        Assert.True(RedactionWindow.CanOpen(pinned));
        Assert.True(pinned.Runtime.Check(RuntimeCapability.RedactionAdvanced).IsAvailable);
        Assert.False(old.Runtime.Check(RuntimeCapability.RedactionAdvanced).IsAvailable);
        Assert.Equal(RuntimeCapabilityCatalog.UnsupportedMessage, old.Runtime.Check(RuntimeCapability.RedactionAdvanced).Reason);
    }

    // ------------------------------------------------------------------ Setup

    [Theory]
    [InlineData("0.8.10", false)]
    [InlineData("95159fd", true)]
    [InlineData(null, false)]
    public async Task Setup_has_the_tile_only_while_the_runtime_has_the_editor(string? set, bool shown)
    {
        var services = Create(set is null ? null : new Runtime(set));
        await Probe(services);
        var shell = UiThread.Run(() => new PanelShell(services, 1200, 900));
        try
        {
            UiThread.Run(() => shell.Show<SetupPanel>());
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "the setup catalog read to finish", SlowRunner);
            var opened = 0;

            UiThread.Run(() =>
            {
                // the catalog wires the tile to the window; a test counts instead of opening one
                Assert.NotNull(vm.OpenRedaction);
                vm.OpenRedaction = () => opened++;
                shell.Host.Relayout();

                Assert.Equal(shown, vm.HasRedactionTile);
                var tiles = RedactionButtons(shell.Page!);
                Assert.Equal(shown ? 1 : 0, tiles.Length);

                if (shown)
                {
                    Assert.True(tiles[0].Command.CanExecute(null));
                    tiles[0].Command.Execute(null);
                    Assert.Equal(1, opened);
                    Assert.True(tiles[0].IsTabStop && tiles[0].Focusable);
                }
                else
                {
                    Assert.False(vm.OpenRedactionTileCommand.CanExecute(null));
                }
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public async Task Setup_without_a_wired_opener_has_no_tile_even_on_the_pinned_runtime()
    {
        var services = Create(new Runtime("95159fd"));
        await Probe(services);

        var vm = UiThread.Run(() => new SetupPanelViewModel(services));

        Assert.Null(vm.OpenRedaction);
        Assert.False(vm.HasRedactionTile);
        Assert.False(vm.OpenRedactionTileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Setup_follows_a_runtime_that_is_learned_or_upgraded_while_the_panel_is_on_screen()
    {
        var runtime = new Runtime("0.8.10");
        var services = Create(runtime);
        await Probe(services);
        var shell = UiThread.Run(() => new PanelShell(services, 1200, 900));
        try
        {
            UiThread.Run(() => shell.Show<SetupPanel>());
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading, "the setup catalog read to finish", SlowRunner);
            UiThread.Run(() =>
            {
                vm.OpenRedaction = () => { };
                shell.Host.Relayout();
                Assert.Empty(RedactionButtons(shell.Page!));
            });

            runtime.Set = "95159fd"; // an upgrade, noticed by the next probe
            await Probe(services);
            UiThread.WaitFor(() => vm.HasRedactionTile, "the tile to appear after the upgrade", SlowRunner);
            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                Assert.Single(RedactionButtons(shell.Page!));
            });

            runtime.Set = "0.8.10"; // and a rollback
            await Probe(services);
            UiThread.WaitFor(() => !vm.HasRedactionTile, "the tile to go away after the rollback", SlowRunner);
            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                Assert.Empty(RedactionButtons(shell.Page!));
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    // ------------------------------------------------------------------ Logs

    [Theory]
    [InlineData("0.8.10", false)]
    [InlineData("95159fd", true)]
    [InlineData(null, false)]
    public async Task Logs_has_the_button_only_while_the_runtime_has_the_editor_and_on_every_stream(string? set, bool shown)
    {
        var services = Create(set is null ? null : new Runtime(set));
        await Probe(services);
        var shell = UiThread.Run(() => new PanelShell(services, 1200, 900));
        try
        {
            UiThread.Run(() => shell.Show<LogsPanel>());
            var vm = UiThread.Run(() => (LogsPanelViewModel)shell.ViewModel);
            var opened = 0;

            UiThread.Run(() =>
            {
                vm.RedactionOpener = () => opened++;
                shell.Host.Relayout();

                Assert.Equal(shown, vm.HasRedactionEntry);
                Assert.Equal(shown ? 1 : 0, RedactionButtons(shell.Page!).Length);
                Assert.Equal(shown, vm.OpenRedactionCommand.CanExecute(null));

                if (shown)
                {
                    // the Mac has the button on Logs whatever stream is showing (the judge button is Verdicts-only)
                    foreach (var source in new[] { "Gateway", LogsPanelViewModel.VerdictsSource, LogsPanelViewModel.EventsSource, "Watchdog" })
                    {
                        vm.ActiveSource = source;
                        shell.Host.Relayout();
                        Assert.Single(RedactionButtons(shell.Page!));
                    }

                    RedactionButtons(shell.Page!)[0].Command.Execute(null);
                    Assert.Equal(1, opened);
                }
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }

    [Fact]
    public async Task Logs_follows_a_runtime_that_is_learned_while_the_panel_is_on_screen()
    {
        var runtime = new Runtime("0.8.10");
        var services = Create(runtime);
        await Probe(services);
        var shell = UiThread.Run(() => new PanelShell(services, 1200, 900));
        try
        {
            UiThread.Run(() => shell.Show<LogsPanel>());
            var vm = UiThread.Run(() => (LogsPanelViewModel)shell.ViewModel);
            Assert.False(vm.HasRedactionEntry);

            runtime.Set = "95159fd";
            await Probe(services);

            UiThread.WaitFor(() => vm.HasRedactionEntry && vm.OpenRedactionCommand.CanExecute(null), "the button to appear after the upgrade", SlowRunner);
            UiThread.Run(() =>
            {
                shell.Host.Relayout();
                Assert.Single(RedactionButtons(shell.Page!));
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
