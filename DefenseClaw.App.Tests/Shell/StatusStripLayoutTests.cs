using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.Core.Gateway.Models;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The status strip on a real view (CUST-273): the chips laid out by the real panel, in the real fonts, at several window widths on the shared UI
/// thread. Whatever the width, nothing overflows or clips; chips go lowest priority first (a chip with a higher number never stays while a lower one goes);
/// a wider window never hides more; the detail sentence gives way before any chip does; the <c>+N</c> chip appears with the first chip hidden and lists
/// them. A PNG of the strip at the widths that matter is written when <c>DC_RENDER_DIR</c> names a folder (and never otherwise). Synthetic data only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class StatusStripLayoutTests
{
    private readonly ITestOutputHelper _output;

    public StatusStripLayoutTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>An ordinary day: both subsystems run, one connector, nothing missing, nothing running, nothing stale - and events that leave unredacted.</summary>
    private static StripScene TypicalScene()
    {
        var scene = new StripScene();
        scene.Strip.Apply(StripScene.Snapshot(new[] { "claudecode" }, version: "0.8.10"));
        scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");
        return scene;
    }

    /// <summary>The strip with everything to say: every chip shows, the keys and the redaction in a warning, a command running, the data stale.</summary>
    private static StripScene FullScene()
    {
        var scene = new StripScene();
        var snapshot = StripScene.Snapshot(new[] { "claudecode", "codex" }, version: "0.8.10") with
        {
            AlertCount = 3,
            CriticalAlertCount = 1,
            RecentAlerts = new[]
            {
                new GatewayAlert { Id = "a", Severity = "HIGH", Timestamp = DateTimeOffset.UtcNow, Action = "block", Target = "synthetic-a" },
                new GatewayAlert { Id = "b", Severity = "CRITICAL", Timestamp = DateTimeOffset.UtcNow, Action = "block", Target = "synthetic-b" },
                new GatewayAlert { Id = "c", Severity = "HIGH", Timestamp = DateTimeOffset.UtcNow, Action = "block", Target = "synthetic-c" },
            },
        };
        scene.Services.ConnectorScope.UpdateRoster(snapshot.ActiveConnectors);
        scene.Strip.Apply(snapshot);
        scene.Services.StatusFacts.PublishMissingKeys(new[] { "OPENAI_API_KEY", "CISCO_AI_DEFENSE_API_KEY", "SPLUNK_HEC_TOKEN" });
        scene.Services.StatusFacts.PublishRedaction("per-route · unredacted");
        scene.Commands.OnStarted(null, StripScene.Running("skill", "scan", "--all"));
        scene.Freshness.SinceLastGoodPoll = TimeSpan.FromSeconds(40);
        scene.Strip.EvaluateFreshness();
        return scene;
    }

    private static readonly StripChipKey[] ByPriority =
    {
        StripChipKey.Guardrail, StripChipKey.Watchdog, StripChipKey.Keys, StripChipKey.Alerts, StripChipKey.Connector,
        StripChipKey.Redaction, StripChipKey.Policy, StripChipKey.Running, StripChipKey.Stale, StripChipKey.Version,
    };

    /// <summary>Opens the strip's row at <paramref name="width"/> and waits for the +N chip to agree with what the panel hid.</summary>
    private static StripRow Open(StripScene scene, double width)
    {
        var row = UiThread.Run(() => new StripRow(scene.Strip, width));
        UiThread.WaitFor(() => row.Hidden.Count == HiddenCount(row), "the +N chip to follow the hidden chips");
        return row;
    }

    private static void Resize(StripRow row, double width)
    {
        UiThread.Run(() => row.Resize(width));
        UiThread.WaitFor(() => row.Hidden.Count == HiddenCount(row), "the +N chip to follow the hidden chips");
    }

    private static int HiddenCount(StripRow row)
    {
        var text = ((StripChip)row.Container(StripChipKey.Overflow).Content).Text;
        return text.Length == 0 ? 0 : int.Parse(text[1..], CultureInfo.InvariantCulture);
    }

    /// <summary>Asserts that every chip that shows ends inside the panel (called on the UI thread).</summary>
    private static void AssertNothingOverflows(StripRow row, string where)
    {
        var panel = row.Panel;
        Assert.True(panel.ActualWidth <= row.Strip.ActualWidth + 0.5, $"{where}: the panel is wider than its strip");
        foreach (var container in row.Containers)
        {
            var chip = (StripChip)container.Content;
            if (!chip.IsShown || ChipStripPanel.GetIsCollapsed(container))
            {
                continue;
            }

            var right = container.TranslatePoint(new Point(container.ActualWidth, 0), panel).X;
            Assert.True(right <= panel.ActualWidth + 0.5, $"{where}: {chip.Key} ends at {right:0.#}, past the panel's {panel.ActualWidth:0.#}");
            Assert.True(container.ActualWidth > 0, $"{where}: {chip.Key} shows but has no room");
        }
    }

    // ---- The collapse order, on a real view, at several widths -----------------------------------------------------------------------------------

    [Fact]
    public void At_every_window_width_nothing_overflows_and_the_chips_that_go_are_the_lowest_priority_ones()
    {
        using var scene = FullScene();
        var row = Open(scene, 1600);
        try
        {
            IReadOnlyList<StripChipKey>? widerHidden = null;
            foreach (var width in new double[] { 1600, 1400, 1300, 1200, 1100, 1000, 940, 900, 800, 700, 600, 500, 400 })
            {
                Resize(row, width);

                UiThread.Run(() =>
                {
                    _output.WriteLine($"{width,5:0}: hidden [{string.Join(", ", row.Hidden)}] visible [{string.Join(", ", row.Visible.Where(k => k != StripChipKey.Detail))}] +N '{scene.Chip(StripChipKey.Overflow).Text}'");

                    // Nothing overflows. (Below the window's own 940 DIP minimum the buttons alone leave a few hundred DIPs: those widths check the order, not the fit.)
                    if (width >= 700)
                    {
                        AssertNothingOverflows(row, width.ToString(CultureInfo.InvariantCulture));
                    }

                    // Hidden is a priority suffix over the chips that have something to say.
                    var hidden = row.Hidden.Select(k => Array.IndexOf(ByPriority, k)).ToList();
                    var shown = row.Visible.Where(k => k is not (StripChipKey.Detail or StripChipKey.Overflow)).Select(k => Array.IndexOf(ByPriority, k)).ToList();
                    if (hidden.Count > 0 && shown.Count > 0)
                    {
                        Assert.True(shown.Max() < hidden.Min(), $"{width}: a lower-priority chip stayed while a higher-priority one went");
                    }

                    // A wider window never hides more than a narrower one: going narrower only adds to the hidden set.
                    if (widerHidden is not null)
                    {
                        Assert.All(widerHidden, key => Assert.Contains(key, row.Hidden));
                    }

                    widerHidden = row.Hidden.ToList();

                    // The +N chip is there exactly when something is hidden, and counts it.
                    var overflow = scene.Chip(StripChipKey.Overflow);
                    var overflowShows = !ChipStripPanel.GetIsCollapsed(row.Container(StripChipKey.Overflow));
                    Assert.Equal(row.Hidden.Count > 0, overflowShows);
                    Assert.Equal(row.Hidden.Count > 0 ? "+" + row.Hidden.Count.ToString(CultureInfo.InvariantCulture) : string.Empty, overflow.Text);
                });
            }
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }

    [Fact]
    public void On_an_ordinary_day_every_chip_shows_at_a_wide_window_and_narrowing_it_takes_them_away_in_reverse_priority_order()
    {
        using var scene = TypicalScene();
        var row = Open(scene, 1800);
        try
        {
            Assert.Empty(UiThread.Run(() => row.Hidden));
            Assert.Equal(
                new[] { StripChipKey.Detail, StripChipKey.Watchdog, StripChipKey.Guardrail, StripChipKey.Alerts, StripChipKey.Connector, StripChipKey.Redaction, StripChipKey.Policy, StripChipKey.Version },
                UiThread.Run(() => row.Visible));

            // Narrow it in small steps and note the order in which chips are first hidden. Measured on the fonts on screen, so it holds whatever
            // they are: the order is the priority's, not a width's.
            var wentAt = new Dictionary<StripChipKey, int>();
            var step = 0;
            for (var width = 1800d; width >= 700; width -= 20, step++)
            {
                Resize(row, width);
                foreach (var key in UiThread.Run(() => row.Hidden))
                {
                    _ = wentAt.TryAdd(key, step);
                }
            }

            _output.WriteLine("went, by step, as the window narrowed: " + string.Join(", ", wentAt.OrderBy(static p => p.Value).Select(static p => $"{p.Key}@{p.Value}")));

            // The chips this day has, lowest priority first. Neighbours may go at the same step (the +N chip's room makes the first hide cost more than the
            // chip's own), never in the wrong order: a chip never goes before one with a lower priority.
            var expected = new[] { StripChipKey.Version, StripChipKey.Policy, StripChipKey.Redaction, StripChipKey.Connector, StripChipKey.Alerts, StripChipKey.Watchdog, StripChipKey.Guardrail };
            var gone = expected.TakeWhile(wentAt.ContainsKey).ToList();
            Assert.Equal(wentAt.Count, gone.Count);
            for (var i = 1; i < gone.Count; i++)
            {
                Assert.True(wentAt[gone[i - 1]] <= wentAt[gone[i]], $"{gone[i]} went before {gone[i - 1]}, which has a lower priority");
            }

            Assert.True(wentAt.Count >= 4, "an ordinary day's chips cannot all fit at 940 beside the pill and the buttons");

            // The first to go is the version; the guardrail goes last of all, if at all.
            Assert.Equal(wentAt.Values.Min(), wentAt[StripChipKey.Version]);
            Assert.True(!wentAt.ContainsKey(StripChipKey.Guardrail) || wentAt[StripChipKey.Guardrail] == wentAt.Values.Max());
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }

    [Fact]
    public void The_guardrail_the_watchdog_and_the_alerts_still_show_at_900_with_the_rest_a_hover_away_in_the_plus_chip()
    {
        using var scene = TypicalScene();
        var row = Open(scene, 900);
        try
        {
            UiThread.Run(() =>
            {
                Assert.Contains(StripChipKey.Guardrail, row.Visible);
                Assert.Contains(StripChipKey.Watchdog, row.Visible);
                Assert.Contains(StripChipKey.Alerts, row.Visible);
                Assert.True(row.Hidden.Count > 0, "an ordinary day's seven chips do not fit beside the pill and the buttons at 900");
                Assert.StartsWith("+", scene.Chip(StripChipKey.Overflow).Text, StringComparison.Ordinal);
                AssertNothingOverflows(row, "900");

                // The redaction warning is one of the hidden, so the +N chip wears its tone: it is never silent.
                Assert.Contains(StripChipKey.Redaction, row.Hidden);
                Assert.Equal("Warn", scene.Chip(StripChipKey.Overflow).Tone);
                Assert.Contains("• Redaction: per-route · unredacted", scene.Chip(StripChipKey.Overflow).ToolTip, StringComparison.Ordinal);
            });
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }

    [Fact]
    public void A_chip_that_appears_or_goes_while_the_window_is_narrow_is_placed_and_the_plus_chip_follows()
    {
        using var scene = TypicalScene();
        var row = Open(scene, 900);
        try
        {
            var before = UiThread.Run(() => row.Hidden.Count);

            // Keys appears (it outranks everything but the two subsystems) and the window cannot hold it: it joins the hidden chips, in red.
            UiThread.Run(() => scene.Services.StatusFacts.PublishMissingKeys(new[] { "EXAMPLE_VERY_LONG_CREDENTIAL_NAME_NUMBER_ONE_KEY", "EXAMPLE_VERY_LONG_CREDENTIAL_NAME_NUMBER_TWO_KEY" }));
            UiThread.WaitFor(() => row.Hidden.Contains(StripChipKey.Keys) && HiddenCount(row) == row.Hidden.Count, "the keys chip to be placed");
            Assert.Equal("Bad", scene.Chip(StripChipKey.Overflow).Tone);
            Assert.Contains("Keys: missing EXAMPLE_VERY_LONG_CREDENTIAL_NAME_NUMBER_ONE_KEY, EXAMPLE_VERY_LONG_CREDENTIAL_NAME_NUMBER_TWO_KEY", scene.Chip(StripChipKey.Overflow).ToolTip, StringComparison.Ordinal);
            Assert.True(UiThread.Run(() => row.Hidden.Count) > before);

            // And goes again: nothing of it is left.
            UiThread.Run(() => scene.Services.StatusFacts.PublishMissingKeys(Array.Empty<string>()));
            UiThread.WaitFor(() => !row.Hidden.Contains(StripChipKey.Keys) && HiddenCount(row) == row.Hidden.Count, "the keys chip to go");
            Assert.Equal("Warn", scene.Chip(StripChipKey.Overflow).Tone);
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }

    // ---- UI Automation -------------------------------------------------------------------------------------------------------------------

    private static List<AutomationPeer> ChipPeers(StripRow row)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(row.Strip);
        var list = peer.GetChildren().Single();
        return list.GetChildren();
    }

    [Fact]
    public void For_UI_Automation_each_chip_is_one_named_element_with_its_tooltip_as_help_and_the_text_inside_is_left_out()
    {
        using var scene = FullScene();
        var row = Open(scene, 1700);
        try
        {
            UiThread.Run(() =>
            {
                var peers = ChipPeers(row);
                var shown = scene.Strip.Chips.Where(static c => c.IsShown).ToList();
                Assert.True(peers.Count >= shown.Count);

                foreach (var chip in shown)
                {
                    var peer = peers.Single(p => p.GetName() == chip.AutomationName);
                    Assert.True(peer.IsControlElement(), $"{chip.Key} is not in the control view");
                    Assert.True(peer.IsContentElement(), $"{chip.Key} is not in the content view");
                    Assert.Equal(chip.ToolTip, peer.GetHelpText());

                    // The words inside are a second, shorter reading of the same thing: left out of both views.
                    foreach (var inner in peer.GetChildren() ?? new List<AutomationPeer>())
                    {
                        Assert.False(inner.IsControlElement() || inner.IsContentElement(), $"{chip.Key}: '{inner.GetName()}' would be read a second time");
                    }
                }
            });
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }

    [Fact]
    public void A_chip_that_does_not_fit_is_off_screen_for_UI_Automation_and_the_plus_chip_names_it()
    {
        using var scene = FullScene();
        var row = Open(scene, 900);
        try
        {
            UiThread.Run(() =>
            {
                var peers = ChipPeers(row);
                var hidden = row.Hidden;
                Assert.NotEmpty(hidden);

                foreach (var key in hidden)
                {
                    var chip = scene.Chip(key);
                    Assert.True(peers.Single(p => p.GetName() == chip.AutomationName).IsOffscreen(), $"{key} is hidden but still reads as on screen");
                    Assert.Contains(chip.Summary, scene.Chip(StripChipKey.Overflow).AutomationName, StringComparison.Ordinal);
                    Assert.Contains(chip.Summary, scene.Chip(StripChipKey.Overflow).ToolTip, StringComparison.Ordinal);
                }

                // The +N chip itself is on screen, with the list as its name.
                var plus = peers.Single(p => p.GetName() == scene.Chip(StripChipKey.Overflow).AutomationName);
                Assert.False(plus.IsOffscreen());
                Assert.StartsWith($"{hidden.Count} more status items hidden at this window width", plus.GetName(), StringComparison.Ordinal);
            });
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }

    [Fact]
    public void The_chips_frames_carry_the_tooltips_and_the_tone_the_view_model_chose_as_a_tag_not_a_frozen_brush()
    {
        using var scene = FullScene();
        var row = Open(scene, 1700);
        try
        {
            UiThread.Run(() =>
            {
                foreach (var container in row.Containers)
                {
                    var chip = (StripChip)container.Content;
                    if (!chip.IsShown || chip.Key == StripChipKey.Overflow)
                    {
                        continue;
                    }

                    var frame = VisualTree.Descendants<FrameworkElement>(container).First(e => e.ToolTip is not null);
                    Assert.Equal(chip.ToolTip, frame.ToolTip);
                    if (frame is Border { Tag: not null } border)
                    {
                        Assert.Equal(chip.Tone, border.Tag);
                    }
                }

                // A tone is a Tag on a DcBadge, so a live light/dark switch recolours it.
                var keys = VisualTree.Descendants<Border>(row.Container(StripChipKey.Keys)).First(static b => b.Tag is not null);
                Assert.Equal("Bad", keys.Tag);
                var redaction = VisualTree.Descendants<Border>(row.Container(StripChipKey.Redaction)).First(static b => b.Tag is not null);
                Assert.Equal("Warn", redaction.Tag);
            });
        }
        finally
        {
            UiThread.Run(row.Dispose);
        }
    }

    // ---- Bindings -------------------------------------------------------------------------------------------------------------------------

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
    public void Every_binding_of_the_strip_names_a_property_that_exists_on_what_it_is_bound_to()
    {
        var errors = new BindingErrors();
        var source = System.Diagnostics.PresentationTraceSources.DataBindingSource;
        using var scene = FullScene();

        UiThread.Run(() =>
        {
            // Refresh first: WPF reads its trace configuration once, and only a refreshed source honours a level set in code.
            System.Diagnostics.PresentationTraceSources.Refresh();
            source.Listeners.Add(errors);
            var level = source.Switch.Level;
            source.Switch.Level = System.Diagnostics.SourceLevels.Warning;
            try
            {
                // the detector itself: a binding to nothing is reported, so an empty result below means something
                var probe = new TextBlock { DataContext = scene.Strip };
                probe.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchThing"));

                using var row = new StripRow(scene.Strip, 1400);

                // every kind of chip, every state it changes between, and the panel's own command
                foreach (var width in new double[] { 1400, 900, 1400 })
                {
                    row.Resize(width);
                }

                scene.Services.StatusFacts.PublishMissingKeys(Array.Empty<string>());
                scene.Services.StatusFacts.PublishRedaction("per-route (loading)");
                scene.Strip.Apply(StripScene.Snapshot(running: false, paused: true, version: null));
                row.Resize(940);
                row.Resize(1400);
            }
            finally
            {
                source.Switch.Level = level;
                source.Listeners.Remove(errors);
            }
        });

        // WPF-UI's own templates may complain about themselves; only what mentions the strip's types is ours.
        Assert.Contains(errors.Messages, m => m.Contains("NoSuchThing", StringComparison.Ordinal));
        var ours = errors.Messages
            .Where(m => !m.Contains("NoSuchThing", StringComparison.Ordinal) &&
                        (m.Contains("StripChip", StringComparison.Ordinal) || m.Contains("StatusStrip", StringComparison.Ordinal) || m.Contains("ChipStripPanel", StringComparison.Ordinal)))
            .ToArray();
        Assert.Empty(ours);
    }

    // ---- Every look -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Default", "Dark")]
    [InlineData("Default", "Light")]
    [InlineData("Linear", "Dark")]
    [InlineData("Tui", "Dark")]
    [InlineData("Tui", "Light")]
    [InlineData("Cisco", "Light")]
    public void Under_every_look_nothing_overflows_and_the_sentence_keeps_its_least(string style, string mode)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(style), Enum.Parse<AppearanceMode>(mode)));
        foreach (var scene in new[] { TypicalScene(), FullScene() })
        {
            using (scene)
            {
                var row = Open(scene, 940);
                try
                {
                    foreach (var width in new double[] { 940, 1100, 1400 })
                    {
                        Resize(row, width);
                        UiThread.Run(() =>
                        {
                            var sentence = row.Container(StripChipKey.Detail);
                            _output.WriteLine($"{style}/{mode} {width,5:0}: hidden [{string.Join(", ", row.Hidden)}] sentence {sentence.ActualWidth:0}");

                            AssertNothingOverflows(row, $"{style}/{mode} {width}");

                            // The sentence is trimmed before any chip goes, but never below 100 DIPs (or its own width, when that is less).
                            Assert.True(sentence.ActualWidth >= 99.5, $"{style}/{mode} {width}: the sentence has only {sentence.ActualWidth:0.#} DIPs");
                        });
                    }
                }
                finally
                {
                    UiThread.Run(row.Dispose);
                }
            }
        }
    }

    // ---- Pictures -----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1900)]
    [InlineData(1400)]
    [InlineData(940)]
    [InlineData(900)]
    public void The_strip_is_drawn_at_the_widths_that_matter(int width)
    {
        using var typical = TypicalScene();
        var typicalRow = Open(typical, width);
        try
        {
            UiThread.Run(() => RenderTo.Png(typicalRow.Host, $"cust273-strip-typical-{width}"));
        }
        finally
        {
            UiThread.Run(typicalRow.Dispose);
        }

        using var full = FullScene();
        var fullRow = Open(full, width);
        try
        {
            UiThread.Run(() => RenderTo.Png(fullRow.Host, $"cust273-strip-everything-{width}"));
        }
        finally
        {
            UiThread.Run(fullRow.Dispose);
        }
    }
}
