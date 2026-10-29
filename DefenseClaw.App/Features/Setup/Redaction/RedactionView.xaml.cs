using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels.Redaction;

namespace DefenseClaw.App.Views.Redaction;

/// <summary>
/// The redaction policy window's content; see <see cref="RedactionViewModel"/>. Esc closes the open review first (the shared review
/// control handles it while it has focus; this covers focus elsewhere), F5 reads the policy again, and a new result is scrolled into view:
/// the forms are above it, and a preview that appeared below the fold would look like nothing happened.
/// </summary>
public partial class RedactionView : UserControl
{
    private RedactionViewModel? _viewModel;

    public RedactionView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True when Esc was used to close a review, so the hosting window should stay open.</summary>
    public bool HandlesEscape => DataContext is RedactionViewModel { Review.IsOpen: true };

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as RedactionViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RedactionViewModel.Outcome) && _viewModel?.Outcome is not null)
        {
            // After layout has made room for it.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => OutcomeCard.BringIntoView());
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not RedactionViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && viewModel.RefreshCommand.CanExecute(null))
        {
            _ = viewModel.RefreshCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}
