using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Audit panel. Paired with
/// <see cref="ViewModels.AuditPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>.
/// <para>
/// <b>Keyboard.</b> Ctrl+F is <see cref="ApplicationCommands.Find"/>'s own gesture; the panel handles
/// that command by focusing the search box, so the shell can also aim it at this page from outside
/// (<c>ApplicationCommands.Find.Execute(null, page)</c>). Esc closes the detail pane; a control that
/// handled Esc itself first (an open ComboBox list) keeps it, because the handler is on the bubbling
/// <c>KeyDown</c>.
/// </para>
/// <para>
/// <b>Focus in the compact layout.</b> On a narrow panel (<see cref="CompactLayout"/>) selecting a row swaps the list for the
/// detail, so the row that had focus is gone and focus would fall to nothing - Esc, which is handled here, would never arrive.
/// Focus goes to the detail's close button when the detail opens, and back to the list when it closes.
/// </para>
/// </summary>
public sealed partial class AuditPanel : UserControl
{
    /// <summary>Ctrl+E: export the list (see <see cref="AuditPanelViewModel"/>'s <c>ExportCommand</c>). Like Find, it works wherever focus is inside the panel.</summary>
    public static readonly RoutedUICommand ExportShortcut = new(
        "Export audit events",
        nameof(ExportShortcut),
        typeof(AuditPanel),
        new InputGestureCollection { new KeyGesture(Key.E, ModifierKeys.Control, "Ctrl+E") });

    public AuditPanel()
    {
        InitializeComponent();

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        _ = CommandBindings.Add(new CommandBinding(ExportShortcut, OnExport, OnCanFind));
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True while the panel is narrow enough that a selected row's detail replaces the list instead of sitting beside it.</summary>
    public bool IsCompact => CompactLayout.GetIsCompact(this);

    /// <summary>Moves keyboard focus to the search box (the toolbar's) and selects its text, ready to type over.</summary>
    public void FocusFilter() => PageToolbar.FocusSearch();

    /// <summary>The table's selection is a set (Extended): the view-model keeps all of it for the row menu; the first row still drives the detail pane.</summary>
    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AuditPanelViewModel viewModel)
        {
            viewModel.NoteSelection(RowList.SelectedItems.OfType<AuditRow>().OrderBy(RowList.Items.IndexOf));
        }
    }

    private void OnCopyDetails(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuditPanelViewModel { ActionRows.Count: > 0 } viewModel)
        {
            _ = DcClipboard.TrySetText(AuditPanelViewModel.CopyDetailsText(viewModel.ActionRows));
        }
    }

    private void OnCopyStructuredJson(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuditPanelViewModel { ActionRows.Count: > 0 } viewModel)
        {
            _ = DcClipboard.TrySetText(AuditPanelViewModel.CopyStructuredJsonText(viewModel.ActionRows));
        }
    }

    private void OnShowSameTarget(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuditPanelViewModel { ActionRows.Count: > 0 } viewModel)
        {
            viewModel.ShowSameTarget(viewModel.ActionRows[0]);
        }
    }

    private void OnShowSameRun(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuditPanelViewModel { ActionRows.Count: > 0 } viewModel)
        {
            viewModel.ShowSameRun(viewModel.ActionRows[0]);
        }
    }

    private void OnExport(object sender, ExecutedRoutedEventArgs e)
    {
        if (DataContext is AuditPanelViewModel viewModel && viewModel.ExportCommand.CanExecute(null))
        {
            viewModel.ExportCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCanFind(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        FocusFilter();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled && DataContext is AuditPanelViewModel { HasSelection: true } viewModel)
        {
            viewModel.ClearSelectionCommand.Execute(null);
            e.Handled = true;
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
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AuditPanelViewModel.HasSelection) || !IsCompact || sender is not AuditPanelViewModel viewModel)
        {
            return;
        }

        var opened = viewModel.HasSelection;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                UIElement? target = opened ? Inspector.CloseButton : RowList;
                _ = target?.Focus();
            }));
    }
}
