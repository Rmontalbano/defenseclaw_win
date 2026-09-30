using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DefenseClaw.App.Views.Panels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Controls;

/// <summary>
/// The app's sections, one tint each (Themes\Styles\*.xaml: DcSection*Brush for card headers, DcNavIcon*Brush for the
/// sidebar and page headers). Every style decides what a section looks like: Linear colours each one, Default tints only
/// the card headers, TUI uses the accent. A view names the section, never the colour.
/// </summary>
public static class DcSections
{
    public const string Overview = "Overview";
    public const string Observe = "Observe";
    public const string Govern = "Govern";
    public const string Discover = "Discover";
    public const string Setup = "Setup";
    public const string Updates = "Updates";

    public static IReadOnlyList<string> All { get; } = new[] { Overview, Observe, Govern, Discover, Setup, Updates };

    /// <summary>
    /// Each panel's section and sidebar glyph. The glyph is a copy of the one in <c>PanelCatalog</c> (the palette shows it
    /// without a catalog to ask; a test holds the two equal), the section is the only place the mapping lives.
    /// </summary>
    private static readonly (string Id, Type View, string Section, SymbolRegular Icon)[] Panels =
    {
        ("overview", typeof(OverviewPanel), Overview, SymbolRegular.AppsListDetail24),
        ("alerts", typeof(AlertsPanel), Observe, SymbolRegular.AlertUrgent24),
        ("logs", typeof(LogsPanel), Observe, SymbolRegular.DocumentBulletList24),
        ("audit", typeof(AuditPanel), Observe, SymbolRegular.DatabaseSearch24),
        ("activity", typeof(ActivityPanel), Observe, SymbolRegular.History32),
        ("skills", typeof(SkillsPanel), Govern, SymbolRegular.PuzzlePiece24),
        ("mcps", typeof(McpsPanel), Govern, SymbolRegular.PlugConnected24),
        ("plugins", typeof(PluginsPanel), Govern, SymbolRegular.PuzzleCube24),
        ("tools", typeof(ToolsPanel), Govern, SymbolRegular.WrenchScrewdriver24),
        ("inventory", typeof(InventoryPanel), Discover, SymbolRegular.BookDatabase24),
        ("ai-discovery", typeof(AiDiscoveryPanel), Discover, SymbolRegular.Bot24),
        ("registries", typeof(RegistriesPanel), Discover, SymbolRegular.Library24),
        ("setup", typeof(SetupPanel), Setup, SymbolRegular.ShieldSettings24),
    };

    /// <summary>The panel ids this table covers, in sidebar order.</summary>
    internal static IEnumerable<string> PanelIds => Panels.Select(p => p.Id);

    /// <summary>The section of the panel whose view is <paramref name="view"/> (a sidebar item's target page), or null.</summary>
    public static string? OfPanelView(Type? view) => Panels.FirstOrDefault(p => p.View == view).Section;

