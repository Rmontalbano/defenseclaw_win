using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The per-run environment overlay (<see cref="CliRunOptions.EnvironmentOverlay"/>): a secret handed to a
/// child as an environment variable, never on argv. Uses cmd.exe as the child, like the rest of the runner
/// tests — the real defenseclaw.exe is never invoked. Values here are synthetic.
/// <para>
/// <b>How a test proves the child saw a value without printing it.</b> <c>cmd /c set NAME</c> prints
/// <c>NAME=value</c>, and the runner scrubs every overlay value out of captured output — so the child having
/// received exactly the secret is proved by the line reading <c>NAME=***REDACTED***</c> (a prefix or a
/// different value would leave text behind the mask). The plaintext is never asserted on and never printed.
/// </para>
/// </summary>
public class CliRunnerEnvironmentTests
{
    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static CliRunner Runner(string dataDirectory) =>
        new(new DefenseClawPaths(
            dataDirectory: dataDirectory,
            binDirectory: Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>()));

    private static IEnumerable<string> AllText(CliInvocation invocation) =>
        invocation.OutputLines.Select(l => l.Text)
            .Append(invocation.CommandLine)
            .Append(invocation.EnvironmentDisplay)
            .Append(string.Join('\n', invocation.Argv))
            .Append(invocation.FailureReason ?? string.Empty);

