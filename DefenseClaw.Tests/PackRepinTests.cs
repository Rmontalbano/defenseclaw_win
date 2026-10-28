using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// "Re-pin edited pack" (runtime 1.0.4, <c>guardrail use-pack PACK --repin --yes --json</c>; cmd_guardrail.py at tag 1.0.4): the argv and the guard
/// that lets only that whole shape through, when the action is offered, what the review states (old and new digest), and the capability that
/// gates it. The digests are synthetic.
/// </summary>
public sealed class PackRepinTests
{
    private const string Folder = @"C:\Users\operator\.defenseclaw\policies\guardrail\protected-codex\strict";
    private static readonly string Old = new('a', 64);
    private static readonly string Edited = new('b', 64);

    private sealed class NoDataFiles : IPolicyDataFiles
    {
        public IReadOnlyList<ToolChain> ReadToolChains(IReadOnlyList<NamedPolicy> policies) => Array.Empty<ToolChain>();

        public IReadOnlyList<RuleFamily> ReadRuleFamilies(string packFolder, IReadOnlyList<NamedPolicy> policies) => Array.Empty<RuleFamily>();

        public string? ReadPackBase(string packFolder) => null;
    }

    private static async Task<PolicyModel> ModelAsync(bool supported, string? pinFolder = Folder, string? digest = null)
    {
        var read = await new PolicyModelReader(PolicyModelFixtures.Runner(PolicyModelFixtures.Connectors), new NoDataFiles()).ReadAsync();
        var pins = pinFolder is null ? Array.Empty<PackPin>() : new[] { new PackPin("protected-codex", pinFolder, "sha256:" + (digest ?? Old)) };
        return new PolicyModel(read.Catalog with { PackPins = pins }) { RepinSupported = supported };
    }

    private static PolicyAction? Repin(PolicyModel model, string scope) =>
        model.Actions("packs", scope).SingleOrDefault(a => a.Kind == PolicyActionKind.RepinPack);

    private static PackValidation Valid(string filesDigest) => new("valid", RuleCount: 10, EnabledRuleCount: 9, RuleFileCount: 2, Digest: new string('c', 64), FilesDigest: filesDigest);

