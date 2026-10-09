using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-270: where a group wizard opens. The Setup editors' Add opens the existing wizard on <c>add</c> (not on the page that asks which subcommand to
/// run), with the folder a failed setup named already typed in. The screens are the ones the installed 0.8.10 CLI prints for
/// <c>setup trusted-paths --help</c> and its three commands; the wizard is built from them the way <c>WizardCatalog</c> builds it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class WizardPresetTests : IDisposable
{
    private const string Folder = @"C:\Users\synthetic\.local\bin";

    private static readonly string GroupHelp = LineEndings.Normalize("""
        Usage: defenseclaw setup trusted-paths [OPTIONS] COMMAND [ARGS]...

          Manage directories DefenseClaw trusts for connector-binary discovery.

          Legacy examples: Action-mode setup reads a connector's version by executing
          its binary, but only when that binary lives under a trusted prefix — a guard
          against a hostile binary planted on $PATH. Built-in defaults cover system
          and Homebrew locations; trust additional roots here for bespoke installs.
          Additions persist to ~/.defenseclaw/config.yaml under
          ai_discovery.trusted_binary_prefixes.

        Options:
          --help  Show this message and exit.

        Commands:
          add     Trust DIRECTORY for connector-binary discovery (persisted to...
          list    List trusted binary prefixes (built-in defaults + operator-added).
          remove  Remove an operator-added trusted prefix (never a built-in...
        """);

    private static readonly IReadOnlyDictionary<string, string> VerbHelp = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["add"] = LineEndings.Normalize("""
            Usage: defenseclaw setup trusted-paths add [OPTIONS] DIRECTORY

              Trust DIRECTORY for connector-binary discovery (persisted to config.yaml).

            Options:
              --force  Record the path even when it is missing or has unsafe permissions.
              --json   Emit machine-readable JSON instead of text.
              --help   Show this message and exit.
            """),
        ["list"] = LineEndings.Normalize("""
            Usage: defenseclaw setup trusted-paths list [OPTIONS]

              List trusted binary prefixes (built-in defaults + operator-added).

            Options:
              --json  Emit machine-readable JSON instead of a table.
              --help  Show this message and exit.
            """),
        ["remove"] = LineEndings.Normalize("""
            Usage: defenseclaw setup trusted-paths remove [OPTIONS] DIRECTORY

              Remove an operator-added trusted prefix (never a built-in default).

            Options:
              --json  Emit machine-readable JSON instead of text.
              --help  Show this message and exit.
            """),
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

    /// <summary>The definition the catalog builds for <c>trusted-paths</c> from those screens.</summary>
    private static WizardDefinition TrustedPaths()
    {
        var group = SetupHelpParser.Parse(GroupHelp, commandDepth: 2);
        var verbs = group.Commands.ToDictionary(c => c.Name, c => SetupHelpParser.Parse(VerbHelp[c.Name], commandDepth: 3));
        var steps = WizardStepFactory.BuildGroup(group, verbs);

        return new WizardDefinition
        {
            Target = "trusted-paths",
            Title = "Trusted binary paths",
            Group = WizardGroups.Connectors,
            Description = "Manage directories DefenseClaw trusts for connector-binary discovery.",
            Steps = steps,
            PlatformStatus = group.PlatformStatus,
            IsDetailLoaded = true,
            HelpText = GroupHelp,
        };
    }

    private (WizardViewModel Vm, AppServices Services) Open(WizardDefinition definition)
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);
        var vm = new WizardViewModel(services, definition);
        _viewModels.Add(vm);
        return (vm, services);
    }

    private static IReadOnlyList<WizardFieldViewModel> Fields(WizardViewModel vm) => vm.Steps.SelectMany(static s => s.Fields).ToArray();

    private static string ValueOf(WizardViewModel vm, Func<WizardFieldViewModel, bool> which) => Fields(vm).Single(which).Value ?? string.Empty;

    private static bool IsDirectoryOf(WizardFieldViewModel field, string verb) =>
        field.Field.IsPositional && field.Field.VisibleWhenValues.Contains(verb, StringComparer.Ordinal);

    private static void GoToReview(WizardViewModel vm)
    {
        for (var i = 0; i < 12 && !vm.IsReview; i++)
        {
            vm.Next();
            Assert.False(vm.HasValidationSummary, vm.ValidationSummary);
        }

        Assert.True(vm.IsReview);
    }

    // ---- the wizard as the catalog builds it ----

    [Fact]
    public void The_wizard_built_from_the_help_has_the_command_page_the_preset_relies_on()
    {
        UiThread.Run(() =>
        {
            var (vm, _) = Open(TrustedPaths());

            var command = Fields(vm).Single(f => f.Id == "subcommand");
            Assert.Equal(new[] { "add", "list", "remove" }, command.Choices.Select(c => c.Value));
            Assert.Equal(0, vm.PageIndex);
            Assert.NotNull(Fields(vm).Single(f => IsDirectoryOf(f, "add")));
            Assert.NotNull(Fields(vm).Single(f => IsDirectoryOf(f, "remove")));
        });
    }

    // ---- Add: the wizard opens on add with the folder typed in ----

    [Fact]
    public void Add_opens_on_the_add_page_with_the_folder_filled_in_and_ends_on_the_exact_command_with_nothing_run()
    {
        UiThread.Run(() =>
        {
            var (vm, services) = Open(TrustedPaths());

            Assert.True(new WizardPreset("add", Folder).ApplyTo(vm));

            // Past the page that asks which subcommand: the next page is add's, with its folder already typed.
            Assert.Equal(1, vm.PageIndex);
            Assert.Equal("add", ValueOf(vm, f => f.Id == "subcommand"));
            Assert.Equal(Folder, ValueOf(vm, f => IsDirectoryOf(f, "add")));
            Assert.Equal(string.Empty, ValueOf(vm, f => IsDirectoryOf(f, "remove")));

            GoToReview(vm);
            var step = Assert.Single(vm.CommandReview!.Steps);
            Assert.Equal(new[] { "setup", "trusted-paths", "add", Folder }, step.Argv);
            Assert.False(vm.HasRun);
            Assert.Empty(services.Cli.Activity); // only answers were set: the command is reviewed, and runs when the operator presses Execute
        });
    }

    [Fact]
    public void Add_with_no_folder_opens_on_the_add_page_with_the_folder_empty()
    {
        UiThread.Run(() =>
        {
            var (vm, _) = Open(TrustedPaths());

            Assert.True(new WizardPreset("add").ApplyTo(vm));

            Assert.Equal(1, vm.PageIndex);
            Assert.Equal("add", ValueOf(vm, f => f.Id == "subcommand"));
            Assert.Equal(string.Empty, ValueOf(vm, f => IsDirectoryOf(f, "add")));
        });
    }

    [Fact]
    public void The_folder_goes_to_the_first_positional_of_the_subcommand_asked_for_and_to_no_other()
    {
        UiThread.Run(() =>
        {
            var (vm, _) = Open(TrustedPaths());

            Assert.True(new WizardPreset("remove", Folder).ApplyTo(vm));

            Assert.Equal("remove", ValueOf(vm, f => f.Id == "subcommand"));
            Assert.Equal(Folder, ValueOf(vm, f => IsDirectoryOf(f, "remove")));
            Assert.Equal(string.Empty, ValueOf(vm, f => IsDirectoryOf(f, "add")));
        });
    }

    [Fact]
    public void A_subcommand_with_no_positional_takes_no_folder_and_still_opens()
    {
        UiThread.Run(() =>
        {
            var (vm, _) = Open(TrustedPaths());

            Assert.True(new WizardPreset("list", Folder).ApplyTo(vm));

            Assert.Equal("list", ValueOf(vm, f => f.Id == "subcommand"));
            Assert.Equal(string.Empty, ValueOf(vm, f => IsDirectoryOf(f, "add")));
            Assert.Equal(string.Empty, ValueOf(vm, f => IsDirectoryOf(f, "remove")));
        });
    }

    // ---- a wizard that has no such page opens as it always did ----

    [Fact]
    public void A_wizard_with_no_command_page_is_left_alone()
    {
        UiThread.Run(() =>
        {
            var (vm, _) = Open(WizardSamples.Galileo());

            Assert.False(new WizardPreset("add", Folder).ApplyTo(vm));

            Assert.Equal(0, vm.PageIndex);
        });
    }

    [Fact]
    public void A_subcommand_the_wizard_does_not_offer_is_left_alone()
    {
        UiThread.Run(() =>
        {
            var (vm, _) = Open(TrustedPaths());
            var before = ValueOf(vm, f => f.Id == "subcommand");

            Assert.False(new WizardPreset("frobnicate", Folder).ApplyTo(vm));

            Assert.Equal(0, vm.PageIndex);
            Assert.Equal(before, ValueOf(vm, f => f.Id == "subcommand"));
            Assert.Equal(string.Empty, ValueOf(vm, f => IsDirectoryOf(f, "add")));
        });
    }

    [Fact]
    public void A_missing_wizard_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new WizardPreset("add").ApplyTo(null!));
    }
}
