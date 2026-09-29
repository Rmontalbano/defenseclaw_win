using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The dashboard window is built on demand, so on a <c>--minimized</c> launch the catalog lives for a long
/// time with no window and therefore no panel: nothing may be built, running or active until a window asks
/// for a page. (Activation itself — a panel is active only while its view is on screen in an interactive
/// window — is the catalog's own contract, exercised by the panels; this pins the "no window yet" state.)
/// </summary>
public class PanelCatalogStartupTests
{
    [Fact]
    public void A_catalog_with_no_window_has_no_active_panel()
    {
        StaThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);

            var catalog = new PanelCatalog(services);

            Assert.Null(catalog.ActivePanel);
            Assert.Null(catalog.ActiveViewModel);
            Assert.NotEmpty(catalog.Panels); // the descriptors exist; only views and view-models are built on demand
        });
    }

    [Fact]
    public void Window_state_changes_before_any_panel_exists_activate_nothing()
    {
        StaThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var catalog = new PanelCatalog(services);

            // What a first show followed by a hide does to a catalog that has not been asked for a page yet.
            catalog.SetWindowInteractive(false);
            catalog.SetWindowInteractive(true);
            catalog.SetWindowInteractive(false);

            Assert.Null(catalog.ActivePanel);
            Assert.Null(catalog.ActiveViewModel);
        });
    }
}
