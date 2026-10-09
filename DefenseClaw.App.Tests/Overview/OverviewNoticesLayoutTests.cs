using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Overview as a real view (CUST-274): the notices the TUI prints and "What needs attention" lacked, the Configuration card's new rows - the AI
/// Defense one a host and never a URL - and the buttons of Quick Actions, each drawn only while it applies and off, with the installation's sentence
/// as its tooltip, on a read-only installation. A PNG is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// Synthetic data only; every credential in the config starts with <c>synth</c>.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OverviewNoticesLayoutTests
{
    private const string Port = "gateway:\n  api_port: 39871\n";

    private const string EveryRow =
        Port +
        "guardrail:\n  connector: claudecode\n  enabled: true\n  hilt:\n    enabled: true\n    min_severity: critical\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n    ClaudeCode:\n      mode: observe\n" +
        "ai_discovery:\n  enabled: false\n" +
        "llm:\n  provider: openai\n  model: gpt-4o\n  api_key: synthkey\n" +
        "cisco_ai_defense:\n  endpoint: https://synthuser:synthpass@aidefense.example.test:8443/synthpath-secret/v1?token=synthtoken#synthfrag\n" +
        "policy_dir: D:\\policies\\acme\n";

    private const string Tidy =
        Port +
        "guardrail:\n  connector: claudecode\n  enabled: true\n  connectors:\n    claudecode:\n      mode: observe\n      hook_fail_mode: open\n" +
        "ai_discovery:\n  enabled: true\n";

    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthtoken", "synthkey", "synthpath-secret", "synthfrag" };

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp;
        private readonly PanelShell _shell;

        private Scene(TempDirectory temp, AppServices services, PanelShell shell, OverviewPanel panel)
        {
            _temp = temp;
            Services = services;
            _shell = shell;
            Panel = panel;
        }

        public AppServices Services { get; }

        public OverviewPanel Panel { get; }

        public OverviewPanelViewModel ViewModel => (OverviewPanelViewModel)_shell.ViewModel;

        public static async Task<Scene> OpenAsync(
            string config,
            GatewaySnapshot snapshot,
            string? doctorCache = null,
            InstallationContext? installation = null,
            int width = 1400,
            int height = 900)
        {
            var temp = new TempDirectory();
            var services = TestServices.Create(temp, config, installation: installation);
            if (doctorCache is not null)
            {
                _ = temp.WriteFile("doctor_cache.json", doctorCache);
            }

            PanelShell? shell = null;
            try
            {
                var panel = UiThread.Run(() =>
                {
                    _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(services.Monitor, new object[] { snapshot });
                    shell = new PanelShell(services, width, height);
                    var view = shell.Show<OverviewPanel>();
                    ((OverviewPanelViewModel)shell.ViewModel).Apply(snapshot);
                    return view;
                });

                var scene = new Scene(temp, services, shell!, panel);
                await UiThread.Run(() => scene.ViewModel.ReloadDoctorCacheAsync(CancellationToken.None));
                UiThread.Run(() => shell!.Host.Relayout());
                return scene;
            }
            catch
            {
                UiThread.Run(() => shell?.Dispose());
                services.Dispose();
                temp.Dispose();
                throw;
            }
        }

        /// <summary>Rewrites config.yaml, takes it into the app as the watcher would, and redraws from the snapshot.</summary>
        public void ChangeConfig(string config, GatewaySnapshot snapshot)
        {
            _ = _temp.WriteFile("config.yaml", config);
            Services.ReloadConfig();
            UiThread.Run(() =>
            {
                ViewModel.Apply(snapshot);
                _shell.Host.Relayout();
            });
        }

        public Wpf.Ui.Controls.Button? Button(string content) =>
            VisualTree.Descendants<Wpf.Ui.Controls.Button>(Panel).FirstOrDefault(b => b.Content as string == content);

        public bool IsDrawn(string content) => Button(content) is { Visibility: Visibility.Visible, IsVisible: true };

        public IEnumerable<string> Texts(string listName)
        {
            var list = VisualTree.Find<ItemsControl>(Panel, l => AutomationProperties.GetName(l) == listName)
                ?? throw new InvalidOperationException($"The '{listName}' list is not in the view.");
            return VisualTree.Descendants<TextBlock>(list).Select(t => t.Text);
        }

        public void Render(string name) => RenderTo.Png(_shell.Host, name);

        public void Dispose()
        {
            UiThread.Run(() => _shell.Dispose());
            Services.Dispose();
            _temp.Dispose();
        }
    }

    private static GatewayHealth Health(string body) =>
        JsonSerializer.Deserialize<GatewayHealth>("{\"uptime_ms\":600000" + body + "}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static GatewaySnapshot Snapshot(string body = ",\"api\":{\"state\":\"running\"},\"gateway\":{\"state\":\"disabled\"}") => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "Gateway answering on 127.0.0.1:39871",
        CliPath = @"C:\Tools\defenseclaw.exe",
        Health = Health(body),
        BinaryVersion = "0.8.10",
        ApiPort = 39871,
        ActiveConnectors = new[] { "claudecode" },
        PolledAt = DateTimeOffset.UtcNow,
    };

    private static string MissingKeys(params string[] names) =>
        "{\"passed\":3,\"failed\":" + names.Length + ",\"warned\":0,\"skipped\":0,\"captured_at\":\"" +
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\",\"checks\":[" +
        string.Join(",", names.Select(n => "{\"status\":\"fail\",\"label\":\"credential " + n + "\",\"detail\":\"d\"}")) + "]}";

    // ------------------------------------------------------------------ Quick Actions: drawn only while they apply

    [Fact]
    public async Task While_ai_discovery_is_off_and_a_key_is_missing_enable_and_fill_missing_keys_are_drawn_and_scan_is_not()
    {
        using var scene = await Scene.OpenAsync(EveryRow, Snapshot(), MissingKeys("ANTHROPIC_API_KEY"));

        UiThread.Run(() =>
        {
            Assert.True(scene.IsDrawn("Enable AI discovery"));
            Assert.True(scene.IsDrawn("Fill missing keys"));
            Assert.True(scene.IsDrawn("Turn notifications off"));
            Assert.False(scene.IsDrawn("Scan AI discovery"));

            // The ones that apply are real, pressable buttons wired to the panel's commands.
            Assert.Same(scene.ViewModel.EnableAiDiscoveryCommand, scene.Button("Enable AI discovery")!.Command);
            Assert.Same(scene.ViewModel.FillKeysInConsoleCommand, scene.Button("Fill missing keys")!.Command);
            Assert.Same(scene.ViewModel.ToggleNotificationsCommand, scene.Button("Turn notifications off")!.Command);
            Assert.Same(scene.ViewModel.ScanAiDiscoveryCommand, scene.Button("Scan AI discovery")!.Command);
            Assert.All(new[] { "Enable AI discovery", "Fill missing keys", "Turn notifications off" }, name => Assert.True(scene.Button(name)!.IsEnabled, name));
        });
    }

    [Fact]
    public async Task While_ai_discovery_is_on_and_no_key_is_missing_scan_is_drawn_and_the_other_two_are_not_and_the_label_follows_the_switch()
    {
        using var scene = await Scene.OpenAsync(Tidy, Snapshot());

        UiThread.Run(() =>
        {
            Assert.True(scene.IsDrawn("Scan AI discovery"));
            Assert.False(scene.IsDrawn("Enable AI discovery"));
            Assert.False(scene.IsDrawn("Fill missing keys"));
            Assert.True(scene.IsDrawn("Turn notifications off"));
        });

        // The operator (or the CLI) turns notifications off in config.yaml: the same button now offers to turn them on.
        scene.ChangeConfig(Tidy + "notifications:\n  enabled: false\n", Snapshot());

        UiThread.Run(() =>
        {
            Assert.True(scene.IsDrawn("Turn notifications on"));
            Assert.False(scene.IsDrawn("Turn notifications off"));
        });
    }

    [Fact]
    public async Task The_buttons_sit_in_quick_actions_between_run_doctor_and_diagnostics_in_that_order()
    {
        using var scene = await Scene.OpenAsync(EveryRow, Snapshot(), MissingKeys("ANTHROPIC_API_KEY"));

        UiThread.Run(() =>
        {
            var quickActions = VisualTree.Descendants<WrapPanel>(scene.Panel).Single(p => p.Children.OfType<Wpf.Ui.Controls.Button>().Any(b => b.Content as string == "Scan Skills"));
            var order = quickActions.Children.Cast<UIElement>()
                .Select(child => (child as ContentControl)?.Content as string ?? string.Empty)
                .ToList();

            int At(string name) => order.IndexOf(name);
            Assert.True(At("Run Doctor") >= 0 && At("Run Doctor") < At("Enable AI discovery"), string.Join(" | ", order));
            Assert.True(At("Enable AI discovery") < At("Scan AI discovery") && At("Scan AI discovery") < At("Fill missing keys"), string.Join(" | ", order));
            Assert.True(At("Fill missing keys") < At("Turn notifications off") && At("Turn notifications off") < At("Diagnostics"), string.Join(" | ", order));
        });
    }

    [Fact]
    public async Task Each_button_says_what_it_runs_to_assistive_technology_and_in_its_tooltip()
    {
        using var scene = await Scene.OpenAsync(EveryRow, Snapshot(), MissingKeys("ANTHROPIC_API_KEY"));

        UiThread.Run(() =>
        {
            string Help(string name) => AutomationProperties.GetHelpText(scene.Button(name)!);

            Assert.Contains("defenseclaw agent discovery enable --yes", Help("Enable AI discovery"), StringComparison.Ordinal);
            Assert.Contains("defenseclaw agent discovery scan", Help("Scan AI discovery"), StringComparison.Ordinal);
            Assert.Contains("defenseclaw keys fill-missing --yes", Help("Fill missing keys"), StringComparison.Ordinal);
            Assert.Contains("defenseclaw setup notifications", Help("Turn notifications off"), StringComparison.Ordinal);

            Assert.Equal(scene.ViewModel.EnableAiDiscoveryTip, scene.Button("Enable AI discovery")!.ToolTip);
            Assert.Equal(scene.ViewModel.FillMissingKeysTip, scene.Button("Fill missing keys")!.ToolTip);
            Assert.Equal(scene.ViewModel.NotificationsTip, scene.Button("Turn notifications off")!.ToolTip);
            Assert.All(new[] { "Enable AI discovery", "Scan AI discovery", "Fill missing keys", "Turn notifications off" }, name => Assert.True(ToolTipService.GetShowOnDisabled(scene.Button(name)!), name));
        });
    }

    [Fact]
    public async Task On_a_read_only_installation_every_button_is_off_and_its_tooltip_is_the_installations_sentence()
    {
        using var scene = await Scene.OpenAsync(EveryRow, Snapshot(), MissingKeys("ANTHROPIC_API_KEY"), TestInstallations.Invalid());
        var reason = scene.Services.Installation.BlockedReason;
        Assert.NotNull(reason);

        UiThread.Run(() =>
        {
            foreach (var name in new[] { "Enable AI discovery", "Fill missing keys", "Turn notifications off" })
            {
                var button = scene.Button(name)!;
                Assert.False(button.IsEnabled, name);
                Assert.Equal(reason, button.ToolTip);
            }
        });
    }

    [Fact]
    public async Task The_diagnostics_menu_has_the_policy_list_after_the_provenance_check()
    {
        using var scene = await Scene.OpenAsync(Tidy, Snapshot());

        UiThread.Run(() =>
        {
            var diagnostics = VisualTree.Descendants<Wpf.Ui.Controls.DropDownButton>(scene.Panel).Single();
            var menu = Assert.IsType<ContextMenu>(diagnostics.Flyout);
            var headers = menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();

            Assert.Equal(headers.IndexOf("Show provenance") + 1, headers.IndexOf("List policies"));
            Assert.Equal("defenseclaw policy list", menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "List policies").ToolTip);
        });
    }

    // ------------------------------------------------------------------ What needs attention

    [Fact]
    public async Task The_new_notices_are_drawn_with_the_scanner_hint_in_a_box_to_copy_from()
    {
        using var scene = await Scene.OpenAsync(
            EveryRow,
            Snapshot(",\"api\":{\"state\":\"reconnecting\"},\"gateway\":{\"state\":\"disabled\"}"),
            MissingKeys("ANTHROPIC_API_KEY", "OPENAI_API_KEY"),
            width: 1400,
            height: 2300);

        UiThread.Run(() =>
        {
            scene.ViewModel.ToggleAttentionCommand.Execute(null);
            scene.ViewModel.ToggleConfigurationCommand.Execute(null);
            UiThread.Settle();

            var titles = scene.Texts("What needs attention").ToList();
            Assert.Contains("The gateway is starting", titles);
            Assert.Contains("Connector roster degraded", titles);
            Assert.Contains("skill-scanner not on PATH", titles);

            // The install hint is text in a read-only box: selectable, copyable, never a button that runs it.
            var hint = VisualTree.Descendants<TextBox>(scene.Panel).Single(t => t.Text == "pip install cisco-ai-skill-scanner");
            Assert.True(hint.IsReadOnly);
            Assert.Equal("Suggested command", AutomationProperties.GetName(hint));

            // The Configuration card draws the new rows, and the address is a host: nothing else of it reaches the screen.
            var configuration = scene.Texts("Configuration").ToList();
            Assert.Contains("Human approval", configuration);
            Assert.Contains("ON (min CRITICAL)", configuration);
            Assert.Contains("Policy dir", configuration);
            Assert.Contains(@"D:\policies\acme", configuration);
            Assert.Contains("LLM provider", configuration);
            Assert.Contains("openai", configuration);
            Assert.Contains("AI Defense", configuration);
            Assert.Contains("aidefense.example.test:8443", configuration);

            var everything = string.Join('\n', VisualTree.Descendants<TextBlock>(scene.Panel).Select(t => t.Text)) + "\n" +
                             string.Join('\n', VisualTree.Descendants<TextBox>(scene.Panel).Select(t => t.Text));
            Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));

            scene.Render("overview-notices-1400x2300");
        });
    }

    [Fact]
    public async Task A_tidy_install_has_none_of_the_new_notices_and_none_of_the_conditional_buttons()
    {
        using var scene = await Scene.OpenAsync(Tidy, Snapshot());

        UiThread.Run(() =>
        {
            var titles = scene.Texts("What needs attention").ToList();
            Assert.DoesNotContain("The gateway is starting", titles);
            Assert.DoesNotContain("Connector roster degraded", titles);
            Assert.DoesNotContain(titles, t => t.StartsWith("credential ", StringComparison.Ordinal));

            Assert.False(scene.IsDrawn("Enable AI discovery"));
            Assert.False(scene.IsDrawn("Fill missing keys"));
        });
    }
}
