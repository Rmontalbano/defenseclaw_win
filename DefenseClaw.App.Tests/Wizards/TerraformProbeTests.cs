using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The read-only look at Terraform (CUST-317): which state each answer of <c>terraform version -json</c> is, that it asks that one question
/// and nothing else, and that a look which could not run never closes the feature. Every dependency is a delegate fed canned answers, so
/// nothing here can find, start or run a real Terraform.
/// </summary>
public sealed class TerraformProbeTests
{
    private const string Exe = @"C:\synthetic\bin\terraform.exe";

    private static TerraformProcessResult Json(string version) =>
        new(0, $$"""{"terraform_version":"{{version}}","platform":"windows_amd64","provider_selections":{},"terraform_outdated":false}""" + "\n", string.Empty, TimedOut: false);

    private sealed class Fake
    {
        public TerraformLocation Location { get; set; } = new(Exe, "terraform", FromVariable: false);

        public Func<TerraformProcessResult> Answer { get; set; } = () => Json("1.9.5");

        public Exception? LocateFails { get; set; }

        public Exception? RunFails { get; set; }

        public List<(string Executable, string[] Arguments, TimeSpan Timeout)> Runs { get; } = new();

        public TerraformProbe Probe => new(
            () => LocateFails is null ? Task.FromResult(Location) : Task.FromException<TerraformLocation>(LocateFails),
            (executable, arguments, timeout, _) =>
            {
                Runs.Add((executable, arguments.ToArray(), timeout));
                return RunFails is null ? Task.FromResult(Answer()) : Task.FromException<TerraformProcessResult>(RunFails);
            });
    }

    // ------------------------------------------------------------------ the one question it asks

    [Fact]
    public async Task It_asks_one_read_only_question_of_the_terraform_it_found_and_nothing_else()
    {
        var fake = new Fake();

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        var run = Assert.Single(fake.Runs);
        Assert.Equal(Exe, run.Executable);
        Assert.Equal(new[] { "version", "-json" }, run.Arguments);
        Assert.Equal(TerraformProbe.VersionTimeout, run.Timeout);
        Assert.Equal(TerraformState.Ready, status.State);
    }

