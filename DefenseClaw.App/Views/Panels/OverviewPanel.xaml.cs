using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// View for the Overview panel. Paired with
/// <see cref="ViewModels.OverviewPanelViewModel"/>; the shell binds the two together in
/// <see cref="Services.PanelCatalog"/>. Esc closes the review dialog (Scan Skills, the gateway actions) from anywhere in the panel.
/// </summary>
public sealed partial class OverviewPanel : UserControl
{
    public OverviewPanel()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => FocusDoctorIfAsked();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is OverviewPanelViewModel old)
        {
            old.DoctorFocusRequested -= OnDoctorFocusRequested;
        }

        if (e.NewValue is OverviewPanelViewModel current)
        {
            current.DoctorFocusRequested += OnDoctorFocusRequested;
        }
    }

    private void OnDoctorFocusRequested(object? sender, EventArgs e) => FocusDoctorIfAsked();

    /// <summary>The palette's "Run doctor": bring the Doctor card's button into view and focus it. Presses nothing.</summary>
    private void FocusDoctorIfAsked()
    {
        if (DataContext is OverviewPanelViewModel { DoctorFocusPending: true } viewModel && IsLoaded)
        {
            viewModel.DoctorFocusPending = false;
            DoctorRunButton.BringIntoView();
            _ = DoctorRunButton.Focus();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is OverviewPanelViewModel viewModel && viewModel.HandleEscape())
        {
            e.Handled = true;
        }
    }
}
