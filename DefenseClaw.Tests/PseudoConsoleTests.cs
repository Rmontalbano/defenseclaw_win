using System.Runtime.InteropServices;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.Tests;

/// <summary>
/// The parts of the pseudo-console host that need no process: the command line it builds (read back by Windows' own parser), the environment
/// block, and whether this Windows has the API at all.
/// </summary>
public sealed class PseudoConsoleTests
{
    [Fact]
    public void A_pseudo_console_is_supported_exactly_where_windows_10_1809_or_later_runs()
    {
        Assert.Equal(OperatingSystem.IsWindowsVersionAtLeast(10, 0, PseudoConsole.MinimumBuild), PseudoConsole.IsSupported);
        Assert.Equal(17763, PseudoConsole.MinimumBuild);
    }

    [Fact]
    public void The_program_is_always_quoted_and_plain_words_are_not()
    {
        Assert.Equal(
            "\"C:\\Program Files\\DefenseClaw\\defenseclaw.exe\" keys set EXAMPLE_KEY",
            PseudoConsole.CommandLine(@"C:\Program Files\DefenseClaw\defenseclaw.exe", new[] { "keys", "set", "EXAMPLE_KEY" }));
        Assert.Equal("\"C:\\x.exe\"", PseudoConsole.CommandLine(@"C:\x.exe", Array.Empty<string>()));
    }

    public static TheoryData<string[]> Tricky => new()
    {
        new[] { "keys", "set", "EXAMPLE_KEY" },
        new[] { "with space", "plain" },
        new[] { "" },
        new[] { "quote\"inside" },
        new[] { "\"leading and trailing\"" },
        new[] { @"C:\dir with space\" },
        new[] { @"C:\dir\" },
        new[] { @"back\\slashes\\" },
        new[] { "a\\\"b" },
        new[] { "tab\tseparated", "new\nline" },
        new[] { "-I", "-c", "import getpass; v = getpass.getpass('Value: '); print(len(v))" },
        new[] { "semi;colon", "amp&ersand", "pipe|bar", "percent%PATH%", "caret^", "dollar$HOME" },
    };

    [Theory]
    [MemberData(nameof(Tricky))]
    public void What_is_built_is_read_back_by_windows_as_the_same_arguments(string[] arguments)
    {
        var commandLine = PseudoConsole.CommandLine(@"C:\Tools\prog.exe", arguments);

        var parsed = ParseLikeWindows(commandLine);

        Assert.Equal(new[] { @"C:\Tools\prog.exe" }.Concat(arguments), parsed);
    }

    [Fact]
    public void The_environment_block_is_sorted_nul_separated_and_ends_with_two_nuls()
    {
        var block = PseudoConsole.EnvironmentBlock(new Dictionary<string, string?>
        {
            ["b_name"] = "2",
            ["A_NAME"] = "1",
            ["removed"] = null,
            ["Z"] = "last",
        });

        Assert.Equal("A_NAME=1\0b_name=2\0Z=last\0\0", block);
    }

    [Fact]
    public void An_empty_environment_is_still_a_valid_block()
    {
        Assert.Equal("\0\0", PseudoConsole.EnvironmentBlock(new Dictionary<string, string?>()));
    }

    private static string[] ParseLikeWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            var arguments = new string[count];
            for (var i = 0; i < count; i++)
            {
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return arguments;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
