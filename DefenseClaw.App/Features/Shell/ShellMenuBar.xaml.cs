using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The main window's menu bar (CUST-384). It draws <see cref="ShellMenuModel"/>'s menus and nothing else: every row is a registry command, so
/// choosing one raises <see cref="CommandChosen"/> with that command and the window runs it the way it runs a palette choice.
/// <para>
/// <b>Fresh on open.</b> When a menu opens, its rows are rebuilt from <see cref="Source"/> - enabled state, reason, check mark and chord read at
/// that moment - in place, so keyboard focus and the open popup are undisturbed; a menu whose rows changed shape (a panel appeared in the sidebar) is
/// rebuilt instead. Opening a menu runs no command.
/// </para>
/// </summary>
public sealed partial class ShellMenuBar : UserControl
{
    /// <summary>How long the pointer rests on a row before its sentence shows, so moving through a menu does not flash a tooltip on every row.</summary>
    private const int ToolTipDelayMilliseconds = 900;

    private bool _built;

    public ShellMenuBar()
    {
        InitializeComponent();

        Bar.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(OnItemClick));
        Bar.AddHandler(MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnSubmenuOpened));
        Loaded += OnLoaded;
    }

    /// <summary>Builds the menus as they are now; set by the window. Null leaves the bar empty.</summary>
    internal Func<IReadOnlyList<ShellMenu>>? Source { get; set; }

    /// <summary>Raised when a row is chosen (clicked, or Enter / its access key in an open menu), with the registry command it stands for.</summary>
    internal event EventHandler<ShellCommand>? CommandChosen;

    /// <summary>The WPF menu itself (its top-level items are the menus), for the tests.</summary>
    internal Menu MenuBar => Bar;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_built)
        {
            Rebuild();
        }
    }

    /// <summary>Builds every menu from <see cref="Source"/>. A source that throws leaves what is there, and says so in the trace.</summary>
    internal void Rebuild()
    {
        if (Read() is not { } menus)
        {
            return;
        }

        Bar.Items.Clear();
        foreach (var menu in menus)
        {
            var top = new ShellMenuBarItem { Header = menu.Header, Tag = menu.Key };
            AutomationProperties.SetName(top, menu.Label);
            AutomationProperties.SetAutomationId(top, "Menu." + menu.Key);
            Populate(top, menu.Entries);
            _ = Bar.Items.Add(top);
        }

        _built = true;
    }

    /// <summary>Re-reads the menu with this key (what opening it does). Runs nothing.</summary>
    internal void Refresh(string key)
    {
        if (TopLevel(key) is not { } top || Read()?.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal)) is not { } menu)
        {
            return;
        }

        Sync(top, menu.Entries);
    }

    /// <summary>The top-level item of the menu with this key, or null.</summary>
    internal MenuItem? TopLevel(string key) =>
        Bar.Items.OfType<MenuItem>().FirstOrDefault(item => item.Tag is string tag && string.Equals(tag, key, StringComparison.Ordinal));

    private IReadOnlyList<ShellMenu>? Read()
    {
        if (Source is not { } source)
        {
            return null;
        }

        try
        {
            return source();
        }
#pragma warning disable CA1031 // A menu that cannot be read keeps the rows it has; it must not take the window down.
        catch (Exception ex)
        {
            Trace.TraceError($"Menu bar: the menus could not be built: {ex}");
            return null;
        }
#pragma warning restore CA1031
    }

    private void OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        // Only a menu of the bar re-reads; a submenu inside one (View's Style) was read with it a moment ago.
        if (e.OriginalSource is MenuItem { Tag: string key } item && Bar.Items.Contains(item))
        {
            Refresh(key);
        }
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { Tag: ShellCommand command, IsEnabled: true })
        {
            e.Handled = true;
            CommandChosen?.Invoke(this, command);
        }
    }

    // ------------------------------------------------------------------ building and syncing the rows

    private void Populate(ItemsControl parent, IReadOnlyList<ShellMenuEntry> entries)
    {
        parent.Items.Clear();
        var first = true;
        foreach (var entry in entries)
        {
            _ = parent.Items.Add(Create(entry, first));
            first = false;
        }
    }

    private object Create(ShellMenuEntry entry, bool first)
    {
        switch (entry)
        {
            case ShellMenuCommand row:
                var item = new MenuItem();
                ToolTipService.SetShowOnDisabled(item, true);
                ToolTipService.SetInitialShowDelay(item, ToolTipDelayMilliseconds);
                Apply(item, row);
                return item;

            case ShellMenuSeparator { Heading: { } heading }:
                var group = new Separator { Tag = heading, Style = (Style)FindResource("DcMenuGroupHeading") };
                if (first)
                {
                    group.Margin = new Thickness(0);
                }

                AutomationProperties.SetName(group, heading);
                return group;

            case ShellSubmenu submenu:
                var header = new MenuItem { Header = submenu.Header, Tag = submenu.Key };
                AutomationProperties.SetName(header, submenu.Label);
                AutomationProperties.SetAutomationId(header, "Menu." + submenu.Key);
                Populate(header, submenu.Entries);
                return header;

            default:
                return new Separator();
        }
    }

    /// <summary>
    /// Puts a row's state on its item: the label, the chord, enabled or not, the check mark, and the sentence (tooltip and help text: what it does, or
    /// why it is unavailable - the palette's own words).
    /// </summary>
    private static void Apply(MenuItem item, ShellMenuCommand row)
    {
        item.Header = row.Header;
        item.Tag = row.Command;
        item.InputGestureText = row.Gesture ?? string.Empty;
        item.IsEnabled = row.IsEnabled;
        item.IsCheckable = row.IsChecked is not null;
        item.IsChecked = row.IsChecked == true;
        item.ToolTip = row.Detail;

        AutomationProperties.SetAutomationId(item, "Menu." + row.Id);
        AutomationProperties.SetName(item, row.Label);
        AutomationProperties.SetHelpText(item, row.IsEnabled ? row.Detail : "Unavailable. " + row.Detail);
        AutomationProperties.SetAcceleratorKey(item, row.Gesture ?? string.Empty);
    }

    /// <summary>
    /// Brings <paramref name="parent"/>'s rows up to <paramref name="entries"/>: in place when the rows are the same ones in the same order (the usual
    /// case: only states changed), rebuilt otherwise.
    /// </summary>
    private void Sync(ItemsControl parent, IReadOnlyList<ShellMenuEntry> entries)
    {
        if (!string.Equals(Shape(parent), Shape(entries), StringComparison.Ordinal))
        {
            Populate(parent, entries);
            return;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            switch (entries[i])
            {
                case ShellMenuCommand row:
                    Apply((MenuItem)parent.Items[i], row);
                    break;
                case ShellSubmenu submenu:
                    Sync((MenuItem)parent.Items[i], submenu.Entries);
                    break;
            }
        }
    }

    /// <summary>What rows a built menu has, by id, in order: the test of "same rows".</summary>
    private static string Shape(ItemsControl parent)
    {
        var shape = new StringBuilder();
        foreach (var child in parent.Items)
        {
            _ = child switch
            {
                MenuItem { Tag: ShellCommand command } => shape.Append("row:").Append(command.Id),
                MenuItem { Tag: string key } submenu => shape.Append("sub:").Append(key).Append('[').Append(Shape(submenu)).Append(']'),
                Separator separator => shape.Append("sep:").Append(separator.Tag as string),
                _ => shape.Append('?'),
            };
            _ = shape.Append(';');
        }

        return shape.ToString();
    }

    private static string Shape(IReadOnlyList<ShellMenuEntry> entries)
    {
        var shape = new StringBuilder();
        foreach (var entry in entries)
        {
            _ = entry switch
            {
                ShellMenuCommand row => shape.Append("row:").Append(row.Id),
                ShellSubmenu submenu => shape.Append("sub:").Append(submenu.Key).Append('[').Append(Shape(submenu.Entries)).Append(']'),
                ShellMenuSeparator separator => shape.Append("sep:").Append(separator.Heading),
                _ => shape.Append('?'),
            };
            _ = shape.Append(';');
        }

        return shape.ToString();
    }
}

/// <summary>
/// One entry of the bar itself (File ... Help): WPF-UI's menu item, template, popup and all, on a 30 DIP strip. WPF-UI's top-level template has fixed
/// margins (4 around its highlight, 10 around the text) that make a 46 DIP toolbar row; they are tightened here each time the template is applied, by
/// the template parts' own names. Should a WPF-UI update rename them, nothing is changed and the bar is only taller (a test watches its height).
/// </summary>
internal sealed class ShellMenuBarItem : MenuItem
{
    /// <summary>Around the highlight: 2 DIP, so neighbouring entries' highlights do not touch.</summary>
    private static readonly Thickness HighlightMargin = new(2);

    /// <summary>Around the text: WPF-UI's width, and a little above and below.</summary>
    private static readonly Thickness TextMargin = new(10, 4, 10, 4);

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("Border") is not Border { Child: Grid layout } highlight)
        {
            return;
        }

        highlight.Margin = HighlightMargin;
        if (layout.Children.OfType<Grid>().FirstOrDefault() is { } text)
        {
            text.Margin = TextMargin;
        }
    }
}
