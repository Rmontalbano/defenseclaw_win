using System.Reflection;
using System.Text.RegularExpressions;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// WPF binds only to public properties. A binding in MainWindow.xaml to a member of <see cref="MainWindowViewModel"/> that exists but is not public
/// fails silently: the self-update banner bound its DataContext to an internal property, so it inherited the window's view model, its
/// <c>ShowBanner</c> binding failed (Visibility stayed Visible) and the bar showed empty with buttons that did nothing.
/// </summary>
public sealed partial class MainWindowBindingTests
{
    [Fact]
    public void Every_main_window_binding_to_a_view_model_member_names_a_public_property()
    {
        var xaml = AppSource.Read("MainWindow.xaml");
        var type = typeof(MainWindowViewModel);
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var hidden = BindingPath().Matches(xaml)
            .Select(match => match.Groups["first"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(name => type.GetProperty(name, all) is { } property && property.GetMethod?.IsPublic != true)
            .ToList();

        Assert.Empty(hidden);
    }

    [Fact]
    public void Every_data_context_in_the_main_window_resolves_to_a_public_property()
    {
        var xaml = AppSource.Read("MainWindow.xaml");

        foreach (Match match in DataContextPath().Matches(xaml))
        {
            var name = match.Groups["first"].Value;
            var property = typeof(MainWindowViewModel).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            Assert.True(property is not null, $"DataContext=\"{{Binding {name}}}\" does not name a public property of MainWindowViewModel.");
            Assert.True(property!.PropertyType.IsVisible, $"{name}'s type {property.PropertyType.Name} is not public.");
        }
    }

    [GeneratedRegex(@"\{Binding\s+(?:Path=)?(?<first>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex BindingPath();

    [GeneratedRegex(@"DataContext=""\{Binding\s+(?:Path=)?(?<first>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex DataContextPath();
}
