using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// The Activity cards' two additions (CUST-264), looked at in the real panel on the shared UI thread: the outcome line under a finished command, and
/// the Rerun button (drawn for a DefenseClaw command, off while it runs, absent for one that carried a secret); and that the review Rerun opens for
/// a destructive command starts with focus on Cancel. The runs are synthetic invocations; nothing is started.
/// </summary>
[Collection(UiCollection.Name)]
public class ActivityRerunViewTests
{
    private static CliInvocation Finished(string executable, int exit, params string[] argv)
    {
        var invocation = InvocationFactory.CreateFor(executable, argv);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private static Task<string> Declined(CliInvocation invocation, CommandTier tier) => Task.FromResult(string.Empty);

    private static Button ButtonIn(FrameworkElement card, string automationName) =>
        VisualTree.Descendants<Button>(card).First(button => AutomationProperties.GetName(button) == automationName);

    private static IEnumerable<Button> ButtonsIn(FrameworkElement card, string automationName) =>
        VisualTree.Descendants<Button>(card).Where(button => AutomationProperties.GetName(button) == automationName);

    private static TextBlock? OutcomeIn(FrameworkElement card) =>
        VisualTree.Descendants<TextBlock>(card).FirstOrDefault(t => (AutomationProperties.GetName(t) ?? string.Empty).StartsWith("Outcome: ", StringComparison.Ordinal));

    // ------------------------------------------------------------------ Rerun

    [Fact]
    public void Rerun_is_drawn_for_a_finished_command_off_while_one_runs_and_absent_when_a_secret_was_supplied()
    {
        using var scene = ActivityScene.Open(940, 620);

        UiThread.Run(() =>
        {
            // Each row goes in at the top, so the one added last is the first card: the finished command with its Rerun.
            var signature = scene.AddRow(Finished(@"C:\Tools\cosign.exe", 0, "verify-blob", "--bundle", "x"), expand: false, rerun: Declined);
            var secretInvocation = Finished("defenseclaw", 0, "keys", "set", "OPENAI_API_KEY");
            InvocationFactory.UseStdinSecret(secretInvocation);
            var secret = scene.AddRow(secretInvocation, expand: false, rerun: Declined);
            var running = scene.AddRow(InvocationFactory.Create(false, "doctor"), expand: false, rerun: Declined);
            var finished = scene.AddRow(Finished("defenseclaw", 1, "skill", "list"), expand: false, rerun: Declined);

            var done = ButtonIn(scene.CardOf(finished)!, "Rerun this command");
            Assert.True(done.IsVisible);
            Assert.True(done.IsEnabled);
            Assert.Same(finished.RerunCommand, done.Command);
            Assert.Contains("Nothing runs until you confirm", (string)done.ToolTip, StringComparison.Ordinal);

            var going = ButtonIn(scene.CardOf(running)!, "Rerun this command");
            Assert.True(going.IsVisible);
            Assert.False(going.IsEnabled);
            Assert.Contains("Wait for this command to finish", (string)going.ToolTip, StringComparison.Ordinal);
            Assert.True(ToolTipService.GetShowOnDisabled(going));

            Assert.Equal(Visibility.Collapsed, ButtonIn(scene.CardOf(secret)!, "Rerun this command").Visibility);
            Assert.Equal(Visibility.Collapsed, ButtonIn(scene.CardOf(signature)!, "Rerun this command").Visibility);

            scene.Render("activity-rerun-buttons-940x620");
        });
    }

    [Fact]
    public void A_panel_whose_rows_have_no_way_to_run_them_shows_no_Rerun_at_all()
    {
        using var scene = ActivityScene.Open(940, 620);

        UiThread.Run(() =>
        {
            var row = scene.AddRow(Finished("defenseclaw", 0, "doctor"), expand: false);

            Assert.Equal(Visibility.Collapsed, ButtonIn(scene.CardOf(row)!, "Rerun this command").Visibility);
        });
    }

    [Fact]
    public void Rerun_comes_on_when_the_command_finishes_and_acts_on_the_entry_of_its_card_after_recycling()
    {
        using var scene = ActivityScene.Open(940, 620);

        UiThread.Run(() =>
        {
            var asked = new List<CliInvocation>();
            Task<string> Record(CliInvocation invocation, CommandTier tier)
            {
                asked.Add(invocation);
                return Task.FromResult(string.Empty);
            }

            var runs = Enumerable.Range(0, 30).Select(i => InvocationFactory.Create(false, "skill", "list", $"--n{i}")).ToList();
            runs.ForEach(r => InvocationFactory.Finish(r, 0));
            var rows = runs.Select(r => scene.AddRow(r, expand: false, rerun: Record)).ToList();
            var live = InvocationFactory.Create(false, "doctor");
            var liveRow = scene.AddRow(live, expand: false, rerun: Record);

            for (var offset = 0.0; offset < 5_000; offset += 200)
            {
                scene.ScrollCardsTo(offset);
            }

            scene.ScrollCardsTo(0);
            foreach (var card in scene.RealizedCards())
            {
                var row = (ActivityRow)card.DataContext;
                var button = ButtonIn(card, "Rerun this command");
                Assert.Same(row.RerunCommand, button.Command);
                Assert.Equal(row.CanOfferRerun, button.IsVisible);
                Assert.Equal(row.CanRerun, button.IsEnabled);
            }

            // While it runs the card's button is off; once it finishes and the row ticks it is on, and pressing it asks for exactly that entry.
            var liveButton = ButtonIn(scene.CardOf(liveRow)!, "Rerun this command");
            Assert.False(liveButton.IsEnabled);
            InvocationFactory.Finish(live, 0);
            liveRow.Tick();
            scene.Host.Relayout();
            Assert.True(liveButton.IsEnabled);

            liveButton.Command!.Execute(null);
            Assert.Same(live, Assert.Single(asked));
            Assert.Equal(30, rows.Count);
        });
    }

    // ------------------------------------------------------------------ the outcome line

    [Fact]
    public void A_finished_command_shows_what_it_did_and_what_to_do_next_under_its_command()
    {
        using var scene = ActivityScene.Open(940, 620);

        UiThread.Run(() =>
        {
            var setup = scene.AddRow(Finished("defenseclaw", 0, "setup", "guardrail"), expand: false);
            var doctor = scene.AddRow(Finished("defenseclaw", 1, "doctor"), expand: false);
            var quiet = scene.AddRow(Finished("defenseclaw", 0, "skill", "list"), expand: false);
            var running = scene.AddRow(InvocationFactory.Create(false, "setup", "guardrail"), expand: false);

            var onSetup = OutcomeIn(scene.CardOf(setup)!);
            Assert.NotNull(onSetup);
            Assert.True(onSetup!.IsVisible);
            Assert.Equal("config reloaded · gateway restarted · next: rerun readiness", onSetup.Text);
            Assert.Equal("Outcome: config reloaded · gateway restarted · next: rerun readiness", AutomationProperties.GetName(onSetup));

            var onDoctor = OutcomeIn(scene.CardOf(doctor)!);
            Assert.True(onDoctor!.IsVisible);
            Assert.Equal("next: open readiness or rerun doctor", onDoctor.Text);

            // Nothing to say, nothing drawn: the card is as tall as it was.
            Assert.False(OutcomeIn(scene.CardOf(quiet)!)!.IsVisible);
            Assert.False(OutcomeIn(scene.CardOf(running)!)!.IsVisible);

            scene.Render("activity-outcome-line-940x620");
        });
    }

    [Fact]
    public void The_outcome_line_appears_when_a_running_command_finishes()
    {
        using var scene = ActivityScene.Open(940, 620);

        UiThread.Run(() =>
        {
            var run = InvocationFactory.Create(false, "doctor");
            var row = scene.AddRow(run, expand: false);
            var line = OutcomeIn(scene.CardOf(row)!)!;
            Assert.False(line.IsVisible);

            InvocationFactory.Finish(run, 0);
            row.Tick();
            scene.Host.Relayout();

            Assert.True(line.IsVisible);
            Assert.Equal("next: review readiness", line.Text);
        });
    }

    // ------------------------------------------------------------------ the review a Rerun opens

    [Fact]
    public void A_destructive_entrys_review_opens_with_focus_on_Cancel_and_a_danger_confirm()
    {
        UiThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var rerun = new CommandRerun(services);
            var entry = Finished("defenseclaw", 0, "skill", "quarantine", "--", "pdf-tools");
            var row = new ActivityRow(entry, rerun: Declined);

            var control = new CommandReviewControl { Review = rerun.ReviewFor(entry, row.Tier) };
            control.Measure(new Size(520, 2000));
            control.Arrange(new Rect(0, 0, 520, Math.Max(control.DesiredSize.Height, 1)));
            control.UpdateLayout();

            Assert.Equal(CommandTier.Destructive, row.Tier);
            Assert.Same(control.CancelButton, control.InitialFocusTarget());
            Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Danger, control.ConfirmButton.Appearance);
            Assert.Equal("Run destructive command", control.ConfirmButton.Content);
            Assert.Equal("Destructive", control.TierText.Text);
        });
    }

    [Fact]
    public void In_a_real_window_the_review_of_a_destructive_rerun_takes_the_keyboard_on_Cancel_and_a_changing_one_on_the_confirm_button()
    {
        UiThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var rerun = new CommandRerun(services);

            foreach (var (argv, destructive) in new[] { (new[] { "skill", "quarantine", "--", "pdf-tools" }, true), (new[] { "skill", "block", "--", "pdf-tools" }, false) })
            {
                var entry = Finished("defenseclaw", 0, argv);
                var row = new ActivityRow(entry, rerun: Declined);
                var control = new CommandReviewControl { Review = rerun.ReviewFor(entry, row.Tier) };
                var window = new Window
                {
                    Content = control,
                    Width = 600,
                    Height = 500,
                    Left = -4000,
                    ShowInTaskbar = false,
                    ShowActivated = true,
                };

                try
                {
                    window.Show();
                    _ = window.Activate();
                    Flush();
                    if (!window.IsActive)
                    {
                        // No interactive desktop to take keyboard focus (a locked or disconnected session): the choice of button is asserted above.
                        return;
                    }

                    Assert.Same(destructive ? control.CancelButton : control.ConfirmButton, System.Windows.Input.Keyboard.FocusedElement);
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    private static void Flush()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void A_state_changing_or_read_only_entrys_review_opens_with_focus_on_the_confirm_button()
    {
        UiThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var rerun = new CommandRerun(services);

            foreach (var (argv, label) in new[] { (new[] { "skill", "block", "--", "pdf-tools" }, "Changes state"), (new[] { "doctor" }, "Read-only") })
            {
                var entry = Finished("defenseclaw", 0, argv);
                var row = new ActivityRow(entry, rerun: Declined);
                var control = new CommandReviewControl { Review = rerun.ReviewFor(entry, row.Tier) };
                control.Measure(new Size(520, 2000));
                control.Arrange(new Rect(0, 0, 520, Math.Max(control.DesiredSize.Height, 1)));
                control.UpdateLayout();

                Assert.Same(control.ConfirmButton, control.InitialFocusTarget());
                Assert.Equal(Wpf.Ui.Controls.ControlAppearance.Primary, control.ConfirmButton.Appearance);
                Assert.Equal(label, control.TierText.Text);
            }
        });
    }
}
