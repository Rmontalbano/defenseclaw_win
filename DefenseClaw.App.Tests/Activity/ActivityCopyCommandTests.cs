using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// The Copy argv button of an Activity card. The card shows a readable command line (whitespace quoted, nothing
/// else); what lands on the clipboard has to be safe to paste into PowerShell, because the arguments include
/// names that came from outside.
/// </summary>
public sealed class ActivityCopyCommandTests
{
    private static (ActivityRow Row, List<string> Copied, List<string> Notices) RowFor(params string[] argv)
    {
        var copied = new List<string>();
        var notices = new List<string>();
        var row = new ActivityRow(InvocationFactory.Create(false, argv), runner: null, notify: notices.Add)
        {
            ClipboardWriter = copied.Add,
        };
        return (row, copied, notices);
    }

    [Fact]
    public void Copy_argv_puts_the_powershell_safe_command_line_on_the_clipboard()
    {
        var (row, copied, notices) = RowFor("skill", "quarantine", "--", "x&calc");

        row.CopyCommand.Execute(null);

        Assert.Equal(@"& 'C:\test\defenseclaw.exe' skill quarantine -- 'x&calc'", Assert.Single(copied));
        Assert.Equal("Copied the command line for PowerShell.", Assert.Single(notices));
    }

    [Theory]
    [InlineData("x;calc", "'x;calc'")]
    [InlineData("x$(calc)", "'x$(calc)'")]
    [InlineData("a $(calc) b", "'a $(calc) b'")]
    [InlineData("x`calc", "'x`calc'")]
    [InlineData("x'y", "'x''y'")]
    public void Every_hostile_name_pastes_as_one_literal_argument(string name, string quoted)
    {
        var (row, copied, _) = RowFor("skill", "quarantine", "--", name);

        row.CopyCommand.Execute(null);

        Assert.Equal(@"& 'C:\test\defenseclaw.exe' skill quarantine -- " + quoted, Assert.Single(copied));
    }

    [Fact]
    public void The_card_still_shows_the_readable_command_line()
    {
        var (row, _, _) = RowFor("skill", "quarantine", "--", "my skill&calc");

        Assert.Equal("C:\\test\\defenseclaw.exe skill quarantine -- \"my skill&calc\"", row.CommandLine);
    }
}