    /// <summary>The section of the panel with this id ("skills", "ai-discovery"), or null.</summary>
    public static string? OfPanelId(string? id) =>
        Panels.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)).Section;

    /// <summary>The sidebar glyph of the panel with this id; what <c>PanelCatalog</c> says, for a caller with no catalog.</summary>
    internal static SymbolRegular? IconOfPanelId(string? id)
    {
        foreach (var panel in Panels)
        {
            if (string.Equals(panel.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return panel.Icon;
            }
        }

        return null;
    }

    /// <summary>
    /// The icon and section of a command-palette row, by the command's id: a "Go to" row wears its panel's glyph and
    /// section, an app or gateway action the glyph of what it does and the section it belongs with.
    /// </summary>
    internal static (SymbolRegular Icon, string Section) OfCommand(string? id)
    {
        id ??= string.Empty;
        if (id.StartsWith("nav.", StringComparison.Ordinal))
        {
            var panel = id["nav.".Length..];
            if (IconOfPanelId(panel) is { } icon)
            {
                return (icon, OfPanelId(panel)!);
            }
        }

        return id switch
        {
            "app.refresh-panel" => (SymbolRegular.ArrowClockwise24, Overview),
            "app.refresh-gateway" => (SymbolRegular.ArrowSync24, Overview),
            "app.config-editor" => (SymbolRegular.DocumentEdit24, Setup),
            "app.check-updates" => (SymbolRegular.ArrowCircleUp24, Updates),
            "app.toggle-autostart" => (SymbolRegular.Power24, Setup),
            "app.reset-seen-alerts" => (SymbolRegular.ArrowReset24, Setup),
            "app.shortcuts" => (SymbolRegular.Keyboard24, Setup),
            "appearance.toggle" => (SymbolRegular.WeatherMoon24, Setup),
            "appearance.system" => (SymbolRegular.Desktop24, Setup),
            _ when id.StartsWith("appearance.", StringComparison.Ordinal) => (SymbolRegular.PaintBrush24, Setup),
            "gateway.start" => (SymbolRegular.Play24, Overview),
            "gateway.stop" => (SymbolRegular.Stop24, Overview),
            "gateway.restart" => (SymbolRegular.ArrowRepeatAll24, Overview),
            _ => (SymbolRegular.Circle24, Setup),
        };
    }
}

/// <summary>
/// Attached properties that tell an icon (or a header that shows one) which glyph and which colour it wears. The
/// <c>Dc*</c> icon styles turn <see cref="SectionProperty"/> and <see cref="TintProperty"/> into a brush by trigger, and
/// the brushes are tokens, so a live style or mode switch recolours every icon.
/// </summary>
public static class DcIcon
{
    /// <summary>The glyph a page header shows (see the <c>DcPageHeader</c> style). Empty: no icon.</summary>
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.RegisterAttached(
        "Symbol",
        typeof(SymbolRegular),
        typeof(DcIcon),
        new PropertyMetadata(SymbolRegular.Empty));

    /// <summary>One of <see cref="DcSections"/>. Inherited, so an icon inside a header takes the header's.</summary>
    public static readonly DependencyProperty SectionProperty = DependencyProperty.RegisterAttached(
        "Section",
        typeof(string),
        typeof(DcIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>
    /// A colour from the tint palette by name (Indigo, Blue, Violet, Teal, Green, Amber, Orange, Red, Pink, Gray), for the
    /// few icons whose meaning is not a section. Wins over <see cref="SectionProperty"/>.
    /// </summary>
    public static readonly DependencyProperty TintProperty = DependencyProperty.RegisterAttached(
        "Tint",
        typeof(string),
        typeof(DcIcon),
        new PropertyMetadata(null));

    public static SymbolRegular GetSymbol(DependencyObject element) => (SymbolRegular)element.GetValue(SymbolProperty);

    public static void SetSymbol(DependencyObject element, SymbolRegular value) => element.SetValue(SymbolProperty, value);

    public static string? GetSection(DependencyObject element) => (string?)element.GetValue(SectionProperty);

    public static void SetSection(DependencyObject element, string? value) => element.SetValue(SectionProperty, value);

    public static string? GetTint(DependencyObject element) => (string?)element.GetValue(TintProperty);

    public static void SetTint(DependencyObject element, string? value) => element.SetValue(TintProperty, value);
}

/// <summary>Turns a sidebar item's target page (a panel view type) into its section, for the sidebar icon's colour.</summary>
[ValueConversion(typeof(Type), typeof(string))]
public sealed class PanelSectionConverter : IValueConverter
{
    public static PanelSectionConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DcSections.OfPanelView(value as Type);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a command-palette command id into the glyph its row shows.</summary>
[ValueConversion(typeof(string), typeof(SymbolRegular))]
public sealed class CommandIconConverter : IValueConverter
{
    public static CommandIconConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DcSections.OfCommand(value as string).Icon;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a command-palette command id into the section its icon is tinted for.</summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class CommandSectionConverter : IValueConverter
{
    public static CommandSectionConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DcSections.OfCommand(value as string).Section;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
