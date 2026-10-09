using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Settings;

/// <summary>
/// A command run from the palette while the Settings page is open writes the palette's recents (CUST-264). The page copies the store into its form
/// whenever the store changes, which would wipe a path half typed in a field that waits for its Apply; the palette's recents are not on the page, so
/// a change to them alone leaves the form as it is.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsPagePaletteRecentsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public SettingsPagePaletteRecentsTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private SettingsPanelViewModel OpenPage()
    {
        var page = UiThread.Run(() => new SettingsPanelViewModel(_services, hooks: null, new FakePlatform().Build()));
        UiThread.Run(() => page.SetActive(true));
        return page;
    }

    /// <summary>The path field once everything the store's change queued for the UI thread has run (the page copies the store on its dispatcher).</summary>
    private static string Settled(SettingsPanelViewModel page) =>
        UiThread.Run(() =>
        {
            UiThread.Settle();
            return page.ArchivePathText;
        });

    [Fact]
    public void A_palette_command_run_while_a_path_is_half_typed_leaves_the_field_alone()
    {
        var page = OpenPage();
        try
        {
            UiThread.Run(() => page.ArchivePathText = @"D:\archive\half-typ");

            _ = _services.Settings.Update(s => s with { Palette = new PaletteSettings { RecentCommandIds = new[] { "cli.doctor" } } });

            Assert.Equal(@"D:\archive\half-typ", Settled(page));
        }
        finally
        {
            UiThread.Run(() => page.SetActive(false));
        }
    }

    [Fact]
    public void A_change_to_anything_the_page_shows_still_reaches_it()
    {
        var page = OpenPage();
        try
        {
            _ = _services.Settings.Update(s => s with { Archive = new ArchiveSettings { Path = @"D:\archive\audit.db" }, Palette = new PaletteSettings { RecentCommandIds = new[] { "cli.doctor" } } });

            Assert.Equal(@"D:\archive\audit.db", Settled(page));
        }
        finally
        {
            UiThread.Run(() => page.SetActive(false));
        }
    }
}
