using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The OpenCode version mismatch on the Overview (CUST-426), synthetic and verified from source only. DefenseClaw 1.0.6 guards OpenCode V2 and its gateway
/// refuses V1 (internal/gateway/connector/opencode_v2.go, <c>RequireOpenCodeV2</c>); 1.0.5 and earlier guard V1 only and run V2 unguarded while status says
/// protected (changelog "Known limit", GAP-0029). Each case runs the real <see cref="OpenCodeInstallEvidence"/> over files in a scratch folder: runtime
/// version by V1/V2/none by configured/not by per-user/managed. Renders the attention card when <c>DC_RENDER_DIR</c> is set.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class OpenCodeAttentionTests : IDisposable
{
    private const string OpenCodeConfig = "guardrail:\n  enabled: true\n  connector: opencode\n  connectors:\n    opencode:\n      mode: observe\n";

    private const string CodexConfig = "guardrail:\n  enabled: true\n  connector: codex\n  connectors:\n    codex:\n      mode: observe\n";

    private readonly TempDirectory _temp = new();
    private AppServices? _services;
    private int _reads;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private AppServices Services => _services!;

    private string Npm => Path.Combine(_temp.Path, "npm-prefix");

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    /// <summary>The package a prefix keeps: V1 is <c>opencode-ai</c>, V2 is <c>@opencode/cli</c>.</summary>
    private void Install(string kind, string version)
    {
        var folder = kind == "v1"
            ? Path.Combine(Npm, "node_modules", "opencode-ai")
            : Path.Combine(Npm, "node_modules", "@opencode", "cli");
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "package.json"), JsonSerializer.Serialize(new { version }));
    }

    private void PutOpenCode(string kind)
    {
        if (kind is "v1" or "both")
        {
            Install("v1", "1.18.34");
        }

        if (kind is "v2" or "both")
        {
            Install("v2", "2.2.4");
        }
    }

    private async Task<OverviewPanelViewModel> PanelAsync(string? version, string kind, string config = OpenCodeConfig, InstallationContext? installation = null, string[]? roster = null)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, config, version is null ? null : Runtime(version), installation);
        if (version is not null)
        {
            _ = await _services.Runtime.RefreshAsync(force: true);
        }

        PutOpenCode(kind);
        var vm = new OverviewPanelViewModel(_services)
        {
            OpenCodeReader = dataDirectory =>
            {
                _ = Interlocked.Increment(ref _reads);
                return OpenCodeInstallEvidence.Read(dataDirectory, [Npm]);
            },
        };
        vm.Apply(Snapshot(null, roster ?? ["opencode"]));
        return vm;
    }

    private static GatewaySnapshot Snapshot(GatewayHealth? health, params string[] active) => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        Health = health,
        ActiveConnectors = active,
        PolledAt = DateTimeOffset.UtcNow,
    };

    // ------------------------------------------------------------------ the matrix

    [Theory]
    // 1.0.6 or later guards V2 only: V1 alone is the unguarded one.
    [InlineData("1.0.6", "v1", OverviewPanelViewModel.OpenCodeV1NotGuardedTitle)]
    [InlineData("1.1.0", "v1", OverviewPanelViewModel.OpenCodeV1NotGuardedTitle)]
    [InlineData("1.0.6", "v2", null)]
    [InlineData("1.0.6", "none", null)]
    [InlineData("1.0.6", "both", null)]
    // 1.0.5 and earlier guard V1 only: V2 is the unguarded one.
    [InlineData("1.0.5", "v2", OverviewPanelViewModel.OpenCodeV2UncheckedTitle)]
    [InlineData("1.0.0", "v2", OverviewPanelViewModel.OpenCodeV2UncheckedTitle)]
    [InlineData("1.0.5", "v1", null)]
    [InlineData("1.0.5", "none", null)]
    // Both together: the installer, which runs the program, is the judge of which one runs.
    [InlineData("1.0.5", "both", OverviewPanelViewModel.OpenCodeV2UncheckedTitle)]
    // A runtime that has not told its version says nothing either way.
    [InlineData(null, "v1", null)]
    [InlineData(null, "v2", null)]
    public async Task The_row_follows_the_runtime_and_the_opencode_it_guards_for_a_per_user_install(string? version, string kind, string? title)
    {
        var vm = await PanelAsync(version, kind);

        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        if (title is null)
        {
            Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
        }
        else
        {
            var row = Assert.Single(vm.Attention, r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
            Assert.Equal(title, row.Title);
            Assert.Equal("High", row.SeverityKey);
        }
    }

    [Fact]
    public async Task On_1_0_6_a_v1_is_not_guarded_and_the_row_gives_the_install_command_and_the_restart()
    {
        var vm = await PanelAsync("1.0.6", "v1");

        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        var row = Assert.Single(vm.Attention, r => r.Title == OverviewPanelViewModel.OpenCodeV1NotGuardedTitle);
        Assert.Equal("npm uninstall -g opencode-ai && npm install -g @opencode/cli@2", row.Command);
        Assert.Equal(OpenCodeInstallEvidence.InstallV2Command, row.Command);
        Assert.Contains("opencode service restart", row.Detail, StringComparison.Ordinal);
        Assert.Contains("OpenCode 1.18.34", row.Detail, StringComparison.Ordinal);
        Assert.Contains("guards OpenCode V2 only", row.Detail, StringComparison.Ordinal);
        Assert.Contains("DefenseClaw 1.0.6", row.Detail, StringComparison.Ordinal);
        Assert.Contains("not checked", row.Detail, StringComparison.Ordinal);
        Assert.Equal(0, row.Actions.Count);
    }

    [Fact]
    public async Task On_1_0_5_a_v2_runs_unchecked_even_though_status_says_protected_and_the_row_says_to_upgrade()
    {
        var vm = await PanelAsync("1.0.5", "v2");

        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        var row = Assert.Single(vm.Attention, r => r.Title == OverviewPanelViewModel.OpenCodeV2UncheckedTitle);
        Assert.Contains("OpenCode 2.2.4", row.Detail, StringComparison.Ordinal);
        Assert.Contains("run unchecked", row.Detail, StringComparison.Ordinal);
        Assert.Contains("status shows OpenCode protected", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Upgrade DefenseClaw to 1.0.6", row.Detail, StringComparison.Ordinal);
        Assert.False(row.HasCommand);
    }

    // ------------------------------------------------------------------ configured or not, per-user or managed

    [Theory]
    [InlineData("1.0.6", "v1")]
    [InlineData("1.0.5", "v2")]
    public async Task Without_opencode_among_the_connectors_there_is_no_row_and_nothing_is_read(string version, string kind)
    {
        // The roster does not name it: the files are not even opened.
        var vm = await PanelAsync(version, kind, CodexConfig, roster: ["codex"]);
        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        Assert.Equal(0, _reads);
        Assert.Equal(OpenCodeInstallFacts.NotConfigured, vm.LastOpenCodeFacts);
        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.0.6", "v1")]
    [InlineData("1.0.5", "v2")]
    public async Task A_roster_that_names_it_with_a_config_that_does_not_is_no_row_either(string version, string kind)
    {
        var vm = await PanelAsync(version, kind, CodexConfig, roster: ["opencode"]);

        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        Assert.Equal(1, _reads);
        Assert.False(vm.LastOpenCodeFacts.Configured);
        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1.0.6", "v1")]
    [InlineData("1.0.5", "v2")]
    public async Task A_managed_installation_is_not_asked(string version, string kind)
    {
        // Upstream's _opencode_configured is false for a managed install (migrations.py:3282), and a managed host reports opencode_v1_unprotected itself.
        var vm = await PanelAsync(version, kind, installation: TestInstallations.Managed(_temp.Path));

        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        Assert.Equal(0, _reads);
        Assert.Equal(OpenCodeInstallFacts.NotConfigured, vm.LastOpenCodeFacts);
        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_row_goes_at_once_when_opencode_leaves_the_roster_before_any_new_read()
    {
        var vm = await PanelAsync("1.0.6", "v1");
        await vm.RefreshOpenCodeAsync(CancellationToken.None);
        Assert.Contains(vm.Attention, static r => r.Title == OverviewPanelViewModel.OpenCodeV1NotGuardedTitle);

        vm.Apply(Snapshot(null, "codex"));

        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moving_to_v2_clears_the_row_on_the_next_read()
    {
        var vm = await PanelAsync("1.0.6", "v1");
        await vm.RefreshOpenCodeAsync(CancellationToken.None);
        Assert.Contains(vm.Attention, static r => r.Title == OverviewPanelViewModel.OpenCodeV1NotGuardedTitle);

        Directory.Delete(Path.Combine(Npm, "node_modules", "opencode-ai"), recursive: true);
        Install("v2", "2.2.4");
        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ evidence that cannot be read

    [Fact]
    public async Task Files_that_cannot_be_read_are_no_row_and_no_error()
    {
        var vm = await PanelAsync("1.0.6", "none");
        var folder = Path.Combine(Npm, "node_modules", "opencode-ai");
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "package.json"), "{ not json");
        File.WriteAllText(Path.Combine(_temp.Path, "agent_discovery.json"), "[[[");

        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        Assert.True(vm.LastOpenCodeFacts.Configured);
        Assert.Null(vm.LastOpenCodeFacts.V1);
        Assert.DoesNotContain(vm.Attention, static r => r.Title.StartsWith("OpenCode V", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reader_that_throws_keeps_the_last_answer_and_does_not_fault()
    {
        var vm = await PanelAsync("1.0.6", "v1");
        await vm.RefreshOpenCodeAsync(CancellationToken.None);
        vm.OpenCodeReader = static _ => throw new IOException("locked");

        await vm.RefreshOpenCodeAsync(CancellationToken.None);

        Assert.Contains(vm.Attention, static r => r.Title == OverviewPanelViewModel.OpenCodeV1NotGuardedTitle);
    }

    [Fact]
    public void A_record_that_is_half_there_makes_no_row()
    {
        var runtime = Probe("1.0.6");
        Assert.Null(OverviewPanelViewModel.OpenCodeRow(runtime, OpenCodeInstallFacts.NotConfigured));
        Assert.Null(OverviewPanelViewModel.OpenCodeRow(runtime, new OpenCodeInstallFacts(Configured: true, V2: null, V1: null)));
        Assert.Null(OverviewPanelViewModel.OpenCodeRow(RuntimeSnapshot.NotProbed, new OpenCodeInstallFacts(true, null, new OpenCodeInstall("1.18.34", "x"))));
    }

    private static RuntimeSnapshot Probe(string version)
    {
        const string help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
        return RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}", help, help, null, null, null, null, null),
            "test", "fp", DateTimeOffset.UnixEpoch);
    }

    // ------------------------------------------------------------------ a picture

    public static IEnumerable<object[]> Renders() =>
        from size in new[] { "980x692", "1468x875" }
        from mode in new[] { "Light", "Dark" }
        select new object[] { size, mode };

    private const string HealthWithFallbacks =
        "{\"uptime_ms\":600000,\"api\":{\"state\":\"running\"}," +
        "\"guardrail\":{\"state\":\"running\",\"details\":{\"policy_mode\":\"observe\",\"judge_state\":\"degraded\",\"judge_recent_calls\":20,\"judge_failed_calls\":3," +
        "\"judge_fallbacks\":12,\"judge_fallbacks_by_connector\":{\"codex\":9,\"claudecode\":3},\"judge_last_fallback\":\"timeout (codex)\"}}}";

    /// <summary>The attention card with the OpenCode V1 row (a 1.0.6 fixture) beside the judge-fallback row.</summary>
    [Theory]
    [MemberData(nameof(Renders))]
    public async Task The_attention_card_draws_the_opencode_row_and_the_judge_fallback_row(string size, string mode)
    {
        _services = TestServices.Create(_temp, OpenCodeConfig, Runtime("1.0.6"));
        _ = await _services.Runtime.RefreshAsync(force: true);
        PutOpenCode("v1");
        var health = JsonSerializer.Deserialize<GatewayHealth>(HealthWithFallbacks, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var snapshot = Snapshot(health, "opencode");
        _ = typeof(GatewayMonitor).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Services.Monitor, new object[] { snapshot });
        var parts = size.Split('x');
        var (width, height) = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        using var fixture = new DefenseClaw.App.Tests.Appearance.AppearanceFixture(
            new DefenseClaw.App.Services.Appearance.AppearanceSettings(
                DefenseClaw.App.Services.Appearance.AppearanceStyle.Default, Enum.Parse<DefenseClaw.App.Services.Appearance.AppearanceMode>(mode)));

        PanelShell? shell = null;
        OverviewPanel? view = null;
        try
        {
            var vm = UiThread.Run(() =>
            {
                shell = new PanelShell(Services, width, height);
                view = shell.Show<OverviewPanel>();
                var panel = (OverviewPanelViewModel)shell.ViewModel;
                panel.OpenCodeReader = dataDirectory => OpenCodeInstallEvidence.Read(dataDirectory, [Npm]);
                panel.Apply(snapshot);
                return panel;
            });
            await UiThread.Run(() => vm.RefreshOpenCodeAsync(CancellationToken.None));

            UiThread.Run(() =>
            {
                if (!vm.ShowAllAttention)
                {
                    vm.ToggleAttentionCommand.Execute(null);
                }

                shell!.Host.Relayout();
                RenderTo.Png(shell.Host, $"cust426-attention-{size}-{mode.ToLowerInvariant()}");
            });

            Assert.Contains(vm.Attention, static r => r.Title == OverviewPanelViewModel.OpenCodeV1NotGuardedTitle);
            Assert.Contains(vm.Attention, static r => r.Title.StartsWith(OverviewPanelViewModel.JudgeFallbacksTitlePrefix, StringComparison.Ordinal));
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }
}
