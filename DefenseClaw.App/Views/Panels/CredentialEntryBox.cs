using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Views.Panels;

/// <summary>
/// Connects the masked box under a credential row to its <see cref="CredentialRowViewModel"/> without a binding (CUST-221), the way
/// <c>SecretEntryBox</c> does for a wizard's secret field.
/// <para>
/// <b>Why not a binding.</b> <see cref="PasswordBox.Password"/> is deliberately not bindable, and a two-way string binding would put the
/// value in a <c>string</c> property of the view-model. The box instead reports each change by handing over its own
/// <see cref="PasswordBox.SecurePassword"/> copy, and the row holds that <see cref="System.Security.SecureString"/> until the confirmed run has used it.
/// </para>
/// <para>
/// The other direction is one event: when the row drops the value (the run took it, Cancel, the list was read again, the installation turned
/// read-only) it raises <see cref="CredentialRowViewModel.EntryCleared"/> and the box empties itself, so what is on screen never claims a value the
/// app no longer holds. The box takes the keyboard when its row opens it, so Set, type, Tab, Enter is the whole gesture. Set
/// <c>panels:CredentialEntryBox.Attach="True"</c> on a password box whose <c>DataContext</c> is the row.
/// </para>
/// </summary>
public static class CredentialEntryBox
{
    public static readonly DependencyProperty AttachProperty = DependencyProperty.RegisterAttached(
        "Attach",
        typeof(bool),
        typeof(CredentialEntryBox),
        new PropertyMetadata(false, OnAttachChanged));

    /// <summary>The subscription this box holds on its row, so it can be undone.</summary>
    private static readonly DependencyProperty LinkProperty = DependencyProperty.RegisterAttached(
        "Link",
        typeof(Link),
        typeof(CredentialEntryBox));

    public static bool GetAttach(DependencyObject element) => (bool)element.GetValue(AttachProperty);

    public static void SetAttach(DependencyObject element, bool value) => element.SetValue(AttachProperty, value);

    private static void OnAttachChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not PasswordBox box)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            box.PasswordChanged += OnPasswordChanged;
            box.DataContextChanged += OnDataContextChanged;
            box.IsVisibleChanged += OnIsVisibleChanged;
            box.Loaded += OnLoaded;
            box.Unloaded += OnUnloaded;
            Listen(box);
        }
        else
        {
            box.PasswordChanged -= OnPasswordChanged;
            box.DataContextChanged -= OnDataContextChanged;
            box.IsVisibleChanged -= OnIsVisibleChanged;
            box.Loaded -= OnLoaded;
            box.Unloaded -= OnUnloaded;
            StopListening(box);
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        // SecurePassword is a copy; the row takes ownership of it.
        if (sender is PasswordBox { DataContext: CredentialRowViewModel row } box)
        {
            row.SetEntry(box.SecurePassword);
        }
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The row opened its box: the cursor goes in, once the box is laid out.
        if (sender is PasswordBox box && e.NewValue is true)
        {
            _ = box.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (box.IsVisible && box.IsEnabled)
                {
                    _ = box.Focus();
                }
            }));
        }
    }

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            StopListening(box);
            Listen(box);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            Listen(box);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // The template is torn down when the operator leaves the page; do not keep the row alive from here.
        if (sender is PasswordBox box)
        {
            StopListening(box);
        }
    }

    private static void Listen(PasswordBox box)
    {
        if (box.DataContext is not CredentialRowViewModel row || box.GetValue(LinkProperty) is not null)
        {
            return;
        }

        var link = new Link(row, (_, _) => box.Clear());
        row.EntryCleared += link.Handler;
        box.SetValue(LinkProperty, link);
    }

    private static void StopListening(PasswordBox box)
    {
        if (box.GetValue(LinkProperty) is Link link)
        {
            link.Row.EntryCleared -= link.Handler;
            box.ClearValue(LinkProperty);
        }
    }

    private sealed record Link(CredentialRowViewModel Row, EventHandler Handler);
}
