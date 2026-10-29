using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A stand-in for <c>MainWindow</c> that keeps the parts which decide how much room a panel gets — the 32 DIP
/// title bar, the status strip with its one icon button (CUST-383), and the <c>NavigationView</c> with its 218 DIP open
/// pane, configured exactly as <c>MainWindow.xaml</c> configures it — and hosts one real panel from the real
/// <see cref="PanelCatalog"/>. <c>MainWindow</c> itself is not built: its constructor needs the tray icon.
/// <para>
/// At the window's 940 x 620 DIP minimum the panel host measures about 713 x 547 DIP. UI thread only.
/// </para>
/// </summary>
internal sealed class PanelShell : IDisposable
{
    private const string ShellXaml = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>
            <Border Grid.Row="0" Height="32" />
            <Border Grid.Row="2" Style="{StaticResource DcStatusStrip}">
                <StackPanel Style="{StaticResource DcToolbarActions}" HorizontalAlignment="Right">
                    <ui:Button Icon="{ui:SymbolIcon ArrowSync24}" />
                </StackPanel>
            </Border>
            <ui:NavigationView x:Name="RootNavigation"
                               Grid.Row="4"
                               IsBackButtonVisible="Collapsed"
                               IsPaneToggleVisible="False"
                               OpenPaneLength="218"
                               PaneDisplayMode="Left" />
        </Grid>
        """;

    private readonly Grid _root;
    private readonly NavigationView _navigation;
    private readonly PanelCatalog _catalog;

    public PanelShell(AppServices services, double windowWidth, double windowHeight)
    {
        _catalog = new PanelCatalog(services);
        _root = (Grid)XamlReader.Parse(ShellXaml);
        _navigation = (NavigationView)_root.FindName("RootNavigation");
        _navigation.SetPageProviderService(_catalog);
        _ = _navigation.MenuItems.Add(new NavigationViewItem { Content = "Panel" });

        Host = new OffscreenHost(_root, windowWidth, windowHeight);
    }

    public OffscreenHost Host { get; }

    public FrameworkElement? Page { get; private set; }

    /// <summary>Navigates to the panel with the given view type and returns its view; its view-model is <see cref="ViewModel"/>.</summary>
    public T Show<T>()
        where T : FrameworkElement
    {
        _ = _navigation.Navigate(typeof(T));
        Host.Relayout();
        Page = VisualTree.Find<T>(_root) ?? throw new InvalidOperationException($"{typeof(T).Name} was not hosted.");
        return (T)Page;
    }

    public PanelViewModelBase ViewModel =>
        (PanelViewModelBase)(Page?.DataContext ?? throw new InvalidOperationException("No panel is shown."));

    /// <summary>Size of the hosted page: what the panel really has to lay out in.</summary>
    public Size PageSize => new(Page!.ActualWidth, Page.ActualHeight);

    public void Dispose() => Host.Dispose();
}
