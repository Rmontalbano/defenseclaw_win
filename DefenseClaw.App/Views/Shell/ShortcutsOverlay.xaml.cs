using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The keyboard-shortcuts overlay. Purely presentational: the shell hands it a
/// <c>ShortcutsModel</c> as its DataContext and listens for <see cref="CloseRequested"/>.
/// </summary>
public partial class ShortcutsOverlay : UserControl
{
    public ShortcutsOverlay()
    {
        InitializeComponent();

        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                FocusClose();
            }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        };
    }

    /// <summary>Raised by Esc, the Close button, or a click on the veil.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Moves keyboard focus into the card so Esc and Tab work from the first keypress.</summary>
    public void FocusClose()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _ = CloseButton.Focus()));
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Scrim))
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
