using DefenseClaw.Core.Runtime;

namespace DefenseClaw.Tests;

/// <summary>
/// The per-user launcher's own failure lines (DefenseClaw 1.0.7 and later) read from a probe's output, and what the detector says when
/// <c>--version-json</c> hits one. Every path is invented.
/// </summary>
public sealed class LauncherFailureTests
{
    private const string Folder = @"C:\Users\example\.defenseclaw";

    private const string GenericReason = "The runtime did not report a version (defenseclaw --version-json gave no usable answer).";

    [Theory]
    [InlineData(@"defenseclaw: read per-user launcher state: open C:\Users\example\.local\bin\defenseclaw-hook-state.json: Access is denied.", LauncherFailureKind.StateUnreadable)]
    [InlineData("defenseclaw: per-user launcher state cannot be read; rerun the Windows installer", LauncherFailureKind.StateUnreadable)]
    [InlineData("defenseclaw: read per-user launcher state: invalid character '}' looking for beginning of value", LauncherFailureKind.StateInvalid)]
    [InlineData("defenseclaw: read per-user launcher state: unexpected end of JSON input", LauncherFailureKind.StateInvalid)]
    [InlineData("defenseclaw: per-user launcher state is invalid; rerun the Windows installer", LauncherFailureKind.StateInvalid)]
    [InlineData(@"defenseclaw: inspect data folder ""C:\Users\example\.defenseclaw"": Access is denied.", LauncherFailureKind.InspectFailed)]
    [InlineData(@"defenseclaw: data folder ""C:\Users\example\.defenseclaw"" is a reparse point; restore the original non-link folder at that path and retry", LauncherFailureKind.DataFolderLink)]
    [InlineData(@"defenseclaw: start per-user CLI: fork/exec C:\Users\example\.defenseclaw\.venv\Scripts\defenseclaw.exe: The system cannot find the file specified.", LauncherFailureKind.StartFailed)]
    public void Each_launcher_line_is_recognised_with_its_kind_and_the_line_as_printed(string line, LauncherFailureKind kind)
    {
        var plain = LauncherFailure.Recognise(line);
        var crlf = LauncherFailure.Recognise(line + "\r\n");

        Assert.NotNull(plain);
        Assert.Equal(kind, plain.Kind);
        Assert.Equal(line, plain.Line);
        Assert.Equal(plain, crlf);
    }

