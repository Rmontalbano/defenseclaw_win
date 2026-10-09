using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Credentials card with the masked box (CUST-221), hosted offscreen with synthetic rows: the box under a row, the Review button that follows
/// what is typed, the Fill missing bar, the review with the value masked, and the console-only layout where the app cannot type. The value is
/// typed into the real <see cref="PasswordBox"/>, so the attached behavior and the row's <c>SecureString</c> are the ones the app uses. No
/// pseudo-console starts and no <c>defenseclaw</c> runs. Set <c>DC_RENDER_DIR</c> to also write the PNGs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupCredentialsInAppViewTests
{
    private const string Value = "synthetic-test-value";
    private const string SecondValue = "synthetic-second-value-0123";

    private static readonly CredentialRow[] Rows =
    {
        new("EXAMPLE_JUDGE_KEY", "EXAMPLE_JUDGE_KEY", "LLM judge", "required", "unset", false, "Key for the judge model"),
        new("EXAMPLE_SCANNER_KEY", "EXAMPLE_SCANNER_KEY", "Skill scanner", "required", "dotenv", true, "Scanner service key"),
        new("EXAMPLE_REVIEWER_KEY", "EXAMPLE_REVIEWER_KEY", "Reviewer", "required", "unset", false, "Key for the reviewing service"),
        new("EXAMPLE_TELEMETRY_KEY", "EXAMPLE_TELEMETRY_KEY", "Telemetry", "optional", "unset", false, "Optional exporter key"),
        new("EXAMPLE_ENV_KEY", "EXAMPLE_ENV_KEY", "Gateway", "required", "env", true, "Set in the environment"),
    };

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public Scene(bool inApp, int width = 1400, int height = 1500)
        {
            Services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(Services, width, height);
                _ = shell.Show<SetupPanel>();
                return shell;
            });
            Setup = UiThread.Run(() => (SetupPanelViewModel)Shell.ViewModel);
            UiThread.WaitFor(() => !Setup.IsLoading && !Setup.Credentials.IsLoading, "setup catalog and the (unavailable) credential read finished");

            UiThread.Run(() =>
            {
                Setup.Credentials.PtyAvailable = () => inApp;
                Setup.Credentials.RunSet = (name, _, _) => throw new InvalidOperationException("The view tests never store a value: " + name);
                Setup.Credentials.ApplyRows(Rows);
                Shell.Host.Relayout();
            });
        }

        public AppServices Services { get; }

        public PanelShell Shell { get; }

        public SetupPanelViewModel Setup { get; }

        public CredentialsViewModel Credentials => Setup.Credentials;

        public FrameworkElement Page => Shell.Page!;

        public IEnumerable<T> All<T>() where T : DependencyObject => VisualTree.Descendants<T>(Page);

        public Wpf.Ui.Controls.Button Button(string automationName) =>
            All<Wpf.Ui.Controls.Button>().First(b => AutomationProperties.GetName(b) == automationName);

        public PasswordBox Box(string variable) =>
            All<PasswordBox>().First(b => AutomationProperties.GetName(b) == "Value for " + variable);

        public void Relayout() => Shell.Host.Relayout();

        public void ScrollToCredentials()
        {
            var scroll = VisualTree.Descendants<ScrollViewer>(Page).First(sv => sv.ScrollableHeight > 0);
            var heading = VisualTree.Descendants<TextBlock>(Page).First(t => t.Text == "Credentials");
            var content = (UIElement)scroll.Content;
            scroll.ScrollToVerticalOffset(Math.Max(0, heading.TranslatePoint(new Point(), content).Y - 70));
            Relayout();
        }

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            Services.Dispose();
            _temp.Dispose();
        }
    }

    [Fact]
    public void A_row_opens_a_masked_box_whose_review_button_follows_what_is_typed_and_the_value_is_nowhere_on_the_page()
    {
        using var scene = new Scene(inApp: true);

        UiThread.Run(() =>
        {
            // Every row has Set and the console button; no box is showing yet.
            Assert.Equal(Rows.Length, scene.All<Wpf.Ui.Controls.Button>().Count(b => AutomationProperties.GetName(b).StartsWith("Set EXAMPLE_", StringComparison.Ordinal) && !AutomationProperties.GetName(b).EndsWith("in a terminal", StringComparison.Ordinal)));
            Assert.Equal(Rows.Length, scene.All<Wpf.Ui.Controls.Button>().Count(b => AutomationProperties.GetName(b).EndsWith("in a terminal", StringComparison.Ordinal) && AutomationProperties.GetName(b).StartsWith("Set EXAMPLE_", StringComparison.Ordinal)));
            Assert.All(scene.All<PasswordBox>(), b => Assert.False(b.IsVisible));

            // Set opens the box of that row and no other, and the cursor goes in.
            var set = scene.Button("Set EXAMPLE_JUDGE_KEY");
            Assert.True(set.Command.CanExecute(null));
            set.Command.Execute(null);
            scene.Relayout();

            var box = scene.Box("EXAMPLE_JUDGE_KEY");
            Assert.True(box.IsVisible);
            Assert.Single(scene.All<PasswordBox>(), b => b.IsVisible);

            // Enter goes on to the review (which shows the command and asks first), Esc closes the box: both are the row's own commands.
            var keys = box.InputBindings.OfType<System.Windows.Input.KeyBinding>().ToArray();
            Assert.Same(scene.Credentials.Rows[0].ReviewCommand, Assert.Single(keys, k => k.Key == System.Windows.Input.Key.Return).Command);
            Assert.Same(scene.Credentials.Rows[0].CancelEntryCommand, Assert.Single(keys, k => k.Key == System.Windows.Input.Key.Escape).Command);
            Assert.False(scene.Button("Review the command that stores EXAMPLE_JUDGE_KEY").IsEnabled);
            Assert.Contains(scene.All<TextBlock>(), t => t.IsVisible && t.Text.StartsWith("Type or paste the value.", StringComparison.Ordinal));

            // Typing is the real password box: the attached behavior hands the row a SecureString.
            box.Password = Value;
            scene.Relayout();

            var row = scene.Credentials.Rows[0];
            Assert.True(row.HasEntry);
            Assert.True(scene.Button("Review the command that stores EXAMPLE_JUDGE_KEY").IsEnabled);
            Assert.Contains(scene.All<TextBlock>(), t => t.IsVisible && t.Text.StartsWith("A value is entered (20 characters, hidden)", StringComparison.Ordinal));
            Assert.All(scene.All<TextBlock>(), t => Assert.DoesNotContain(Value, t.Text, StringComparison.Ordinal));
            Assert.All(scene.All<Wpf.Ui.Controls.Button>(), b => Assert.DoesNotContain(Value, b.Content?.ToString() ?? string.Empty, StringComparison.Ordinal));
            Assert.All(scene.All<FrameworkElement>(), e => Assert.DoesNotContain(Value, AutomationProperties.GetName(e) + AutomationProperties.GetHelpText(e) + (e.ToolTip as string), StringComparison.Ordinal));

            // Cancel closes it, empties the box and forgets the value.
            scene.Button("Cancel entering a value for EXAMPLE_JUDGE_KEY").Command.Execute(null);
            scene.Relayout();
            Assert.False(box.IsVisible);
            Assert.Equal(string.Empty, box.Password);
            Assert.False(row.HasEntry);

            // Type again, then take the review, whose last word is the value masked.
            set.Command.Execute(null);
            scene.Relayout();
            scene.Box("EXAMPLE_JUDGE_KEY").Password = Value;
            scene.Relayout();
            scene.ScrollToCredentials();
            RenderTo.Png(scene.Shell.Host, "cust221-credentials-box-1400x1500");

            scene.Button("Review the command that stores EXAMPLE_JUDGE_KEY").Command.Execute(null);
            scene.Relayout();
            Assert.True(scene.Setup.Review.IsOpen);
            Assert.Contains(scene.All<TextBlock>(), t => t.IsVisible && t.Text.Contains(CredentialsViewModel.MaskedValue, StringComparison.Ordinal) && t.Text.Contains("20 characters, hidden", StringComparison.Ordinal));
            Assert.Contains(scene.All<TextBox>(), t => t.IsVisible && t.Text == "defenseclaw keys set EXAMPLE_JUDGE_KEY");
            Assert.All(scene.All<TextBlock>(), t => Assert.DoesNotContain(Value, t.Text, StringComparison.Ordinal));
            Assert.All(scene.All<TextBox>(), t => Assert.DoesNotContain(Value, t.Text, StringComparison.Ordinal));
            RenderTo.Png(scene.Shell.Host, "cust221-credentials-review-1400x1500");
        });
    }

    [Fact]
    public void Fill_missing_opens_a_box_on_each_required_credential_that_is_unset_and_the_bar_counts_what_was_typed()
    {
        using var scene = new Scene(inApp: true);

        UiThread.Run(() =>
        {
            var fill = scene.Button("Fill missing credentials");
            Assert.True(fill.IsEnabled);
            Assert.Contains(scene.All<Wpf.Ui.Controls.Button>(), b => AutomationProperties.GetName(b) == "Fill missing credentials in a terminal" && b.IsVisible);
            Assert.DoesNotContain(scene.All<Wpf.Ui.Controls.Button>(), b => AutomationProperties.GetName(b) == "Review the commands that store the values typed" && b.IsVisible);

            fill.Command.Execute(null);
            scene.Relayout();

            // The two required credentials that are unset have a box; the set ones, the optional one and the environment one do not.
            Assert.Equal(
                new[] { "Value for EXAMPLE_JUDGE_KEY", "Value for EXAMPLE_REVIEWER_KEY" },
                scene.All<PasswordBox>().Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b)).ToArray());
            var review = scene.Button("Review the commands that store the values typed");
            Assert.True(review.IsVisible);
            Assert.False(review.IsEnabled);
            Assert.Equal("Review 0 values…", review.Content);

            scene.Box("EXAMPLE_JUDGE_KEY").Password = Value;
            scene.Relayout();
            Assert.Equal("Review 1 value…", review.Content);
            Assert.True(review.IsEnabled);

            scene.Box("EXAMPLE_REVIEWER_KEY").Password = SecondValue;
            scene.Relayout();
            Assert.Equal("Review 2 values…", review.Content);

            scene.ScrollToCredentials();
            RenderTo.Png(scene.Shell.Host, "cust221-credentials-fill-1400x1500");

            review.Command.Execute(null);
            scene.Relayout();
            Assert.True(scene.Setup.Review.IsOpen);
            Assert.Equal("Store 2 values?", scene.Setup.Review.CommandReview!.Title);
            Assert.All(scene.All<TextBlock>(), t => Assert.DoesNotContain(Value, t.Text, StringComparison.Ordinal));
            Assert.All(scene.All<TextBlock>(), t => Assert.DoesNotContain(SecondValue, t.Text, StringComparison.Ordinal));
            RenderTo.Png(scene.Shell.Host, "cust221-credentials-fill-review-1400x1500");

            // Cancelling the review keeps the boxes; Cancel in the bar closes them all.
            scene.Setup.Review.DismissCommand.Execute(null);
            scene.Relayout();
            Assert.Equal(2, scene.All<PasswordBox>().Count(b => b.IsVisible));
            scene.Button("Cancel Fill missing").Command.Execute(null);
            scene.Relayout();
            Assert.DoesNotContain(scene.All<PasswordBox>(), b => b.IsVisible);
            Assert.Equal(string.Empty, scene.Box("EXAMPLE_JUDGE_KEY").Password);
        });
    }

    [Fact]
    public void Where_the_app_cannot_type_a_value_the_card_is_the_console_route_it_was()
    {
        using var scene = new Scene(inApp: false);

        UiThread.Run(() =>
        {
            // Set is the console button: named for it, and there is no second button beside it and no box.
            var names = scene.All<Wpf.Ui.Controls.Button>().Select(b => (Name: AutomationProperties.GetName(b), b.IsVisible)).ToArray();
            Assert.Equal(Rows.Length, names.Count(n => n.IsVisible && n.Name.StartsWith("Set EXAMPLE_", StringComparison.Ordinal) && n.Name.EndsWith("in a terminal", StringComparison.Ordinal)));
            Assert.DoesNotContain(names, n => n.IsVisible && n.Name.StartsWith("Set EXAMPLE_", StringComparison.Ordinal) && !n.Name.EndsWith("in a terminal", StringComparison.Ordinal));
            Assert.Single(names, n => n.IsVisible && n.Name == "Fill missing credentials in a terminal");

            scene.Button("Set EXAMPLE_JUDGE_KEY in a terminal").Command.CanExecute(null);
            Assert.All(scene.All<PasswordBox>(), b => Assert.False(b.IsVisible));
            Assert.Contains(scene.All<TextBlock>(), t => t.IsVisible && t.Text.Contains("open a console window", StringComparison.Ordinal));

            scene.ScrollToCredentials();
            RenderTo.Png(scene.Shell.Host, "cust221-credentials-console-only-1400x1500");
        });
    }

    [Fact]
    public void On_a_read_only_installation_every_box_and_button_that_would_change_something_is_off_with_the_reason()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, installation: TestInstallations.ManagedAt(temp.Path));
        var shell = UiThread.Run(() =>
        {
            var s = new PanelShell(services, 1400, 1500);
            _ = s.Show<SetupPanel>();
            return s;
        });
        try
        {
            var vm = UiThread.Run(() => (SetupPanelViewModel)shell.ViewModel);
            UiThread.WaitFor(() => !vm.IsLoading && !vm.Credentials.IsLoading, "setup catalog and the (unavailable) credential read finished");

            UiThread.Run(() =>
            {
                vm.Credentials.PtyAvailable = () => true;
                vm.Credentials.ApplyRows(Rows);
                shell.Host.Relayout();

                var page = shell.Page!;
                var sets = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page)
                    .Where(b => AutomationProperties.GetName(b) is var n && n.StartsWith("Set EXAMPLE_", StringComparison.Ordinal)).ToArray();
                Assert.Equal(Rows.Length * 2, sets.Length);
                Assert.All(sets, b =>
                {
                    Assert.False(b.Command.CanExecute(null));
                    Assert.Equal(TestInstallations.ManagedReason, b.ToolTip);
                });

                var fill = VisualTree.Descendants<Wpf.Ui.Controls.Button>(page).First(b => AutomationProperties.GetName(b) == "Fill missing credentials");
                Assert.False(fill.IsEnabled);
                Assert.Equal(TestInstallations.ManagedReason, fill.ToolTip);
                Assert.All(VisualTree.Descendants<PasswordBox>(page), b => Assert.False(b.IsVisible));
            });
        }
        finally
        {
            UiThread.Run(shell.Dispose);
        }
    }
}
