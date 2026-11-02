using DefenseClaw.App.Services;
using DefenseClaw.App.Services.FirstRun;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// OpenCode in the connector batch dialog on DefenseClaw 1.0.6 (CUST-425): it is a preview connector, so the review warns about it in its own words;
/// and when the batch is refused because the installed OpenCode is V1, the refusal's install guidance (a ~360-character line, <c>cmd_setup.py</c>
/// 6546 and 6514) reaches the review in full instead of being cut off mid-sentence. The CLI is a script that prints the refusal; nothing runs.
/// </summary>
public sealed class OpenCodeBatchTests : IDisposable
{
    private const string Roster = "guardrail:\n  connectors:\n    codex:\n      mode: observe\n";

    // 1.0.6's _opencode_v2_required(...) wrapped by ux.err's "  ✗ " prefix, then the ClickException (cmd_setup.py 6546-6549, 6577-6578).
    private const string Guidance =
        "Install OpenCode V2 with 'npm install -g @opencode/cli@2' (where you cannot write npm's global prefix, for your account: " +
        "'npm install -g --prefix ~/.npm-global @opencode/cli@2', with ~/.npm-global/bin first on PATH), then run: defenseclaw setup opencode.";

    private const string Refusal =
        "  ✗ OpenCode V2 is required; detected 1.4.2. This DefenseClaw guards OpenCode V2 only (V1 is not supported). " + Guidance;

    private const string Stopped = "Error: OpenCode setup stopped before any change; nothing was changed";

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

    private static readonly IReadOnlyList<OfferedConnector> Connectors = new[]
    {
        new OfferedConnector("codex", "Codex", "codex", PlatformStatus.Certified),
        new OfferedConnector("opencode", "OpenCode", "opencode", PlatformStatus.Preview),
        new OfferedConnector("cursor", "Cursor", "cursor", PlatformStatus.NotCertified),
    };

    private sealed class Script
    {
        public int Exit { get; set; }

        public string[] Output { get; set; } = Array.Empty<string>();

        public Task<CliInvocation> Step(string executable, IReadOnlyList<string> argv, CliRunOptions? options)
        {
            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            foreach (var line in Output)
            {
                InvocationFactory.Append(invocation, line);
            }

            InvocationFactory.Finish(invocation, Exit);
            return Task.FromResult(invocation);
        }
    }

    private async Task<(ConnectorBatchViewModel Vm, Script Cli)> OpenedDialog()
    {
        var services = TestServices.Create(_temp, configYaml: Roster);
        _services.Add(services);
        var cli = new Script();
        var help = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup.txt"));
        var vm = new ConnectorBatchViewModel(services, _ => Task.FromResult(new HelpProbeResult(help, null)), () => Connectors);
        vm.Review.RunStep = cli.Step;
        vm.Open();
        for (var i = 0; i < 100 && vm.IsChecking; i++)
        {
            await Task.Delay(10);
        }

        return (vm, cli);
    }

    [Fact]
    public async Task A_preview_connector_is_warned_about_as_a_preview_and_a_not_certified_one_as_not_certified()
    {
        var (vm, _) = await OpenedDialog();
        vm.Rows.Single(r => r.Id == "opencode").IsOn = true;
        vm.Rows.Single(r => r.Id == "cursor").IsOn = true;

        vm.ReviewChangesCommand.Execute(null);

        var warnings = vm.Review.CommandReview!.Warnings;
        var preview = Assert.Single(warnings, w => w.Title == "Preview on Windows");
        Assert.Contains("OpenCode", preview.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Cursor", preview.Message, StringComparison.Ordinal);
        var notCertified = Assert.Single(warnings, w => w.Title == "Not certified on Windows");
        Assert.Contains("Cursor", notCertified.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenCode", notCertified.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_preview_connector_alone_gets_only_the_preview_warning()
    {
        var (vm, _) = await OpenedDialog();
        vm.Rows.Single(r => r.Id == "opencode").IsOn = true;

        vm.ReviewChangesCommand.Execute(null);

        Assert.Contains(vm.Review.CommandReview!.Warnings, w => w.Title == "Preview on Windows");
        Assert.DoesNotContain(vm.Review.CommandReview.Warnings, w => w.Title == "Not certified on Windows");
    }

    [Fact]
    public async Task The_V1_refusal_keeps_its_install_guidance_and_the_stopped_line_in_the_review_output()
    {
        Assert.True(Refusal.Length > 300, "the refusal must be longer than the old 300-character cut for this test to mean anything");

        var (vm, cli) = await OpenedDialog();
        cli.Exit = 1;
        cli.Output = new[] { Refusal, Stopped };
        vm.Rows.Single(r => r.Id == "opencode").IsOn = true;
        vm.ReviewChangesCommand.Execute(null);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal("Bad", vm.ResultKey);
        var output = vm.Review.ResultOutput;
        Assert.NotNull(output);
        Assert.Contains("(V1 is not supported)", output, StringComparison.Ordinal);
        Assert.Contains("npm install -g --prefix ~/.npm-global @opencode/cli@2", output, StringComparison.Ordinal);
        Assert.Contains("then run: defenseclaw setup opencode.", output, StringComparison.Ordinal);
        Assert.Contains(Stopped, output, StringComparison.Ordinal);
        Assert.DoesNotContain("…", output, StringComparison.Ordinal);
    }
}
