using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The curated <c>setup provider add</c> pages (CUST-269): the base provider type picks which cloud's settings follow, the way the provider picks
/// them in the LLM wizard, and every other subcommand of the group keeps the pages the CLI's help gives it. Built through the catalog from the
/// help screens the installed 0.8.10 CLI prints (the list, remove and show screens are not needed: a screen the CLI would not answer contributes
/// no fields).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ProviderAddWizardTests : IDisposable
{
    private static readonly string[] CloudPages =
    {
        WizardPages.ProviderAddBedrock, WizardPages.ProviderAddVertex, WizardPages.ProviderAddAzure,
    };

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

    private static Task<WizardDefinition> Provider() =>
        CatalogHelp.RealAsync("provider", ("provider add", "setup-provider-add"));

    private static string[] CloudShown(WizardDefinition definition, WizardValues values) =>
        WizardAnswers.Pages(definition, values).Where(id => CloudPages.Contains(id)).ToArray();

    private static WizardValues Add(WizardDefinition definition, params (string Id, string Value)[] changes) =>
        WizardAnswers.Start(definition, new[] { ("subcommand", "add") }.Concat(changes).ToArray());

    // ------------------------------------------------------------------ the layout

    [Fact]
    public async Task Add_has_its_own_pages_and_the_other_subcommands_keep_the_pages_the_help_gives_them()
    {
        var definition = await Provider();

        Assert.Equal(
            new[]
            {
                "command", WizardPages.ProviderAddBasics, WizardPages.ProviderAddModels, WizardPages.ProviderAddBedrock, WizardPages.ProviderAddVertex,
                WizardPages.ProviderAddAzure, WizardPages.ProviderAddTls, "add:apply", "list:empty", "remove:empty", "show:empty",
            },
            definition.Steps.Select(s => s.Id).ToArray());

        Assert.Equal(
            new[] { "add:name", "add:base-provider-type", "add:base-url", "add:domain", "add:env-key" },
            definition.Steps.Single(s => s.Id == WizardPages.ProviderAddBasics).Fields.Select(f => f.Id).ToArray());
        Assert.Equal(
            new[] { "add:ca-cert-file", "add:insecure-skip-verify" },
            definition.Steps.Single(s => s.Id == WizardPages.ProviderAddTls).Fields.Select(f => f.Id).ToArray());
    }

    [Fact]
    public async Task Every_flag_of_add_is_on_a_page_and_the_name_is_required()
    {
        var definition = await Provider();
        var help = SetupHelpParser.Parse(CatalogHelp.Real("setup-provider-add"), commandDepth: 3);

        var onPages = definition.AllFields.Where(f => f.Id.StartsWith("add:", StringComparison.Ordinal)).Select(f => f.Flag).ToHashSet(StringComparer.Ordinal);
        Assert.All(help.Options, option => Assert.Contains(option.Flag, onPages));

        Assert.True(WizardAnswers.ById(definition, "add:name").IsRequired);
    }

    // ------------------------------------------------------------------ the cloud pages follow the base provider type

    [Theory]
    [InlineData("bedrock", new[] { WizardPages.ProviderAddBedrock })]
    [InlineData("vertex_ai", new[] { WizardPages.ProviderAddVertex })]
    [InlineData("azure", new[] { WizardPages.ProviderAddAzure })]
    [InlineData("openai", new string[0])]
    [InlineData("ollama", new string[0])]
    [InlineData("", new string[0])]
    public async Task The_base_provider_type_shows_its_own_cloud_page_and_no_other(string baseType, string[] expected)
    {
        var definition = await Provider();

        Assert.Equal(expected, CloudShown(definition, Add(definition, ("add:base-provider-type", baseType))));
    }

    [Fact]
    public async Task The_tls_overrides_are_asked_whatever_the_base_provider_type_is()
    {
        var definition = await Provider();

        foreach (var baseType in new[] { "openai", "bedrock", string.Empty })
        {
            Assert.Contains(WizardPages.ProviderAddTls, WizardAnswers.Pages(definition, Add(definition, ("add:base-provider-type", baseType))));
        }
    }

    [Fact]
    public async Task A_base_type_left_over_from_add_never_opens_a_cloud_page_under_another_subcommand()
    {
        var definition = await Provider();
        var values = WizardAnswers.Start(definition, ("subcommand", "list"), ("add:base-provider-type", "bedrock"), ("add:bedrock-region", "us-east-1"));

        var pages = WizardAnswers.Pages(definition, values);

        Assert.DoesNotContain(pages, id => id.StartsWith("add:", StringComparison.Ordinal));
        Assert.Equal(new[] { "setup", "provider", "list" }, definition.BuildArgv(values));
    }

    [Theory]
    [InlineData("api_key", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("iam_credentials", new[] { "add:bedrock-access-key-env", "add:bedrock-secret-key-env", "add:bedrock-session-token-env" })]
    [InlineData("profile", new[] { "add:bedrock-profile-name" })]
    public async Task The_bedrock_page_asks_for_credentials_only_as_the_auth_mode_needs_them(string authMode, string[] extras)
    {
        var definition = await Provider();
        var values = Add(definition, ("add:base-provider-type", "bedrock"), ("add:bedrock-auth-mode", authMode));

        var expected = new[] { "add:bedrock-region", "add:bedrock-auth-mode", "add:bedrock-inference-profile", "add:bedrock-deployment" }.Concat(extras);

        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            WizardAnswers.Shown(definition, values, WizardPages.ProviderAddBedrock).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_bedrock_instance_becomes_the_command_with_only_the_flags_that_were_answered()
    {
        var definition = await Provider();
        var values = Add(
            definition,
            ("add:name", "corp-bedrock"),
            ("add:base-provider-type", "bedrock"),
            ("add:bedrock-region", "eu-west-1"),
            ("add:bedrock-auth-mode", "profile"),
            ("add:bedrock-profile-name", "team"),
            ("add:azure-endpoint", "https://stale.example.test"),
            ("add:available-model", "us.acme.large-v1:0\neu.acme.large-v1:0"));

        var argv = definition.BuildArgv(values);

        Assert.Equal(
            new[]
            {
                "setup", "provider", "add", "--name", "corp-bedrock", "--base-provider-type", "bedrock", "--available-model", "us.acme.large-v1:0",
                "--available-model", "eu.acme.large-v1:0", "--bedrock-region", "eu-west-1", "--bedrock-auth-mode", "profile",
                "--bedrock-profile-name", "team",
            },
            argv);
        Assert.DoesNotContain("--azure-endpoint", argv);
    }

    [Fact]
    public async Task Skipping_tls_verification_for_an_instance_is_a_reviewed_switch()
    {
        var definition = await Provider();
        var values = Add(definition, ("add:name", "lab"), ("add:insecure-skip-verify", ToggleValues.On));

        var argv = definition.BuildArgv(values);

        Assert.Contains("--insecure-skip-verify", argv);
        Assert.Equal(WizardCautions.InsecureTlsTitle, Assert.Single(WizardCautions.For(argv)).Title);
    }

    // ------------------------------------------------------------------ the preset, and the window

    [Fact]
    public async Task The_editors_preset_opens_the_wizard_on_the_first_add_page()
    {
        var definition = await Provider();
        var services = TestServices.Create(_temp);
        _services.Add(services);
        var vm = UiThread.Run(() => new WizardViewModel(services, definition));
        _viewModels.Add(vm);

        var opened = UiThread.Run(() => new WizardPreset("add").ApplyTo(vm));

        Assert.True(opened);
        Assert.Equal("Provider", vm.PageTitle);
        Assert.Equal("add", vm.Steps.SelectMany(s => s.Fields).Single(f => f.Id == "subcommand").Value);
    }

    [Fact]
    public async Task Without_the_base_provider_type_flag_the_add_pages_are_the_generated_ones()
    {
        var group = CatalogHelp.Real("setup-provider");
        var add = CatalogHelp.Screen("provider add", "--name TEXT  Provider name.", "--base-url TEXT  Base URL.");
        var definition = await CatalogHelp.DefinitionAsync(
            "provider",
            new Dictionary<string, string> { ["provider"] = group, ["provider add"] = add });

        Assert.DoesNotContain(definition.Steps, s => s.Id == WizardPages.ProviderAddBasics);
        Assert.Contains(definition.Steps, s => s.Id == "add:page-1");
    }
}
