using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// A line that came from stderr is marked on screen, not only coloured (D4-9). The three consoles (the Activity transcript, the
/// Updates window's resolver console, the setup wizard's run output) each drew stderr in a tone colour and nothing else, so the
/// difference did not survive a colour-blind reader, a greyscale print or a screenshot. Each line now has a gutter that holds
/// an "!" for stderr and keeps its width otherwise, so the text stays in one column.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class StderrMarkerTests
{
    [Fact]
    public void The_activity_transcript_marks_stderr_lines_and_only_those()
    {
        using var scene = ActivityScene.Open(1400, 900);
        var invocation = InvocationFactory.Create(false, "doctor");
        InvocationFactory.Append(invocation, "first line");
        InvocationFactory.Append(invocation, "went wrong", CliStream.StandardError);
        InvocationFactory.Append(invocation, "last line");

        UiThread.Run(() =>
        {
            var row = scene.AddRow(invocation);
            var list = scene.ListFor(row);
            scene.Host.Relayout();

            var items = VisualTree.Descendants<ListBoxItem>(list).ToList();
            Assert.Equal(3, items.Count);
            foreach (var item in items)
            {
                var line = (ActivityOutputLine)item.DataContext;
                var texts = VisualTree.Descendants<TextBlock>(item).ToList();

                // The line's text is still the first text block in the row; the gutter follows it in the tree.
                Assert.Equal(line.DisplayText, texts[0].Text);
                var marker = Assert.Single(texts, t => t.Text == "!");
                Assert.Equal(line.IsError ? Visibility.Visible : Visibility.Hidden, marker.Visibility);
            }
        });
    }

    [Theory]
    [InlineData("Views/Panels/ActivityPanel.xaml", "OutputLine")]
    [InlineData("Views/Updates/UpdatesWindow.xaml", "ConsoleLine")]
    [InlineData("Views/Wizards/WizardWindow.xaml", "OutputLine")]
    public void Every_console_line_template_has_the_stderr_gutter_beside_the_line(string file, string lineStyle)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(Path.Combine(AppDirectory(), file.Replace('/', Path.DirectorySeparatorChar)));

        var lines = document.Descendants(presentation + "TextBlock")
            .Where(t => (string?)t.Attribute("Style") == "{StaticResource " + lineStyle + "}")
            .ToList();
        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            Assert.Contains(
                line.Parent!.Elements(presentation + "TextBlock"),
                sibling => (string?)sibling.Attribute("Style") == "{StaticResource OutputMarker}");
        }
    }

    private static string AppDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new FileNotFoundException("DefenseClaw.App\\MainWindow.xaml was not found above the test output directory.");
    }
}
