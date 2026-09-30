using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The Registries panel as a real view, over a synthetic cached <c>index.json</c> (the <c>registry-index.synced.json</c>
/// fixture: one clean entry, one with two MEDIUM findings, one with four HIGH ones, one whose fetch failed, one
/// pending). An entry with an error or findings gets a second line under its row — the error in the Bad tone, a
/// findings badge toned by the worst severity — and every other entry stays one compact line.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class RegistriesPanelLayoutTests
{
    [Fact]
    public void An_entry_with_an_error_or_findings_gets_a_detail_line_and_the_others_stay_one_line()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.Rows();
            Assert.Equal(new[] { "pdf-tools", "docs-mcp", "remote-mcp", "broken-skill", "new-entry" }, rows.Select(r => r.Name).ToArray());

            // Nothing extra for a clean entry or a pending one (the presenter keeps a 1 DIP rule, whatever it holds).
            Assert.All(new[] { "pdf-tools", "new-entry" }, name => Assert.True(scene.DetailHeight(name) <= 2, $"{name} drew a detail line of {scene.DetailHeight(name)}"));

            // A second line for the other three.
            Assert.All(new[] { "docs-mcp", "remote-mcp", "broken-skill" }, name => Assert.True(scene.DetailHeight(name) >= 18, $"{name} has no detail line"));

            // The words on screen: a count badge each for the two scanned entries, the error line for the failed one.
            Assert.Equal(new[] { "2 findings" }, scene.VisibleTexts("docs-mcp"));
            Assert.Equal(new[] { "4 findings" }, scene.VisibleTexts("remote-mcp"));
            Assert.Equal(new[] { "Error: fetch failed: HTTP 404" }, scene.VisibleTexts("broken-skill"));
            Assert.Empty(scene.VisibleTexts("pdf-tools"));

            scene.Render("registries-entries-1400x900");
        });
    }

    [Fact]
    public void The_badge_carries_the_severity_tone_the_tooltip_and_an_accessible_name_and_the_error_is_bad()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var docs = scene.Badge("docs-mcp");
            Assert.Equal("Medium", docs.Tag);
            Assert.Equal("2 findings, worst severity MEDIUM", System.Windows.Automation.AutomationProperties.GetName(docs));

            // The tooltip is a wrapping text block bound to the row's own FindingsToolTip (a tooltip's content is only
            // resolved once it opens); the same words are the badge's help text, which is bound in the row's context.
            var tip = Assert.IsType<TextBlock>(docs.ToolTip);
            Assert.Equal("FindingsToolTip", System.Windows.Data.BindingOperations.GetBinding(tip, TextBlock.TextProperty)!.Path.Path);
            Assert.Contains("worst severity MEDIUM", System.Windows.Automation.AutomationProperties.GetHelpText(docs), StringComparison.Ordinal);

            var remote = scene.Badge("remote-mcp");
            Assert.Equal("High", remote.Tag);
            Assert.Equal("4 findings, worst severity HIGH", System.Windows.Automation.AutomationProperties.GetName(remote));

            var error = scene.ErrorLine("broken-skill");
            Assert.Equal("Bad", error.Tag);
            Assert.Equal("error: fetch failed: HTTP 404", System.Windows.Automation.AutomationProperties.GetName(error));
        });
    }

    [Fact]
    public void At_the_narrowest_width_the_detail_lines_stay_inside_the_entries_grid()
    {
        // The minimum window width (the entries card is then at its 340 DIP floor), tall enough to see the entries.
        using var scene = Scene.Open(940, 1000);

        UiThread.Run(() =>
        {
            var grid = scene.EntriesGrid;
            Assert.All(new[] { "docs-mcp", "remote-mcp", "broken-skill" }, name =>
            {
                var line = scene.DetailHeight(name);
                Assert.True(line >= 18, $"{name} has no detail line at the narrowest width");
                Assert.True(scene.DetailRight(name) <= grid.ActualWidth + 1, $"{name}'s detail line runs past the grid");
            });

            scene.Render("registries-entries-940x1000");
        });
    }

    // ------------------------------------------------------------------ the scene

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(int width, int height)
        {
            var folder = Path.Combine(_temp.Path, "registries", "corp-skills");
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.json"), PayloadFixtures.Read("registry-index.synced.json"));

            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                _ = shell.Show<RegistriesPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (RegistriesPanelViewModel)Shell.ViewModel);

            // The isolated services have no CLI, so the panel's own read ends at "not found"; settle that, then put the
            // synthetic source in front of it (the entries come from the index.json above, through the real reader).
            UiThread.WaitFor(() => ViewModel.HasLoaded && !ViewModel.IsLoading, "registries read finished");
            UiThread.Run(() =>
            {
                var source = new RegistrySourceRow
                {
                    Id = "corp-skills",
                    Kind = "http_yaml",
                    Content = "both",
                    Enabled = true,
                    EntriesSummary = "5 (1 clean, 1 warning, 1 blocked, 1 error)",
                    LastSync = "2026-09-20T15:04:05+00:00",
                    LastStatus = "ok",
                    Location = "https://registry.example.test/defenseclaw-registry.yaml",
                    Fields = new[] { new RegistryFieldRow("id", "corp-skills"), new RegistryFieldRow("kind", "http_yaml") },
                };
                ViewModel.CliErrorMessage = null;
                ViewModel.Sources.Add(source);
                ViewModel.HasSources = true;
                ViewModel.SelectedSource = source;
            });
            UiThread.WaitFor(() => ViewModel.Entries.Count == 5 && !ViewModel.IsEntriesLoading, "entries read");
            UiThread.Run(() => Host.Relayout());
        }

        public PanelShell Shell { get; }

        public RegistriesPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public static Scene Open(int width, int height) => new(width, height);

        public Wpf.Ui.Controls.DataGrid EntriesGrid =>
            VisualTree.Find<Wpf.Ui.Controls.DataGrid>(
                Shell.Page!,
                g => System.Windows.Automation.AutomationProperties.GetName(g) == "Cached entries of the selected source")
            ?? throw new InvalidOperationException("The entries grid was not built.");

        public IReadOnlyList<RegistryEntryRow> Rows() => ViewModel.Entries.ToArray();

        private DataGridRow RowOf(string name) =>
            VisualTree.Descendants<DataGridRow>(EntriesGrid).First(r => r.Item is RegistryEntryRow { } e && e.Name == name);

        /// <summary>The height the row's details area takes (0 when the entry has nothing to say).</summary>
        public double DetailHeight(string name) => VisualTree.Find<DataGridDetailsPresenter>(RowOf(name))?.ActualHeight ?? 0;

        public double DetailRight(string name)
        {
            var presenter = VisualTree.Find<DataGridDetailsPresenter>(RowOf(name))!;
            return presenter.TranslatePoint(new Point(presenter.ActualWidth, 0), EntriesGrid).X;
        }

        /// <summary>The text of every visible text block inside the row's details area.</summary>
        public string[] VisibleTexts(string name)
        {
            var presenter = VisualTree.Find<DataGridDetailsPresenter>(RowOf(name));
            return presenter is null
                ? Array.Empty<string>()
                : VisualTree.Descendants<TextBlock>(presenter).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text).ToArray();
        }

        public Border Badge(string name) =>
            VisualTree.Find<Border>(RowOf(name), b => b.Tag is string && b.IsVisible)
            ?? throw new InvalidOperationException($"{name} has no visible badge.");

        public TextBlock ErrorLine(string name) =>
            VisualTree.Find<TextBlock>(RowOf(name), t => t.IsVisible && t.Text.StartsWith("Error:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{name} has no visible error line.");

        /// <summary>Scrolls the selected source's card so its entries are what the picture shows, then renders.</summary>
        public void Render(string fileName)
        {
            var scroll = VisualTree.Descendants<ScrollViewer>(Shell.Page!)
                .FirstOrDefault(sv => sv.IsAncestorOf(EntriesGrid) && sv.ScrollableHeight > 0);
            if (scroll is not null)
            {
                var top = EntriesGrid.TranslatePoint(new Point(0, 0), scroll).Y + scroll.VerticalOffset;
                scroll.ScrollToVerticalOffset(Math.Max(0, top - 70));
                Host.Relayout();
            }

            RenderTo.Png(Host, fileName);
        }

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
