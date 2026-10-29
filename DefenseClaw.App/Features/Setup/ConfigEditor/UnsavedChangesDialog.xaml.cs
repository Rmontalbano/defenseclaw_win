using System;
using System.Windows;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.ViewModels.ConfigEditor;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// The "you have unsaved changes" question, as a small modal window (see the XAML for why it is not a
/// <c>MessageBox</c>). It only renders an <see cref="UnsavedChangesRequest"/> and reports which button was pressed;
/// what Save, Discard and Cancel <i>do</i>, and every default, live in
/// <see cref="ConfigEditorWindowViewModel.ResolveUnsavedChangesAsync"/> and <see cref="UnsavedChangesRequest.Create"/>.
/// Any way of dismissing it other than the Save and Discard buttons — Esc, the title-bar X, Alt+F4 — is Cancel.
/// </summary>
public sealed partial class UnsavedChangesDialog : FluentWindow
{
    private UnsavedChangesChoice _choice = UnsavedChangesChoice.Cancel;

    internal UnsavedChangesDialog(UnsavedChangesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        InitializeComponent();
        AppearanceService.Current?.Attach(this);

        HeadingText.Text = request.Heading;
        MessageText.Text = request.Message;

        WarningBar.IsOpen = request.Warning is { Length: > 0 };
        WarningBar.Message = request.Warning ?? string.Empty;

        DiscardButton.Content = request.DiscardLabel;

        SaveButton.Visibility = request.CanSave ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Content = request.SaveLabel ?? "Save";

        // Enter presses the default; the safe answer takes focus so a stray Enter cannot discard anything.
        var saveIsDefault = request.DefaultChoice == UnsavedChangesChoice.Save && request.CanSave;
        var defaultButton = saveIsDefault ? SaveButton : CancelButton;
        defaultButton.IsDefault = true;

        // The accent colour follows Enter, so the button that looks primary is the one Enter presses.
        SaveButton.Appearance = saveIsDefault ? ControlAppearance.Primary : ControlAppearance.Secondary;
        CancelButton.Appearance = saveIsDefault ? ControlAppearance.Secondary : ControlAppearance.Primary;
        Loaded += (_, _) => _ = defaultButton.Focus();
    }

    /// <summary>
    /// Shows the question over <paramref name="owner"/> and returns the answer. Modal: the editor cannot be touched
    /// (or closed again) while it is up.
    /// </summary>
    internal static UnsavedChangesChoice Ask(Window? owner, UnsavedChangesRequest request)
    {
        var dialog = new UnsavedChangesDialog(request);

        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _ = dialog.ShowDialog();
        return dialog._choice;
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        _choice = UnsavedChangesChoice.Save;
        DialogResult = true;
    }

    private void OnDiscardClicked(object sender, RoutedEventArgs e)
    {
        _choice = UnsavedChangesChoice.Discard;
        DialogResult = true;
    }
}