    // ---- the command -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_argv_is_the_whole_reviewed_shape_with_yes_and_json()
    {
        Assert.Equal(
            new[] { "guardrail", "use-pack", Folder, "--connector", "codex", "--repin", "--yes", "--json" },
            PolicyIntents.RepinPack(Folder, "codex").Args);
        Assert.Equal(
            new[] { "guardrail", "use-pack", Folder, "--repin", "--yes", "--json" },
            PolicyIntents.RepinPack(Folder).Args);
        Assert.Throws<ArgumentException>(() => PolicyIntents.RepinPack("relative\\pack"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.RepinPack("--repin"));
        Assert.Throws<ArgumentException>(() => PolicyIntents.RepinPack(Folder, "co dex"));
    }

    [Fact]
    public void The_guard_allows_only_the_whole_repin_shape()
    {
        Assert.True(PolicyActionGuard.IsAllowedChange(PolicyIntents.RepinPack(Folder, "codex").Args));
        Assert.True(PolicyActionGuard.IsAllowedChange(PolicyIntents.RepinPack(Folder).Args));

        // --json without --yes is refused by the CLI (exit 2) and without --json prints no result; a prompt would wait on a terminal.
        Assert.False(PolicyActionGuard.IsAllowedChange(new[] { "guardrail", "use-pack", Folder, "--repin" }));
        Assert.False(PolicyActionGuard.IsAllowedChange(new[] { "guardrail", "use-pack", Folder, "--repin", "--json" }));
        Assert.False(PolicyActionGuard.IsAllowedChange(new[] { "guardrail", "use-pack", Folder, "--repin", "--yes" }));
        Assert.False(PolicyActionGuard.IsAllowedChange(new[] { "guardrail", "use-pack", Folder, "--yes", "--repin", "--json" }));
        Assert.False(PolicyActionGuard.IsAllowedChange(new[] { "guardrail", "use-pack", Folder, "--repin", "--yes", "--json", "--no-validate" }));
        Assert.False(PolicyActionGuard.IsAllowedChange(new[] { "guardrail", "use-pack", "strict", "--repin", "--yes", "--json" }));
        Assert.False(PolicyActionGuard.IsAllowedChange(new[] { "guardrail", "use-pack", Folder, "--connector", "--repin", "--yes", "--json" }));
    }

    // ---- when it is offered ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task It_is_offered_for_the_custom_pack_a_scope_uses_when_the_runtime_has_it_and_the_pack_is_pinned()
    {
        var model = await ModelAsync(supported: true);

        var action = Repin(model, "codex");
        Assert.NotNull(action);
        Assert.Equal("Re-pin edited pack", action!.Title);
        Assert.False(action.IsRead);
        Assert.False(action.IsCurrent);
        Assert.Equal(Folder, action.PackFolder);
        Assert.Equal("sha256:" + Old, PackPin.Spelled(action.PinnedDigest));
        Assert.Equal(new[] { "guardrail", "use-pack", Folder, "--connector", "codex", "--repin", "--yes", "--json" }, action.Argv);
        Assert.True(PolicyActionGuard.IsAllowedChange(action.Argv));
    }

    [Fact]
    public async Task It_is_not_offered_on_a_runtime_without_it_so_older_runtimes_look_exactly_as_before()
    {
        var supported = await ModelAsync(supported: true);
        var older = await ModelAsync(supported: false);

        Assert.Null(Repin(older, "codex"));
        Assert.Equal(
            older.Actions("packs", "codex").Select(a => a.Id),
            supported.Actions("packs", "codex").Where(a => a.Kind != PolicyActionKind.RepinPack).Select(a => a.Id));
    }

    [Fact]
    public async Task It_is_not_offered_for_a_preset_or_a_pack_nothing_pins()
    {
        Assert.Null(Repin(await ModelAsync(supported: true, pinFolder: null), "codex"));
        Assert.Null(Repin(await ModelAsync(supported: true, pinFolder: @"C:\elsewhere\other-pack"), "codex"));

        // the global scope uses the default preset
        Assert.Null(Repin(await ModelAsync(supported: true), "global"));
    }

    [Fact]
    public async Task The_pin_is_found_whatever_the_slash_or_case_config_yaml_spells_the_folder_with()
    {
        var model = await ModelAsync(supported: true, pinFolder: Folder.Replace('\\', '/').ToUpperInvariant() + "/");
        Assert.NotNull(Repin(model, "codex"));
    }

    // ---- the review -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_review_states_the_old_and_the_new_digest()
    {
        var action = Repin(await ModelAsync(supported: true), "codex")!;

        var decision = PackRepin.Decide(action, Valid(Edited));

        Assert.True(decision.CanReview);
        var review = decision.Consequence!;
        Assert.Contains("sha256:" + Old, review.Summary, StringComparison.Ordinal);
        Assert.Contains("sha256:" + Edited, review.Summary, StringComparison.Ordinal);
        Assert.Contains("Pinned now: sha256:" + Old, review.Details);
        Assert.Contains("The pack now: sha256:" + Edited, review.Details);
        Assert.Contains(review.Details, d => d.Contains("rule files are not touched", StringComparison.Ordinal));
        Assert.Equal("Re-pin edited pack", review.ConfirmLabel);
    }

    [Fact]
    public async Task A_pin_that_already_matches_is_not_re_pinned_and_nothing_is_reviewed()
    {
        var action = Repin(await ModelAsync(supported: true, digest: Edited), "codex")!;

        var decision = PackRepin.Decide(action, Valid(Edited));

        Assert.False(decision.CanReview);
        Assert.Contains("nothing to re-pin", decision.Problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_invalid_pack_or_a_validator_that_does_not_report_the_files_digest_is_withheld()
    {
        var action = Repin(await ModelAsync(supported: true), "codex")!;

        Assert.False(PackRepin.Decide(action, new PackValidation("invalid", "bad rule file")).CanReview);
        Assert.False(PackRepin.Decide(action, new PackValidation("unavailable", "gateway missing")).CanReview);
        var noDigest = PackRepin.Decide(action, Valid(string.Empty));
        Assert.False(noDigest.CanReview);
        Assert.Contains("digest", noDigest.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_reads_the_files_digest_apart_from_the_pack_digest()
    {
        var json = "{\"version\":1,\"valid\":true,\"summary\":{\"rule_count\":3,\"enabled_rule_count\":3,\"rule_file_count\":1,\"digest\":\"" + new string('c', 64) + "\",\"files_digest\":\"" + Edited + "\"}}";

        var parsed = PolicyCatalogJson.ParseValidation(0, json);

        Assert.True(parsed.IsValid);
        Assert.Equal(new string('c', 64), parsed.Digest);
        Assert.Equal(Edited, parsed.FilesDigest);
    }

    [Fact]
    public void The_pins_are_read_from_the_guardrail_section()
    {
        var json = "{\"guardrail\":{\"mode\":\"action\",\"custom_packs\":{\"mine\":{\"path\":\"C:\\\\p\\\\mine\",\"digest\":\"sha256:" + Old + "\"},\"odd\":\"x\"}}}";

        Assert.True(PolicyCatalogJson.TryParseGuardrailSettings(json, out var settings, out _));

        var pin = Assert.Single(settings.CustomPacks!);
        Assert.Equal(new PackPin("mine", @"C:\p\mine", "sha256:" + Old), pin);
        Assert.True(PolicyCatalogJson.TryParseGuardrailSettings("""{"guardrail":{"mode":"action"}}""", out var none, out _));
        Assert.Empty(none.CustomPacks!);
    }

    // ---- the capability ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.0.4", true)]
    [InlineData("1.0.5", true)]
    [InlineData("1.1.0", true)]
    [InlineData("1.0.3", false)]
    [InlineData("1.0.0", false)]
    [InlineData("0.8.10", false)]
    [InlineData("nightly", false)]
    public void The_capability_follows_the_reported_version(string version, bool expected)
    {
        const string help = "Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n";
        var snapshot = RuntimeProbe.Evaluate(
            new RuntimeProbeScreens("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\"}", help, help, null, null, null, null, null),
            "test",
            "fp",
            DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, snapshot.Capabilities.Has(RuntimeCapability.GuardrailRepin));
        Assert.Equal(expected, RuntimeGate.Check(snapshot.Capabilities, RuntimeCapability.GuardrailRepin).IsAvailable);
    }

    [Fact]
    public void The_capability_is_named_and_described()
    {
        Assert.Contains(RuntimeCapability.GuardrailRepin, RuntimeCapabilityCatalog.All);
        Assert.False(string.IsNullOrWhiteSpace(RuntimeCapabilityCatalog.DisplayName(RuntimeCapability.GuardrailRepin)));
        Assert.Contains("1.0.4", RuntimeCapabilityCatalog.Marker(RuntimeCapability.GuardrailRepin), StringComparison.Ordinal);
    }
}
