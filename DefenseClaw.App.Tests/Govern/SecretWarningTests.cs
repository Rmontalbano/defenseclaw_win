using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// A value typed into the MCP, plugin, registry or wizard forms is not known to the CLI runner, so nothing refuses
/// it on the command line. When it looks like a secret the review says so, prominently and without repeating it
/// (CUST-197 / D3-04); and a plugin target that would be resolved against the CLI's working directory is refused
/// (D3-11). Nothing runs here: the confirm step is the only thing that would start a process.
/// Every token-shaped literal is assembled from two halves and is not a real credential.
/// </summary>
public sealed class SecretWarningTests : IDisposable
{
    private const string SlackWebhook = "https://hooks.slack" + ".com/services/T00000000/B00000000/abcdefghijklmnopqrstuvwx";
    private const string GitHubToken = "ghp" + "_0123456789abcdefghijklmnopqrstuvwxyz";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public SecretWarningTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static CommandReviewWarning? SecretBar(GovernPanelViewModelBase vm) =>
        vm.ConfirmReview?.Warnings.SingleOrDefault(w => w.Title == SecretFieldWarnings.Title);

    private static IReadOnlyList<CommandReviewWarning> SecretBars(GovernPanelViewModelBase vm) =>
        vm.ConfirmReview?.Warnings.Where(w => w.Title == SecretFieldWarnings.Title).ToArray() ?? Array.Empty<CommandReviewWarning>();

    private McpsPanelViewModel Mcp(string name = "srv", string command = "", string url = "", string args = "", string env = "")
    {
        var vm = new McpsPanelViewModel(_services)
        {
            SetName = name,
            SetCommand = command,
            SetUrl = url,
            SetArgs = args,
            SetEnv = env,
        };
        vm.SubmitSetFormCommand.Execute(null);
        return vm;
    }

    // ------------------------------------------------------------------ MCP

