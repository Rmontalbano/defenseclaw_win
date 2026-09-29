using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;


namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The shared review control on an STA thread with no Application: what it draws for each tier, which
/// buttons a phase offers, where focus starts, that Esc cancels, and what a screen reader is told. (Colour and
/// layout are looked at in rendered screenshots, not asserted here.)
/// </summary>
public class CommandReviewControlTests
{
    private static readonly string[] ReadOnlyArgv = { "skill", "list" };
    private static readonly string[] ChangeArgv = { "skill", "block", "--connector", "claudecode", "--", "pdf-tools" };
    private static readonly string[] DestroyArgv = { "skill", "remove", "--connector", "claudecode", "--", "pdf-tools" };

    private static CommandReview ReviewOf(string[] argv, string title = "Review this?") =>
        CommandReview.ForCommand(title, argv);

    private static CommandReviewControl Build(CommandReview? review, Action<CommandReviewControl>? configure = null)
    {
        var control = new CommandReviewControl { Review = review };
        configure?.Invoke(control);
        Layout(control);
        return control;
    }

    private static void Layout(FrameworkElement element, double width = 520)
    {
        element.Measure(new Size(width, 2000));
        element.Arrange(new Rect(0, 0, width, Math.Max(element.DesiredSize.Height, 1)));
        element.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static bool Shown(UIElement element) => element.Visibility == Visibility.Visible;

    // ------------------------------------------------------------------ what it draws

    [Theory]
    [InlineData("readonly", "Read-only", "Neutral")]
    [InlineData("change", "Changes state", "Warn")]
    [InlineData("destroy", "Destructive", "Bad")]
    public void The_badge_says_the_tier_in_words_and_carries_its_tone(string which, string label, string tone)
    {
        StaThread.Run(() =>
        {
            var argv = which switch { "readonly" => ReadOnlyArgv, "change" => ChangeArgv, _ => DestroyArgv };
            var control = Build(ReviewOf(argv, "Title here?"));

            Assert.Equal(label, control.TierText.Text);
            Assert.Equal(tone, control.TierBadge.Tag);
            Assert.Equal("Title here?", control.TitleText.Text);
            Assert.Equal("Command tier: " + label, AutomationProperties.GetName(control.TierBadge));
        });
    }

    [Fact]
    public void Nothing_is_drawn_without_a_review()
    {
        StaThread.Run(() =>
        {
            var control = Build(null);

            Assert.Equal(Visibility.Collapsed, control.Layout.Visibility);
            Assert.Null(control.InitialFocusTarget());
            Assert.False(Shown(control.CopyButton));
        });
    }

    [Fact]
    public void The_exact_argv_is_shown_in_a_selectable_read_only_box_named_for_a_screen_reader()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(ChangeArgv));

