using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// The status strip's row as <c>MainWindow</c> lays it out - the state pill, the real <see cref="StatusStripControl"/> in the star column, the three
/// buttons - hosted offscreen at an exact width in DIPs. <c>MainWindow</c> itself is not built (its constructor needs the tray icon); the pill is the
/// window's markup with its text fixed, and the buttons are the real WPF-UI ones with the real icons, so the room the chips get is the room they get in
/// the window. UI thread only.
/// </summary>
internal sealed class StripRow : IDisposable
{
    private const string RowXaml = """
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                xmlns:shell="clr-namespace:DefenseClaw.App.Views.Shell;assembly=DefenseClaw.App"
                xmlns:ctl="clr-namespace:DefenseClaw.App.Views.Controls;assembly=DefenseClaw.App"
                Style="{StaticResource DcStatusStrip}">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <Border Grid.Column="0" Style="{StaticResource DcBadge}" Tag="Ok" Padding="10,4,12,4" VerticalAlignment="Center">
                    <StackPanel Orientation="Horizontal">
                        <ctl:DcStatusGlyph Tone="Ok" Margin="0,0,8,0" />
                        <TextBlock Style="{StaticResource DcBadgeText}" FontSize="13" VerticalAlignment="Center" Text="Running" />
                    </StackPanel>
                </Border>
                <shell:StatusStripControl x:Name="Strip" Grid.Column="1" Margin="12,0,12,0" VerticalAlignment="Center" />
                <StackPanel x:Name="Buttons" Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                    <ui:Button Content="Refresh" Icon="{ctl:DcSymbolIcon ArrowSync24}" />
                    <ui:Button Margin="8,0,0,0" Icon="{ctl:DcSymbolIcon Search24}" />
                    <ui:Button Margin="8,0,0,0" Icon="{ctl:DcSymbolIcon Keyboard24}" />
                </StackPanel>
            </Grid>
        </Border>
        """;

    private readonly Border _root;

    public StripRow(StatusStripViewModel viewModel, double width)
    {
        _root = (Border)XamlReader.Parse(RowXaml);
        Strip = (StatusStripControl)_root.FindName("Strip");
        Buttons = (StackPanel)_root.FindName("Buttons");
        Strip.DataContext = viewModel;
        Host = new OffscreenHost(_root, width, 49);
    }

    public OffscreenHost Host { get; }

    public StatusStripControl Strip { get; }

    public StackPanel Buttons { get; }

    /// <summary>The strip's panel, once the control has been laid out.</summary>
    public ChipStripPanel Panel => VisualTree.Find<ChipStripPanel>(Strip) ?? throw new InvalidOperationException("The strip has no panel yet.");

    /// <summary>Every chip's container, in the order the chips are drawn.</summary>
    public IReadOnlyList<ContentPresenter> Containers
    {
        get
        {
            var panel = Panel;
            return Enumerable.Range(0, System.Windows.Media.VisualTreeHelper.GetChildrenCount(panel))
                .Select(i => System.Windows.Media.VisualTreeHelper.GetChild(panel, i))
                .OfType<ContentPresenter>()
                .Where(static c => c.Content is StripChip)
                .ToList();
        }
    }

    /// <summary>The container that holds <paramref name="key"/>'s chip.</summary>
    public ContentPresenter Container(StripChipKey key) =>
        Containers.Single(c => ((StripChip)c.Content).Key == key);

    /// <summary>True when the panel has hidden the chip because it does not fit.</summary>
    public bool IsHidden(StripChipKey key) => ChipStripPanel.GetIsCollapsed(Container(key));

    /// <summary>The chips that show and are not hidden, in drawing order.</summary>
    public IReadOnlyList<StripChipKey> Visible =>
        Containers
            .Where(static c => ((StripChip)c.Content).IsShown && !ChipStripPanel.GetIsCollapsed(c))
            .Select(static c => ((StripChip)c.Content).Key)
            .ToList();

    /// <summary>The chips the panel hid for want of room (not the ones that have nothing to say).</summary>
    public IReadOnlyList<StripChipKey> Hidden =>
        Containers
            .Where(static c => ((StripChip)c.Content) is { IsShown: true, Key: not StripChipKey.Overflow } && ChipStripPanel.GetIsCollapsed(c))
            .Select(static c => ((StripChip)c.Content).Key)
            .ToList();

    /// <summary>Resizes the window the row stands in for, and lets the layout and the panel's follow-up (the +N chip) settle.</summary>
    public void Resize(double width) => Host.Resize(width, 49);

    public void Dispose() => Host.Dispose();
}
