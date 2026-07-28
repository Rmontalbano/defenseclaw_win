using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the MCPs panel. Paired with
/// <see cref="ViewModels.McpsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// </summary>
public partial class McpsPanel : UserControl
{
    public McpsPanel()
    {
        InitializeComponent();
    }
}

/// <summary>
/// Panel-local visibility converter: <see cref="Visibility.Collapsed"/> for null, an empty
/// or whitespace-only string, a zero count, or <see langword="false"/>; <see cref="Visibility.Visible"/>
/// for anything else. Kept local to this panel rather than added to <c>App.xaml</c>'s
/// shared resources, per the panel contract (only this panel's own files change).
/// </summary>
public sealed class McpsTruthyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            _ => true,
        };

        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