            var box = Assert.Single(Descendants<TextBox>(control.StepList));
            Assert.Equal("defenseclaw skill block --connector claudecode -- pdf-tools", box.Text);
            Assert.True(box.IsReadOnly);
            Assert.Equal("Command that will run", AutomationProperties.GetName(box));
        });
    }

    [Fact]
    public void A_long_argv_is_kept_whole_and_scrolls_rather_than_growing_the_dialog_without_limit()
    {
        StaThread.Run(() =>
        {
            var longArgv = new[] { "mcp", "set", "--args", "[" + string.Join(",", Enumerable.Range(0, 120).Select(i => $"\"--flag-number-{i}=value\"")) + "]", "--", "docs" };
            var control = Build(ReviewOf(longArgv), c => c.CommandMaxHeight = 120);

            var box = Assert.Single(Descendants<TextBox>(control.StepList));
            Assert.Equal(control.Review!.CommandText, box.Text);
            Assert.Equal(120, box.MaxHeight);
            Assert.True(box.ActualHeight <= 120);
            Assert.Equal(ScrollBarVisibility.Auto, box.VerticalScrollBarVisibility);
        });
    }

    [Fact]
    public void Several_steps_each_get_a_heading_tier_and_command_and_a_status_once_they_run()
    {
        StaThread.Run(() =>
        {
            var review = new CommandReview
            {
                Title = "Add and sync",
                Steps = new[]
                {
                    new CommandReviewStep(new[] { "registry", "add", "corp" }, "Add the source.", CommandTier.StateChanging, number: 1),
                    new CommandReviewStep(new[] { "registry", "remove", "old" }, "Drop the old one.", CommandTier.StateChanging, number: 2),
                },
            };
            var control = Build(review);

            Assert.True(control.ShowStepHeadings);
            Assert.Equal(2, Descendants<TextBox>(control.StepList).Count());
            Assert.Equal(
                new[] { "Command for step 1", "Command for step 2" },
                Descendants<TextBox>(control.StepList).Select(AutomationProperties.GetName).ToArray());
            Assert.Equal("Destructive", control.TierText.Text);

            review.Steps[0].SetStatus("Succeeded (exit 0)", "Ok");
            Layout(control);

            var badges = Descendants<Border>(control.StepList).Where(b => b.Tag is "Ok").ToArray();
            var status = Assert.Single(badges);
            Assert.Equal(Visibility.Visible, status.Visibility);
        });
    }

    [Fact]
    public void One_step_shows_no_step_heading()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(ChangeArgv));

            Assert.False(control.ShowStepHeadings);
        });
    }

    [Fact]
    public void Warnings_and_the_restart_badge_show_while_the_operator_decides_and_go_quiet_afterwards()
    {
        StaThread.Run(() =>
        {
            var review = ReviewOf(ChangeArgv) with
            {
                RestartsGateway = true,
                Warnings = new[]
                {
                    CommandReviewWarning.GatewayRestart(),
                    new CommandReviewWarning("Before you continue", "Sync and approve entries first."),
                },
            };
            var control = Build(review);

            Assert.Equal(2, control.WarningList.Items.Count);
            Assert.True(Shown(control.WarningList));
            Assert.True(Shown(control.RestartBadge));

            control.Phase = CommandReviewPhase.Finished;
            Layout(control);

            Assert.False(Shown(control.WarningList));
        });
    }

    [Fact]
    public void The_summary_shows_only_when_there_is_one_and_never_in_the_compact_form()
    {
        StaThread.Run(() =>
        {
            var plain = Build(ReviewOf(ChangeArgv));
            Assert.False(Shown(plain.SummaryText));

            var review = ReviewOf(ChangeArgv) with { Summary = "Blocks the skill for one connector." };
            var control = Build(review);
            Assert.True(Shown(control.SummaryText));
            Assert.Equal("Blocks the skill for one connector.", control.SummaryText.Text);
            Assert.True(Shown(control.ExactCaption));

            control.Compact = true;
            Layout(control);
            Assert.False(Shown(control.SummaryText));
            Assert.False(Shown(control.ExactCaption));
        });
    }

    [Fact]
    public void Host_content_lands_under_the_command_and_beside_the_copy_button()
    {
        StaThread.Run(() =>
        {
            var extra = new CheckBox { Content = "Restart the gateway" };
            var footer = new TextBlock { Text = "footer" };
            var control = Build(ReviewOf(ChangeArgv), c =>
            {
                c.ExtraContent = extra;
                c.FooterContent = footer;
            });

            Assert.Same(extra, control.ExtraPresenter.Content);
            Assert.Same(footer, control.FooterPresenter.Content);
            Assert.Contains(extra, Descendants<CheckBox>(control.ExtraPresenter));
        });
    }

    // ------------------------------------------------------------------ buttons by phase

    [Fact]
    public void A_review_offers_cancel_and_the_confirm_and_names_the_confirm_for_the_action()
    {
        StaThread.Run(() =>
        {
            var review = ReviewOf(ChangeArgv) with { ConfirmLabel = "Block skill" };
            var control = Build(review);

            Assert.True(Shown(control.CancelButton));
            Assert.True(Shown(control.ConfirmButton));
            Assert.False(Shown(control.CloseButton));
            Assert.Equal("Cancel", control.CancelButton.Content);
            Assert.Equal("Block skill", control.ConfirmButton.Content);
            Assert.True(Shown(control.CopyButton));
        });
    }

    [Fact]
    public void A_destructive_command_gets_the_danger_confirm_and_anything_else_the_primary_one()
    {
        StaThread.Run(() =>
        {
            var danger = Build(ReviewOf(DestroyArgv));
            var normal = Build(ReviewOf(ChangeArgv));

            Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Danger, danger.ConfirmButton.Appearance);
            Assert.Equal("Run destructive command", danger.ConfirmButton.Content);
            Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Primary, normal.ConfirmButton.Appearance);
            Assert.Equal("Run command", normal.ConfirmButton.Content);
        });
    }

    [Fact]
    public void Appearance_follows_the_review_when_the_same_control_is_reused()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(DestroyArgv));
            Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Danger, control.ConfirmButton.Appearance);

            control.Review = ReviewOf(ChangeArgv);
            Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Primary, control.ConfirmButton.Appearance);
        });
    }

    [Fact]
    public void While_the_commands_run_nothing_can_be_confirmed_or_cancelled_and_afterwards_only_close_is_offered()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(ChangeArgv));

            control.Phase = CommandReviewPhase.Running;
            Assert.False(Shown(control.CancelButton));
            Assert.False(Shown(control.ConfirmButton));
            Assert.False(Shown(control.CloseButton));
            Assert.True(Shown(control.CopyButton));

            control.Phase = CommandReviewPhase.Finished;
            Assert.False(Shown(control.CancelButton));
            Assert.False(Shown(control.ConfirmButton));
            Assert.True(Shown(control.CloseButton));
            Assert.Equal("Close", control.CloseButton.Content);
            Assert.False(Shown(control.ExactCaption));

            control.CloseLabel = "Done";
            Assert.Equal("Done", control.CloseButton.Content);
        });
    }

    [Fact]
    public void Shown_inline_the_control_has_no_decision_buttons_only_copy()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(DestroyArgv), c =>
            {
                c.ShowActions = false;
                c.Compact = true;
            });

            Assert.False(Shown(control.CancelButton));
            Assert.False(Shown(control.ConfirmButton));
            Assert.False(Shown(control.CloseButton));
            Assert.True(Shown(control.CopyButton));
        });
    }

    [Fact]
    public void The_buttons_run_the_hosts_commands()
    {
        StaThread.Run(() =>
        {
            var confirmed = 0;
            var cancelled = 0;
            var control = Build(ReviewOf(ChangeArgv), c =>
            {
                c.ConfirmCommand = new RelayCommand(() => confirmed++);
                c.CancelCommand = new RelayCommand(() => cancelled++);
            });

            control.ConfirmButton.Command!.Execute(null);
            control.CancelButton.Command!.Execute(null);
            control.CloseButton.Command!.Execute(null);

            Assert.Equal(1, confirmed);
            Assert.Equal(2, cancelled);
        });
    }

    // ------------------------------------------------------------------ focus

    [Fact]
    public void Focus_starts_on_cancel_for_a_destructive_command_so_enter_cannot_run_it()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(DestroyArgv));

            Assert.Same(control.CancelButton, control.InitialFocusTarget());
        });
    }

    [Theory]
    [InlineData("readonly")]
    [InlineData("change")]
    public void Focus_starts_on_the_confirm_for_anything_else(string which)
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(which == "readonly" ? ReadOnlyArgv : ChangeArgv));

            Assert.Same(control.ConfirmButton, control.InitialFocusTarget());
        });
    }

    [Fact]
    public void Focus_stays_inside_the_dialog_as_it_moves_through_its_phases()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(ChangeArgv));

            control.Phase = CommandReviewPhase.Running;
            Assert.Same(control.CopyButton, control.InitialFocusTarget());

            control.Phase = CommandReviewPhase.Finished;
            Assert.Same(control.CloseButton, control.InitialFocusTarget());
        });
    }

    [Fact]
    public void An_inline_review_takes_no_focus()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(ChangeArgv), c => c.ShowActions = false);

            Assert.Null(control.InitialFocusTarget());
        });
    }

    /// <summary>Lets everything queued at or above Input priority run (visibility bindings, then the focus move).</summary>
    private static void Flush()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void In_a_real_window_focus_lands_on_the_safe_button_and_goes_back_where_it_was_on_close()
    {
        StaThread.Run(() =>
        {
            var trigger = new System.Windows.Controls.Button { Content = "Row action" };
            var scrim = new Grid { Visibility = Visibility.Collapsed };
            var control = new CommandReviewControl();
            control.CancelCommand = new RelayCommand(() => scrim.Visibility = Visibility.Collapsed);
            scrim.Children.Add(control);
            var stack = new StackPanel();
            stack.Children.Add(trigger);
            var root = new Grid();
            root.Children.Add(stack);
            root.Children.Add(scrim);
            var window = new Window
            {
                Content = root,
                Width = 600,
                Height = 400,
                Left = -4000,
                ShowInTaskbar = false,
                ShowActivated = true,
            };

            try
            {
                window.Show();
                window.Activate();
                Flush();
                if (!window.IsActive)
                {
                    // No interactive desktop to take keyboard focus (a locked or disconnected session): the
                    // choice of button is asserted by the tests above; only the live focus move cannot be seen.
                    return;
                }

                _ = trigger.Focus();
                Flush();

                control.Review = ReviewOf(DestroyArgv);
                scrim.Visibility = Visibility.Visible;
                Flush();
                Assert.Same(control.CancelButton, Keyboard.FocusedElement);

                control.CancelButton.Command!.Execute(null);
                Flush();
                Assert.Same(trigger, Keyboard.FocusedElement);

                control.Review = ReviewOf(ChangeArgv);
                scrim.Visibility = Visibility.Visible;
                Flush();
                Assert.Same(control.ConfirmButton, Keyboard.FocusedElement);

                control.Phase = CommandReviewPhase.Running;
                Flush();
                Assert.Same(control.CopyButton, Keyboard.FocusedElement);

                control.Phase = CommandReviewPhase.Finished;
                Flush();
                Assert.Same(control.CloseButton, Keyboard.FocusedElement);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------ Esc

    private static bool PressEscape(UIElement target)
    {
        using var source = new HwndSource(new HwndSourceParameters("CommandReviewControlTests"));
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        target.RaiseEvent(press);
        return press.Handled;
    }

    [Fact]
    public void Escape_cancels_and_is_consumed()
    {
        StaThread.Run(() =>
        {
            var cancelled = 0;
            var control = Build(ReviewOf(DestroyArgv), c => c.CancelCommand = new RelayCommand(() => cancelled++));

            Assert.True(PressEscape(control));
            Assert.Equal(1, cancelled);
        });
    }

    [Fact]
    public void Escape_from_inside_the_dialog_reaches_the_control_too()
    {
        StaThread.Run(() =>
        {
            var cancelled = 0;
            var control = Build(ReviewOf(ChangeArgv), c => c.CancelCommand = new RelayCommand(() => cancelled++));

            Assert.True(PressEscape(control.ConfirmButton));
            Assert.Equal(1, cancelled);
        });
    }

    [Fact]
    public void Escape_closes_a_finished_dialog()
    {
        StaThread.Run(() =>
        {
            var cancelled = 0;
            var control = Build(ReviewOf(ChangeArgv), c =>
            {
                c.CancelCommand = new RelayCommand(() => cancelled++);
                c.Phase = CommandReviewPhase.Finished;
            });

            Assert.True(PressEscape(control));
            Assert.Equal(1, cancelled);
        });
    }

    [Fact]
    public void Escape_does_nothing_while_the_commands_run_but_the_page_behind_does_not_see_it_either()
    {
        StaThread.Run(() =>
        {
            var cancelled = 0;
            var control = Build(ReviewOf(ChangeArgv), c =>
            {
                c.CancelCommand = new RelayCommand(() => cancelled++);
                c.Phase = CommandReviewPhase.Running;
            });

            Assert.True(PressEscape(control));
            Assert.Equal(0, cancelled);
        });
    }

    [Fact]
    public void An_inline_review_leaves_escape_to_the_page_it_sits_in()
    {
        StaThread.Run(() =>
        {
            var cancelled = 0;
            var control = Build(ReviewOf(ChangeArgv), c =>
            {
                c.ShowActions = false;
                c.CancelCommand = new RelayCommand(() => cancelled++);
            });

            Assert.False(PressEscape(control));
            Assert.Equal(0, cancelled);
        });
    }

    // ------------------------------------------------------------------ copy

    [Fact]
    public void Copy_puts_every_command_on_the_clipboard_and_says_so()
    {
        StaThread.Run(() =>
        {
            string? copied = null;
            var review = new CommandReview
            {
                Title = "t",
                Steps = new[]
                {
                    new CommandReviewStep(new[] { "registry", "add", "corp" }),
                    new CommandReviewStep(new[] { "registry", "sync", "corp" }),
                },
            };
            var control = Build(review, c => c.ClipboardWriter = text => { copied = text; return true; });

            control.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(review.CommandText, copied);
            Assert.Equal("Copied", control.CopyStatus.Text);
            Assert.True(Shown(control.CopyStatus));
        });
    }

    [Fact]
    public void A_clipboard_that_cannot_be_written_is_reported_not_swallowed()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(ChangeArgv), c => c.ClipboardWriter = _ => false);

            control.CopyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.StartsWith("Could not copy", control.CopyStatus.Text, StringComparison.Ordinal);
            Assert.True(Shown(control.CopyStatus));
        });
    }

    // ------------------------------------------------------------------ accessibility

    [Fact]
    public void A_screen_reader_is_told_the_dialog_the_tier_and_that_nothing_runs_until_confirmed()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(DestroyArgv, "Remove skill “pdf-tools”?"));

            var peer = UIElementAutomationPeer.CreatePeerForElement(control);

            Assert.Equal("Review command: Remove skill “pdf-tools”?", peer.GetName());
            Assert.Contains("Destructive command", peer.GetHelpText(), StringComparison.Ordinal);
            Assert.Contains("Nothing runs until you confirm", peer.GetHelpText(), StringComparison.Ordinal);
            Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(control.Header));
        });
    }

    [Fact]
    public void The_confirm_button_says_when_it_runs_something_destructive()
    {
        StaThread.Run(() =>
        {
            var danger = Build(ReviewOf(DestroyArgv));
            var normal = Build(ReviewOf(ChangeArgv));

            Assert.Equal("Runs the destructive command shown above.", AutomationProperties.GetHelpText(danger.ConfirmButton));
            Assert.Equal("Runs the command shown above.", AutomationProperties.GetHelpText(normal.ConfirmButton));
            Assert.Equal("Copy the command to the clipboard", AutomationProperties.GetName(normal.CopyButton));
        });
    }

    [Fact]
    public void The_dialog_name_follows_the_review()
    {
        StaThread.Run(() =>
        {
            var control = Build(ReviewOf(ChangeArgv, "First?"));
            var peer = UIElementAutomationPeer.CreatePeerForElement(control);
            Assert.Equal("Review command: First?", peer.GetName());

            control.Review = ReviewOf(DestroyArgv, "Second?");

            Assert.Equal("Review command: Second?", peer.GetName());
        });
    }
}
