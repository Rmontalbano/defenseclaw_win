using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.ClaudeCode;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The review of <c>uninstall</c> and <c>reset</c> says what the CLI says only after the fact: DefenseClaw cannot remove its Cowork plugin from the Claude
/// account, so until the user does, Cowork tool calls are blocked (CUST-445, runtime 1.0.8). The bar needs the capability, one of the two verbs and the
/// plugin state file; the check is a read-only look at one file. Synthetic data folders only.
/// </summary>
public sealed class CoworkUninstallReviewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static RuntimeProbeRunner Runtime(string version) => (args, _) => Task.FromResult(
        args.Contains("--version-json")
            ? RuntimeProbeOutput.Ok("{\"name\":\"defenseclaw-cli\",\"version\":\"" + version + "\",\"schema_version\":1}")
            : RuntimeProbeOutput.Ok("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\n\nCommands:\n  setup  Configure.\n"));

    private async Task<RuntimeCapabilities> CapabilitiesAsync(string version)
    {
        _services?.Dispose();
        _services = TestServices.Create(_temp, "guardrail:\n  enabled: true\n", Runtime(version));
        _ = await _services.Runtime.RefreshAsync(force: true);
        return _services.Runtime.Capabilities;
    }

    private string DataFolderWithPlugin(bool state)
    {
        var data = Path.Combine(_temp.Path, "data-" + (state ? "with" : "without"));
        _ = Directory.CreateDirectory(Path.Combine(data, CoworkProtectionFacts.StateFolderName));
        if (state)
        {
            File.WriteAllText(CoworkProtectionFacts.PluginStatePath(data), "{\"version\":\"1.0.20261101120000\"}");
        }

        return data;
    }

    [Theory]
    [InlineData("uninstall")]
    [InlineData("reset")]
    [InlineData("Uninstall")]
    public async Task Uninstall_and_reset_carry_the_remove_plugin_bar_when_the_plugin_state_exists(string verb)
    {
        var capabilities = await CapabilitiesAsync("1.0.8");

        var warnings = CoworkUninstallReview.Warnings(new[] { verb, "--yes" }, capabilities, DataFolderWithPlugin(state: true));

        var bar = Assert.Single(warnings);
        Assert.Equal("Remove the Cowork plugin in Claude Desktop", bar.Title);
        Assert.Equal(CoworkProtectionFacts.RemovePluginStep, bar.Message);
        Assert.Contains("Customize > Plugins", bar.Message, StringComparison.Ordinal);
        Assert.Contains("Cowork tool calls are blocked", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_bar_without_the_plugin_state_file()
    {
        var capabilities = await CapabilitiesAsync("1.0.8");

        Assert.Empty(CoworkUninstallReview.Warnings(new[] { "uninstall" }, capabilities, DataFolderWithPlugin(state: false)));
        Assert.Empty(CoworkUninstallReview.Warnings(new[] { "uninstall" }, capabilities, null));
        Assert.Empty(CoworkUninstallReview.Warnings(new[] { "uninstall" }, capabilities, Path.Combine(_temp.Path, "no-such-folder")));
    }

    [Theory]
    [InlineData("1.0.6")]
    [InlineData("1.0.7")]
    [InlineData("1.0.0")]
    public async Task A_runtime_before_1_0_8_gets_no_bar_even_with_the_state_file(string version)
    {
        var capabilities = await CapabilitiesAsync(version);

        Assert.Empty(CoworkUninstallReview.Warnings(new[] { "uninstall" }, capabilities, DataFolderWithPlugin(state: true)));
        Assert.Empty(CoworkUninstallReview.Warnings(new[] { "reset" }, capabilities, DataFolderWithPlugin(state: true)));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("setup")]
    [InlineData("doctor")]
    [InlineData("")]
    public async Task Any_other_verb_gets_no_bar(string verb)
    {
        var capabilities = await CapabilitiesAsync("1.0.8");

        Assert.Empty(CoworkUninstallReview.Warnings(new[] { verb }, capabilities, DataFolderWithPlugin(state: true)));
        Assert.Empty(CoworkUninstallReview.Warnings(Array.Empty<string>(), capabilities, DataFolderWithPlugin(state: true)));

        // The verb has to be the first one: "setup uninstall" is not an uninstall.
        Assert.Empty(CoworkUninstallReview.Warnings(new[] { "setup", "uninstall" }, capabilities, DataFolderWithPlugin(state: true)));
    }

    [Fact]
    public async Task The_state_check_can_be_supplied()
    {
        var capabilities = await CapabilitiesAsync("1.0.8");
        string? asked = null;

        var warnings = CoworkUninstallReview.Warnings(new[] { "reset" }, capabilities, "C:\\Users\\example\\.defenseclaw", folder =>
        {
            asked = folder;
            return true;
        });

        _ = Assert.Single(warnings);
        Assert.Equal("C:\\Users\\example\\.defenseclaw", asked);
    }

    [Fact]
    public void A_runtime_that_was_never_probed_gets_no_bar()
    {
        Assert.Empty(CoworkUninstallReview.Warnings(new[] { "uninstall" }, RuntimeCapabilities.Unknown, DataFolderWithPlugin(state: true), _ => true));
    }
}
