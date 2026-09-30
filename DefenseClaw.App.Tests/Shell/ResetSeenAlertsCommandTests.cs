using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// "Reset seen-alert history" is in the command palette (the Settings page will add a button for the same action). The palette is
/// built here over an uninitialised tray, as <c>MainWindowConstructionTests</c> does: building the list reads no tray state, and
/// running the command is the notifier's own tests' business.
/// </summary>
public sealed class ResetSeenAlertsCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public ResetSeenAlertsCommandTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private IReadOnlyList<ShellCommand> Palette()
    {
        var catalog = new PanelCatalog(_services);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var actions = new ShellActions(_services, catalog, tray, () => null);
        return ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { });
    }

    [Fact]
    public void The_palette_lists_the_reset_as_an_app_action_that_is_always_available()
    {
        var command = Assert.Single(Palette(), c => c.Id == "app.reset-seen-alerts");

        Assert.Equal("Reset seen-alert history", command.Title);
        Assert.Equal(ShellCommandRegistry.AppCategory, command.Category);
        Assert.True(command.IsEnabled);
        Assert.Null(command.Shortcut);
    }

    [Theory]
    [InlineData("reset seen")]
    [InlineData("seen-alert")]
    [InlineData("forget announced")]
    [InlineData("notifications")]
    public void It_is_found_by_the_words_an_operator_would_type(string query)
    {
        var ranked = CommandPaletteViewModel.Rank(Palette(), query);

        Assert.Equal("app.reset-seen-alerts", ranked[0].Id);
    }

    [Fact]
    public void It_has_a_glyph_of_its_own_in_a_known_section()
    {
        var (icon, section) = DcSections.OfCommand("app.reset-seen-alerts");

        Assert.NotEqual(SymbolRegular.Circle24, icon);
        Assert.Contains(section, DcSections.All);
    }
}
