using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Setup;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="CliRunOptions.OutputLineFilter"/> (CUST-270): a line the child prints is filtered before it is stored in the invocation or streamed,
/// so what a command prints that is secret in a way no value is known for - <c>setup webhook test</c> prints the webhook's address whole - never
/// reaches Activity. The child is <c>cmd.exe</c>, like the rest of the runner tests; every credential is a made-up value that starts with
/// <c>synth</c>.
/// </summary>
public class CliRunnerOutputFilterTests
{
    private const string Printed = "Testing webhook example-slack [slack] -> https://synthuser:synthpass@hooks.example.test/services/synthtoken0/synthpath-secret?api_key=synthkey";

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static CliRunner Runner(string dataDirectory) =>
        new(new DefenseClawPaths(
            dataDirectory: dataDirectory,
            binDirectory: Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>()));

    private static string[] Secrets => new[] { "synthuser", "synthpass", "synthtoken", "synthpath-secret", "synthkey", "api_key" };

    [Fact]
    public async Task The_endpoint_filter_cuts_the_address_a_child_prints_to_its_host_in_the_stored_lines_and_the_streamed_ones()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var streamed = new List<string>();
        runner.OutputReceived += (_, line) => streamed.Add(line.Text);

        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo", Printed },
            options: new CliRunOptions { OutputLineFilter = EndpointHost.ScrubLine });

        Assert.Equal(0, invocation.ExitCode);
        var line = Assert.Single(invocation.OutputLines, l => l.Text.Contains("Testing webhook", StringComparison.Ordinal));
        Assert.Contains("https://hooks.example.test", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("services", line.Text, StringComparison.Ordinal);

        // Nothing the child printed is left anywhere the runner keeps text: the lines, the stream, the recorded copy.
        var everything = invocation.OutputLines.Select(l => l.Text).Concat(streamed).Concat(runner.Activity.SelectMany(a => a.OutputLines.Select(l => l.Text)));
        Assert.All(everything, text => Assert.All(Secrets, secret => Assert.DoesNotContain(secret, text, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Both_streams_are_filtered()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // One argument, so cmd reads it as two commands; the second echo is redirected to stderr (1>&2).
        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo out https://synthuser:synthpass@one.example.test/synthpath-secret & echo err https://two.example.test/synthtoken0?api_key=synthkey 1>&2" },
            options: new CliRunOptions { OutputLineFilter = EndpointHost.ScrubLine });

        Assert.Contains(invocation.OutputLines, l => l.Stream == CliStream.StandardOutput && l.Text.Contains("https://one.example.test", StringComparison.Ordinal));
        Assert.Contains(invocation.OutputLines, l => l.Stream == CliStream.StandardError && l.Text.Contains("https://two.example.test", StringComparison.Ordinal));
        Assert.All(invocation.OutputLines, l => Assert.All(Secrets, secret => Assert.DoesNotContain(secret, l.Text, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_a_filter_a_line_is_stored_as_the_child_printed_it()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "plain https://one.example.test/path" });

        Assert.Contains(invocation.OutputLines, l => l.Text.Contains("https://one.example.test/path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_filter_that_throws_costs_the_line_and_never_the_run_or_the_original_text()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo", Printed },
            options: new CliRunOptions { OutputLineFilter = static _ => throw new InvalidOperationException("synthpath-secret in the message too") });

        Assert.Equal(0, invocation.ExitCode);
        Assert.NotEmpty(invocation.OutputLines);
        Assert.All(invocation.OutputLines, l => Assert.Equal(CliRunner.FilterFailedNotice, l.Text));
    }

    [Fact]
    public async Task A_filter_that_returns_nothing_leaves_an_empty_line_not_the_original()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo", Printed },
            options: new CliRunOptions { OutputLineFilter = static _ => null! });

        Assert.All(invocation.OutputLines, l => Assert.Equal(string.Empty, l.Text));
    }

    [Fact]
    public void The_filter_is_part_of_the_options_and_survives_the_presets()
    {
        Func<string, string> filter = EndpointHost.ScrubLine;

        Assert.Same(filter, (CliRunOptions.JsonRead with { OutputLineFilter = filter }).OutputLineFilter);
        Assert.Same(filter, (CliRunOptions.WithTimeout(TimeSpan.FromSeconds(5)) with { OutputLineFilter = filter }).OutputLineFilter);
        Assert.Null(CliRunOptions.Default.OutputLineFilter);
        Assert.Null(CliRunOptions.JsonRead.OutputLineFilter);
    }
}
