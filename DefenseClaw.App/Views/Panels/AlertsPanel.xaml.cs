using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Alerts panel. Paired with
/// <see cref="ViewModels.AlertsPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Keyboard.</b> Ctrl+F is <see cref="ApplicationCommands.Find"/>'s own gesture; the panel
/// handles that command by focusing the filter box, so the shell can also aim it at this page from
/// outside (<c>ApplicationCommands.Find.Execute(null, page)</c>) regardless of where focus is. Esc
/// closes the acknowledge/dismiss review if it is open, otherwise the detail pane. A control that
/// handled Esc itself first (an open ComboBox list) keeps it: the handler listens on the bubbling
/// <c>KeyDown</c>, not the tunnelling preview event.
/// </para>
/// </summary>
public sealed partial class AlertsPanel : UserControl
{
    /// <summary>Where focus was before the review dialog took it, so closing the dialog gives it back.</summary>
    private IInputElement? _focusBeforeReview;

    public AlertsPanel()
    {
        InitializeComponent();

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        KeyDown += OnKeyDown;
        ReviewScrim.IsVisibleChanged += OnReviewVisibleChanged;
    }

    /// <summary>Moves keyboard focus to the filter box and selects its text, ready to type over.</summary>
    public void FocusFilter()
    {
        _ = FilterBox.Focus();
        Keyboard.Focus(FilterBox);
        FilterBox.SelectAll();
    }

    private void OnCanFind(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        FocusFilter();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || DataContext is not AlertsPanelViewModel viewModel)
        {
            return;
        }

        if (viewModel.IsReviewOpen)
        {
            viewModel.CancelReviewCommand.Execute(null);
            e.Handled = true;
        }
        else if (viewModel.HasSelection)
        {
            viewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// A modal dialog has to take keyboard focus with it: without this the operator opens the review
    /// with the keyboard and is left focused on a button now hidden behind the scrim. Focus goes to
    /// Cancel - the safe default - and comes back to where it was when the dialog closes.
    /// </summary>
    private void OnReviewVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _focusBeforeReview = Keyboard.FocusedElement;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ReviewCancelButton.Focus()));
            return;
        }

        var back = _focusBeforeReview;
        _focusBeforeReview = null;
        if (back is UIElement { IsVisible: true } element)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => element.Focus()));
        }
    }
}
