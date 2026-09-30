using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The command palette's "Check for updates" (CUST-206): one entry, always available, that says what it does. Running it opens the
/// Updates window (and starts the watcher's own check), which is not something a test can do without reaching GitHub, so this holds
/// the entry itself; the watcher's behaviour is in <see cref="UpdateWatcherTests"/>.
/// </summary>
public sealed class UpdateCheckPaletteTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public UpdateCheckPaletteTests() => _services = TestServices.Create(_temp);

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void There_is_one_enabled_Check_for_updates_entry_and_it_says_it_looks_now_and_opens_the_window()
    {
        // The tray is an uninitialised instance: ShellActions only stores it, and a real one would put an icon in the notification area.
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var catalog = new PanelCatalog(_services);
        var actions = new ShellActions(_services, catalog, tray, () => null);

        var commands = ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { });

        var entry = Assert.Single(commands, c => c.Id == "app.check-updates");
        Assert.Equal("Check for updates", entry.Title);
        Assert.Equal(ShellCommandRegistry.AppCategory, entry.Category);
        Assert.True(entry.IsEnabled);
        Assert.Contains("newer DefenseClaw release", entry.Description, StringComparison.Ordinal);
        Assert.Contains("Updates window", entry.Description, StringComparison.Ordinal);
        Assert.Contains("update", entry.Keywords, StringComparison.Ordinal);
    }
}
