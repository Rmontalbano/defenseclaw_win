using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Runtime;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// "Re-pin edited pack" in the Policies panel (CUST-378; runtime 1.0.4, <c>guardrail use-pack PACK --repin --yes --json</c>): offered only on a
/// runtime that has it, validates the pack and compares the digest of its files with the pin, shows both digests in the review, and runs the
/// reviewed argv only after the operator confirms. The CLI is scripted over synthetic fixtures; nothing starts.
/// </summary>
public sealed class PolicyModelRepinTests : IDisposable
{
    private const string Folder = @"C:\Users\operator\.defenseclaw\policies\guardrail\protected-codex\strict";
    private static readonly string Old = new('a', 64);
    private static readonly string Edited = new('b', 64);

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

    /// <summary>The probe of the pinned fixtures, except that the runtime says <paramref name="version"/>.</summary>
    private static RuntimeProbeRunner ProbeAs(string version)
    {
        var inner = ProbeRunner(Pinned);
        return (arguments, token) => string.Join(' ', arguments) == "--version-json"
            ? Task.FromResult(RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"schema_version\":1,\"version\":\"" + version + "\"}"))
            : inner(arguments, token);
    }

    private static string ConfigWithPin(string digest)
    {
        var root = JsonNode.Parse(Fixture("cli/config-show-guardrail.connectors.json"))!;
        root["guardrail"]!["custom_packs"] = new JsonObject
        {
            ["protected-codex"] = new JsonObject { ["path"] = Folder, ["digest"] = "sha256:" + digest },
        };
        return root.ToJsonString();
    }

    private async Task<(PolicyModelViewModel Vm, ScriptedCli Cli, List<string> Ran)> OpenAsync(string version, string pinnedDigest, string filesDigest)
    {
        var services = TestServices.Create(_temp, runtimeProbeRunner: ProbeAs(version));
        _services.Add(services);
        _ = await services.Runtime.RefreshAsync(force: true);
        var cli = new ScriptedCli(Connectors);
        cli.Overrides["config show --section guardrail --format json"] = (0, ConfigWithPin(pinnedDigest), string.Empty);
        cli.Overrides["guardrail validate-pack " + Folder + " --json"] = (
            0,
            "{\"valid\": true, \"summary\": {\"rule_count\": 31, \"enabled_rule_count\": 29, \"rule_file_count\": 8, \"digest\": \"" + new string('c', 64) + "\", \"files_digest\": \"" + filesDigest + "\"}}",
            string.Empty);
        var ran = new List<string>();
        var vm = new PolicyModelViewModel(services, SevenViewPolicyBackend.Instance, new FixtureData()) { RunCli = cli.Run };
        vm.Review.RunStep = (executable, argv, _) =>
        {
            ran.Add(executable + " " + string.Join(' ', argv));
            return Task.FromResult(Result(0, argv, "{\"ok\": true}"));
        };
        await vm.InitializeAsync();
        vm.SelectedNav = vm.Nav.Single(n => n.View == "packs");
        vm.SelectedRow = vm.Rows.Single(r => r.Key == "codex");
        return (vm, cli, ran);
    }

    private static PolicyActionViewModel? Repin(PolicyModelViewModel vm) =>
        vm.ActionGroups.SelectMany(g => g.Actions).SingleOrDefault(a => a.Title == "Re-pin edited pack");

    [Fact]
    public async Task On_1_0_4_an_edited_pack_is_re_pinned_through_the_review_with_both_digests_and_runs_only_after_confirmation()
    {
        var (vm, cli, ran) = await OpenAsync("1.0.4", Old, Edited);
        var action = Repin(vm);
        Assert.NotNull(action);
        Assert.True(action!.IsEnabled);

        await vm.RunActionCommand.ExecuteAsync(action);

        Assert.Contains("guardrail validate-pack " + Folder + " --json", cli.Calls);
        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.Equal("defenseclaw guardrail use-pack " + Folder + " --connector codex --repin --yes --json", review.CommandText);
        Assert.Contains("sha256:" + Old, review.Summary, StringComparison.Ordinal);
        Assert.Contains("sha256:" + Edited, review.Summary, StringComparison.Ordinal);
        Assert.Empty(ran);
        Assert.DoesNotContain(cli.Calls, c => c.Contains("--repin", StringComparison.Ordinal));

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "defenseclaw guardrail use-pack " + Folder + " --connector codex --repin --yes --json" }, ran);
    }

    [Fact]
    public async Task A_pin_that_still_matches_opens_no_review_and_runs_nothing()
    {
        var (vm, _, ran) = await OpenAsync("1.0.4", Edited, Edited);

        await vm.RunActionCommand.ExecuteAsync(Repin(vm));

        Assert.False(vm.Review.IsOpen);
        Assert.Equal("Re-pin withheld", vm.NoticeTitle);
        Assert.Empty(ran);
    }

    [Theory]
    [InlineData("1.0.3")]
    [InlineData("1.0.0")]
    public async Task On_an_older_runtime_the_choice_is_not_there(string version)
    {
        var (vm, _, _) = await OpenAsync(version, Old, Edited);

        Assert.Null(Repin(vm));
    }

    [Fact]
    public async Task While_an_upgrade_runs_the_re_pin_is_off_with_the_upgrades_reason()
    {
        var (vm, _, ran) = await OpenAsync("1.0.4", Old, Edited);
        var action = Repin(vm)!;

        var scope = _services[^1].Cli.EnterUpgradeRun();
        try
        {
            Assert.Equal(CliRunner.UpgradeInProgressReason, vm.ChangesBlockedReason);
            await vm.RunActionCommand.ExecuteAsync(action);
        }
        finally
        {
            scope.Dispose();
        }

        Assert.False(vm.Review.IsOpen);
        Assert.Empty(ran);
    }
}
