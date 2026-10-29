using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Settings panel. Paired with <see cref="ViewModels.SettingsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>. Reached from the footer of the sidebar, from Ctrl+, and from the tray flyout's gear.
/// <para>
/// Four areas (CUST-386), one scroller each so each keeps its own place. Ctrl+Tab and Ctrl+Shift+Tab (and Ctrl+Page Down / Page Up) move between
/// them from anywhere on the page, as in a Windows property sheet; a <see cref="SettingsAreaLink"/> with a section scrolls that block into view.
/// </para>
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    private SettingsPanelViewModel? _model;

    public SettingsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.SectionRequested -= OnSectionRequested;
        }

        _model = e.NewValue as SettingsPanelViewModel;
        if (_model is not null)
        {
            _model.SectionRequested += OnSectionRequested;
        }
    }

    /// <summary>Ctrl+Tab / Ctrl+Page Down: the next area; with Shift, or Ctrl+Page Up, the one before. Wraps, like a tab strip.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            return;
        }

        var step = e.Key switch
        {
            Key.Tab => (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1,
            Key.PageDown => 1,
            Key.PageUp => -1,
            _ => 0,
        };

        if (step == 0)
        {
            return;
        }

        var areas = Enum.GetValues<SettingsArea>();
        var next = (Array.IndexOf(areas, _model.SelectedArea) + step + areas.Length) % areas.Length;
        _model.SelectedArea = areas[next];
        e.Handled = true;
    }

    /// <summary>Scrolls the asked-for block into view once its area has been laid out (it may have been collapsed a moment ago).</summary>
    private void OnSectionRequested(object? sender, string section)
    {
        FrameworkElement? target = section switch
        {
            SettingsAreaLink.InstallationSection => InstallationCard,
            _ => null,
        };

        if (target is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => target.BringIntoView(new Rect(0, 0, target.ActualWidth, target.ActualHeight))));
    }
}
