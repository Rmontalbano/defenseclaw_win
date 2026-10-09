using System.Text;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.Tests;

/// <summary>
/// The small terminal that turns what a pseudo-console draws into lines (<see cref="VtScreen"/>). The first stream is what the Windows 11
/// pseudo-console actually sent for <c>getpass.getpass('Value: ')</c> followed by <c>print(len(v))</c>, captured while proving the technique
/// (CUST-221); the window title carries a synthetic path. The rest are the sequences a CLI's output and a renderer's repaint are made of.
/// </summary>
public sealed class VtScreenTests
{
    private const string Esc = "\u001b";

    /// <summary>The prompt arrives in pieces with sequences between them, and the answer is drawn after the cursor was sent home.</summary>
    private static readonly string[] Capture =
    {
        Esc + "[?9001h" + Esc + "[?1004h" + Esc + "[?25l" + Esc + "[2J" + Esc + "[m" + Esc + "[HV",
        Esc + "]0;C:\\Tools\\python.exe\a" + Esc + "[?25halue: ",
        Esc + "[?25l" + Esc + "[H" + Esc + "[?25h\r\n20\r\n" + Esc + "[?9001l" + Esc + "[?1004l",
    };

    private static List<string> AllLines(VtScreen screen, params string[] chunks)
    {
        var lines = new List<string>();
        foreach (var chunk in chunks)
        {
            screen.Write(chunk);
            lines.AddRange(screen.TakeLines());
        }

        lines.AddRange(screen.Flush());
        return lines;
    }

    [Fact]
    public void The_captured_stream_reads_as_the_prompt_and_the_answer()
    {
        var screen = new VtScreen();

        Assert.Equal(new[] { "Value:", "20" }, AllLines(screen, Capture));
    }

