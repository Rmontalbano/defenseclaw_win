using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// The Activity panel's outer list: up to 200 cards, each collapsed or open on a transcript, realized only while in view.
/// Built in the shell stand-in at the window's 940 x 620 DIP minimum and at 1400 x 900 with 200 synthetic invocations
/// (<see cref="InvocationFactory"/>), laid out off-screen. The transcript lists inside the cards are covered by
/// <see cref="ActivityPanelLayoutTests"/>; this is about the cards - what is built, and what a recycled card shows.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class ActivityCardListTests
{
    /// <summary>
    /// What "only the cards in view" means here. A card is at least ~167 DIPs tall, so a 900 DIP window shows five or six;
    /// the list keeps one more either side so Tab has a card to land on. Nowhere near 200.
    /// </summary>
    private const int MostRealizedCards = 10;

    private const int Entries = 200;

    private readonly ITestOutputHelper _output;

    public ActivityCardListTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ what is built

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void Two_hundred_entries_build_only_the_cards_in_view(int width, int height)
    {
        using var scene = ActivityScene.Open(width, height);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries, (i, invocation) =>
            {
                // A spread of entries: some chatty, some carrying a secret, a few still running.
                if (i % 9 == 0)
                {
                    InvocationFactory.AppendNumbered(invocation, 300, first: 5);
                }

                if (i % 7 == 0)
                {
                    InvocationFactory.UseEnvironmentSecret(invocation, "DEFENSECLAW_TOKEN");
                }
            });

            var cards = scene.RealizedCards();
            _output.WriteLine($"{scene.Name}: {cards.Count} of {Entries} cards built; viewport {scene.CardScroll.ViewportHeight:0} DIPs, extent {scene.CardScroll.ExtentHeight:0}");

            Assert.Equal(Entries, scene.Cards.Items.Count);
            Assert.InRange(cards.Count, 2, MostRealizedCards);

            // Every card that is built shows the entry it was built for, from the top of the list.
            Assert.Equal(scene.ViewModel.Rows.Take(cards.Count), scene.RealizedRows());

            // Collapsed entries build no transcript rows.
            Assert.Empty(VisualTree.Descendants<ListBoxItem>(scene.Cards));

            // The list is bounded by the page, not by its content: nothing above it hands it unlimited height.
            Assert.Empty(Ancestors<ScrollViewer>(scene.Cards));
            Assert.True(scene.CardScroll.ViewportHeight < scene.Shell.PageSize.Height);
            Assert.True(scene.CardScroll.ExtentHeight > scene.CardScroll.ViewportHeight * 10, "the list should scroll, not grow to fit 200 cards");

            scene.Render($"activity-cards-{scene.Name}");
        });
    }

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void A_busy_list_renders_at_both_window_sizes(int width, int height)
    {
        using var scene = ActivityScene.Open(width, height);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries, (i, invocation) =>
            {
                if (i % 4 == 0)
                {
                    InvocationFactory.UseEnvironmentSecret(invocation, "DEFENSECLAW_TOKEN");
                }
            });

            // Newest first: the running command, a failed one that is open on its output, a destructive one, a plain one.
            _ = scene.AddRow(InvocationFactory.Create(false, "status"), expand: false);
            var destructive = InvocationFactory.Create(false, "skill", "remove", "sample-skill");
            InvocationFactory.Finish(destructive, 0);
            _ = scene.AddRow(destructive, expand: false);
            var failed = InvocationFactory.Create(false, "policy", "activate", "strict");
            InvocationFactory.AppendNumbered(failed, 6, prefix: "applying rule");
            InvocationFactory.Append(failed, "error: rule set rejected", CliStream.StandardError);
            InvocationFactory.Finish(failed, 2);
            _ = scene.AddRow(failed);
            var running = InvocationFactory.Create(false, "doctor");
            InvocationFactory.UseEnvironmentSecret(running, "DEFENSECLAW_TOKEN");
            InvocationFactory.AppendNumbered(running, 60, prefix: "check");
            var runningRow = scene.AddRow(running);

            scene.ScrollCardsTo(0);
            Assert.NotNull(scene.CardOf(runningRow));
            scene.Render($"activity-busy-{scene.Name}-top");

            // Some way down, where recycled cards are showing entries that were built far from here.
            scene.ScrollCardsTo(3_000);
            AssertEveryCardShowsItsOwnEntry(scene);
            scene.Render($"activity-busy-{scene.Name}-scrolled");
        });
    }

    [Fact]
    public void The_card_list_scrolls_by_the_pixel_and_recycles_its_cards()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var cards = scene.Cards;

            Assert.True(VirtualizingPanel.GetIsVirtualizing(cards));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(cards));
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(cards));
            Assert.True(ScrollViewer.GetCanContentScroll(cards));
            Assert.True(scene.CardScroll.CanContentScroll);
            Assert.True(scene.CardPanel.CanVerticallyScroll);
        });
    }

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void Scrolling_through_all_two_hundred_entries_keeps_the_built_cards_bounded(int width, int height)
    {
        using var scene = ActivityScene.Open(width, height);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var seen = new HashSet<ActivityRow>();
            var most = 0;

            // A wheel notch is ~48 DIPs, a page is a viewport: walk the whole list in steps between the two.
            var step = scene.CardScroll.ViewportHeight * 0.8;
            for (var offset = 0.0; offset < scene.CardScroll.ExtentHeight + step; offset += step)
            {
                scene.ScrollCardsTo(offset);
                var built = scene.RealizedRows();
                most = Math.Max(most, built.Count);
                Assert.InRange(built.Count, 1, MostRealizedCards);
                seen.UnionWith(built);
            }

            _output.WriteLine($"{scene.Name}: walked the list, at most {most} cards built at once, {seen.Count} distinct entries shown.");
            Assert.Contains(rows[^1], seen);
            Assert.True(seen.Count > 150, "walking the list should have shown nearly every entry");
        });
    }

    [Fact]
    public void Open_entries_build_their_transcript_rows_only_while_their_card_is_in_view()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries, (i, invocation) => InvocationFactory.AppendNumbered(invocation, 2_000, first: 5));
            foreach (var row in rows.Take(8))
            {
                row.IsExpanded = true;
            }

            scene.Host.Relayout();
            var built = VisualTree.Descendants<ListBoxItem>(scene.Cards).Count();
            _output.WriteLine($"8 open entries of 2,000 lines each: {built} transcript rows built.");

            // At most the open cards in view, each with the rows of its 360 DIP list - not 16,000.
            Assert.InRange(built, 1, MostRealizedCards * 40);

            // Scrolled past them, the open cards' rows are not on screen (a recycled card that was open keeps its
            // last few rows as invisible elements until it is next opened - a few dozen at most).
            scene.ScrollCardsTo(5_000);
            Assert.DoesNotContain(VisualTree.Descendants<ListBoxItem>(scene.Cards), item => item.IsVisible);
            Assert.InRange(VisualTree.Descendants<ListBoxItem>(scene.Cards).Count(), 0, MostRealizedCards * 40);
            Assert.All(rows.Take(8), row => Assert.True(row.IsExpanded));
        });
    }

    // ------------------------------------------------------------------ recycled cards carry no one else's state

    [Fact]
    public void An_open_entry_stays_open_when_scrolled_out_and_back_and_a_recycled_card_is_not_open_for_another()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            rows[2].IsExpanded = true;
            scene.Host.Relayout();
            Assert.NotNull(scene.CardOf(rows[2]));
            AssertEveryCardShowsItsOwnEntry(scene);

            // Walk far down and back, checking every card on the way: whichever container the open card's elements were
            // reused for must show its new entry shut.
            foreach (var offset in new[] { 400.0, 1_500, 4_000, 9_000, 20_000, 33_000, 9_000, 1_500, 0 })
            {
                scene.ScrollCardsTo(offset);
                AssertEveryCardShowsItsOwnEntry(scene);
                Assert.True(rows[2].IsExpanded);
                Assert.Equal(1, rows.Count(r => r.IsExpanded));
            }

            var card = scene.CardOf(rows[2])!;
            Assert.True(VisualTree.Find<Expander>(card)!.IsExpanded);
            Assert.True(scene.ListFor(rows[2]).IsVisible);
        });
    }

    [Fact]
    public void Opening_and_shutting_a_card_changes_its_height_at_once()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var card = scene.CardOf(rows[1])!;
            var shut = card.ActualHeight;

            rows[1].IsExpanded = true;
            scene.Host.Relayout();
            Assert.True(card.ActualHeight > shut + 40, $"open card is {card.ActualHeight}, shut card was {shut}");

            // No animation to wait out: the expander WPF-UI ships holds a collapsing card open for 200 ms.
            rows[1].IsExpanded = false;
            scene.Host.Relayout();
            Assert.Equal(shut, card.ActualHeight, 0.5);
        });
    }

    [Fact]
    public void A_transcripts_follow_flag_reading_position_and_selection_survive_scrolling_its_card_out_and_back()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var reader = InvocationFactory.Create(false, "skill", "list");
        InvocationFactory.AppendNumbered(reader, 1_000);
        InvocationFactory.Finish(reader);
        var follower = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(follower, 600);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var readerRow = scene.AddRow(reader);
            var followerRow = scene.AddRow(follower);
            scene.ScrollCardsTo(0);

            // The operator scrolls the first transcript up and selects two of its lines.
            var readerList = scene.ListFor(readerRow);
            var readerScroll = VisualTree.Find<ScrollViewer>(readerList)!;
            Assert.True(IsAtEnd(readerScroll), "a transcript opens on its newest line");
            readerScroll.ScrollToVerticalOffset(400);
            UserScrolled(readerList);
            scene.Host.Relayout();
            Assert.False(readerRow.IsFollowing);
            Assert.Equal(400, readerRow.Transcript.ReadingSequence);

            readerList.SelectedItems.Add(readerList.Items[405]);
            readerList.SelectedItems.Add(readerList.Items[410]);
            Assert.Equal(2, readerRow.Transcript.SelectedCount);

            var followerList = scene.ListFor(followerRow);
            Assert.True(followerRow.IsFollowing);
            Assert.True(IsAtEnd(VisualTree.Find<ScrollViewer>(followerList)!));

            // Far away: neither card is built any more.
            scene.ScrollCardsTo(15_000);
            Assert.Null(scene.CardOf(readerRow));
            Assert.Null(scene.CardOf(followerRow));
            AssertEveryCardShowsItsOwnEntry(scene);
            Assert.False(readerRow.IsFollowing);
            Assert.Equal(400, readerRow.Transcript.ReadingSequence);
            Assert.Equal(2, readerRow.Transcript.SelectedCount);
            Assert.True(readerRow.IsExpanded);

            // Back: the same card state, in whatever elements it is built in now.
            scene.ScrollCardsTo(0);
            AssertEveryCardShowsItsOwnEntry(scene);

            readerList = scene.ListFor(readerRow);
            readerScroll = VisualTree.Find<ScrollViewer>(readerList)!;
            Assert.False(readerRow.IsFollowing);
            Assert.Equal("line 401", FirstVisibleText(readerList));
            Assert.Equal(400, readerScroll.VerticalOffset);
            Assert.Equal(new[] { "line 406", "line 411" }, readerList.SelectedItems.OfType<ActivityOutputLine>().Select(l => l.Text).OrderBy(t => t, StringComparer.Ordinal).ToArray());

            followerList = scene.ListFor(followerRow);
            Assert.True(followerRow.IsFollowing);
            Assert.True(IsAtEnd(VisualTree.Find<ScrollViewer>(followerList)!));
            Assert.Empty(followerList.SelectedItems);
            Assert.Equal(200, rows.Count);
        });
    }

    [Fact]
    public void A_recycled_transcript_list_never_shows_the_scroll_position_or_selection_of_the_entry_before_it()
    {
        using var scene = ActivityScene.Open(940, 620);

        UiThread.Run(() =>
        {
            // Entries 0, 20 and 40 are open on long transcripts. 0 is being read (not following, scrolled, two lines
            // selected); 20 follows; 40 is not following but was never scrolled to a line of its own.
            var rows = scene.AddInvocations(Entries, (i, invocation) =>
            {
                if (i % 20 == 0)
                {
                    InvocationFactory.AppendNumbered(invocation, 800, first: 5);
                }
            });
            foreach (var index in new[] { 0, 20, 40 })
            {
                rows[index].IsExpanded = true;
            }

            scene.Host.Relayout();
            var readerList = scene.ListFor(rows[0]);
            var readerScroll = VisualTree.Find<ScrollViewer>(readerList)!;
            readerScroll.ScrollToVerticalOffset(250);
            UserScrolled(readerList);
            scene.Host.Relayout();
            readerList.SelectedItems.Add(readerList.Items[255]);
            readerList.SelectedItems.Add(readerList.Items[256]);
            Assert.False(rows[0].IsFollowing);

            // Walk the list in small steps so every card is built, recycled and rebuilt many times, checking each step.
            for (var offset = 0.0; offset < 3_500; offset += 130)
            {
                scene.ScrollCardsTo(offset);
                AssertEveryCardShowsItsOwnEntry(scene);
            }

            for (var offset = 3_500.0; offset >= 0; offset -= 130)
            {
                scene.ScrollCardsTo(offset);
                AssertEveryCardShowsItsOwnEntry(scene);
            }

            // Entries 20 and 40 follow, so they are on their end whichever elements they were given; 0 is where it was left.
            scene.ScrollCardsTo(0);
            Assert.Equal("line 251", FirstVisibleText(scene.ListFor(rows[0])));
            Assert.Equal(2, scene.ListFor(rows[0]).SelectedItems.Count);
            Assert.Equal(0, rows.Skip(1).Sum(r => r.Transcript.SelectedCount));
        });
    }

    [Fact]
    public void Selecting_everything_in_a_long_transcript_is_restored_in_one_step()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var invocation = InvocationFactory.Create(true, "skill", "list", "--json");
        InvocationFactory.AppendNumbered(invocation, 50_000);
        InvocationFactory.Finish(invocation);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            ApplicationCommands.SelectAll.Execute(null, list);
            Assert.Equal(50_000, list.SelectedItems.Count);
            Assert.True(row.Transcript.IsEverythingSelected);

            scene.ScrollCardsTo(9_000);
            Assert.Null(scene.CardOf(row));
            scene.ScrollCardsTo(0);

            list = scene.ListFor(row);
            Assert.Equal(50_000, list.SelectedItems.Count);
        });
    }

    [Fact]
    public void Turning_follow_off_with_the_checkbox_records_where_the_operator_is_reading()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var running = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(running, 500);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var row = scene.AddRow(running);
            var list = scene.ListFor(row);
            var scroll = VisualTree.Find<ScrollViewer>(list)!;
            Assert.True(IsAtEnd(scroll));

            // Follow output off: the list stays where it is, and that is now the place to come back to.
            var checkbox = VisualTree.Find<CheckBox>(scene.CardOf(row)!)!;
            checkbox.IsChecked = false;
            scene.Host.Relayout();
            Assert.False(row.IsFollowing);
            var reading = (int)scroll.VerticalOffset;
            Assert.Equal(reading, row.Transcript.ReadingSequence);

            // New output does not move it - not now, and not after the card has been scrolled away and back.
            InvocationFactory.AppendNumbered(running, 200, first: 501);
            row.Tick();
            scene.ScrollCardsTo(9_000);
            InvocationFactory.AppendNumbered(running, 200, first: 701);
            row.Tick();
            scene.ScrollCardsTo(0);

            list = scene.ListFor(row);
            Assert.False(row.IsFollowing);
            Assert.Equal(reading, VisualTree.Find<ScrollViewer>(list)!.VerticalOffset);
            Assert.Equal($"line {reading + 1}", FirstVisibleText(list));
        });
    }

    [Fact]
    public void A_bulk_append_keeps_the_selection_the_list_and_the_entry_agree_on()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var running = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(running, 100);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var row = scene.AddRow(running);
            var list = scene.ListFor(row);
            list.SelectedItems.Add(list.Items[10]);
            list.SelectedItems.Add(list.Items[20]);

            // More than TranscriptCollection.BulkThreshold lines at once arrive as one Reset.
            InvocationFactory.AppendNumbered(running, 1_200, first: 101);
            row.Tick();
            scene.Host.Relayout();
            Assert.Equal(row.Transcript.SelectedCount, list.SelectedItems.Count);

            scene.ScrollCardsTo(9_000);
            scene.ScrollCardsTo(0);
            Assert.Equal(row.Transcript.SelectedCount, scene.ListFor(row).SelectedItems.Count);
        });
    }

    [Fact]
    public void Text_selected_in_a_cards_command_line_does_not_follow_its_elements_to_another_entry()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var command = VisualTree.Descendants<TextBox>(scene.CardOf(rows[0])!).First(t => AutomationProperties.GetName(t) == "Command line");
            command.SelectAll();
            Assert.True(command.SelectionLength > 0);

            foreach (var offset in new[] { 3_000.0, 9_000, 0, 1_500, 0 })
            {
                scene.ScrollCardsTo(offset);
                foreach (var card in scene.RealizedCards())
                {
                    var box = VisualTree.Descendants<TextBox>(card).First(t => AutomationProperties.GetName(t) == "Command line");
                    var row = (ActivityRow)card.DataContext;
                    Assert.True(
                        box.SelectionLength == 0 || ReferenceEquals(row, rows[0]),
                        $"{row.ShortCommand}: {box.SelectionLength} characters of its command line are selected");
                }
            }
        });
    }

    // ------------------------------------------------------------------ actions on recycled cards

    [Fact]
    public void Every_action_chip_and_badge_on_a_recycled_card_belongs_to_its_own_entry()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries, (i, invocation) =>
            {
                if (i % 3 == 0)
                {
                    InvocationFactory.UseEnvironmentSecret(invocation, "DEFENSECLAW_TOKEN", "DEFENSECLAW_API_KEY");
                }
            });

            // Destructive, state-changing and read-only entries, some failed, one still running: the badge texts differ.
            var destructive = InvocationFactory.Create(false, "skill", "remove", "sample");
            InvocationFactory.Finish(destructive, 1);
            var running = InvocationFactory.Create(false, "policy", "activate", "strict");
            InvocationFactory.UseEnvironmentSecret(running, "DEFENSECLAW_TOKEN");
            var destructiveRow = scene.AddRow(destructive, expand: false);
            var runningRow = scene.AddRow(running, expand: false);

            foreach (var offset in new[] { 0.0, 700, 2_400, 6_000, 12_345, 400, 0 })
            {
                scene.ScrollCardsTo(offset);
                AssertEveryCardShowsItsOwnEntry(scene);
            }

            scene.ScrollCardsTo(0);
            var runningCard = scene.CardOf(runningRow)!;
            Assert.True(Chip(runningCard, "env secret").IsVisible);
            Assert.Equal(runningRow.TierText, VisualTree.Descendants<TextBlock>(runningCard).First(t => t.Text == runningRow.TierText).Text);
            Assert.Equal("running", VisualTree.Descendants<TextBlock>(runningCard).First(t => t.Text == "running").Text);
            Assert.Equal("exit 1", VisualTree.Descendants<TextBlock>(scene.CardOf(destructiveRow)!).First(t => t.Text.StartsWith("exit", StringComparison.Ordinal)).Text);
            Assert.False(Chip(scene.CardOf(rows[1]) ?? throw new InvalidOperationException("entry 1 not built"), "env secret").IsVisible);
        });
    }

    [Fact]
    public void Cancel_copy_and_export_act_on_the_entry_of_the_card_they_are_in_after_recycling()
    {
        using var scene = ActivityScene.Open(940, 620);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var running = InvocationFactory.Create(false, "doctor");
            InvocationFactory.AppendNumbered(running, 3);
            var runningRow = scene.AddRow(running, expand: false);

            // Recycle every card at least once, ending back at the top.
            for (var offset = 0.0; offset < 4_000; offset += 200)
            {
                scene.ScrollCardsTo(offset);
            }

            scene.ScrollCardsTo(0);
            foreach (var card in scene.RealizedCards())
            {
                var row = (ActivityRow)card.DataContext;
                Assert.Same(row.CopyCommand, ButtonIn(card, "Copy argv").Command);
                Assert.Same(row.CopyOutputCommand, ButtonIn(card, "Copy output").Command);
                Assert.Same(row.ExportLogCommand, ButtonIn(card, "Export log").Command);

                var cancel = ButtonIn(card, "Cancel this command");
                Assert.Same(row.CancelCommand, cancel.Command);
                Assert.Equal(row.IsRunning, cancel.IsVisible);
                Assert.Equal(row.CanCancel, cancel.IsEnabled);
            }

            // Cancel on the running entry's card reaches the runner for that entry (it is not one of the runner's, so it says why).
            var runningCard = scene.CardOf(runningRow)!;
            var cancelButton = ButtonIn(runningCard, "Cancel this command");
            Assert.True(cancelButton.IsVisible && cancelButton.IsEnabled);
            Assert.True(cancelButton.Command!.CanExecute(null));
            cancelButton.Command.Execute(null);
            Assert.Single(scene.Notices);
            Assert.Contains("not running under this runner", scene.Notices[0], StringComparison.Ordinal);
            Assert.False(running.CancelRequested);
            Assert.Equal(Entries + 1, scene.Cards.Items.Count);
            Assert.Equal(Entries, rows.Count);
        });
    }

    // ------------------------------------------------------------------ a new run arriving at the top

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void A_new_run_arriving_at_the_top_does_not_move_what_the_operator_is_reading(int width, int height)
    {
        using var scene = ActivityScene.Open(width, height);

        UiThread.Run(() =>
        {
            // Open entries of different heights above and around the place being read.
            var rows = scene.AddInvocations(Entries, (i, invocation) =>
            {
                if (i is 3 or 9 or 12)
                {
                    InvocationFactory.AppendNumbered(invocation, 300, first: 5);
                }
            });
            foreach (var index in new[] { 3, 9, 12 })
            {
                rows[index].IsExpanded = true;
            }

            scene.Host.Relayout();

            // The operator reads some way down.
            scene.ScrollCardsTo(2_150);
            var reading = TopMostVisibleCard(scene);
            var readingRow = (ActivityRow)reading.DataContext;
            var top = scene.TopOf(reading);
            var offset = scene.CardScroll.VerticalOffset;
            Assert.True(offset > 2_000);

            // A run starts: the view-model puts it at the top (and drops the oldest entry once the list is full).
            var started = InvocationFactory.Create(false, "policy", "activate", "strict");
            scene.ViewModel.Rows.Insert(0, new ActivityRow(started, scene.Cli, scene.Notices.Add));
            scene.ViewModel.Rows.RemoveAt(scene.ViewModel.Rows.Count - 1);
            scene.Host.Relayout();

            var after = scene.CardOf(readingRow);
            Assert.NotNull(after);
            Assert.Equal(top, scene.TopOf(after!), 1.0);
            Assert.True(scene.CardScroll.VerticalOffset > offset, "the list scrolls by the new card's height to hold the place");
            Assert.Equal(Entries, scene.Cards.Items.Count);

            // Several at once (a Refresh that finds more than one) push the card past what is built: still held.
            for (var i = 0; i < 3; i++)
            {
                scene.ViewModel.Rows.Insert(0, new ActivityRow(InvocationFactory.Create(false, "doctor"), scene.Cli, scene.Notices.Add));
            }

            scene.Host.Relayout();
            Assert.Equal(top, scene.TopOf(scene.CardOf(readingRow)!), 1.0);

            scene.ScrollCardsTo(0);
            AssertEveryCardShowsItsOwnEntry(scene);
        });
    }

    [Fact]
    public void At_the_top_a_new_run_appears_at_the_top()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            Assert.Equal(0, scene.CardScroll.VerticalOffset);

            var started = new ActivityRow(InvocationFactory.Create(false, "doctor"), scene.Cli, scene.Notices.Add);
            scene.ViewModel.Rows.Insert(0, started);
            scene.Host.Relayout();

            Assert.Equal(0, scene.CardScroll.VerticalOffset);
            Assert.Equal(0, scene.TopOf(scene.CardOf(started)!), 0.5);
            Assert.Equal("running", VisualTree.Descendants<TextBlock>(scene.CardOf(started)!).Select(t => t.Text).First(t => t == "running"));
        });
    }

    [Fact]
    public void A_card_growing_above_the_place_being_read_does_not_move_it()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            scene.ScrollCardsTo(0);
            var below = scene.TopOf(scene.CardOf(rows[3])!);

            // Read from the top of the fourth card; the third is built just above the view and is about to open.
            scene.ScrollCardsTo(below + 40);
            Assert.NotNull(scene.CardOf(rows[2]));
            var reading = TopMostVisibleCard(scene);
            var top = scene.TopOf(reading);

            rows[2].IsExpanded = true;
            scene.Host.Relayout();

            Assert.Equal(top, scene.TopOf(scene.CardOf((ActivityRow)reading.DataContext)!), 1.0);
        });
    }

    [Fact]
    public void A_following_transcript_below_the_fold_never_drags_the_page()
    {
        using var scene = ActivityScene.Open(940, 620);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 800);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var row = scene.AddRow(invocation);
            scene.ScrollCardsTo(120);
            var offset = scene.PageOffset();

            for (var tick = 0; tick < 5; tick++)
            {
                InvocationFactory.AppendNumbered(invocation, 100, first: 801 + (tick * 100));
                row.Tick();
                scene.Host.Relayout();
            }

            Assert.Equal(offset, scene.PageOffset(), 0.5);
            Assert.True(IsAtEnd(VisualTree.Find<ScrollViewer>(scene.ListFor(row))!));
        });
    }

    // ------------------------------------------------------------------ the wheel, between the transcript and the list of cards

    [Fact]
    public void The_wheel_scrolls_a_transcript_that_can_use_it_and_leaves_the_card_list_alone()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_000);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var inner = VisualTree.Find<ScrollViewer>(list)!;
            inner.ScrollToVerticalOffset(300);
            scene.Host.Relayout();
            var outerBefore = scene.PageOffset();

            var seenByCards = 0;
            scene.CardScroll.AddHandler(UIElement.MouseWheelEvent, new MouseWheelEventHandler((_, _) => seenByCards++), handledEventsToo: false);

            Wheel(VisualTree.Descendants<ListBoxItem>(list).First(), -120);
            Wheel(VisualTree.Descendants<ListBoxItem>(list).First(), 120);
            Wheel(VisualTree.Descendants<ListBoxItem>(list).First(), 120);
            scene.Host.Relayout();

            Assert.NotEqual(300, inner.VerticalOffset);
            Assert.Equal(outerBefore, scene.PageOffset());
            Assert.Equal(0, seenByCards);
        });
    }

    [Fact]
    public void A_notch_the_transcript_cannot_use_scrolls_the_card_list_instead()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 1_000);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var inner = VisualTree.Find<ScrollViewer>(list)!;

            // A following transcript is at its end: down is the card list's.
            Assert.True(IsAtEnd(inner));
            var innerAt = inner.VerticalOffset;
            var outer = scene.PageOffset();
            Wheel(VisualTree.Descendants<ListBoxItem>(list).Last(), -120);
            scene.Host.Relayout();
            Assert.Equal(innerAt, inner.VerticalOffset);
            Assert.True(scene.PageOffset() > outer, "the card list should have taken the notch");

            // Up scrolls the transcript again, and not the card list.
            outer = scene.PageOffset();
            Wheel(VisualTree.Descendants<ListBoxItem>(list).Last(), 120);
            scene.Host.Relayout();
            Assert.True(inner.VerticalOffset < innerAt);
            Assert.Equal(outer, scene.PageOffset());

            // At the top of the transcript, up is the card list's.
            inner.ScrollToHome();
            scene.Host.Relayout();
            scene.ScrollCardsTo(300);
            outer = scene.PageOffset();
            Wheel(VisualTree.Descendants<ListBoxItem>(list).First(), 120);
            scene.Host.Relayout();
            Assert.Equal(0, inner.VerticalOffset);
            Assert.True(scene.PageOffset() < outer);
        });
    }

    [Fact]
    public void A_transcript_that_fits_leaves_every_notch_to_the_card_list()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.AppendNumbered(invocation, 5);

        UiThread.Run(() =>
        {
            _ = scene.AddInvocations(Entries);
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            var outer = scene.PageOffset();

            Wheel(VisualTree.Descendants<ListBoxItem>(list).First(), -120);
            scene.Host.Relayout();

            Assert.True(scene.PageOffset() > outer);
        });
    }

    [Fact]
    public void The_wheel_over_a_card_outside_its_transcript_scrolls_the_card_list()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var outer = scene.PageOffset();
            var card = scene.CardOf(rows[1])!;

            Wheel(VisualTree.Descendants<Border>(card).First(), -120);
            scene.Host.Relayout();

            Assert.True(scene.PageOffset() > outer);
        });
    }

    // ------------------------------------------------------------------ keyboard and screen readers

    [Fact]
    public void The_card_list_and_its_cards_are_named_for_a_screen_reader()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries, (i, invocation) =>
            {
                if (i == 1)
                {
                    InvocationFactory.Finish(invocation, 3);
                }
            });

            Assert.Equal("Commands run in this session, newest first", AutomationProperties.GetName(scene.Cards));

            var peer = UIElementAutomationPeer.CreatePeerForElement(scene.Cards);
            Assert.Equal("Commands run in this session, newest first", peer.GetName());

            // A screen reader is told what each card is - the command, how it ended, its tier, when - not a type name.
            var children = peer.GetChildren();
            var built = scene.RealizedRows();
            _output.WriteLine($"{children.Count} cards offered to a screen reader, {built.Count} built");
            Assert.InRange(children.Count, 1, MostRealizedCards);
            foreach (var child in children)
            {
                _output.WriteLine($"card name: {child.GetName()}");
            }

            Assert.Equal(built.Select(row => row.ToString()), children.Select(child => child.GetName()));
            Assert.Contains("exit 3", children[1].GetName(), StringComparison.Ordinal);
            Assert.Contains(rows[0].TierText, children[0].GetName(), StringComparison.Ordinal);
            Assert.StartsWith(rows[0].ShortCommand, children[0].GetName(), StringComparison.Ordinal);

            // The rest are reached the way sighted operators reach them: by scrolling, which the scroll viewer exposes.
            var scroll = (IScrollProvider)UIElementAutomationPeer.CreatePeerForElement(scene.CardScroll).GetPattern(PatternInterface.Scroll)!;
            Assert.True(scroll.VerticallyScrollable);
            scroll.SetScrollPercent(ScrollPatternIdentifiers.NoScroll, 50);
            scene.Host.Relayout();
            Assert.DoesNotContain(rows[0], scene.RealizedRows());
            Assert.True(scene.CardScroll.VerticalOffset > 1_000);
        });
    }

    [Fact]
    public void The_output_toggle_is_a_named_expander_a_keyboard_can_open()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var card = scene.CardOf(rows[0])!;
            var expander = VisualTree.Find<Expander>(card)!;

            Assert.Equal("Output", AutomationProperties.GetName(expander));
            var toggle = VisualTree.Find<System.Windows.Controls.Primitives.ToggleButton>(expander)!;
            Assert.True(toggle.Focusable);
            Assert.True(toggle.IsTabStop);
            Assert.Contains(VisualTree.Descendants<TextBlock>(toggle), t => t.Text == rows[0].OutputHeader);

            // The expand/collapse pattern a screen reader uses is still the Expander's own.
            var pattern = (IExpandCollapseProvider)UIElementAutomationPeer.CreatePeerForElement(expander).GetPattern(PatternInterface.ExpandCollapse)!;
            pattern.Expand();
            scene.Host.Relayout();
            Assert.True(rows[0].IsExpanded);
            pattern.Collapse();
            scene.Host.Relayout();
            Assert.False(rows[0].IsExpanded);

            // The header is a toggle button, which is what Space and Enter press.
            var press = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(toggle).GetPattern(PatternInterface.Toggle)!;
            press.Toggle();
            Assert.True(rows[0].IsExpanded);
            press.Toggle();
            Assert.False(rows[0].IsExpanded);
        });
    }

    [Fact]
    public void Page_keys_from_inside_a_card_scroll_the_card_list_by_a_page()
    {
        using var scene = ActivityScene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.AddInvocations(Entries);
            var button = ButtonIn(scene.CardOf(rows[1])!, "Copy argv");
            var page = scene.CardScroll.ViewportHeight;

            Assert.True(PressKey(button, Key.PageDown));
            scene.Host.Relayout();
            Assert.Equal(page, scene.CardScroll.VerticalOffset, 2.0);

            Assert.True(PressKey(button, Key.PageDown));
            scene.Host.Relayout();
            Assert.Equal(2 * page, scene.CardScroll.VerticalOffset, 2.0);

            // The cards that come into view on the way are built for the entries that belong there.
            AssertEveryCardShowsItsOwnEntry(scene);

            Assert.True(PressKey(ButtonIn(scene.RealizedCards()[1], "Copy argv"), Key.PageUp));
            scene.Host.Relayout();
            Assert.Equal(page, scene.CardScroll.VerticalOffset, 2.0);
        });
    }

    [Fact]
    public void Tab_walks_from_card_to_card_and_builds_the_next_card_as_it_goes()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        UiThread.Run(() =>
        {
            var viewModel = new ActivityPanelViewModel(services);
            var rows = new List<ActivityRow>();
            for (var i = 0; i < Entries; i++)
            {
                var invocation = InvocationFactory.Create(false, "skill", "list", $"--n{i}");
                InvocationFactory.Finish(invocation);
                var row = new ActivityRow(invocation, services.Cli, notify: null);
                rows.Add(row);
                viewModel.Rows.Add(row);
            }

            viewModel.IsEmpty = false;
            var panel = new ActivityPanel { DataContext = viewModel };
            var window = new Window
            {
                Content = panel,
                Width = 940,
                Height = 620,
                Left = -4000,
                ShowInTaskbar = false,
                ShowActivated = true,
            };

            try
            {
                window.Show();
                _ = window.Activate();
                UiThread.Settle();
                if (!window.IsActive)
                {
                    // No interactive desktop to take keyboard focus (a locked or disconnected session): the card a Tab
                    // lands on is built by the list's cache, which the tests above measure; only the live focus walk
                    // cannot be seen.
                    return;
                }

                var cards = (ItemsControl)panel.FindName("Cards");
                var scroll = (ScrollViewer)cards.Template.FindName("CardScroll", cards);
                var first = VisualTree.Descendants<Button>(panel).First(b => AutomationProperties.GetName(b) == "Copy argv");
                _ = first.Focus();
                UiThread.Settle();

                var visited = new List<int>();
                for (var step = 0; step < 60; step++)
                {
                    if (!window.IsActive)
                    {
                        // Something else took the desktop's focus mid-walk (another window, a locked session): nothing to conclude.
                        return;
                    }

                    var focused = Keyboard.FocusedElement as DependencyObject;
                    var card = focused is null ? null : Ancestors<ContentPresenter>(focused).FirstOrDefault(c => c.DataContext is ActivityRow);
                    Assert.NotNull(card);
                    var index = rows.IndexOf((ActivityRow)card!.DataContext);
                    if (visited.Count == 0 || visited[^1] != index)
                    {
                        visited.Add(index);
                    }

                    _ = ((UIElement)focused!).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    UiThread.Settle();
                }

                _output.WriteLine($"Tab visited cards {string.Join(", ", visited)}; page offset {scroll.VerticalOffset:0}");
                Assert.Equal(Enumerable.Range(0, visited.Count).ToArray(), visited.ToArray());
                Assert.True(visited.Count >= 8, $"Tab only got as far as card {visited[^1]}");
                Assert.True(scroll.VerticalOffset > 0, "the list should have scrolled to follow the focus");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Whether <paramref name="scroll"/> shows its last item (the transcript list scrolls by item).</summary>
    private static bool IsAtEnd(ScrollViewer scroll) => scroll.VerticalOffset >= scroll.ScrollableHeight - 1.0;

    /// <summary>Text of the first transcript row whose top edge is inside the list.</summary>
    private static string FirstVisibleText(ListBox list) =>
        VisualTree.Descendants<ListBoxItem>(list)
            .Where(item => item.TranslatePoint(new Point(0, 0), list).Y >= -0.5)
            .OrderBy(item => item.TranslatePoint(new Point(0, 0), list).Y)
            .Select(item => ((ActivityOutputLine)item.DataContext).Text)
            .First();

    /// <summary>The operator's input reaching a transcript list (what makes the view look at where it has been left).</summary>
    private static void UserScrolled(ListBox list) =>
        list.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = list,
        });

    /// <summary>
    /// One mouse-wheel notch over <paramref name="target"/>, the way the input system delivers it: the tunnelling event
    /// first, then - unless something handled that - the bubbling one that the scroll viewers act on.
    /// </summary>
    private static void Wheel(UIElement target, int delta)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = target,
        };
        target.RaiseEvent(preview);
        if (preview.Handled)
        {
            return;
        }

        target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = target,
        });
    }

    /// <summary>A key press reaching <paramref name="target"/> (as the focused element); whether anything handled it.</summary>
    private static bool PressKey(UIElement target, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = target,
        };
        target.RaiseEvent(press);
        return press.Handled;
    }

    private static IEnumerable<T> Ancestors<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T match)
            {
                yield return match;
            }
        }
    }

    /// <summary>The built card whose top is the first one showing in the viewport.</summary>
    private static FrameworkElement TopMostVisibleCard(ActivityScene scene) =>
        scene.RealizedCards().First(card => scene.TopOf(card) + card.ActualHeight > 0.5);

    private static Button ButtonIn(FrameworkElement card, string automationName) =>
        VisualTree.Descendants<Button>(card).First(button => AutomationProperties.GetName(button) == automationName);

    private static FrameworkElement Chip(FrameworkElement card, string text) =>
        (FrameworkElement)VisualTreeHelper.GetParent(VisualTree.Descendants<TextBlock>(card).First(t => t.Text == text));

    /// <summary>
    /// The invariant recycling must keep: every card that is built shows its own entry and nothing left over from the entry
    /// its elements were used for before - open or shut, the follow box, the transcript it lists, where that is scrolled and
    /// what is selected in it, the command, its badges and chips, and what its buttons are bound to.
    /// </summary>
    private static void AssertEveryCardShowsItsOwnEntry(ActivityScene scene)
    {
        foreach (var card in scene.RealizedCards())
        {
            var row = (ActivityRow)card.DataContext;
            var label = row.ShortCommand;

            Assert.Equal(row.CommandLine, VisualTree.Descendants<TextBox>(card).First(t => AutomationProperties.GetName(t) == "Command line").Text);
            Assert.Equal(row.ExitBadgeText, VisualTree.Descendants<TextBlock>(card).First(t => t.Text == row.ExitBadgeText).Text);
            Assert.Equal(row.UsedEnvironmentSecret, Chip(card, "env secret").IsVisible);
            Assert.Equal(row.UsedStdinSecret, Chip(card, "stdin secret").IsVisible);
            Assert.Same(row.CopyOutputCommand, ButtonIn(card, "Copy output").Command);

            var expander = VisualTree.Find<Expander>(card)!;
            Assert.True(row.IsExpanded == expander.IsExpanded, $"{label}: entry open={row.IsExpanded}, card open={expander.IsExpanded}");

            var list = VisualTree.Find<ListBox>(card);
            if (!row.IsExpanded)
            {
                Assert.True(list is null || !list.IsVisible, $"{label}: a shut entry shows a transcript");
                continue;
            }

            Assert.NotNull(list);

            Assert.Same(row.Output, list!.ItemsSource);
            Assert.Same(row, list.DataContext);
            Assert.Equal(row.Transcript.SelectedCount, list.SelectedItems.Count);

            var checkbox = VisualTree.Find<CheckBox>(card)!;
            Assert.Equal(row.IsFollowing, checkbox.IsChecked == true);

            var scroll = VisualTree.Find<ScrollViewer>(list)!;
            if (list.Items.Count > 0 && scroll.ScrollableHeight > 0)
            {
                if (row.IsFollowing)
                {
                    Assert.True(IsAtEnd(scroll), $"{label}: follows its output but the list is at {scroll.VerticalOffset} of {scroll.ScrollableHeight}");
                }
                else
                {
                    Assert.True(
                        Math.Abs(scroll.VerticalOffset - row.Transcript.ReadingIndex) <= 1,
                        $"{label}: was left reading at {row.Transcript.ReadingIndex} but the list is at {scroll.VerticalOffset}");
                }
            }
        }
    }
}