    [Fact]
    public async Task The_child_sees_the_overlay_value_and_only_the_output_is_masked()
    {
        const string name = "DCW_TEST_OVERLAY_SEEN";
        const string value = "synthetic-overlay-token-000111";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var options = CliRunOptions.Default.WithEnvironment(name, new SecretValue(value));

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "set", name }, options: options);

        Assert.Equal(0, invocation.ExitCode);

        // The child printed "NAME=<value>" and the runner masked the value: the whole remainder was the secret.
        Assert.Contains(invocation.OutputLines, l => l.Text == $"{name}={SecretValue.Redacted}");
        Assert.All(AllText(invocation), text => Assert.DoesNotContain(value, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_masked_line_is_also_what_streams_live()
    {
        const string name = "DCW_TEST_OVERLAY_STREAM";
        const string value = "synthetic-streamed-token-222333";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var streamed = new List<string>();
        runner.OutputReceived += (_, line) => streamed.Add(line.Text);

        await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "set", name },
            options: CliRunOptions.Default.WithEnvironment(name, new SecretValue(value)));

        Assert.Contains($"{name}={SecretValue.Redacted}", streamed);
        Assert.DoesNotContain(streamed, s => s.Contains(value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Argv_display_and_environment_names_never_carry_the_value()
    {
        const string name = "DCW_TEST_OVERLAY_NAMES";
        const string value = "synthetic-names-token-444555";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo", "hello" },
            options: CliRunOptions.Default.WithEnvironment(name, new SecretValue(value)));

        Assert.Equal(new[] { "/c", "echo", "hello" }, invocation.Argv);
        Assert.Equal(new[] { name }, invocation.EnvironmentNames);
        Assert.Equal($"env: {name}=•••", invocation.EnvironmentDisplay);
        Assert.All(AllText(invocation), text => Assert.DoesNotContain(value, text, StringComparison.Ordinal));

        // The recorded entry and the copy the UI reads carry the same names.
        Assert.Equal(new[] { name }, runner.Activity[0].EnvironmentNames);
        Assert.Equal(new[] { name }, invocation.Snapshot().EnvironmentNames);
        Assert.Equal(invocation.EnvironmentDisplay, invocation.Snapshot().EnvironmentDisplay);
    }

    [Fact]
    public async Task Several_variables_are_listed_by_name_in_a_stable_order()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var options = CliRunOptions.Default
            .WithEnvironment("DCW_TEST_ZULU", new SecretValue("zulu-value-777"))
            .WithEnvironment("DCW_TEST_ALPHA", new SecretValue("alpha-value-888"));

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" }, options: options);

        Assert.Equal(new[] { "DCW_TEST_ALPHA", "DCW_TEST_ZULU" }, invocation.EnvironmentNames);
        Assert.Equal("env: DCW_TEST_ALPHA=••• DCW_TEST_ZULU=•••", invocation.EnvironmentDisplay);
    }

    [Fact]
    public async Task A_run_without_an_overlay_reports_no_environment()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var invocation = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" });

        Assert.Empty(invocation.EnvironmentNames);
        Assert.Equal(string.Empty, invocation.EnvironmentDisplay);
    }

    [Fact]
    public async Task The_overlay_does_not_leak_into_the_app_or_into_later_runs()
    {
        const string name = "DCW_TEST_OVERLAY_LEAK";
        const string value = "synthetic-leak-token-666999";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var first = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "set", name },
            options: CliRunOptions.Default.WithEnvironment(name, new SecretValue(value)));
        Assert.Contains(first.OutputLines, l => l.Text == $"{name}={SecretValue.Redacted}");

        // The app's own environment was never touched...
        Assert.Null(Environment.GetEnvironmentVariable(name));

        // ...so a later run with no overlay, and one with a different options object, does not see it.
        var second = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "set", name });
        var third = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "set", name }, options: CliRunOptions.LongRunning);

        foreach (var later in new[] { second, third })
        {
            Assert.Empty(later.EnvironmentNames);
            Assert.DoesNotContain(later.OutputLines, l => l.Text.StartsWith(name + "=", StringComparison.Ordinal));
            Assert.NotEqual(0, later.ExitCode); // `set NAME` exits 1 when NAME is not defined
        }
    }

    [Fact]
    public async Task The_overlay_replaces_the_apps_value_for_that_child_only()
    {
        const string name = "DCW_TEST_OVERLAY_SHADOW";
        const string value = "synthetic-shadow-token-121212";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        Environment.SetEnvironmentVariable(name, "value-from-the-app");
        try
        {
            var overlaid = await runner.RunExecutableAsync(
                CmdPath,
                new[] { "/c", "set", name },
                options: CliRunOptions.Default.WithEnvironment(name, new SecretValue(value)));
            var plain = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "set", name });

            Assert.Contains(overlaid.OutputLines, l => l.Text == $"{name}={SecretValue.Redacted}");
            Assert.Contains(plain.OutputLines, l => l.Text == $"{name}=value-from-the-app");
            Assert.Equal("value-from-the-app", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task An_empty_value_is_skipped_so_it_cannot_shadow_a_stored_one()
    {
        // DefenseClaw's .env loader never overwrites a variable that is already present, and an empty one
        // is present. Setting the empty string would therefore hide the value stored in ~/.defenseclaw/.env.
        const string name = "DCW_TEST_OVERLAY_EMPTY";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        Environment.SetEnvironmentVariable(name, "stored-elsewhere");
        try
        {
            var invocation = await runner.RunExecutableAsync(
                CmdPath,
                new[] { "/c", "set", name },
                options: CliRunOptions.Default.WithEnvironment(name, new SecretValue(string.Empty)));

            Assert.Empty(invocation.EnvironmentNames);
            Assert.Equal(string.Empty, invocation.EnvironmentDisplay);
            Assert.Contains(invocation.OutputLines, l => l.Text == $"{name}=stored-elsewhere");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task An_overlay_value_in_argv_is_refused_and_nothing_is_recorded_or_started()
    {
        const string name = "DCW_TEST_OVERLAY_ARGV";
        const string value = "synthetic-argv-token-343434";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var options = CliRunOptions.Default.WithEnvironment(name, new SecretValue(value));

        var exact = await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", value }, options: options));
        var embedded = await Assert.ThrowsAsync<SecretInArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "--token=" + value }, options: options));

        Assert.Equal(2, exact.ArgumentIndex);
        Assert.Equal(2, embedded.ArgumentIndex);
        Assert.DoesNotContain(value, exact.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, embedded.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Activity);
    }

    [Fact]
    public async Task An_overlay_value_that_the_child_echoes_is_masked_wherever_it_appears_in_a_line()
    {
        const string name = "DCW_TEST_OVERLAY_ECHO";
        const string value = "synthetic-echo-token-565656";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        // The child reports "invalid key: <value> (rejected)" — the shape a CLI's own error takes.
        var invocation = await runner.RunExecutableAsync(
            CmdPath,
            new[] { "/c", "echo", "invalid", "key:", "%" + name + "%", "(rejected)" },
            options: CliRunOptions.Default.WithEnvironment(name, new SecretValue(value)));

        Assert.Contains(invocation.OutputLines, l => l.Text.Contains(SecretValue.Redacted, StringComparison.Ordinal));
        Assert.All(AllText(invocation), text => Assert.DoesNotContain(value, text, StringComparison.Ordinal));
    }

    [Fact]
    public void The_options_never_print_a_value_even_redacted_or_through_ToString()
    {
        const string value = "synthetic-tostring-token-787878";
        var options = CliRunOptions.Default.WithEnvironment("DCW_TEST_OVERLAY_PRINT", new SecretValue(value));

        var text = options.ToString();

        Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        Assert.Contains("DCW_TEST_OVERLAY_PRINT", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithEnvironment_leaves_the_original_options_alone_and_is_case_insensitive()
    {
        var original = CliRunOptions.LongRunning;
        var first = original.WithEnvironment("DCW_TEST_CASE", new SecretValue("one-value-1"));
        var second = first.WithEnvironment("dcw_test_case", new SecretValue("two-value-2"));

        Assert.Empty(original.EnvironmentOverlay);
        Assert.Single(first.EnvironmentOverlay);
        Assert.Single(second.EnvironmentOverlay);
        Assert.Equal("two-value-2", second.EnvironmentOverlay["DCW_TEST_CASE"].Reveal());
        Assert.Equal(CliRunner.LongRunningTimeout, second.Timeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HAS SPACE")]
    [InlineData("HAS=EQUALS")]
    [InlineData("1LEADING_DIGIT")]
    [InlineData("DASH-ED")]
    [InlineData("NUL\0INSIDE")]
    public void An_invalid_name_is_rejected_when_the_overlay_is_built(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CliRunOptions.Default.WithEnvironment(name, new SecretValue("synthetic-value-9")));

        Assert.DoesNotContain("synthetic-value-9", ex.Message, StringComparison.Ordinal);
        Assert.False(CliRunOptions.IsValidEnvironmentName(name));
    }

    [Fact]
    public async Task The_runner_re_validates_an_overlay_built_by_hand_before_recording_anything()
    {
        const string value = "synthetic-handmade-token-90909";
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);

        var badName = new CliRunOptions
        {
            EnvironmentOverlay = new Dictionary<string, SecretValue> { ["NOT=VALID"] = new SecretValue(value) },
        };
        var duplicate = new CliRunOptions
        {
            EnvironmentOverlay = new Dictionary<string, SecretValue>(StringComparer.Ordinal)
            {
                ["DCW_TEST_DUP"] = new SecretValue(value),
                ["dcw_test_dup"] = new SecretValue(value + "-2"),
            },
        };

        var first = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" }, options: badName));
        var second = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunExecutableAsync(CmdPath, new[] { "/c", "exit", "0" }, options: duplicate));

        Assert.DoesNotContain(value, first.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, second.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Activity);
    }

    [Theory]
    [InlineData("SPLUNK_ACCESS_TOKEN")]
    [InlineData("DEFENSECLAW_SPLUNK_HEC_TOKEN")]
    [InlineData("DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN")]
    [InlineData("_UNDERSCORE_FIRST")]
    [InlineData("lower_case_9")]
    public void The_names_the_wizards_use_are_valid(string name) =>
        Assert.True(CliRunOptions.IsValidEnvironmentName(name));
}