    [Fact]
    public void The_prompt_is_the_cursor_row_while_the_child_waits_at_it()
    {
        var screen = new VtScreen();

        screen.Write(Capture[0]);
        Assert.Equal("V", screen.CursorLine);

        screen.Write(Capture[1]);
        Assert.Equal("Value: ", screen.CursorLine);
        Assert.Empty(screen.TakeLines()); // the prompt's row is not a finished line while the cursor is on it

        screen.Write(Capture[2]);
        Assert.Equal(string.Empty, screen.CursorLine);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public void However_the_stream_is_cut_the_lines_are_the_same(int size)
    {
        var whole = string.Concat(Capture);
        var chunks = new List<string>();
        for (var i = 0; i < whole.Length; i += size)
        {
            chunks.Add(whole.Substring(i, Math.Min(size, whole.Length - i)));
        }

        Assert.Equal(new[] { "Value:", "20" }, AllLines(new VtScreen(), chunks.ToArray()));
    }

    [Fact]
    public void A_row_drawn_again_replaces_the_text_instead_of_doubling_it()
    {
        var screen = new VtScreen();

        // The renderer sends the cursor home and draws the row it already drew.
        var lines = AllLines(screen, "Value: ", Esc + "[H", "Value: abc", "\r\nnext\r\n");

        Assert.Equal(new[] { "Value: abc", "next" }, lines);
    }

    [Fact]
    public void A_carriage_return_overwrites_a_progress_line()
    {
        var lines = AllLines(new VtScreen(), "10%\r50%\r100%\r\nDone\r\n");

        Assert.Equal(new[] { "100%", "Done" }, lines);
    }

    [Fact]
    public void Absolute_cursor_moves_place_text_on_the_row_they_name()
    {
        var lines = AllLines(new VtScreen(), Esc + "[2;1Hsecond" + Esc + "[1;1Hfirst" + Esc + "[3;1Hthird");

        Assert.Equal(new[] { "first", "second", "third" }, lines);
    }

    [Fact]
    public void Erase_in_line_clears_the_end_of_the_row_the_start_or_all_of_it()
    {
        var lines = AllLines(
            new VtScreen(),
            "abcdef\r" + "xy" + Esc + "[K\r\n" +
            "abcdef" + Esc + "[3D" + Esc + "[1K\r\n" +
            "gone" + Esc + "[2K\r\n" +
            "end\r\n");

        Assert.Equal(new[] { "xy", "    ef", string.Empty, "end" }, lines);
    }

    [Fact]
    public void Characters_can_be_inserted_deleted_and_erased()
    {
        var lines = AllLines(
            new VtScreen(),
            "abcdef" + Esc + "[4D" + Esc + "[2P\r\n" +
            "abcdef" + Esc + "[4D" + Esc + "[2@\r\n" +
            "abcdef" + Esc + "[4D" + Esc + "[2X\r\n");

        Assert.Equal(new[] { "abef", "ab  cdef", "ab  ef" }, lines);
    }

    [Fact]
    public void A_line_that_wraps_at_the_right_edge_is_still_one_line()
    {
        // The pseudo-console is wide so that this does not happen; when it does, a value or a sentence is not cut in two.
        var lines = AllLines(new VtScreen(columns: 10, rows: 5), "0123456789abcde\r\nnext\r\n");

        Assert.Equal(new[] { "0123456789abcde", "next" }, lines);
    }

    [Fact]
    public void A_wrapped_line_is_not_finished_while_the_cursor_is_on_its_tail()
    {
        var screen = new VtScreen(columns: 10, rows: 5);

        screen.Write("0123456789abc");

        Assert.Empty(screen.TakeLines());
        screen.Write("\r\n");
        Assert.Equal(new[] { "0123456789abc" }, screen.TakeLines());
    }

    [Fact]
    public void Colours_titles_modes_and_string_sequences_are_dropped()
    {
        var text =
            Esc + "[1;31mred" + Esc + "[0m " +
            Esc + "]0;a title" + Esc + "\\" + "ok " +
            Esc + "]8;;http://example.test" + "\a" + "link" + Esc + "]8;;" + "\a " +
            Esc + "P1$r" + Esc + "\\" + "dcs " +
            Esc + "[?1049h" + Esc + "[38:5:196mx" + Esc + "(B" + Esc + "=" + Esc + ">y";

        Assert.Equal(new[] { "red ok link dcs xy" }, AllLines(new VtScreen(), text));
    }

    [Fact]
    public void A_sequence_cut_between_two_writes_is_still_one_sequence()
    {
        var lines = AllLines(new VtScreen(), "ab" + Esc + "[3", "1mcd" + Esc, "]0;t", "itle\a" + "ef");

        Assert.Equal(new[] { "abcdef" }, lines);
    }

    [Fact]
    public void An_escape_inside_a_sequence_starts_a_new_one()
    {
        // ESC [ 1 ; ESC [ 31 m ...: the first is abandoned, the second is a colour.
        Assert.Equal(new[] { "ok" }, AllLines(new VtScreen(), Esc + "[1;" + Esc + "[31mok"));
    }

    [Fact]
    public void A_sequence_with_a_runaway_parameter_list_is_ignored_and_does_not_grow()
    {
        var screen = new VtScreen();

        screen.Write(Esc + "[" + new string('1', 100_000) + "Hafter");

        Assert.Equal(new[] { "after" }, screen.Flush());
    }

    [Fact]
    public void Backspace_tab_and_other_controls()
    {
        var lines = AllLines(new VtScreen(), "ab\bc\r\n" + "a\tb\r\n" + "bell\a\0done\r\n");

        Assert.Equal(new[] { "ac", "a       b", "belldone" }, lines);
    }

    [Fact]
    public void Cursor_position_can_be_saved_and_restored()
    {
        var lines = AllLines(new VtScreen(), "abc" + Esc + "7" + "XYZ" + Esc + "8" + "-" + Esc + "[s" + "12" + Esc + "[u" + "+");

        Assert.Equal(new[] { "abc-+2" }, lines);
    }

    [Fact]
    public void A_screen_that_scrolls_keeps_every_line_in_order()
    {
        var screen = new VtScreen(columns: 20, rows: 3);
        var text = new StringBuilder();
        for (var i = 1; i <= 12; i++)
        {
            text.Append("line ").Append(i).Append("\r\n");
        }

        var lines = AllLines(screen, text.ToString());

        Assert.Equal(Enumerable.Range(1, 12).Select(i => "line " + i), lines);
    }

    [Fact]
    public void Clearing_the_screen_keeps_what_was_on_it_in_the_transcript()
    {
        var lines = AllLines(new VtScreen(), "old one\r\nold two" + Esc + "[2J" + Esc + "[H" + "new");

        Assert.Equal(new[] { "old one", "old two", "new" }, lines);
    }

    [Fact]
    public void A_line_is_handed_out_once()
    {
        var screen = new VtScreen();

        screen.Write("a\r\nb\r\nc");

        Assert.Equal(new[] { "a", "b" }, screen.TakeLines());
        Assert.Empty(screen.TakeLines());
        Assert.Equal(new[] { "c" }, screen.Flush());
        Assert.Empty(screen.Flush());
    }

    [Fact]
    public void Blank_lines_inside_the_output_stay_and_the_blank_rows_below_the_last_text_go_at_the_end()
    {
        var screen = new VtScreen();

        Assert.Equal(new[] { "a", string.Empty, "b" }, AllLines(screen, "a\r\n\r\nb\r\n"));

        // Rows drawn blank below the cursor's text (a repaint's padding) are not lines.
        var padded = new VtScreen();
        padded.Write("text" + Esc + "[3;1H   ");
        Assert.Equal(new[] { "text" }, padded.Flush());
    }

    [Fact]
    public void The_queries_a_terminal_would_answer_are_listed_and_never_answered()
    {
        var screen = new VtScreen();

        screen.Write(Esc + "[6n" + Esc + "[c" + Esc + "[>c" + "after");

        Assert.Equal(3, screen.Queries.Count);
        Assert.Equal("after", screen.CursorLine);
    }

    [Fact]
    public void Text_past_a_long_history_is_still_read_in_order()
    {
        // 6,000 lines through a small screen: the rows above the screen that were handed out are dropped, and nothing is lost or repeated.
        var screen = new VtScreen(columns: 40, rows: 4);
        var seen = new List<string>();
        for (var i = 0; i < 6_000; i++)
        {
            screen.Write($"row {i}\r\n");
            seen.AddRange(screen.TakeLines());
        }

        seen.AddRange(screen.Flush());

        Assert.Equal(6_000, seen.Count);
        Assert.Equal("row 0", seen[0]);
        Assert.Equal("row 5999", seen[^1]);
        Assert.Equal(seen, seen.Distinct().ToList());
    }
}