    [Fact]
    public void A_secret_in_an_mcp_env_value_gets_a_warning_that_names_the_kind_and_not_the_value()
    {
        var vm = Mcp(command: "node", env: "API_KEY=abcd1234\nLOG_LEVEL=debug");

        Assert.True(vm.IsConfirmOpen);
        var bar = Assert.IsType<CommandReviewWarning>(SecretBar(vm));
        Assert.Contains("An environment value looks like it carries a secret", bar.Message, StringComparison.Ordinal);
        Assert.Contains("API_KEY", bar.Message, StringComparison.Ordinal);
        Assert.Contains("visible on the command line", bar.Message, StringComparison.Ordinal);
        Assert.Contains("Activity", bar.Message, StringComparison.Ordinal);
        Assert.Contains("exported log", bar.Message, StringComparison.Ordinal);
        Assert.Contains("no other way", bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcd1234", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_secret_env_values_share_one_warning()
    {
        var vm = Mcp(command: "node", env: "API_KEY=abcd1234\nDB_PASSWORD=hunter22\nLOG_LEVEL=debug");

        var bar = Assert.IsType<CommandReviewWarning>(SecretBar(vm));
        Assert.Contains("Environment values look like they carry a secret", bar.Message, StringComparison.Ordinal);
        Assert.Contains("API_KEY", bar.Message, StringComparison.Ordinal);
        Assert.Contains("DB_PASSWORD", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_values_that_are_not_secrets_add_no_warning_but_keep_the_existing_note()
    {
        var vm = Mcp(command: "node", env: "LOG_LEVEL=debug\nNODE_ENV=production\nAPI_KEY=$MY_KEY");

        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(SecretBars(vm));
        Assert.Contains("Environment values are part of this command line", vm.ConfirmNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_credential_in_an_mcp_url_gets_a_warning()
    {
        var vm = Mcp(url: "https://user:hunter2@mcp.example.test/sse");

        var bar = Assert.IsType<CommandReviewWarning>(SecretBar(vm));
        Assert.Contains("The URL", bar.Message, StringComparison.Ordinal);
        Assert.Contains("password inside a URL", bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_webhook_style_mcp_url_gets_a_warning()
    {
        var vm = Mcp(url: SlackWebhook);

        var bar = Assert.IsType<CommandReviewWarning>(SecretBar(vm));
        Assert.Contains("Slack webhook", bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwx", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_mcp_url_gets_no_warning()
    {
        var vm = Mcp(url: "https://mcp.deepwiki.com/mcp");

        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(SecretBars(vm));
    }

    [Theory]
    [InlineData("-y\n@example/docs-mcp\n--api-key=abcdef12", "An argument")]
    [InlineData("--api-key\nabcdef1234", "An argument")]
    [InlineData("[\"--token\", \"abcdef123456\"]", "An argument")]
    public void A_secret_among_the_mcp_arguments_gets_a_warning_however_the_arguments_were_entered(string args, string field)
    {
        var vm = Mcp(command: "npx", args: args);

        var bar = Assert.IsType<CommandReviewWarning>(SecretBar(vm));
        Assert.Contains(field, bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_mcp_arguments_get_no_warning()
    {
        var vm = Mcp(command: "npx", args: "-y\n@modelcontextprotocol/server-filesystem\nC:\\work\n--auth-mode\noauth");

        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(SecretBars(vm));
    }

    [Fact]
    public void The_secret_warning_is_added_to_the_review_and_the_command_is_unchanged()
    {
        var vm = Mcp(command: "node", env: "API_KEY=abcd1234");

        Assert.Equal(
            new[] { "mcp", "set", "--command", "node", "--env", "API_KEY=abcd1234", "--", "srv" },
            vm.ConfirmReview!.Steps.Single().Argv);
        Assert.Contains(vm.ConfirmReview.Warnings, w => w.Title == SecretFieldWarnings.Title);
    }

    // ------------------------------------------------------------------ plugins

    private PluginsPanelViewModel Plugin(string target, bool force = false)
    {
        var vm = new PluginsPanelViewModel(_services) { InstallNameOrPath = target, InstallForce = force };
        vm.SubmitInstallFormCommand.Execute(null);
        return vm;
    }

    [Theory]
    [InlineData("https://ci:hunter2@example.test/plugin.tgz")]
    [InlineData("https://example.test/plugin.tgz?token=abcdef123456")]
    [InlineData("https://example.test/download?api_key=abcd1234")]
    [InlineData(GitHubToken)] // spelled like a registry name, but it is a token: judged by what it looks like
    public void A_plugin_source_that_carries_a_secret_gets_a_warning_that_says_what_to_do_instead(string target)
    {
        var vm = Plugin(target);

        Assert.True(vm.IsConfirmOpen, vm.InstallFormError);
        var bar = Assert.IsType<CommandReviewWarning>(SecretBar(vm));
        Assert.Contains("The install source", bar.Message, StringComparison.Ordinal);
        Assert.Contains("Download and extract the archive yourself", bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef123456", bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_source_with_no_secret_gets_no_warning()
    {
        var vm = Plugin("https://example.test/plugin.tgz");

        Assert.True(vm.IsConfirmOpen);
        Assert.Empty(SecretBars(vm));
    }

    [Theory]
    [InlineData("https://example.test/plugin.tgz")]
    [InlineData("http://example.test/plugin.tgz")]
    [InlineData("clawhub://example/pkg")]
    [InlineData("clawhub://voice-call@1.2.0")]
    [InlineData("@openclasw/voice-call")]
    [InlineData("voice-call")]
    [InlineData("voice-call@1.2.0")]
    [InlineData("@scope/pkg@latest")]
    [InlineData("C:\\plugins\\my-plugin")]
    [InlineData("C:/plugins/my-plugin")]
    [InlineData("\\\\server\\share\\plugins\\my-plugin")]
    public void A_url_a_registry_name_or_an_absolute_folder_is_accepted(string target)
    {
        Assert.Null(PluginsPanelViewModel.InstallTargetProblem(target));

        var vm = Plugin(target);

        Assert.True(vm.IsConfirmOpen, vm.InstallFormError);
        Assert.False(vm.HasInstallFormError);
    }

    [Theory]
    [InlineData("./local-plugin")]
    [InlineData(".\\local-plugin")]
    [InlineData("../local-plugin")]
    [InlineData("plugins\\mine")]
    [InlineData("plugins/mine")]
    [InlineData("~/plugins/mine")]
    [InlineData("~\\plugins\\mine")]
    [InlineData("\\plugins\\mine")]
    [InlineData("/plugins/mine")]
    [InlineData("C:plugins")]
    [InlineData("HTTPS://example.test/plugin.tgz")]
    [InlineData("two words")]
    public void A_relative_or_drive_relative_folder_is_refused_because_it_would_resolve_against_the_clis_own_directory(string target)
    {
        Assert.NotNull(PluginsPanelViewModel.InstallTargetProblem(target));

        var vm = Plugin(target);

        Assert.False(vm.IsConfirmOpen);
        Assert.True(vm.HasInstallFormError);
        Assert.Contains("absolute folder path", vm.InstallFormError, StringComparison.Ordinal);
        Assert.Contains("working directory", vm.InstallFormError, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absolute_folder_installs_with_the_path_after_the_double_dash()
    {
        var folder = Path.Combine(_temp.Path, "local-plugin");

        var vm = Plugin(folder);

        Assert.True(vm.IsConfirmOpen);
        Assert.Equal(new[] { "plugin", "install", "--", folder }, vm.ConfirmReview!.Steps.Single().Argv);
        Assert.Empty(SecretBars(vm));
    }

    // ------------------------------------------------------------------ registries

    [Theory]
    [InlineData("https://user:hunter2@registry.example.test/catalog.yaml")]
    [InlineData("https://registry.example.test/catalog.yaml?token=abc")]
    [InlineData("https://registry.example.test/catalog.yaml?api_key=abcd1234")]
    [InlineData(SlackWebhook)]
    public void A_registry_address_that_carries_a_credential_is_refused_and_points_at_the_env_route(string url)
    {
        var vm = new RegistriesPanelViewModel(_services)
        {
            AddId = "my-source",
            AddKind = "http_yaml",
            AddUrl = url,
        };

        vm.SubmitAddCommand.Execute(null);

        Assert.NotNull(vm.AddValidationMessage);
        Assert.Contains("carries a credential", vm.AddValidationMessage, StringComparison.Ordinal);
        Assert.Contains("environment variable", vm.AddValidationMessage, StringComparison.Ordinal);
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public void A_registry_address_without_a_credential_goes_to_review()
    {
        var vm = new RegistriesPanelViewModel(_services)
        {
            AddId = "my-source",
            AddKind = "http_yaml",
            AddUrl = "https://registry.example.test/catalog.yaml",
        };

        vm.SubmitAddCommand.Execute(null);

        Assert.Null(vm.AddValidationMessage);
        Assert.True(vm.Review.IsOpen);
    }

    // ------------------------------------------------------------------ wizards (argv-level)

    [Fact]
    public void The_wizard_warning_names_the_option_and_never_the_value()
    {
        var argv = new[] { "setup", "webhook", "add", "slack", "--url", SlackWebhook, "--min-severity", "HIGH" };

        var bar = WizardReview.SecretValueWarning(argv);

        Assert.NotNull(bar);
        Assert.Equal(SecretFieldWarnings.Title, bar.Title);
        Assert.Contains("The value of --url looks like it carries a secret", bar.Message, StringComparison.Ordinal);
        Assert.Contains("Slack webhook", bar.Message, StringComparison.Ordinal);
        Assert.Contains("config.yaml", bar.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwx", bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wizard_warning_covers_several_options_in_one_bar()
    {
        var argv = new[] { "setup", "x", "--url", SlackWebhook, "--backup-url", "https://u:p4ssw0rd@example.test/" };

        var bar = WizardReview.SecretValueWarning(argv);

        Assert.NotNull(bar);
        Assert.Contains("The values of --url, --backup-url look like they carry a secret", bar.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("setup", "webhook", "add", "slack", "--url", "https://hooks.example.test/incoming", "--name", "chat")]
    [InlineData("setup", "llm", "--api-key-env", "MY_LLM_KEY", "--base-url", "https://api.example.test/v1")]
    [InlineData("setup", "guardrail", "--mode", "action", "--dry-run")]
    [InlineData("setup", "webhook", "add", "pagerduty", "--secret-env", "DEFENSECLAW_PD_KEY")]
    public void An_ordinary_wizard_command_gets_no_secret_warning(params string[] argv) =>
        Assert.Null(WizardReview.SecretValueWarning(argv));

    [Fact]
    public void The_wizard_review_carries_the_warning_through_the_view_model()
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            var definition = new WizardDefinition
            {
                Target = "webhook",
                Title = "Webhook",
                Group = WizardGroups.Other,
                Steps = new[]
                {
                    new WizardStep
                    {
                        Id = "main",
                        Title = "Destination",
                        Fields = new[] { new WizardField { Id = "url", Label = "URL", Kind = WizardFieldKind.Text, Flag = "--url" } },
                    },
                },
                FinalArgvBuilder = (_, _) => new[] { "setup", "webhook", "add", "slack", "--url", SlackWebhook },
            };
            using var vm = new WizardViewModel(services, definition);

            while (!vm.IsReview)
            {
                vm.Next();
            }

            var warnings = vm.CommandReview!.Warnings;
            var bar = Assert.Single(warnings, w => w.Title == SecretFieldWarnings.Title);
            Assert.Contains("--url", bar.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdefghijklmnopqrstuvwx", bar.Message, StringComparison.Ordinal);
        });
    }
}
