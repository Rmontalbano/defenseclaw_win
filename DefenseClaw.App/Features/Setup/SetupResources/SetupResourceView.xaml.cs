using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DefenseClaw.App.ViewModels.SetupResources;

namespace DefenseClaw.App.Views.SetupResources;

/// <summary>
/// The content of the Setup list editors (see <see cref="SetupResourceViewModel"/>). The columns of the list are data - the view-model names
/// them - so they are built here when the view-model arrives. Keys: F5 reads the list again, Delete reviews a Remove (it opens the review; it
/// runs nothing), Esc closes the review, then the notice, then the details. <b>Enter and a double click on a row only show it</b>
/// (<see cref="SetupResourceViewModel.ActivateRowCommand"/>): they never start a test and never a change.
/// </summary>
public partial class SetupResourceView : UserControl
{
    private SetupResourceViewModel? _viewModel;

    public SetupResourceView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True when Esc would close something inside the view (the review), so the hosting window should stay open.</summary>
    public bool HandlesEscape => DataContext is SetupResourceViewModel viewModel && (viewModel.Review.IsOpen || viewModel.HasNotice || viewModel.HasSelection);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _viewModel = e.NewValue as SetupResourceViewModel;
        RowList.Columns.Clear();
        if (_viewModel is null)
        {
            return;
        }

        foreach (var column in _viewModel.Columns)
        {
            RowList.Columns.Add(MakeColumn(column));
        }
    }

    private DataGridColumn MakeColumn(SetupColumn spec)
    {
        DataGridColumn column = spec.Kind == SetupColumnKind.State
            ? new DataGridTemplateColumn { CellTemplate = (DataTemplate)FindResource("StateCell") }
            : new DataGridTextColumn
            {
                Binding = new Binding($"Cells[{spec.Key}]") { Mode = BindingMode.OneWay },
                ElementStyle = (Style)FindResource(spec.Kind switch
                {
                    SetupColumnKind.Mono => "DcCellTextMono",
                    SetupColumnKind.Quiet => "DcCellTextQuiet",
                    _ => "DcCellText",
                }),
            };

        column.Header = spec.Header;
        column.Width = new DataGridLength(spec.Weight, DataGridLengthUnitType.Star);
        column.MinWidth = spec.MinWidth;
        column.CanUserSort = false;
        return column;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when viewModel.HandleEscape():
                e.Handled = true;
                break;

            case Key.F5 when viewModel.RefreshCommand.CanExecute(null):
                _ = viewModel.RefreshCommand.ExecuteAsync(null);
                e.Handled = true;
                break;

            // Enter on a row shows it, as the TUI's does. It never tests: the row's command is ActivateRow, which has no way to start one.
            case Key.Enter when e.OriginalSource is DataGridCell or DataGridRow or DataGrid:
                e.Handled = true;
                _ = viewModel.ActivateRowCommand.ExecuteAsync(null);
                break;

            // Delete asks for a removal the way the Remove button does: a review opens, nothing is removed.
            case Key.Delete when e.OriginalSource is DataGridCell or DataGridRow or DataGrid && viewModel.HasRemove:
                e.Handled = true;
                viewModel.RemoveCommand.Execute(null);
                break;
        }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is { } viewModel && sender is DataGridRow row && ReferenceEquals(row.Item, viewModel.SelectedRow))
        {
            e.Handled = true;
            _ = viewModel.ActivateRowCommand.ExecuteAsync(null);
        }
    }
}
