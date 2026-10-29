using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Views.Wizards;

/// <summary>
/// Connects a <see cref="PasswordBox"/> to a <see cref="WizardFieldViewModel"/> without a binding.
/// <para>
/// <b>Why not a binding.</b> <see cref="PasswordBox.Password"/> is deliberately not bindable, and a two-way
/// string binding would put the secret in a <c>string</c> on the view-model — exactly what this exists to avoid.
/// Instead the box reports each change by handing over its own <see cref="PasswordBox.SecurePassword"/> copy, and
/// the view-model holds that <c>SecureString</c> until the run has used it. Set
/// <c>wizards:SecretEntryBox.Attach="True"</c> on a password box whose <c>DataContext</c> is the field.
/// </para>
/// <para>
/// The other direction is one event: when the view-model drops the value (the run ended, the destination
/// changed) it raises <see cref="WizardFieldViewModel.EntryCleared"/> and the box empties itself, so what is on
/// screen never claims a value the app no longer holds. A box that is recreated when the operator returns to the
/// page starts empty; the field's status line says a value is still held.
/// </para>
/// </summary>
public static class SecretEntryBox
{
    public static readonly DependencyProperty AttachProperty = DependencyProperty.RegisterAttached(
        "Attach",
        typeof(bool),
        typeof(SecretEntryBox),
        new PropertyMetadata(false, OnAttachChanged));

    /// <summary>The subscription this box holds on its field, so it can be undone.</summary>
    private static readonly DependencyProperty LinkProperty = DependencyProperty.RegisterAttached(
        "Link",
        typeof(Link),
        typeof(SecretEntryBox));

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
            box.Loaded += OnLoaded;
            box.Unloaded += OnUnloaded;
            Listen(box);
        }
        else
        {
            box.PasswordChanged -= OnPasswordChanged;
            box.DataContextChanged -= OnDataContextChanged;
            box.Loaded -= OnLoaded;
            box.Unloaded -= OnUnloaded;
            StopListening(box);
        }
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        // SecurePassword is a copy; the view-model takes ownership of it.
        if (sender is PasswordBox { DataContext: WizardFieldViewModel field } box)
        {
            field.SetEntry(box.SecurePassword);
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
        // The template is torn down when the operator leaves the page; do not keep the field alive from here.
        if (sender is PasswordBox box)
        {
            StopListening(box);
        }
    }

    private static void Listen(PasswordBox box)
    {
        if (box.DataContext is not WizardFieldViewModel field || box.GetValue(LinkProperty) is not null)
        {
            return;
        }

        var link = new Link(field, (_, _) => box.Clear());
        field.EntryCleared += link.Handler;
        box.SetValue(LinkProperty, link);
    }

    private static void StopListening(PasswordBox box)
    {
        if (box.GetValue(LinkProperty) is Link link)
        {
            link.Field.EntryCleared -= link.Handler;
            box.ClearValue(LinkProperty);
        }
    }

    private sealed record Link(WizardFieldViewModel Field, EventHandler Handler);
}