    [Fact]
    public async Task Nothing_is_run_when_there_is_no_terraform_to_run()
    {
        var fake = new Fake { Location = new TerraformLocation(null, "terraform", FromVariable: false) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Empty(fake.Runs);
        Assert.Equal(TerraformState.NotInstalled, status.State);
    }

    // ------------------------------------------------------------------ the states

    [Theory]
    [InlineData("1.5.0")]
    [InlineData("1.5.7")]
    [InlineData("1.9.5")]
    [InlineData("1.10.0")]
    [InlineData("2.0.0")]
    [InlineData("1.6.0-beta1")]
    [InlineData("1.5.0-rc1")]
    [InlineData("v1.7.2")]
    public async Task A_version_the_module_accepts_is_ready_and_says_which(string version)
    {
        var fake = new Fake { Answer = () => Json(version) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.Ready, status.State);
        Assert.True(status.AllowsDashboards);
        Assert.Equal(version, status.Version);
        Assert.Equal(Exe, status.Path);
        Assert.Equal($"Terraform {version} is available.", status.Summary);
    }

    [Theory]
    [InlineData("1.4.7")]
    [InlineData("1.2.0")]
    [InlineData("1.0.11")]
    [InlineData("0.15.5")]
    [InlineData("1.4")]
    public async Task A_terraform_older_than_the_modules_required_version_is_a_definite_no_that_names_both_versions(string version)
    {
        var fake = new Fake { Answer = () => Json(version) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.TooOld, status.State);
        Assert.False(status.AllowsDashboards);
        Assert.Contains(version, status.Summary, StringComparison.Ordinal);
        Assert.Contains("1.5.0", status.Summary, StringComparison.Ordinal);
        Assert.Contains(Exe, status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_minimum_is_the_required_version_of_the_module_the_cli_ships()
    {
        // _data/splunk_o11y_dashboards/terraform/main.tf: required_version = ">= 1.5.0" (0.8.10; the same at the pinned source).
        Assert.Equal(new Version(1, 5, 0), TerraformProbe.MinimumVersion);
    }

    [Fact]
    public async Task No_executable_on_path_is_not_installed_and_says_where_it_looked()
    {
        var fake = new Fake { Location = new TerraformLocation(null, "terraform", FromVariable: false) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.NotInstalled, status.State);
        Assert.False(status.AllowsDashboards);
        Assert.Equal("Terraform was not found on this machine's PATH.", status.Summary);
    }

    [Fact]
    public async Task A_terraform_bin_that_names_nothing_is_not_installed_and_says_it_was_the_variable()
    {
        var fake = new Fake { Location = new TerraformLocation(null, @"D:\gone\terraform.exe", FromVariable: true) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.NotInstalled, status.State);
        Assert.Contains("TERRAFORM_BIN", status.Summary, StringComparison.Ordinal);
        Assert.Contains(@"D:\gone\terraform.exe", status.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("PATH", status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_terraform_whose_version_command_fails_is_not_working_and_says_why()
    {
        var fake = new Fake { Answer = () => new TerraformProcessResult(1, string.Empty, "flag provided but not defined: -json\nUsage: terraform version\n", TimedOut: false) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.NotWorking, status.State);
        Assert.False(status.AllowsDashboards);
        Assert.Contains("exit code 1", status.Summary, StringComparison.Ordinal);
        Assert.Contains("flag provided but not defined: -json", status.Summary, StringComparison.Ordinal);
        Assert.Contains("1.5.0 or later", status.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Usage: terraform version", status.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("Terraform v1.9.5\non windows_amd64\n")]
    [InlineData("{}")]
    [InlineData("""{"platform":"windows_amd64"}""")]
    [InlineData("""{"terraform_version":42}""")]
    [InlineData("""["terraform_version"]""")]
    public async Task An_answer_that_says_no_version_is_not_working(string stdout)
    {
        var fake = new Fake { Answer = () => new TerraformProcessResult(0, stdout, string.Empty, TimedOut: false) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.NotWorking, status.State);
        Assert.Contains("did not report a version", status.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("dev")]
    [InlineData("x.y.z")]
    public async Task A_version_nobody_can_read_is_not_working_rather_than_assumed_fine(string version)
    {
        var fake = new Fake { Answer = () => Json(version) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.NotWorking, status.State);
        Assert.Contains(version, status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_of_something_else_before_the_document_does_not_hide_the_version()
    {
        var fake = new Fake
        {
            Answer = () => new TerraformProcessResult(
                0,
                "wrapper: starting terraform\n" + Json("1.8.1").StandardOutput,
                string.Empty,
                TimedOut: false),
        };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.Ready, status.State);
        Assert.Equal("1.8.1", status.Version);
    }

    // ------------------------------------------------------------------ fail open on "could not look"

    [Fact]
    public async Task A_look_that_timed_out_could_not_look_and_does_not_close_the_feature()
    {
        var fake = new Fake { Answer = () => new TerraformProcessResult(-1, string.Empty, string.Empty, TimedOut: true) };

        var status = await fake.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.Unknown, status.State);
        Assert.True(status.AllowsDashboards);
        Assert.Contains("did not answer", status.Summary, StringComparison.Ordinal);
        Assert.Contains("checks it again itself", status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_look_that_threw_could_not_look_and_does_not_close_the_feature()
    {
        var run = new Fake { RunFails = new InvalidOperationException("access is denied") };
        var locate = new Fake { LocateFails = new IOException("the disk went away") };

        var fromRun = await run.Probe.ProbeAsync(CancellationToken.None);
        var fromLocate = await locate.Probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(TerraformState.Unknown, fromRun.State);
        Assert.True(fromRun.AllowsDashboards);
        Assert.Contains("access is denied", fromRun.Summary, StringComparison.Ordinal);
        Assert.Equal(TerraformState.Unknown, fromLocate.State);
        Assert.True(fromLocate.AllowsDashboards);
        Assert.Contains("the disk went away", fromLocate.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_look_is_cancelled_and_not_an_answer()
    {
        var fake = new Fake { RunFails = new OperationCanceledException() };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.Probe.ProbeAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(TerraformState.Ready, true)]
    [InlineData(TerraformState.Unknown, true)]
    [InlineData(TerraformState.NotInstalled, false)]
    [InlineData(TerraformState.TooOld, false)]
    [InlineData(TerraformState.NotWorking, false)]
    public void Only_a_definite_no_closes_the_dashboards(TerraformState state, bool allows)
    {
        Assert.Equal(allows, new TerraformStatus(state, "x", string.Empty, string.Empty).AllowsDashboards);
    }

    [Fact]
    public void Before_the_first_look_there_is_nothing_to_report_and_the_wording_is_checking()
    {
        Assert.Equal("Checking for Terraform…", TerraformStatus.Checking.Summary);
        Assert.Equal(TerraformState.Unknown, TerraformStatus.Checking.State);
    }

    // ------------------------------------------------------------------ reading the answer

    [Theory]
    [InlineData("""{"terraform_version":"1.9.5"}""", "1.9.5")]
    [InlineData("  {\"terraform_version\": \"1.9.5\", \"platform\": \"windows_amd64\"}  \r\n", "1.9.5")]
    [InlineData("banner\r\n{\"terraform_version\":\"1.2.3\"}\r\n", "1.2.3")]

    // The document as Terraform indents it, bare and between a wrapper's lines.
    [InlineData("{\n  \"terraform_version\": \"1.9.5\",\n  \"platform\": \"windows_amd64\",\n  \"provider_selections\": {},\n  \"terraform_outdated\": false\n}\n", "1.9.5")]
    [InlineData("wrapper: starting\r\n{\r\n  \"terraform_version\": \"1.7.0\",\r\n  \"provider_selections\": {}\r\n}\r\nwrapper: done\r\n", "1.7.0")]
    public void The_version_is_the_terraform_version_of_the_json_document(string stdout, string expected)
    {
        Assert.True(TerraformProbe.TryReadVersion(stdout, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData("1.9.5", 1, 9, 5)]
    [InlineData("v1.10.0-beta1", 1, 10, 0)]
    [InlineData("1.5", 1, 5, 0)]
    [InlineData("0.13.7", 0, 13, 7)]
    [InlineData("1.9.0-dev", 1, 9, 0)]
    public void A_version_is_its_numbers_before_any_pre_release_suffix(string text, int major, int minor, int patch)
    {
        Assert.True(TerraformProbe.TryParseVersion(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("a.b.c")]
    public void Text_without_a_major_and_a_minor_is_not_a_version(string text)
    {
        Assert.False(TerraformProbe.TryParseVersion(text, out _));
    }

    // ------------------------------------------------------------------ which executable: the one the CLI would run

    private static DefenseClawPaths PathsWith(params string[] files) =>
        new(
            dataDirectory: @"C:\synthetic\data",
            binDirectory: @"C:\synthetic\dc-bin",
            searchPath: new[] { @"C:\synthetic\tools", @"C:\synthetic\more" },
            fileExists: path => files.Contains(path, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task With_no_variable_terraform_is_looked_up_on_path()
    {
        var paths = PathsWith(@"C:\synthetic\more\terraform.exe");

        var location = await TerraformProbe.LocateAsync(paths, configured: null, fileExists: _ => false);

        Assert.Equal(@"C:\synthetic\more\terraform.exe", location.Path);
        Assert.Equal("terraform", location.Requested);
        Assert.False(location.FromVariable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public async Task A_blank_variable_is_no_variable(string configured)
    {
        var paths = PathsWith(@"C:\synthetic\tools\terraform.exe");

        var location = await TerraformProbe.LocateAsync(paths, configured, fileExists: _ => false);

        Assert.Equal(@"C:\synthetic\tools\terraform.exe", location.Path);
        Assert.False(location.FromVariable);
    }

    [Fact]
    public async Task A_variable_that_is_a_bare_name_is_looked_up_on_path_like_the_cli_would()
    {
        var paths = PathsWith(@"C:\synthetic\tools\tofu.exe", @"C:\synthetic\tools\terraform.exe");

        var location = await TerraformProbe.LocateAsync(paths, " tofu ", fileExists: _ => false);

        Assert.Equal(@"C:\synthetic\tools\tofu.exe", location.Path);
        Assert.Equal("tofu", location.Requested);
        Assert.True(location.FromVariable);
    }

    [Fact]
    public async Task A_variable_that_is_a_path_is_that_file_with_or_without_its_extension()
    {
        var paths = PathsWith();

        var withExtension = await TerraformProbe.LocateAsync(paths, "\"D:\\hashi\\terraform.exe\"", path => path == @"D:\hashi\terraform.exe");
        var without = await TerraformProbe.LocateAsync(paths, @"D:\hashi\terraform", path => path == @"D:\hashi\terraform.exe");

        Assert.Equal(@"D:\hashi\terraform.exe", withExtension.Path);
        Assert.Equal(@"D:\hashi\terraform.exe", without.Path);
        Assert.True(withExtension.FromVariable);
    }

    [Theory]
    [InlineData(@".\tools\terraform.exe")]
    [InlineData("tools/terraform.exe")]
    [InlineData(@"..\terraform")]
    public async Task A_relative_path_is_relative_to_where_the_cli_runs_and_never_to_where_this_app_was_started(string configured)
    {
        var asked = new List<string>();
        var paths = PathsWith();

        var location = await TerraformProbe.LocateAsync(paths, configured, path =>
        {
            asked.Add(path);
            return false;
        });

        Assert.Null(location.Path);
        Assert.True(location.FromVariable);

        // The file it looked for is the one the CLI would have looked for, resolved against the CLI's working directory...
        var resolved = Path.GetFullPath(configured, CliWorkingDirectory.DefaultPath);
        Assert.Equal(Path.HasExtension(resolved) ? new[] { resolved } : new[] { resolved + ".exe", resolved }, asked);

        // ... and not the one this process's own working directory would have given.
        Assert.NotEqual(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public async Task A_variable_that_names_a_file_that_is_not_there_finds_nothing_even_when_terraform_is_on_path()
    {
        var paths = PathsWith(@"C:\synthetic\tools\terraform.exe");

        var location = await TerraformProbe.LocateAsync(paths, @"D:\gone\terraform.exe", _ => false);

        // The CLI would be told the same variable and fail the same way: the look must not report a Terraform it would not run.
        Assert.Null(location.Path);
        Assert.Equal(@"D:\gone\terraform.exe", location.Requested);
        Assert.True(location.FromVariable);
    }

    // ------------------------------------------------------------------ the process it starts

    [Fact]
    public void The_process_does_not_inherit_secret_looking_variables()
    {
        const string Name = "DCTEST_TFPROBE_API_KEY";
        Environment.SetEnvironmentVariable(Name, "synthetic");
        try
        {
            var info = TerraformProbe.CreateStartInfo(Exe, new[] { "version" });

            Assert.False(info.Environment.ContainsKey(Name));
            Assert.True(info.Environment.ContainsKey("PATH") || info.Environment.ContainsKey("Path"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    [Fact]
    public void The_process_is_an_argument_list_with_no_shell_no_window_and_no_update_check()
    {
        var info = TerraformProbe.CreateStartInfo(Exe, new[] { "version", "-json" });

        Assert.Equal(Exe, info.FileName);
        Assert.Equal(new[] { "version", "-json" }, info.ArgumentList.ToArray());
        Assert.Equal(string.Empty, info.Arguments);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.True(info.RedirectStandardInput);

        // `terraform version` asks HashiCorp whether a newer release exists unless told not to; a read-only look at this machine does not.
        Assert.Equal("1", info.Environment["CHECKPOINT_DISABLE"]);

        // Not wherever the app happened to be started: nowhere a Terraform working directory (and its lock file) could be.
        Assert.Equal(Path.GetTempPath(), info.WorkingDirectory);
    }

    [Fact]
    public void The_default_probe_is_built_without_running_anything()
    {
        using var temp = new TempDirectory();
        var paths = new DefenseClawPaths(dataDirectory: temp.Path, binDirectory: Path.Combine(temp.Path, "no-such-bin"), searchPath: Array.Empty<string>());

        // Construction only: no look is asked for, so nothing is located and nothing is started.
        Assert.NotNull(TerraformProbe.CreateDefault(paths));
    }
}
