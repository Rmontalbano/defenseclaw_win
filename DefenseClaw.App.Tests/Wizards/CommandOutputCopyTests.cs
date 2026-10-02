using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// A command's output can be selected and copied: the shared <see cref="DcCommandOutput"/> is a read-only, selectable box with a
/// Copy output button, and what Copy puts on the clipboard is exactly the text the box displays (never anything else).
/// </summary>
[Collection(UiCollection.Name)]
public class CommandOutputCopyTests
{
    [Fact]
    public void The_output_box_is_read_only_selectable_and_select_all_selects_the_whole_text()
    {
        UiThread.Run(() =>
        {
            var output = new DcCommandOutput { Text = "alpha\nbeta\ngamma" };
            using var host = new OffscreenHost(output, 500, 200);
            host.Relayout();

            var box = output.OutputBox;
            Assert.True(box.IsReadOnly);
            Assert.True(box.IsReadOnlyCaretVisible);
            Assert.True(box.Focusable);
            Assert.Equal("alpha\nbeta\ngamma", box.Text);

            box.SelectAll();
            Assert.Equal("alpha\nbeta\ngamma", box.SelectedText);
        });
    }

    [Fact]
    public void Copy_output_puts_exactly_the_displayed_text_on_the_clipboard()
    {
        UiThread.Run(() =>
        {
            string? copied = null;
            var output = new DcCommandOutput { Text = "ok  step one\n! warning on stderr\n", ClipboardWriter = t => copied = t };
            using var host = new OffscreenHost(output, 500, 200);
            host.Relayout();

            Assert.True(output.CopyOutputButton.IsEnabled);
            Assert.Equal("Copy output", System.Windows.Automation.AutomationProperties.GetName(output.CopyOutputButton));

            Assert.True(output.CopyOutput());
            Assert.Equal(output.DisplayedText, copied);
            Assert.Equal("ok  step one\n! warning on stderr\n", copied);
        });
    }

    [Fact]
    public void Copy_does_nothing_for_an_empty_box_and_follows_the_text_when_it_changes()
    {
        UiThread.Run(() =>
        {
            var copies = new List<string>();
            var output = new DcCommandOutput { ClipboardWriter = copies.Add };
            using var host = new OffscreenHost(output, 500, 200);
            host.Relayout();

            Assert.False(output.CopyButtonEnabled());
            Assert.False(output.CopyOutput());
            Assert.Empty(copies);

            output.Text = "first";
            output.Text = "first\nsecond";
            Assert.True(output.CopyOutput());
            Assert.Equal(new[] { "first\nsecond" }, copies);
        });
    }

    [Fact]
    public void Very_long_output_is_shown_from_its_tail_with_a_note_and_copy_copies_what_is_shown()
    {
        UiThread.Run(() =>
        {
            string? copied = null;
            var lines = Enumerable.Range(0, 3000).Select(i => "line " + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
            var text = string.Join("\n", lines);
            var output = new DcCommandOutput { MaxDisplayChars = 2000, ClipboardWriter = t => copied = t };
            using var host = new OffscreenHost(output, 500, 200);

            output.Text = text;
            host.Relayout();

            var shown = output.DisplayedText;
            Assert.True(shown.Length < text.Length);
            Assert.StartsWith("… ", shown, StringComparison.Ordinal);
            Assert.Contains("earlier characters are not shown", shown, StringComparison.Ordinal);
            Assert.EndsWith("line 02999", shown, StringComparison.Ordinal);

            // The first shown line after the note is whole, not cut mid-line.
            var firstLine = shown.Split(Environment.NewLine)[1];
            Assert.StartsWith("line ", firstLine, StringComparison.Ordinal);

            Assert.True(output.CopyOutput());
            Assert.Equal(shown, copied);
        });
    }

    [Fact]
    public void A_clipboard_that_refuses_is_reported_not_claimed_as_copied()
    {
        UiThread.Run(() =>
        {
            var output = new DcCommandOutput
            {
                Text = "something",
                ClipboardWriter = _ => throw new System.Runtime.InteropServices.COMException("locked"),
            };
            using var host = new OffscreenHost(output, 500, 200);
            host.Relayout();

            Assert.False(output.CopyOutput());
        });
    }

    [Fact]
    public void A_stderr_line_is_marked_in_the_text_a_box_displays()
    {
        Assert.Equal("fine", new CliOutputRow("fine", false).DisplayLine);
        Assert.Equal("! went wrong", new CliOutputRow("went wrong", true).DisplayLine);
    }

    [Fact]
    public void The_wizard_run_output_is_selectable_and_Copy_puts_the_displayed_text_on_the_clipboard()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var (viewModel, host) = UiThread.Run(() =>
        {
            var vm = new WizardViewModel(services, WizardSamples.Galileo());
            var window = new WizardWindow(vm);
            var content = (Grid)window.Content;
            window.Content = null;
            foreach (var bar in content.Children.OfType<Wpf.Ui.Controls.TitleBar>().ToArray())
            {
                content.Children.Remove(bar);
            }

            content.DataContext = vm;
            return (vm, new OffscreenHost(content, 900, 900));
        });

        try
        {
            UiThread.Run(() =>
            {
                string? copied = null;
                for (var i = 0; i < 12 && !viewModel.IsReview; i++)
                {
                    viewModel.Next();
                }

                host.Relayout();
                Assert.True(viewModel.IsReview, viewModel.ValidationSummary);
                var output = VisualTree.Find<DcCommandOutput>(host.Content)
                    ?? throw new InvalidOperationException("The wizard window has no command output control.");
                output.ClipboardWriter = t => copied = t;

                // What the runner printed, as the view-model accumulates it (stderr marked).
                viewModel.OutputText = "Galileo logging enabled\n! heads up: no project set\n";
                host.Relayout();

                Assert.Equal("Galileo logging enabled\n! heads up: no project set\n", output.OutputBox.Text);
                Assert.True(output.OutputBox.IsReadOnly);
                Assert.True(output.OutputBox.IsReadOnlyCaretVisible);

                output.OutputBox.SelectAll();
                Assert.Equal(output.OutputBox.Text, output.OutputBox.SelectedText);

                Assert.True(output.CopyOutputButton.IsEnabled);
                output.CopyOutputButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(output.OutputBox.Text, copied);
                Assert.True(output.IsVisible);
                RenderTo.Png(host, "wizard-output-selectable");
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                viewModel.Dispose();
                host.Dispose();
            });
        }
    }
}

internal static class DcCommandOutputTestExtensions
{
    public static bool CopyButtonEnabled(this DcCommandOutput output) => output.CopyOutputButton.IsEnabled;
}