    [Fact]
    public void A_launcher_line_among_other_output_is_found_and_the_link_names_its_folder()
    {
        var text = "Traceback (most recent call last):\r\n  File \"x.py\", line 1, in <module>\r\nOSError: boom\r\n" +
                   $"defenseclaw: data folder \"{Folder}\" is a reparse point; restore the original non-link folder at that path and retry\r\n";

        var failure = LauncherFailure.Recognise(text);

        Assert.NotNull(failure);
        Assert.Equal(LauncherFailureKind.DataFolderLink, failure.Kind);
        Assert.Equal(Folder, failure.Path);
        Assert.StartsWith("defenseclaw: data folder", failure.Line, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', failure.Line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):\n  File \"cli.py\", line 9, in main\nModuleNotFoundError: No module named 'defenseclaw'\n")]
    [InlineData("Error: No such option: --version-json\n")]
    [InlineData("Usage: defenseclaw [OPTIONS] COMMAND [ARGS]...\nTry 'defenseclaw --help' for help.\n\nError: No such command 'x'.\n")]
    [InlineData("defenseclaw: error: unrecognized arguments: --version-json\n")]
    [InlineData("run defenseclaw: read per-user launcher state: nope\n")]
    [InlineData("{\"name\":\"defenseclaw-cli\",\"version\":\"1.0.6\",\"schema_version\":1}")]
    public void Python_tracebacks_normal_cli_errors_and_older_runtime_output_are_not_launcher_failures(string? text)
    {
        Assert.Null(LauncherFailure.Recognise(text));
    }

    [Theory]
    [InlineData("defenseclaw: per-user launcher state cannot be read; rerun the Windows installer")]
    [InlineData(@"defenseclaw: start per-user CLI: fork/exec C:\x\defenseclaw.exe: not found")]
    public void State_and_start_problems_say_to_run_the_installer_again(string line)
    {
        var reason = LauncherFailure.Recognise(line)!.UserReason();

        Assert.Contains($"\"{line}\"", reason, StringComparison.Ordinal);
        Assert.EndsWith("Run the DefenseClaw installer (install.ps1) again to repair the per-user install.", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_linked_data_folder_says_to_restore_that_folder_then_run_the_installer()
    {
        var line = $"defenseclaw: data folder \"{Folder}\" is a reparse point; restore the original non-link folder at that path and retry";

        var reason = LauncherFailure.Recognise(line)!.UserReason();

        Assert.Contains($"\"{line}\"", reason, StringComparison.Ordinal);
        Assert.EndsWith($"Restore {Folder} as a regular folder (not a junction or link), then run the DefenseClaw installer again.", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nonzero_exit_keeps_the_launcher_line_and_any_other_nonzero_exit_stays_exactly_as_before()
    {
        var launcher = RuntimeProbeOutput.FailedExit(1, "defenseclaw: per-user launcher state is invalid; rerun the Windows installer\r\n");
        var other = RuntimeProbeOutput.FailedExit(2, "Usage: defenseclaw [OPTIONS]\n\nError: No such command 'x'.\n");

        Assert.Null(launcher.Text);
        Assert.Equal("exit 1: defenseclaw: per-user launcher state is invalid; rerun the Windows installer", launcher.Error);
        Assert.Equal(LauncherFailureKind.StateInvalid, launcher.Launcher!.Kind);
        Assert.Equal(RuntimeProbeOutput.Fail("exit 2"), other);
    }

    private static RuntimeDetector Detector(RuntimeProbeOutput versionAnswer)
    {
        Task<RuntimeProbeOutput> Run(IReadOnlyList<string> arguments, CancellationToken token) =>
            Task.FromResult(arguments.Count == 1 && arguments[0] == "--version-json" ? versionAnswer : RuntimeProbeOutput.Fail("exit 1"));

        return new RuntimeDetector(Run, () => "fp", "defenseclaw.exe");
    }

    [Fact]
    public async Task Version_json_stopped_by_the_launcher_gives_an_unknown_reason_that_names_the_line_and_the_fix()
    {
        var line = $"defenseclaw: data folder \"{Folder}\" is a reparse point; restore the original non-link folder at that path and retry";
        var detector = Detector(RuntimeProbeOutput.FailedExit(1, line + "\r\n"));

        var snapshot = await detector.RefreshAsync();

        Assert.Null(snapshot.Identity);
        Assert.False(snapshot.IsKnown);
        Assert.Contains($"\"{line}\"", snapshot.UnknownReason, StringComparison.Ordinal);
        Assert.Contains($"Restore {Folder} as a regular folder", snapshot.UnknownReason, StringComparison.Ordinal);
        Assert.Equal(snapshot.UnknownReason, RuntimeSummary.Identity(snapshot));
    }

    [Fact]
    public async Task Exit_1_without_a_launcher_line_keeps_the_generic_unknown_reason_of_older_runtimes()
    {
        var detector = Detector(RuntimeProbeOutput.FailedExit(1, "Error: No such option: --version-json\n"));

        var snapshot = await detector.RefreshAsync();

        Assert.Equal(GenericReason, snapshot.UnknownReason);
    }

    [Fact]
    public async Task A_runner_that_reports_a_plain_failure_keeps_the_generic_unknown_reason()
    {
        var snapshot = await Detector(RuntimeProbeOutput.Fail("exit 1")).RefreshAsync();

        Assert.Equal(GenericReason, snapshot.UnknownReason);
    }

    [Fact]
    public void A_very_long_launcher_line_is_clipped_for_display_with_an_ellipsis()
    {
        var line = "defenseclaw: start per-user CLI: " + new string('x', 5000);

        var failure = LauncherFailure.Recognise(line);

        Assert.NotNull(failure);
        Assert.Equal(LauncherFailure.MaxDisplayLength, failure.Line.Length);
        Assert.EndsWith("\u2026", failure.Line, StringComparison.Ordinal);
        Assert.True(failure.UserReason().Length < LauncherFailure.MaxDisplayLength + 200);
    }
}
