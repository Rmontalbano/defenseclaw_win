using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>The F1 overlay lists the light/dark chord.</summary>
public sealed class AppearanceShortcutOverlayTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public AppearanceShortcutOverlayTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void The_shortcuts_overlay_lists_the_toggle_once_with_its_chord_split_into_key_caps()
    {
        var model = ShortcutCatalog.Build(new PanelCatalog(_services));

        var rows = model.Others.SelectMany(s => s.Rows).Where(r => r.Keys == ShellShortcuts.ToggleThemeText).ToList();

        var row = Assert.Single(rows);
        Assert.Equal(new[] { "Ctrl", "Shift", "L" }, row.Parts);
        Assert.Contains("light and dark", row.Description, StringComparison.Ordinal);
        Assert.StartsWith("Switch between light and dark", row.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void No_chord_in_the_overlay_is_listed_twice()
    {
        var model = ShortcutCatalog.Build(new PanelCatalog(_services));

        var all = model.Panels.Rows.Concat(model.Others.SelectMany(s => s.Rows)).Select(r => r.Keys).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }
}
