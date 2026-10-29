using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// A sidebar entry's platform word (CUST-383): the title, then a small neutral chip ("Windows" on the Windows-only Agents panel, "Linux/macOS" on
/// Sandboxes). The chip is the shared <c>DcChip</c> look with <c>DcBadgeText</c>, so it follows the style and the mode like every other chip. It is
/// not announced on its own: the entry's peer has no children, its name is the title and its help text says what the word means.
/// </summary>
internal static class SidebarTag
{
    /// <summary>The name the chip carries, for a test that looks for it.</summary>
    internal const string ChipName = "SidebarTagChip";

    /// <summary>The entry's content: <paramref name="title"/> and the chip of <paramref name="tag"/>, on one line.</summary>
    public static FrameworkElement Content(string title, PanelTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        var word = new TextBlock { Text = tag.Text, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        word.SetResourceReference(FrameworkElement.StyleProperty, "DcBadgeText");

        var chip = new Border
        {
            Name = ChipName,
            Child = word,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(5, 0, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tag.Description,
        };
        chip.SetResourceReference(FrameworkElement.StyleProperty, "DcChip");

        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _ = line.Children.Add(text);
        _ = line.Children.Add(chip);
        return line;
    }
}
