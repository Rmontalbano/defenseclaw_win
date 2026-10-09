using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// What the review page says that the command line does not (CUST-269): that <c>setup llm --ping</c> sends a request to the provider, that
/// <c>--insecure-skip-verify</c> turns certificate checking off, that <c>guardrail disable</c> tears the hooks down. Judged on the argv, which is
/// what runs.
/// </summary>
public class WizardCautionsTests
{
    private static string[] Titles(params string[] argv) => WizardCautions.For(argv).Select(w => w.Title).ToArray();

    [Fact]
    public void Ping_is_a_reviewed_request_to_the_provider_and_the_bar_says_what_is_sent()
    {
        var warning = Assert.Single(WizardCautions.For(new[] { "setup", "llm", "--ping", "--non-interactive" }));

        Assert.Equal(WizardCautions.PingTitle, warning.Title);
        Assert.Contains("one request", warning.Message, StringComparison.Ordinal);
        Assert.Contains("\"ping\"", warning.Message, StringComparison.Ordinal);
        Assert.Contains("one token", warning.Message, StringComparison.Ordinal);
        Assert.Contains("saved whether or not", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup", "llm", "--no-ping", "--non-interactive")]
    [InlineData("setup", "llm", "--non-interactive")]
    [InlineData("setup", "guardrail", "--ping")]
    [InlineData("setup", "claude-code", "--ping")]
    public void Ping_is_flagged_only_when_setup_llm_is_asked_to_send_it(params string[] argv)
    {
        Assert.DoesNotContain(WizardCautions.For(argv), w => w.Title == WizardCautions.PingTitle);
    }

    [Fact]
    public void A_model_named_like_the_flag_is_a_model_and_not_a_ping()
    {
        // The value of --model is whatever follows it; the CLI never reads it as an option.
        Assert.Empty(Titles("setup", "llm", "--model", "--ping", "--non-interactive"));
    }

    [Fact]
    public void Nothing_after_the_double_dash_is_a_flag()
    {
        Assert.Empty(Titles("setup", "llm", "--non-interactive", "--", "--ping"));
    }

    [Theory]
    [InlineData("setup", "llm", "--insecure-skip-verify", "--non-interactive")]
    [InlineData("setup", "guardrail", "--judge-insecure-skip-verify", "--non-interactive")]
    [InlineData("setup", "provider", "add", "--name", "lab", "--insecure-skip-verify")]
    public void Skipping_certificate_checks_is_said_in_plain_words(params string[] argv)
    {
        var warning = Assert.Single(WizardCautions.For(argv));

        Assert.Equal(WizardCautions.InsecureTlsTitle, warning.Title);
        Assert.Contains("will not check this endpoint's certificate", warning.Message, StringComparison.Ordinal);
        Assert.Contains("lab use only", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Giving_the_cli_a_ca_bundle_is_not_a_warning()
    {
        Assert.Empty(Titles("setup", "llm", "--tls-ca-cert-file", @"C:\certs\ca.pem", "--non-interactive"));
    }

    [Fact]
    public void Both_bars_can_be_on_one_review_whichever_switch_comes_first()
    {
        Assert.Equal(
            new[] { WizardCautions.PingTitle, WizardCautions.InsecureTlsTitle },
            Titles("setup", "llm", "--ping", "--insecure-skip-verify", "--non-interactive"));

        // The order the LLM wizard builds it in: the TLS page comes before the apply page.
        Assert.Equal(
            new[] { WizardCautions.PingTitle, WizardCautions.InsecureTlsTitle },
            Titles("setup", "llm", "--tls-ca-cert-file", @"C:\certs\ca.pem", "--insecure-skip-verify", "--ping", "--non-interactive"));
    }

    [Fact]
    public void Only_turning_the_guardrail_off_is_floored_at_destructive()
    {
        Assert.Equal(CommandTier.Destructive, WizardCautions.Floor(new[] { "guardrail", "disable", "--yes" }));
        Assert.Null(WizardCautions.Floor(new[] { "guardrail", "enable", "--yes" }));
        Assert.Null(WizardCautions.Floor(new[] { "setup", "llm", "--ping" }));
    }
}

/// <summary>
/// The review and the installation guard for the new actions (CUST-269, on CUST-308): the LLM wizard's ping and the guardrail wizard's Disable are
/// changes (or reach out), so on a managed or invalid installation their Execute is off with the installation's own sentence, nothing is started,
/// and the runner records a refusal if a run reaches it anyway. Reading a model catalogue and choosing a goal change nothing and stay on.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class WizardInstallationGuardTests : IDisposable
{
    private const string LlmConfig = "llm:\n  provider: openai\n  model: orbit-5\n  api_key_env: OPENAI_API_KEY\n";

    private const string GuardrailConfig = "guardrail:\n  connector: claudecode\n  mode: observe\n";

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();
    private readonly List<WizardViewModel> _viewModels = new();

    public void Dispose()
    {
        UiThread.Run(() =>
        {
            foreach (var vm in _viewModels)
            {
                vm.Dispose();
            }
        });

        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private (WizardViewModel Vm, AppServices Services) Open(WizardDefinition definition, string config, bool managed)
    {
        var services = TestServices.Create(_temp, config, installation: managed ? TestInstallations.ManagedAt(_temp.Path) : null);
        _services.Add(services);
        var vm = UiThread.Run(() => new WizardViewModel(services, definition));
        _viewModels.Add(vm);
        return (vm, services);
    }

    private static void ToReview(WizardViewModel vm)
    {
        UiThread.Run(() =>
        {
            for (var i = 0; i < 12 && !vm.IsReview; i++)
            {
                vm.Next();
                Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
            }

            Assert.True(vm.IsReview);
        });
    }

    private static WizardFieldViewModel Field(WizardViewModel vm, string id) => vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == id);

    // ------------------------------------------------------------------ the llm wizard's ping

    [Fact]
    public async Task A_ping_on_a_writable_installation_is_reviewed_with_its_bar_and_can_be_executed()
    {
        var (vm, _) = Open(await CatalogHelp.RealAsync("llm"), LlmConfig, managed: false);
        UiThread.Run(() => Assert.True(vm.SelectGoal("test")));
        ToReview(vm);

        UiThread.Run(() =>
        {
            Assert.Equal(new[] { "setup", "llm", "--ping", "--non-interactive" }, vm.CommandReview!.Steps[0].Argv);
            Assert.Contains(vm.CommandReview.Warnings, w => w.Title == WizardCautions.PingTitle);
            Assert.True(vm.CanExecute);
            Assert.Null(vm.InstallationBlockedReason);
        });
    }

    [Fact]
    public async Task A_ping_on_a_managed_installation_cannot_be_executed_and_nothing_is_run_or_recorded()
    {
        var (vm, services) = Open(await CatalogHelp.RealAsync("llm"), LlmConfig, managed: true);
        UiThread.Run(() => Assert.True(vm.SelectGoal("test")));
        ToReview(vm);

        UiThread.Run(() =>
        {
            Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
            Assert.False(vm.CanExecute);
            Assert.Contains(vm.CommandReview!.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
            Assert.Contains(vm.CommandReview.Warnings, w => w.Title == WizardCautions.PingTitle);

            vm.ExecuteCommand.Execute(null);
            Assert.False(vm.IsRunning);
            Assert.False(vm.HasRun);
            Assert.Empty(services.Cli.Activity);
        });
    }

    [Fact]
    public async Task The_runner_refuses_and_records_a_ping_that_reaches_it_on_a_managed_installation()
    {
        var (_, services) = Open(await CatalogHelp.RealAsync("llm"), LlmConfig, managed: true);

        var invocation = await services.Cli.RunNamedAsync("defenseclaw", new[] { "setup", "llm", "--ping", "--non-interactive" });

        Assert.StartsWith(CliRunner.RefusedPrefix, invocation.FailureReason, StringComparison.Ordinal);
        Assert.Null(invocation.ExitCode);
        Assert.Single(services.Cli.Activity);
        Assert.Contains(TestInstallations.ManagedReason, invocation.FailureReason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the guardrail wizard's disable branch

    [Fact]
    public async Task Turning_the_guardrail_off_on_a_managed_installation_cannot_be_executed_and_the_runner_would_refuse_it()
    {
        var (vm, services) = Open(await CatalogHelp.RealAsync("guardrail"), GuardrailConfig, managed: true);
        UiThread.Run(() => Assert.True(vm.SelectGoal("disable")));
        ToReview(vm);

        UiThread.Run(() =>
        {
            Assert.Equal(new[] { "guardrail", "disable", "--yes" }, vm.CommandReview!.Steps[0].Argv);
            Assert.Equal(CommandTier.Destructive, vm.CommandReview.Tier);
            Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
            Assert.False(vm.CanExecute);

            vm.ExecuteCommand.Execute(null);
            Assert.False(vm.HasRun);
            Assert.Empty(services.Cli.Activity);
        });

        var refused = await services.Cli.RunNamedAsync("defenseclaw", new[] { "guardrail", "disable", "--yes" });
        Assert.StartsWith(CliRunner.RefusedPrefix, refused.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_installation_turning_read_only_while_the_disable_review_is_open_turns_its_execute_off()
    {
        var (vm, services) = Open(await CatalogHelp.RealAsync("guardrail"), GuardrailConfig, managed: false);
        UiThread.Run(() => Assert.True(vm.SelectGoal("disable")));
        ToReview(vm);
        UiThread.Run(() => Assert.True(vm.CanExecute));

        UiThread.Run(() => services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path)));

        UiThread.Run(() =>
        {
            Assert.False(vm.CanExecute);
            Assert.True(vm.CommandReview!.IsBlocked);
            Assert.Equal(TestInstallations.ManagedReason, vm.CommandReview.BlockedReason);
        });
    }

    // ------------------------------------------------------------------ what stays on

    [Fact]
    public async Task The_model_catalogue_and_the_goal_choice_work_on_a_managed_installation_because_they_change_nothing()
    {
        var (vm, services) = Open(await CatalogHelp.RealAsync("llm"), LlmConfig, managed: true);
        var catalogue = ModelCatalogue.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "llm", "model-catalog.synthetic.json")))!;

        UiThread.Run(() =>
        {
            vm.UseModelCatalogue(catalogue);
            Assert.True(vm.SelectGoal("main"));
        });

        var model = Field(vm, "model");
        Assert.True(model.ShowsModelPicker);

        // The box starts at the stored model, so the list is narrowed to what matches it, best match first.
        Assert.Equal(new[] { "orbit-5", "orbit-5-mini" }, model.ModelRows.Select(r => r.Value).ToArray());
        Assert.Empty(services.Cli.Activity);
    }
}
