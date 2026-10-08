using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Redaction;
using DefenseClaw.App.Views.Redaction;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.Redaction;

/// <summary>
/// The redaction window's content (CUST-295) built for real over a view-model fed by the fake CLI: it lays out in each look, shows exactly the
/// controls of the chosen operation (all 21), draws a preview as a readable diff, puts the review in front of an apply, and says only that the
/// runtime lacks the editor when it does. Set <c>DC_RENDER_DIR</c> to write PNGs of it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RedactionViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>Builds the view-model and reads it, all on the UI thread (the fake answers at once, so every step completes in place).</summary>
    private (RedactionViewModel Vm, FakeRedactionCli Cli) Open(Action<FakeRedactionCli>? script = null, bool supported = true)
    {
        var services = TestServices.Create(_temp);
        _services.Add(services);
        var cli = new FakeRedactionCli();
        script?.Invoke(cli);
        var vm = new RedactionViewModel(services)
        {
            RunCli = cli.Run,
            Gate = () => supported ? GateDecision.Open : GateDecision.Closed,
        };
        vm.Review.RunStep = cli.Step;
        _ = vm.RefreshCommand.ExecuteAsync(null);
        return (vm, cli);
    }

    private static void Routes(FakeRedactionCli cli)
    {
        cli.Status = FakeRedactionCli.Fixture("status-mixed.json");
        cli.ProfileList = FakeRedactionCli.Fixture("profile-list-custom.json");
        cli.Routes["example-otlp"] = FakeRedactionCli.Fixture("route-list-two.json");
    }

    private static IReadOnlyList<string> Texts(FrameworkElement root) =>
        VisualTree.Descendants<TextBlock>(root).Where(t => t.IsVisible).Select(t => t.Text).Where(t => t.Length > 0).ToArray();

    private static T Named<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        VisualTree.Descendants<T>(root).Single(e => e.IsVisible && AutomationProperties.GetName(e) == name);

    private static IReadOnlyList<string> VisibleNames(FrameworkElement root, IEnumerable<string> universe)
    {
        var wanted = universe.ToHashSet(StringComparer.Ordinal);
        return VisualTree.Descendants<FrameworkElement>(root)
            .Where(e => e.IsVisible && wanted.Contains(AutomationProperties.GetName(e)))
            .Select(AutomationProperties.GetName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    // ------------------------------------------------------------------ layout in each look

    [Theory]
    [InlineData("Cisco", "Light")]
    [InlineData("Linear", "Dark")]
    public void The_view_lays_out_the_policy_the_quick_sheet_and_the_editor_then_a_preview_and_a_review(string styleName, string modeName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));

        UiThread.Run(() =>
        {
            var (vm, _) = Open(Routes);
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 2600);

            // the current policy
            var texts = Texts(view);
            Assert.Contains("Current policy", texts);
            Assert.Contains("Quick apply: one profile everywhere", texts);
            Assert.Contains("Advanced: every operation", texts);
            Assert.All(RedactionVocabulary.Buckets, bucket => Assert.Contains(bucket, texts));
            Assert.Contains("local-sqlite", texts);
            Assert.Contains("example-otlp", texts);
            Assert.Contains("managed-enterprise-ai-defense", texts);
            Assert.Contains("Managed by your organisation: locked.", texts);
            Assert.Contains(vm.Summary, texts);
            Assert.Contains(texts, t => t.StartsWith("drop logs", StringComparison.Ordinal)); // example-route's summary
            Assert.Contains(texts, t => t.Contains("No redaction profile covers this store.", StringComparison.Ordinal));

            var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Where(b => b.IsVisible).Select(b => b.Content?.ToString()).ToList();
            Assert.Contains("Refresh", buttons);
            Assert.Contains("Preview", buttons);
            Assert.Contains("Apply…", buttons);

            // the review is not up, and the result card is not there yet
            Assert.DoesNotContain(VisualTree.Descendants<CommandReviewControl>(view), c => c.IsVisible);
            Assert.DoesNotContain("Dismiss", buttons);
            RenderTo.Png(host, $"cust295-policy-{style}-{mode}");

            // a preview: the difference as rows
            _ = vm.PreviewQuickCommand.ExecuteAsync(null);
            host.Relayout();
            texts = Texts(view);
            Assert.Contains("Preview: Apply profile everywhere", texts);
            Assert.Contains("Dry run: nothing written", texts);
            Assert.Contains("14 delivery legs would change.", texts);
            Assert.Contains("starts redacting", texts);
            Assert.Contains("local-sqlite / all-collected-logs-and-mandatory-floor", texts);
            Assert.Contains("Nothing was written and the gateway was not restarted.", texts);
            Assert.Contains("Apply this change…", VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Where(b => b.IsVisible).Select(b => b.Content?.ToString()));
            RenderTo.Png(host, $"cust295-preview-{style}-{mode}");

            // an apply opens the review, with the command and the restart spelled out
            vm.QuickProfile = "strict";
            _ = vm.ApplyQuickCommand.ExecuteAsync(null);
            host.Resize(880, 1000);
            var review = Assert.Single(VisualTree.Descendants<CommandReviewControl>(view), c => c.IsVisible);
            Assert.Same(vm.Review.CommandReview, review.Review);

            // the exact command sits in a selectable box of the review
            var command = VisualTree.Descendants<TextBox>(review).Where(b => b.IsVisible).Select(b => b.Text);
            Assert.Contains(command, t => t.Contains("setup redaction apply --scope all-configurable --profile strict --yes --json --no-restart", StringComparison.Ordinal));
            RenderTo.Png(host, $"cust295-review-{style}-{mode}");
        });
    }

    [Fact]
    public void The_quick_sheet_offers_the_four_built_in_profiles_in_the_runtime_s_order_each_with_its_description()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, _) = Open();
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 1200);

            var segments = VisualTree.Descendants<Views.Controls.DcSegment>(view).Where(s => s.IsVisible).ToArray();

            // the segments are written out in the XAML; this holds them to the vocabulary, so a profile added or reworded there is noticed here
            Assert.Equal(RedactionVocabulary.BuiltInProfiles, segments.Select(static s => (string)s.Value!).ToArray());
            Assert.Equal(RedactionVocabulary.BuiltInProfiles.Select(RedactionVocabulary.Describe), segments.Select(static s => (string)s.ToolTip).ToArray());

            // the chosen one is the view-model's, and choosing another reaches it
            Assert.Equal(vm.QuickProfile, segments.Single(static s => s.IsSelected).Value);
            segments.Single(static s => Equals(s.Value, "content")).IsSelected = true;
            Assert.Equal("content", vm.QuickProfile);
            Assert.Equal(RedactionVocabulary.Describe("content"), vm.QuickDescription);
            host.Relayout();
            Assert.Contains(RedactionVocabulary.Describe("content"), Texts(view));
        });
    }

    // ------------------------------------------------------------------ not on this runtime

    [Fact]
    public void A_runtime_without_the_editor_shows_the_one_sentence_and_nothing_to_press()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, cli) = Open(supported: false);
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 700);

            var texts = Texts(view);
            Assert.Contains("Redaction policy is not available", texts);
            Assert.Contains(RuntimeCapabilityCatalog.UnsupportedMessage, texts);
            Assert.DoesNotContain("Current policy", texts);
            Assert.DoesNotContain("Quick apply: one profile everywhere", texts);
            Assert.DoesNotContain("Advanced: every operation", texts);

            var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Where(b => b.IsVisible).Select(b => b.Content?.ToString()).ToList();
            Assert.DoesNotContain("Refresh", buttons);
            Assert.DoesNotContain("Preview", buttons);
            Assert.Empty(cli.Ran);
            RenderTo.Png(host, "cust295-unavailable");
        });
    }

    // ------------------------------------------------------------------ the 21 forms

    private static readonly string[] FieldNames =
    [
        "Profile to apply", "Profile, optional", "Bucket", "Destination", "New route name", "Route", "Position, counted from 1", "Signals", "Buckets",
        "Collect logs", "Collect traces", "Collect metrics", "Profile name", "Built-in profile this one starts from", "Detector groups", "Field modes",
        "Profile that takes over", "What the route does with matching events", "Minimum severity it matches", "Event sources it matches",
        "Connectors it matches", "Producer actions it matches", "Event names it matches",
    ];

    private static readonly string[] CollectNames = ["Collect logs", "Collect metrics", "Collect traces"];

    private static readonly string[] RouteFilterNames =
    [
        "Connectors it matches", "Event names it matches", "Event sources it matches", "Minimum severity it matches", "Producer actions it matches", "What the route does with matching events",
    ];

    private static string[] Expected(RedactionOperation op) => op switch
    {
        RedactionOperation.ApplyEverywhere or RedactionOperation.ApplyDefaults => ["Profile to apply"],
        RedactionOperation.DefaultsSet => ["Profile, optional", .. CollectNames],
        RedactionOperation.BucketSet => ["Bucket", "Profile, optional", .. CollectNames],
        RedactionOperation.BucketReset => ["Bucket"],
        RedactionOperation.ProfileShow => ["Profile name"],
        RedactionOperation.ProfileSet => ["Built-in profile this one starts from", "Detector groups", "Field modes", "Profile name"],
        RedactionOperation.ProfileRemove => ["Profile name", "Profile that takes over"],
        RedactionOperation.DestinationShow or RedactionOperation.DestinationInherit or RedactionOperation.RouteList => ["Destination"],
        RedactionOperation.DestinationSend => ["Buckets", "Destination", "Profile, optional", "Signals"],
        RedactionOperation.RouteAdd => ["Buckets", "Destination", "New route name", "Position, counted from 1", "Signals", .. RouteFilterNames],
        RedactionOperation.RouteSet => ["Buckets", "Destination", "Route", "Signals", .. RouteFilterNames],
        RedactionOperation.RouteMove => ["Destination", "Position, counted from 1", "Route"],
        RedactionOperation.RouteRemove => ["Destination", "Route"],
        _ => [],
    };

    [Fact]
    public void Each_of_the_21_operations_shows_exactly_its_own_controls()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, _) = Open(Routes);
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 3000);

            Assert.Equal(21, vm.Operations.Count);
            foreach (var info in vm.Operations)
            {
                vm.SelectedOperation = info;
                host.Relayout();

                var shown = VisibleNames(view, FieldNames);
                var expected = Expected(info.Operation).Order(StringComparer.Ordinal).ToArray();
                Assert.True(expected.SequenceEqual(shown), $"{info.Title}: expected [{string.Join(", ", expected)}] but the form shows [{string.Join(", ", shown)}]");

                // the dry-run box and the restart box exist for a change and for nothing else
                var checks = VisualTree.Descendants<CheckBox>(view).Where(c => c.IsVisible).Select(c => c.Content?.ToString()).ToList();
                Assert.Equal(info.IsMutation, checks.Contains("Preview only (dry run)"));
                Assert.Equal(info.Operation == RedactionOperation.BucketSet, checks.Contains("Give the bucket back the default profile"));
                Assert.Equal(info.Operation == RedactionOperation.DestinationSend, checks.Contains("All buckets (*)"));

                // the primary button says what it will do
                var primary = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Single(b => b.IsVisible && AutomationProperties.GetName(b) == "Run the operation, preview it, or review and apply it");
                Assert.Equal(info.IsMutation ? "Preview" : "Run", primary.Content);
            }
        });
    }

    [Fact]
    public void The_dry_run_box_is_on_and_the_restart_box_is_off_and_unavailable_until_dry_run_is_turned_off()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, _) = Open(Routes);
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 2600);
            vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ApplyEverywhere);
            vm.Form.Profile = "strict";
            host.Relayout();

            CheckBox Box(string text) => VisualTree.Descendants<CheckBox>(view).Last(c => c.IsVisible && Equals(c.Content, text));
            var dry = Box("Preview only (dry run)");
            var restart = Box("Restart the gateway after applying"); // the editor's: the last one on the page
            Assert.True(dry.IsChecked);
            Assert.False(restart.IsChecked);
            Assert.False(restart.IsEnabled);

            dry.IsChecked = false;
            host.Relayout();
            Assert.False(vm.DryRun);
            Assert.True(restart.IsEnabled);
            var primary = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Single(b => b.IsVisible && AutomationProperties.GetName(b) == "Run the operation, preview it, or review and apply it");
            Assert.Equal("Review and apply…", primary.Content);

            restart.IsChecked = true;
            Assert.True(vm.Restart);
            Assert.Contains("--restart", vm.CommandText, StringComparison.Ordinal);

            dry.IsChecked = true;
            host.Relayout();
            Assert.False(vm.Restart); // turning the preview back on clears the restart
            Assert.False(restart.IsEnabled);
            Assert.DoesNotContain("--restart", vm.CommandText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_choice_made_in_a_box_reaches_the_form_and_survives_the_policy_being_read_again()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, _) = Open(Routes);
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 2600);
            vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.RouteMove);
            host.Relayout();

            var route = Named<ComboBox>(view, "Route");
            Assert.Equal(["example-route", "example-strict-logs"], route.Items.Cast<RedactionChoice>().Select(c => c.Value).ToArray());
            route.SelectedValue = "example-strict-logs";
            Assert.Equal("example-strict-logs", vm.Form.RouteName);

            Named<TextBox>(view, "Position, counted from 1").Text = "1";
            Assert.Equal("1", vm.Form.PositionText);
            Assert.Equal(
                "defenseclaw setup redaction route move example-otlp example-strict-logs --position 1 --json --dry-run",
                vm.CommandText);

            // the policy is read again: the lists are filled in place and the choices stay
            _ = vm.RefreshCommand.ExecuteAsync(null);
            host.Relayout();
            Assert.Equal("example-strict-logs", Named<ComboBox>(view, "Route").SelectedValue);
            Assert.Equal("example-otlp", Named<ComboBox>(view, "Destination").SelectedValue);
            Assert.Equal("example-strict-logs", vm.Form.RouteName);
            Assert.Equal("1", vm.Form.PositionText);

            // and a value the form already holds shows in its box
            vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.BucketSet);
            vm.Form.Bucket = "tool.activity";
            host.Relayout();
            Assert.Equal("tool.activity", Named<ComboBox>(view, "Bucket").SelectedValue);

            // a profile's name is typed or picked, in one box
            vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ProfileSet);
            host.Relayout();
            var name = Named<ComboBox>(view, "Profile name");
            Assert.True(name.IsEditable);
            Assert.Equal(["example-profile"], name.Items.Cast<string>().ToArray()); // the custom profiles the runtime listed
            name.Text = "mine";
            Assert.Equal("mine", vm.Form.ProfileName);
            name.SelectedItem = "example-profile";
            Assert.Equal("example-profile", vm.Form.ProfileName);
        });
    }

    // ------------------------------------------------------------------ results and the review

    [Fact]
    public void A_send_policy_preview_is_three_rows_and_a_raw_content_preview_is_red()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, cli) = Open(Routes);
            cli.PreviewAnswer = op => FakeRedactionCli.Fixture(op == RedactionOperation.DestinationSend ? "dry-destination-send.json" : "dry-route-remove.json");
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 2800);

            vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.DestinationSend);
            vm.Form.SignalChecks[0].IsChecked = true;
            vm.Form.BucketChecks[1].IsChecked = true;
            _ = vm.RunCommand.ExecuteAsync(null);
            host.Relayout();

            var rows = VisualTree.Descendants<Border>(view)
                .Where(b => b.IsVisible)
                .Select(AutomationProperties.GetName)
                .Where(n => n.Contains(" to ", StringComparison.Ordinal) && n.Contains("delivering", StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(3, rows.Length);
            Assert.Equal(2, rows.Count(n => n.EndsWith("stops delivering", StringComparison.Ordinal)));
            Assert.Single(rows, n => n.EndsWith("starts delivering", StringComparison.Ordinal));
            Assert.Contains(Texts(view), t => t == "1 start delivering · 28 stop delivering");

            vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.RouteRemove);
            vm.Form.RouteName = "example-route";
            _ = vm.RunCommand.ExecuteAsync(null);
            host.Relayout();
            Assert.Contains(Texts(view), t => t.Contains("would carry raw, unredacted content", StringComparison.Ordinal));
            Assert.Contains("starts delivering, unredacted", Texts(view));
            RenderTo.Png(host, "cust295-raw-preview");
        });
    }

    [Fact]
    public void Escape_closes_the_review_before_anything_else_and_f5_reads_the_policy_again()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, cli) = Open();
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 1200);

            Assert.False(view.HandlesEscape);
            vm.QuickProfile = "strict";
            _ = vm.ApplyQuickCommand.ExecuteAsync(null);
            Assert.True(view.HandlesEscape);
            Assert.True(vm.HandleEscape());
            Assert.False(view.HandlesEscape);
            Assert.Empty(cli.Applied);

            var reads = cli.Ran.Count(a => RedactionArgv.Identify(a) == RedactionOperation.Status);
            Assert.True(vm.RefreshCommand.CanExecute(null));
            _ = vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(reads + 1, cli.Ran.Count(a => RedactionArgv.Identify(a) == RedactionOperation.Status));
        });
    }

    /// <summary>Collects what WPF's binding engine says went wrong, so a typo in a binding path fails a test instead of showing a blank.</summary>
    private sealed class BindingErrors : System.Diagnostics.TraceListener
    {
        public List<string> Messages { get; } = new();

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is { Length: > 0 })
            {
                Messages.Add(message);
            }
        }
    }

    [Fact]
    public void Every_binding_of_the_view_names_a_property_that_exists_on_what_it_is_bound_to()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));
        var errors = new BindingErrors();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;

        UiThread.Run(() =>
        {
            // Refresh first: WPF reads its trace configuration once, and only a refreshed source honours a level set in code.
            System.Diagnostics.PresentationTraceSources.Refresh();
            source.Listeners.Add(errors);
            var level = source.Switch.Level;
            source.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            try
            {
                var (vm, cli) = Open(Routes);
                cli.PreviewAnswer = op => FakeRedactionCli.Fixture(op == RedactionOperation.ApplyEverywhere ? "dry-route-remove.json" : RedactionFakeFile(op));

                // the detector itself: a binding to nothing is reported, so an empty result below means something
                var control = new TextBlock { DataContext = vm };
                control.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchThing"));

                var view = new RedactionView { DataContext = vm };
                using var host = new OffscreenHost(view, 880, 3200);

                // every form, a read, a preview, a failure and an apply review
                foreach (var info in vm.Operations)
                {
                    vm.SelectedOperation = info;
                    host.Relayout();
                }

                vm.SelectedOperation = RedactionOperations.Info(RedactionOperation.ProfileShow);
                vm.Form.ProfileName = "strict";
                _ = vm.RunCommand.ExecuteAsync(null);
                host.Relayout();

                _ = vm.PreviewQuickCommand.ExecuteAsync(null);
                host.Relayout();
                vm.ShowRaw = true;
                host.Relayout();

                cli.ExitCode = 1;
                _ = vm.PreviewQuickCommand.ExecuteAsync(null);
                host.Relayout();
                cli.ExitCode = 0;

                vm.QuickProfile = "strict";
                _ = vm.ApplyQuickCommand.ExecuteAsync(null);
                host.Relayout();
                _ = vm.Review.ConfirmCommand.ExecuteAsync(null);
                host.Relayout();
            }
            finally
            {
                source.Switch.Level = level;
                source.Listeners.Remove(errors);
            }
        });

        // WPF-UI's own templates may complain about themselves; only what mentions this window's types is ours.
        Assert.Contains(errors.Messages, m => m.Contains("NoSuchThing", StringComparison.Ordinal));
        var ours = errors.Messages
            .Where(m => (m.Contains("Redaction", StringComparison.Ordinal) || m.Contains("RelativeSource", StringComparison.Ordinal)) && !m.Contains("NoSuchThing", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(ours);
    }

    private static string RedactionFakeFile(RedactionOperation op) => FakeRedactionCli.PreviewFile(op);

    [Fact]
    public void Every_control_that_can_be_pressed_or_typed_in_has_a_name_a_screen_reader_can_say()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var (vm, _) = Open(Routes);
            var view = new RedactionView { DataContext = vm };
            using var host = new OffscreenHost(view, 880, 3000);
            _ = vm.PreviewQuickCommand.ExecuteAsync(null);

            foreach (var info in vm.Operations)
            {
                vm.SelectedOperation = info;
                host.Relayout();
                var unnamed = VisualTree.Descendants<Control>(view)
                    .Where(c => c.IsVisible && c is Button or ComboBox or TextBox or CheckBox or ListBox or RadioButton)
                    .Where(c => string.IsNullOrWhiteSpace(AutomationProperties.GetName(c)) && string.IsNullOrWhiteSpace(c is ContentControl { Content: string text } ? text : null))
                    .Where(c => c is not (TextBox { IsReadOnly: true }) && c is not ListBoxItem && c is not TextBox { TemplatedParent: ComboBox })
                    .Select(c => $"{c.GetType().Name} in {info.Title}")
                    .ToArray();
                Assert.Empty(unnamed);
            }
        });
    }
}
