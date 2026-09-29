using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// The confirm-and-run dialog the Discover panels share. All state lives in the bound
/// <see cref="DiscoverActionReview"/>; this code only manages keyboard focus: the safe button gets
/// focus when the dialog opens (Cancel for a destructive command), Close gets it when the run
/// finishes, and Esc dismisses the dialog.
/// </summary>
public partial class DiscoverReviewOverlay : UserControl
{
    private DiscoverActionReview? _review;

    public DiscoverReviewOverlay()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_review is not null)
        {
            _review.PropertyChanged -= OnReviewPropertyChanged;
        }

        _review = e.NewValue as DiscoverActionReview;
        if (_review is not null)
        {
            _review.PropertyChanged += OnReviewPropertyChanged;
        }
    }

    private void OnReviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_review is null)
        {
            return;
        }

        // Wait for the visibility bindings to settle, then move focus to the button that should have it.
        if (e.PropertyName == nameof(DiscoverActionReview.IsOpen) && _review.IsOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Control target = _review.IsDestructive ? CancelButton : RunButton;
                _ = target.Focus();
            }));
        }
        else if (e.PropertyName == nameof(DiscoverActionReview.IsFinished) && _review.IsFinished)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _ = CloseButton.Focus()));
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _review is { IsOpen: true } review)
        {
            _ = review.HandleEscape();
            e.Handled = true;
        }
    }
}
