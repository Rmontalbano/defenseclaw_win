using System.Windows;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using Microsoft.Data.Sqlite;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The Audit panel as a real view with an archive set in Settings: the Live | Archive switch, the "Archived history, up to ..." banner,
/// and the error banner for an archive that cannot be read. A PNG of each is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AuditArchiveViewTests : IDisposable
{
    private static readonly DateTimeOffset ArchiveNewest = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _live = new();
    private readonly TempDirectory _archiveDir = new();
    private readonly IDisposable _theme = UiThread.FluentTheme();
    private readonly AppServices _services;
    private PanelShell? _shell;

    public AuditArchiveViewTests()
    {
        AuditTestDatabase.Create(Path.Combine(_live.Path, "audit.db"), 30, structuredJson: "{\"decision\":\"allow\"}");
        _services = TestServices.Create(_live);
    }

    public void Dispose()
    {
        if (_shell is not null)
        {
            UiThread.Run(_shell.Dispose);
        }

        _theme.Dispose();
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        _live.Dispose();
        _archiveDir.Dispose();
    }

    private string MakeArchive()
    {
        var path = Path.Combine(_archiveDir.Path, "audit-2026-03-02.db");
        AuditTestDatabase.Create(path, 40, newest: ArchiveNewest, idPrefix: "arc-", structuredJson: "{\"decision\":\"block\"}");
        return path;
    }

    private (AuditPanel Panel, AuditPanelViewModel ViewModel) Open(string? archivePath)
    {
        Assert.True(_services.Settings.Update(s => s with { Archive = new ArchiveSettings { Path = archivePath } }));
        return UiThread.Run(() =>
        {
            _shell = new PanelShell(_services, 1200, 760);
            var panel = _shell.Show<AuditPanel>();
            var viewModel = (AuditPanelViewModel)_shell.ViewModel;

            // These rows are all quiet INFO hook decisions; the panel opens on the actionable events (CUST-262), and this is about the archive.
            viewModel.ActionableOnly = false;
            return (panel, viewModel);
        });
    }

    [Fact]
    public void The_switch_is_absent_without_an_archive_and_present_with_one()
    {
        var (panel, viewModel) = Open(null);
        UiThread.WaitFor(() => viewModel.Rows.Count == 30 && !viewModel.IsLoading, "live rows");

        UiThread.Run(() =>
        {
            var source = (DcSegmented)panel.FindName("SourceSwitch");
            Assert.NotEqual(Visibility.Visible, source.Visibility);
            Assert.False(((InfoBar)panel.FindName("ArchiveBannerBar")).IsOpen);
        });
    }

    [Fact]
    public void Choosing_Archive_shows_the_banner_the_archives_rows_and_the_switch_on_Archive()
    {
        var (panel, viewModel) = Open(MakeArchive());
        UiThread.WaitFor(() => viewModel.Rows.Count == 30 && !viewModel.IsLoading, "live rows");

        UiThread.Run(() => viewModel.SourceKey = AuditPanelViewModel.SourceArchive);
        UiThread.WaitFor(() => viewModel.Rows.Count == 40 && !viewModel.IsLoading && viewModel.ShowArchiveBanner, "archive rows");

        UiThread.Run(() =>
        {
            _shell!.Host.Relayout();
            var source = (DcSegmented)panel.FindName("SourceSwitch");
            Assert.Equal(Visibility.Visible, source.Visibility);
            Assert.Equal("archive", source.SelectedValue);

            var banner = (InfoBar)panel.FindName("ArchiveBannerBar");
            Assert.True(banner.IsOpen);
            Assert.StartsWith("Archived history, up to", banner.Title, StringComparison.Ordinal);
            Assert.False(((InfoBar)panel.FindName("ArchiveProblemBar")).IsOpen);

            RenderTo.Png(_shell.Host, "audit-archive-banner");
        });
    }

    [Fact]
    public void A_missing_archive_shows_the_error_banner_and_the_empty_state_with_the_reason()
    {
        var (panel, viewModel) = Open(Path.Combine(_archiveDir.Path, "audit-gone.db"));
        UiThread.WaitFor(() => viewModel.Rows.Count == 30 && !viewModel.IsLoading, "live rows");

        UiThread.Run(() => viewModel.SourceKey = AuditPanelViewModel.SourceArchive);
        UiThread.WaitFor(() => viewModel.ShowArchiveProblem, "archive error");

        UiThread.Run(() =>
        {
            _shell!.Host.Relayout();
            var problem = (InfoBar)panel.FindName("ArchiveProblemBar");
            Assert.True(problem.IsOpen);
            Assert.Contains("does not exist", problem.Message, StringComparison.Ordinal);
            Assert.False(((InfoBar)panel.FindName("ArchiveBannerBar")).IsOpen);

            // The way back is still on the page.
            Assert.Equal(Visibility.Visible, ((DcSegmented)panel.FindName("SourceSwitch")).Visibility);

            RenderTo.Png(_shell.Host, "audit-archive-error");
        });
    }
}
