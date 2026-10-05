using System.Runtime.InteropServices;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;

namespace DefenseClaw.App.Tests.Controls;

/// <summary>
/// Every copy goes through <see cref="DcClipboard"/>: it retries briefly while another program holds the clipboard open
/// (CLIPBRD_E_CANT_OPEN), reports a failure instead of swallowing it, and cuts a huge payload with a note. The seams stand in for
/// the real clipboard, so no test touches it.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DcClipboardTests : IDisposable
{
    private const int CantOpen = unchecked((int)0x800401D0);

    private readonly List<string> _notices = [];

    public DcClipboardTests()
    {
        DcClipboard.Notice += _notices.Add;
        DcClipboard.Sleeper = _ => { };
    }

    public void Dispose()
    {
        DcClipboard.Notice -= _notices.Add;
        DcClipboard.Writer = null;
        DcClipboard.Sleeper = null;
    }

    private static COMException Busy() => new("Clipboard busy", CantOpen);

    [Fact]
    public void A_copy_that_works_writes_the_text_and_says_nothing()
    {
        string? written = null;
        DcClipboard.Writer = t => written = t;

        Assert.True(DcClipboard.TrySetText("hello"));

        Assert.Equal("hello", written);
        Assert.Empty(_notices);
    }

    [Fact]
    public void A_busy_clipboard_is_retried_and_the_copy_then_succeeds()
    {
        var attempts = 0;
        string? written = null;
        DcClipboard.Writer = t =>
        {
            if (++attempts < 3)
            {
                throw Busy();
            }

            written = t;
        };

        Assert.Equal(ClipboardResult.Copied, DcClipboard.TryCopy("late"));

        Assert.Equal(3, attempts);
        Assert.Equal("late", written);
        Assert.Empty(_notices);
    }

    [Fact]
    public void A_clipboard_held_open_for_good_fails_after_the_retry_window_and_says_so()
    {
        var attempts = 0;
        DcClipboard.Writer = _ =>
        {
            attempts++;
            throw Busy();
        };
        // Each wait really takes its time here, so the half-second window ends the retries.
        DcClipboard.Sleeper = Thread.Sleep;

        Assert.False(DcClipboard.TrySetText("x"));

        Assert.True(attempts > 1, "a busy clipboard is tried again");
        Assert.Equal([DcClipboard.FailureText], _notices);
    }

    [Fact]
    public void A_refusal_that_is_not_a_busy_clipboard_is_not_retried()
    {
        var attempts = 0;
        DcClipboard.Writer = _ =>
        {
            attempts++;
            throw new COMException("Other failure", unchecked((int)0x80004005));
        };

        Assert.Equal(ClipboardResult.Failed, DcClipboard.TryCopy("x"));

        Assert.Equal(1, attempts);
        Assert.Single(_notices);
    }

    [Fact]
    public void A_caller_with_its_own_status_line_can_keep_the_toast_quiet()
    {
        DcClipboard.Writer = _ => throw new COMException("Other failure", unchecked((int)0x80004005));

        var result = DcClipboard.TryCopy("x", report: false);

        Assert.Equal(ClipboardResult.Failed, result);
        Assert.Empty(_notices);
    }

    [Fact]
    public void A_huge_payload_is_cut_with_a_note_and_reported()
    {
        string? written = null;
        DcClipboard.Writer = t => written = t;
        var text = new string('a', DcClipboard.MaxChars + 1234);

        Assert.Equal(ClipboardResult.Truncated, DcClipboard.TryCopy(text));

        Assert.NotNull(written);
        Assert.StartsWith(new string('a', DcClipboard.MaxChars), written, StringComparison.Ordinal);
        Assert.Contains("1,234", written, StringComparison.Ordinal);
        Assert.Contains("Truncated", written, StringComparison.Ordinal);
        Assert.Equal([DcClipboard.TruncatedText], _notices);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var text = new string('a', DcClipboard.MaxChars - 1) + "\U0001F600" + "tail";

        var cut = DcClipboard.Truncate(text);

        var head = cut[..cut.IndexOf(Environment.NewLine, StringComparison.Ordinal)];
        Assert.Equal(DcClipboard.MaxChars - 1, head.Length);
        Assert.False(char.IsHighSurrogate(head[^1]));
    }

    [Fact]
    public void Text_at_the_limit_is_copied_whole()
    {
        string? written = null;
        DcClipboard.Writer = t => written = t;
        var text = new string('b', DcClipboard.MaxChars);

        Assert.Equal(ClipboardResult.Copied, DcClipboard.TryCopy(text));

        Assert.Same(text, written);
    }

    [Fact]
    public void The_settings_copy_hook_reports_a_failure_instead_of_throwing()
    {
        DcClipboard.Writer = _ => throw new COMException("Other failure", unchecked((int)0x80004005));

        SettingsPlatform.Real.CopyText("path");

        Assert.Equal([DcClipboard.FailureText], _notices);
    }

    [Fact]
    public void The_command_output_box_says_the_clipboard_failed_through_the_shared_helper()
    {
        UiThread.Run(() =>
        {
            DcClipboard.Writer = _ => throw new COMException("Other failure", unchecked((int)0x80004005));
            var output = new DcCommandOutput { Text = "some output" };
            using var host = new OffscreenHost(output, 400, 200);
            host.Relayout();

            Assert.False(output.CopyOutput());
        });

        Assert.Empty(_notices);
    }
}
