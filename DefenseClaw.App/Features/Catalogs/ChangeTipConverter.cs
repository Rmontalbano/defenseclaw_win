using System.Globalization;
using System.Windows.Data;

namespace DefenseClaw.App.Views.Panels.Govern;

/// <summary>
/// The tooltip of a button or menu item that changes something: normally the sentence written next to it (the converter parameter),
/// and, while the panel's list may not authorize a change (partial, failed, old - <see cref="ViewModels.CatalogTrust"/>) or the installation is
/// read-only (<c>PanelViewModelBase.InstallationBlockedReason</c>, which comes first), the reason it is off. Bound to the reason (a string,
/// null while changes are allowed).
/// </summary>
public sealed class ChangeTipConverter : IValueConverter
{
    public static readonly ChangeTipConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } reason ? reason : parameter as string;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
