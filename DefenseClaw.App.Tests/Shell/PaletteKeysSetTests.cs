using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The palette's <c>keys set</c> (CUST-328): Run no longer copies the command for a terminal. It opens the Setup page's Credentials card on the name typed, where the
/// value goes into a masked box and through the review (the card's own tests are in <c>CredentialsInAppTests</c>). Here: the row takes a NAME, the installation's
/// sentence comes first, a bad name opens nothing, the value is never part of anything the palette holds, and nothing is run or copied. No process starts.
/// </summary>
public sealed class PaletteKeysSetTests : IDisposable
{
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

    private sealed record Harness(ShellActions Actions, AppServices Services, List<string> Toasts, List<string> Clipboard, List<CommandReview> Reviews);

    private Harness Create(InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _services.Add(services);

        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var toasts = new List<string>();
        var clipboard = new List<string>();
        var reviews = new List<CommandReview>();
        var actions = new ShellActions(services, new PanelCatalog(services), tray, () => null)
        {
            Toast = (title, message) => toasts.Add(title + ": " + message),
            ClipboardWriter = clipboard.Add,
            Confirmer = review =>
            {
                reviews.Add(review);
                return false;
            },
        };

        return new Harness(actions, services, toasts, clipboard, reviews);
    }

    private static CuratedCommand KeysSet() =>
        CuratedCommand.FromRegistry(TuiRegistryCatalogues.Baseline.Find("keys set") ?? throw new InvalidOperationException("no keys set"));

    [Fact]
    public void The_keys_set_row_takes_a_name_and_shows_the_command_that_runs()
    {
        var command = KeysSet();

        Assert.True(command.TypesInApp);
        Assert.True(command.NeedsTerminal); // the console is still what it falls back to
        Assert.NotNull(command.Form);
        Assert.Equal(new[] { "keys", "set", "EXAMPLE_KEY" }, command.ArgvWith("EXAMPLE_KEY"));
        Assert.Equal("defenseclaw keys set EXAMPLE_KEY", command.CommandLineWith("EXAMPLE_KEY"));
        Assert.Equal(command.CommandLineWith("EXAMPLE_KEY"), command.ClipboardTextWith("EXAMPLE_KEY"));

        // Every other command keeps the "--" before a typed target, and no other row types in the app.
        var skill = CuratedCommand.FromRegistry(TuiRegistryCatalogues.Baseline.Find("skill info") ?? throw new InvalidOperationException("no skill info"));
        Assert.False(skill.TypesInApp);
        Assert.Contains("--", skill.ArgvWith("x"));
    }

    [Fact]
    public async Task Run_with_a_name_opens_the_credentials_card_on_it_and_copies_runs_and_reviews_nothing()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(KeysSet(), "EXAMPLE_KEY");

        var request = h.Services.Navigation.Pending;
        Assert.NotNull(request);
        Assert.Equal("setup", request!.PanelId);
        Assert.Equal(new CredentialSet("EXAMPLE_KEY"), request.Payload);
        Assert.Empty(h.Clipboard);
        Assert.Empty(h.Reviews);
        Assert.Empty(h.Toasts);
        Assert.Empty(h.Services.Cli.Activity);
    }

    [Theory]
    [InlineData("1BAD")]
    [InlineData("A.B")]
    [InlineData("A-B")]
    [InlineData("a*")]
    public async Task A_name_that_is_not_a_variable_name_opens_nothing(string name)
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(KeysSet(), name);

        Assert.Null(h.Services.Navigation.Pending);
        Assert.Empty(h.Clipboard);
        _ = Assert.Single(h.Toasts);
    }

    [Fact]
    public async Task Run_without_a_name_copies_the_command_to_complete_as_every_command_that_needs_one_does()
    {
        var h = Create();

        await h.Actions.RunCuratedAsync(KeysSet());

        Assert.Null(h.Services.Navigation.Pending);
        Assert.Equal("defenseclaw keys set", Assert.Single(h.Clipboard));
        Assert.Contains("Needs <ENV_NAME>", Assert.Single(h.Toasts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_installation_comes_first_and_the_row_is_off_with_its_sentence()
    {
        var h = Create(TestInstallations.Managed(_temp.Path));

        await h.Actions.RunCuratedAsync(KeysSet(), "EXAMPLE_KEY");

        Assert.Null(h.Services.Navigation.Pending);
        Assert.Empty(h.Clipboard);
        Assert.Equal(KeysSet().Title + ": " + TestInstallations.ManagedReason, Assert.Single(h.Toasts));

        var row = ShellCommandRegistry.BuildCliCommands(new[] { KeysSet() }, h.Actions).Single();
        Assert.False(row.IsEnabled);
        Assert.Equal(TestInstallations.ManagedReason, row.DisabledReason);
    }

    [Fact]
    public void On_a_writable_installation_the_row_is_on_and_takes_a_typed_name()
    {
        var h = Create();

        var row = ShellCommandRegistry.BuildCliCommands(new[] { KeysSet() }, h.Actions).Single();

        Assert.True(row.IsEnabled);
        Assert.NotNull(row.RunWith);
        Assert.NotNull(row.CopyWith);
    }

    [Fact]
    public void The_palettes_note_and_button_say_what_Run_does_now()
    {
        var h = Create();
        var row = ShellCommandRegistry.BuildCliCommands(new[] { KeysSet() }, h.Actions).Single();
        var item = new PaletteItem(row);

        Assert.Equal("Enter value…", item.RunLabel);
        Assert.Contains("masked box", item.RunNote, StringComparison.Ordinal);
        Assert.DoesNotContain("copies the command", item.RunNote, StringComparison.Ordinal);
        Assert.Equal("defenseclaw keys set <ENV_NAME>", item.ArgvPreview);
    }
}
