using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Views.Shell;

/// <summary>
/// The review-before-run control every confirmation surface in the app shares. It draws a
/// <see cref="CommandReview"/> (tier badge, title, summary, the exact argv of each step, warnings, buttons)
/// and does what every one of those surfaces has to do the same way:
/// <list type="bullet">
/// <item>Opens with focus on Cancel for a destructive command (so Enter cannot run it by accident) and on the
/// confirm button for anything else; moves it to Copy while the commands run and to Close when they finish.</item>
/// <item>Esc cancels (not while the commands are running, when there is nothing to cancel).</item>
/// <item>Hands focus back to the element that had it when the review opened, if that element is still there.</item>
/// <item>Tells UI Automation the dialog's name and tier, and re-announces them when the review changes.</item>
/// </list>
/// The control owns no state and runs nothing: the host binds <see cref="ConfirmCommand"/> and
/// <see cref="CancelCommand"/> and decides what a confirmed review does. With <see cref="ShowActions"/> off it is a
/// read-only rendering of the command (the wizard's review page), which takes no focus and swallows no keys.
/// </summary>
public partial class CommandReviewControl : UserControl
{
    public static readonly DependencyProperty ReviewProperty = DependencyProperty.Register(
        nameof(Review),
        typeof(CommandReview),
        typeof(CommandReviewControl),
        new PropertyMetadata(null, OnDisplayedStateChanged));

    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase),
        typeof(CommandReviewPhase),
        typeof(CommandReviewControl),
        new PropertyMetadata(CommandReviewPhase.Review, OnPhaseChanged));

    public static readonly DependencyProperty ConfirmCommandProperty = DependencyProperty.Register(
        nameof(ConfirmCommand),
        typeof(ICommand),
        typeof(CommandReviewControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
        nameof(CancelCommand),
        typeof(ICommand),
        typeof(CommandReviewControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ShowActionsProperty = DependencyProperty.Register(
        nameof(ShowActions),
        typeof(bool),
        typeof(CommandReviewControl),
        new PropertyMetadata(true, OnDisplayedStateChanged));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact),
        typeof(bool),
        typeof(CommandReviewControl),
        new PropertyMetadata(false, OnDisplayedStateChanged));

    public static readonly DependencyProperty CloseLabelProperty = DependencyProperty.Register(
        nameof(CloseLabel),
        typeof(string),
        typeof(CommandReviewControl),
        new PropertyMetadata("Close", OnDisplayedStateChanged));

    public static readonly DependencyProperty CommandMaxHeightProperty = DependencyProperty.Register(
        nameof(CommandMaxHeight),
        typeof(double),
        typeof(CommandReviewControl),
        new PropertyMetadata(180d));

    public static readonly DependencyProperty ShowStepHeadingsProperty = DependencyProperty.Register(
        nameof(ShowStepHeadings),
        typeof(bool),
        typeof(CommandReviewControl),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ExtraContentProperty = DependencyProperty.Register(
        nameof(ExtraContent),
        typeof(object),
        typeof(CommandReviewControl),
        new PropertyMetadata(null));

    public static readonly DependencyProperty FooterContentProperty = DependencyProperty.Register(
        nameof(FooterContent),
        typeof(object),
        typeof(CommandReviewControl),
        new PropertyMetadata(null));

    private static readonly TimeSpan CopyStatusLifetime = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _copyStatusTimer;
    private IInputElement? _returnFocusTo;
    private string? _announced;

    public CommandReviewControl()
    {
        InitializeComponent();

        _copyStatusTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = CopyStatusLifetime };
        _copyStatusTimer.Tick += (_, _) =>
        {
            _copyStatusTimer.Stop();
            CopyStatus.Text = string.Empty;
            CopyStatus.Visibility = Visibility.Collapsed;
        };

        IsVisibleChanged += OnIsVisibleChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Refresh();
    }

    /// <summary>What to show. Null shows nothing.</summary>
    public CommandReview? Review
    {
        get => (CommandReview?)GetValue(ReviewProperty);
        set => SetValue(ReviewProperty, value);
    }

    /// <summary>Waiting for a decision, running, or finished: decides which buttons are offered.</summary>
    public CommandReviewPhase Phase
    {
        get => (CommandReviewPhase)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    /// <summary>Runs when the operator confirms.</summary>
    public ICommand? ConfirmCommand
    {
        get => (ICommand?)GetValue(ConfirmCommandProperty);
        set => SetValue(ConfirmCommandProperty, value);
    }

    /// <summary>Runs on Cancel, on Esc and on Close.</summary>
    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    /// <summary>
    /// True (the default) for a dialog: Cancel / confirm buttons, focus management and Esc. False renders the
    /// review inline with just a Copy button, taking no focus and no keys.
    /// </summary>
    public bool ShowActions
    {
        get => (bool)GetValue(ShowActionsProperty);
        set => SetValue(ShowActionsProperty, value);
    }

    /// <summary>One-row header (title beside the badge, no summary or "exact command" line) for use inside a page.</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>The text of the button shown once the run has finished.</summary>
    public string CloseLabel
    {
        get => (string)GetValue(CloseLabelProperty);
        set => SetValue(CloseLabelProperty, value);
    }

    /// <summary>Tallest a command block grows before it scrolls. Infinity lets it show a whole long argv.</summary>
    public double CommandMaxHeight
    {
        get => (double)GetValue(CommandMaxHeightProperty);
        set => SetValue(CommandMaxHeightProperty, value);
    }

    /// <summary>Whether each step shows its own heading, tier and purpose (a review of several commands).</summary>
    public bool ShowStepHeadings
    {
        get => (bool)GetValue(ShowStepHeadingsProperty);
        private set => SetValue(ShowStepHeadingsProperty, value);
    }

    /// <summary>Host content under the command blocks: an option that changes the argv, a run's result.</summary>
    public object? ExtraContent
    {
        get => GetValue(ExtraContentProperty);
        set => SetValue(ExtraContentProperty, value);
    }

    /// <summary>Host content beside the Copy button.</summary>
    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    /// <summary>Test seam: what Copy writes with. Null uses the real clipboard; returns false when it could not.</summary>
    internal Func<string, bool>? ClipboardWriter { get; set; }

    /// <summary>The button that should hold focus in the current phase, or null when the control takes no focus.</summary>
    internal Control? InitialFocusTarget()
    {
        if (!ShowActions || Review is null)
        {
            return null;
        }

        return Phase switch
        {
            CommandReviewPhase.Review => Review.IsDestructive ? CancelButton : ConfirmButton,
            CommandReviewPhase.Running => CopyButton,
            _ => CloseButton,
        };
    }

    private static void OnDisplayedStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CommandReviewControl)d).Refresh();

    private static void OnPhaseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (CommandReviewControl)d;
        control.Refresh();

        // The button that was pressed has just been collapsed: keep the keyboard inside the dialog.
        if (control.IsVisible && control.ShowActions && e.NewValue is CommandReviewPhase.Running or CommandReviewPhase.Finished)
        {
            control.FocusInitialTarget();
        }
    }

    /// <summary>Pushes the review, the phase and the layout switches onto the parts. Cheap; runs on every change.</summary>
    private void Refresh()
    {
        var review = Review;
        Layout.Visibility = review is null ? Visibility.Collapsed : Visibility.Visible;
        StepList.ItemsSource = review?.Steps;
        WarningList.ItemsSource = review?.Warnings;
        ShowStepHeadings = review is { HasMultipleSteps: true };

        var phase = Phase;
        var dialog = ShowActions;

        // Header: badges above the title in a dialog, title then badges on one row in a page.
        TierBadge.Tag = review?.TierKey ?? "Neutral";
        TierText.Text = review?.TierLabel ?? string.Empty;
        AutomationProperties.SetName(TierBadge, "Command tier: " + TierText.Text);
        RestartBadge.Visibility = review is { RestartsGateway: true } ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = review?.Title ?? string.Empty;
        TitleText.SetResourceReference(StyleProperty, Compact ? "DcCardTitle" : "DcSectionHeader");
        Header.ColumnDefinitions[0].Width = Compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        Header.ColumnDefinitions[1].Width = Compact ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetRow(Badges, 0);
        Grid.SetColumn(Badges, Compact ? 1 : 0);
        Grid.SetColumnSpan(Badges, Compact ? 1 : 2);
        Grid.SetRow(TitleText, Compact ? 0 : 1);
        Grid.SetColumn(TitleText, 0);
        Grid.SetColumnSpan(TitleText, Compact ? 1 : 2);
        Badges.Margin = Compact ? new Thickness(8, 0, 0, 6) : new Thickness(0, 0, 0, 8);
        TitleText.Margin = Compact ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 0, 8);
        TitleText.VerticalAlignment = Compact ? VerticalAlignment.Center : VerticalAlignment.Top;

        SummaryText.Text = review?.Summary ?? string.Empty;
        SummaryText.Visibility = !Compact && SummaryText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExactCaption.Visibility = !Compact && dialog && phase == CommandReviewPhase.Review ? Visibility.Visible : Visibility.Collapsed;

        // Consequences are for the decision; once it has been made and run they are noise.
        WarningList.Visibility = phase == CommandReviewPhase.Review && review is { Warnings.Count: > 0 }
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Buttons.
        ButtonBar.Margin = new Thickness(0, Compact ? 8 : 16, 0, 0);
        CopyButton.Visibility = review is null ? Visibility.Collapsed : Visibility.Visible;
        var deciding = dialog && phase == CommandReviewPhase.Review;
        CancelButton.Visibility = deciding ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Visibility = deciding ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = dialog && phase == CommandReviewPhase.Finished ? Visibility.Visible : Visibility.Collapsed;

        CancelButton.Content = review?.CancelLabel ?? "Cancel";
        ConfirmButton.Content = review?.ConfirmLabel ?? CommandReview.DefaultConfirmLabel(CommandTier.StateChanging);
        CloseButton.Content = CloseLabel;
        ConfirmButton.Appearance = review is { IsDestructive: true } ? ControlAppearance.Danger : ControlAppearance.Primary;
        AutomationProperties.SetHelpText(
            ConfirmButton,
            review is { IsDestructive: true } ? "Runs the destructive command shown above." : "Runs the command shown above.");

        // A different command or tier is news: say so, tier first. An edit to the same review (a checkbox that
        // adds a flag) is not, so it stays quiet. No peer exists unless a UI Automation client is attached.
        var announcement = review is null ? null : review.TierLabel + "|" + review.Title;
        if (announcement != _announced)
        {
            _announced = announcement;
            if (IsVisible)
            {
                Announce();
            }
        }
    }

    private void Announce() =>
        UIElementAutomationPeer.FromElement(Header)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _returnFocusTo = ShowActions && CurrentFocus() is DependencyObject focused && !IsInside(focused) ? (IInputElement)focused : null;
            FocusInitialTarget();
            Announce();
        }
        else
        {
            RestoreFocus();
        }
    }

    private void FocusInitialTarget()
    {
        if (InitialFocusTarget() is null)
        {
            return;
        }

        // Wait for the visibility bindings (and the layout they cause) to settle, then look again: the phase
        // or the review may have moved on by the time this runs.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible && InitialFocusTarget() is { IsVisible: true, IsEnabled: true } target)
            {
                _ = target.Focus();
            }
        }));
    }

    /// <summary>
    /// Gives focus back to the element that had it when the review opened, unless focus has already gone
    /// somewhere else on purpose (the host moved it) or the element has left the page (a list that reloaded).
    /// </summary>
    private void RestoreFocus()
    {
        var target = _returnFocusTo as UIElement;
        _returnFocusTo = null;
        if (target is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var current = CurrentFocus() as DependencyObject;
            var strayed = current is null || current is Window || IsInside(current);
            if (strayed && target.IsVisible && target.IsEnabled && target.Focusable)
            {
                _ = target.Focus();
            }
        }));
    }

    /// <summary>Where the keyboard is, or where it will be when the window is next activated.</summary>
    private IInputElement? CurrentFocus() =>
        Keyboard.FocusedElement
        ?? (FocusManager.GetFocusScope(this) is { } scope ? FocusManager.GetFocusedElement(scope) : null);

    private bool IsInside(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = GetParent(node))
        {
            if (ReferenceEquals(node, this))
            {
                return true;
            }
        }

        return false;

        static DependencyObject? GetParent(DependencyObject node) =>
            node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !ShowActions || Review is null || e.Handled)
        {
            return;
        }

        // Nothing here can stop a command that is already running; swallow the key so the page behind
        // the dialog does not react to it either.
        if (Phase != CommandReviewPhase.Running && CancelCommand is { } cancel && cancel.CanExecute(null))
        {
            cancel.Execute(null);
        }

        e.Handled = true;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        // Not what the box shows: that quotes only whitespace, and a name like x&calc would paste as a second
        // command. The clipboard gets the form PowerShell reads as one literal argument per name.
        var text = Review?.ClipboardText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var copied = Write(text);
        CopyStatus.Text = copied ? "Copied" : "Could not copy: select the command instead";
        CopyStatus.Visibility = Visibility.Visible;
        _copyStatusTimer.Stop();
        _copyStatusTimer.Start();
        UIElementAutomationPeer.FromElement(CopyStatus)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private bool Write(string text)
    {
        if (ClipboardWriter is { } writer)
        {
            return writer(text);
        }

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException)
        {
            // The clipboard is locked by another process; the command text is selectable in the dialog too.
            return false;
        }
    }

    /// <summary>
    /// A ScrollViewer marks the wheel as handled even when there is nothing to scroll, which would freeze the
    /// page around an inline review. Only a dialog needs its body to scroll, so hand the wheel to the parent otherwise.
    /// </summary>
    private void OnBodyPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ShowActions || e.Handled || Body.ScrollableHeight > 0)
        {
            return;
        }

        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = sender,
        };
        (VisualTreeHelper.GetParent(this) as UIElement)?.RaiseEvent(forwarded);
    }
}
