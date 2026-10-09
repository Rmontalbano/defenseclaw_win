using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// A13: the Help entry opens the DefenseClaw docs in the default browser. The address is the one the Mac app's Help menu opens; the app
/// shows it and hands it to the URL-open seam (<see cref="ShellActions.UrlOpener"/>, faked here), and never fetches it.
/// </summary>
public sealed class DocsLinkTests : IDisposable
{
    private const string MacDocsUrl = "https://cisco-ai-defense.github.io/defenseclaw/docs/";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public DocsLinkTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    [Fact]
    public void The_docs_address_is_the_one_the_mac_app_opens()
    {
        Assert.Equal(MacDocsUrl, ShellActions.DocsUrl);
    }

    [Fact]
    public void The_palette_entry_shows_the_address_and_opens_it_through_the_url_seam()
    {
        StaThread.Run(() =>
        {
            var opened = new List<string>();
            var catalog = new PanelCatalog(_services);
            var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
            var actions = new ShellActions(_services, catalog, tray, () => null) { UrlOpener = url => opened.Add(url) };

            var rows = ShellCommandRegistry.Build(catalog, actions, _ => { }, () => { }, curated: Array.Empty<CuratedCommand>());
            var docs = Assert.Single(rows, row => row.Id == "app.docs");

            Assert.True(docs.IsEnabled);
            Assert.Contains(MacDocsUrl, docs.Description, StringComparison.Ordinal);
            Assert.Empty(opened);

            docs.Run();

            Assert.Equal(new[] { MacDocsUrl }, opened);
        });
    }
}
