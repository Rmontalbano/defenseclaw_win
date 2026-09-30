using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DefenseClaw.App.ViewModels;

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
    public AuditPanel()
    {
        InitializeComponent();

        _ = CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, OnFind, OnCanFind));
        KeyDown += OnKeyDown;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>True while the panel is narrow enough that a selected row's detail replaces the list instead of sitting beside it.</summary>
    public bool IsCompact => CompactLayout.GetIsCompact(this);

    /// <summary>Moves keyboard focus to the search box (the toolbar's) and selects its text, ready to type over.</summary>
    public void FocusFilter() => PageToolbar.FocusSearch();

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
                UIElement target = opened ? DetailClose : RowList;
                _ = target.Focus();
            }));
    }
}
