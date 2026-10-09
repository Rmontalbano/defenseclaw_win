using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the AI Discovery panel. Paired with
/// <see cref="ViewModels.AiDiscoveryPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Keyboard.</b> Ctrl+F focuses the search box; Esc closes the review dialog first (tunnelling, as the dialog is modal) and
/// then, on the bubbling <c>KeyDown</c>, the model inspector - a drop-down or a text box that used Esc itself keeps it.
/// </para>
/// <para>
/// <b>Models table columns.</b> Modality, Owners and Relevance are declared in the XAML and shown here, only while some model has one
/// (<see cref="AiDiscoveryPanelViewModel.HasModalityFilter"/>, <see cref="AiDiscoveryPanelViewModel.HasOwnerData"/>,
/// <see cref="AiDiscoveryPanelViewModel.HasRelevanceFilter"/>): a column the data cannot fill is not offered empty.
/// </para>
/// <para>
/// <b>Focus in the compact layout.</b> On a narrow panel (<see cref="CompactLayout"/>) selecting a model swaps the page for the
/// inspector, so the row that had focus is gone; focus goes to the inspector's close button when it opens, and back to the table when
/// it closes.
/// </para>
/// </summary>
public partial class AiDiscoveryPanel : UserControl
{
    public AiDiscoveryPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True while the panel is narrow enough that a selected model's details replace the page instead of sitting beside it.</summary>
    public bool IsCompact => CompactLayout.GetIsCompact(this);

    /// <summary>Ctrl+F focuses the search box; Esc closes the review dialog.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (DataContext is AiDiscoveryPanelViewModel { Review.IsOpen: true })
            {
                return;
            }

            _ = SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && DataContext is AiDiscoveryPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }

    /// <summary>Esc closes the model inspector, unless something inside the panel (an open drop-down) used it first.</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && DataContext is AiDiscoveryPanelViewModel { HasModelSelection: true } viewModel)
        {
            viewModel.ClearModelSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The models table is a box of its own inside a scrolling page: with the pointer over it the wheel scrolls it and, at its first or
    /// last row, would stop there and leave the page stuck. At an edge the wheel goes on to the page instead.
    /// </summary>
    private void OnModelListWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject grid || FindScrollViewer(grid) is not { } inner)
        {
            return;
        }

        var atEdge = e.Delta > 0 ? inner.VerticalOffset <= 0 : inner.VerticalOffset >= inner.ScrollableHeight;
        if (!atEdge)
        {
            return;
        }

        e.Handled = true;
        for (var line = 0; line < SystemParameters.WheelScrollLines; line++)
        {
            if (e.Delta > 0)
            {
                PageScroll.LineUp();
            }
            else
            {
                PageScroll.LineDown();
            }
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged old)
        {
            old.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged current)
        {
            current.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyColumns();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AiDiscoveryPanelViewModel.HasModalityFilter):
            case nameof(AiDiscoveryPanelViewModel.HasOwnerData):
            case nameof(AiDiscoveryPanelViewModel.HasRelevanceFilter):
                ApplyColumns();
                break;

            case nameof(AiDiscoveryPanelViewModel.HasModelSelection) when IsCompact && sender is AiDiscoveryPanelViewModel viewModel:
                var opened = viewModel.HasModelSelection;
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() =>
                    {
                        UIElement? target = opened ? ModelInspector.CloseButton : ModelList;
                        _ = target?.Focus();
                    }));
                break;
        }
    }

    /// <summary>Shows the optional columns of the models table (Owners, Modality, Relevance) when some model has a value for them, and hides each otherwise.</summary>
    private void ApplyColumns()
    {
        if (DataContext is not AiDiscoveryPanelViewModel viewModel)
        {
            return;
        }

        OwnersColumn.Visibility = viewModel.HasOwnerData ? Visibility.Visible : Visibility.Collapsed;
        ModalityColumn.Visibility = viewModel.HasModalityFilter ? Visibility.Visible : Visibility.Collapsed;
        RelevanceColumn.Visibility = viewModel.HasRelevanceFilter ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer self)
        {
            return self;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
