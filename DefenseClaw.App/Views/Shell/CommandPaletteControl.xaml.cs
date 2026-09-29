using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The command palette overlay. All state lives in <see cref="CommandPaletteViewModel"/>; this
/// file only does what a view has to: put keyboard focus in the search box when the overlay
/// appears, translate arrow/Enter/Esc into view-model calls, keep the highlighted row scrolled into
/// view, and tell UI Automation when the polite status line changes.
/// </summary>
public partial class CommandPaletteControl : UserControl
{
    private CommandPaletteViewModel? _viewModel;

    public CommandPaletteControl()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Puts the caret in the search box with the old text selected, so typing replaces it.</summary>
    public void FocusSearch()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _ = SearchBox.Focus();
            _ = Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }));
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as CommandPaletteViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            FocusSearch();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommandPaletteViewModel.Selected) when _viewModel?.Selected is { } selected:
                ResultsList.ScrollIntoView(selected);
                break;

            case nameof(CommandPaletteViewModel.StatusLine):
                // No peer exists unless a UI Automation client is attached, so this is free otherwise.
                UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                break;
        }
    }

    /// <summary>
    /// Esc closes and Enter runs from wherever focus is inside the palette; the arrow keys move the
    /// highlight only while the search box has focus (a focused list handles its own arrows, and its
    /// selection flows back through the two-way binding).
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.RequestClose();
                e.Handled = true;
                break;

            case Key.Enter when _viewModel.ChooseSelected():
                e.Handled = true;
                break;

            case Key.Down when SearchBox.IsKeyboardFocusWithin:
                _viewModel.MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up when SearchBox.IsKeyboardFocusWithin:
                _viewModel.MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.PageDown when SearchBox.IsKeyboardFocusWithin:
                _viewModel.MoveSelection(6);
                e.Handled = true;
                break;

            case Key.PageUp when SearchBox.IsKeyboardFocusWithin:
                _viewModel.MoveSelection(-6);
                e.Handled = true;
                break;
        }
    }

    private void OnScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Only a click on the veil itself dismisses; clicks inside the card land on the card.
        if (ReferenceEquals(e.OriginalSource, Scrim))
        {
            _viewModel?.RequestClose();
            e.Handled = true;
        }
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: PaletteItem item } && _viewModel is not null && _viewModel.Choose(item))
        {
            e.Handled = true;
        }
    }
}
