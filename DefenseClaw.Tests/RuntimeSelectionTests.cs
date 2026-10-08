using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Net;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The developer runtime selector's Core half (CUST-291): validation, the paths it yields, the container wrapper, and that a
/// default selection changes nothing.
/// </summary>
public class RuntimeSelectionTests
{
    private const string Cli = @"D:\next\bin\defenseclaw.exe";
    private const string Home = @"D:\next\home";
    private const string DataCopy = @"D:\next\container-data";

    private static bool Exists(string path) => path is Cli or DataCopy;

    [Fact]
    public void The_default_selection_is_the_installed_runtime_and_always_valid()
    {
        Assert.True(RuntimeSelection.Installed.IsDefault);
        Assert.Equal(RuntimeKind.Installed, RuntimeSelection.Installed.Kind);
        Assert.Null(RuntimeSelection.Installed.Validate());
        Assert.Null(RuntimeSelection.Installed.DataDirectoryOverride);
        Assert.Equal(RuntimeSelection.Installed, new RuntimeSelection());
    }

    [Fact]
    public void A_side_by_side_cli_needs_an_existing_defenseclaw_exe_and_an_absolute_home()
    {
        Assert.Null(RuntimeSelection.ForCli(Cli, Home, null).Validate(Exists, Exists));
        Assert.Null(RuntimeSelection.ForCli(Cli, Home, "http://127.0.0.1:18972").Validate(Exists, Exists));

        Assert.NotNull(RuntimeSelection.ForCli(null, Home, null).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForCli(@"D:\next\bin\other.exe", Home, null).Validate(_ => true, Exists));
        Assert.NotNull(RuntimeSelection.ForCli(@"D:\missing\defenseclaw.exe", Home, null).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForCli(Cli, null, null).Validate(Exists, Exists)); // a shared home would share the live data
        Assert.NotNull(RuntimeSelection.ForCli(Cli, @"relative\home", null).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForCli(Cli, Home, "http://example.com:18972").Validate(Exists, Exists));
    }

    [Fact]
    public void A_container_needs_a_valid_name_a_loopback_gateway_with_a_port_and_an_existing_data_copy()
    {
        Assert.Null(RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", DataCopy).Validate(Exists, Exists));

        Assert.NotNull(RuntimeSelection.ForContainer(null, "http://127.0.0.1:18971", DataCopy).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForContainer("-rm", "http://127.0.0.1:18971", DataCopy).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForContainer("a b", "http://127.0.0.1:18971", DataCopy).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForContainer("dc-next-1", null, DataCopy).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", null).Validate(Exists, Exists));
        Assert.NotNull(RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", @"D:\nowhere").Validate(Exists, Exists));
    }

    [Theory]
    [InlineData("http://127.0.0.1:18971", true, 18971)]
    [InlineData("http://localhost:18971", true, 18971)]
    [InlineData("https://[::1]:18971", true, 18971)]
    [InlineData("http://127.0.0.1:18971/", true, 18971)]
    [InlineData("http://127.0.0.1", false, 0)]
    [InlineData("http://10.0.0.5:18971", false, 0)]
    [InlineData("http://example.com:18971", false, 0)]
    [InlineData("http://user:pw@127.0.0.1:18971", false, 0)]
    [InlineData("http://127.0.0.1:18971/api", false, 0)]
    [InlineData("http://127.0.0.1:18971/?token=x", false, 0)]
    [InlineData("ftp://127.0.0.1:18971", false, 0)]
    [InlineData("127.0.0.1:18971", false, 0)]
    [InlineData("not a url", false, 0)]
    public void The_gateway_address_is_loopback_only_with_no_credentials_path_or_query(string url, bool ok, int port)
    {
        var problem = RuntimeSelection.CheckGatewayUrl(url, out var parsed);

        Assert.Equal(ok, problem is null);
        Assert.Equal(port, parsed);
        Assert.Equal(ok, RuntimeSelection.ForCli(Cli, Home, url).TryGetGatewayPort(out var viaSelection) && viaSelection == port);
    }

    [Fact]
    public void Fields_are_trimmed_and_blank_is_absent()
    {
        var selection = RuntimeSelection.ForCli("  " + Cli + " ", " ", null);

        Assert.Equal(Cli, selection.CliPath);
        Assert.Null(selection.HomeDirectory);
    }

    [Fact]
    public void A_default_selection_builds_the_same_paths_as_no_selection()
    {
        var viaFactory = RuntimeEnvironment.CreatePaths(RuntimeSelection.Installed);
        var plain = new DefenseClawPaths();

        Assert.Equal(plain.DataDirectory, viaFactory.DataDirectory);
        Assert.Equal(plain.BinDirectory, viaFactory.BinDirectory);
        Assert.Equal(plain.DataDirectoryOrigin, viaFactory.DataDirectoryOrigin);
        Assert.True(viaFactory.Runtime.IsDefault);
        Assert.False(viaFactory.DataDirectoryReadOnly);
        Assert.Equal(plain.CliPathOverride, viaFactory.CliPathOverride);
        Assert.Equal(plain.DataDirectory, RuntimeEnvironment.CreatePaths(null).DataDirectory);
    }

    [Fact]
    public void A_side_by_side_cli_gets_its_own_home_its_own_bin_folder_and_ignores_PATH()
    {
        var paths = RuntimeEnvironment.CreatePaths(RuntimeSelection.ForCli(Cli, Home, "http://127.0.0.1:18972"));

        Assert.Equal(Home, paths.DataDirectory);
        Assert.Equal(@"D:\next\bin", paths.BinDirectory);
        Assert.Equal(Cli, paths.CliPathOverride);
        Assert.False(paths.DataDirectoryReadOnly);
        Assert.Equal(RuntimeKind.Cli, paths.Runtime.Kind);

        // PATH is not consulted, so the installed defenseclaw-gateway can never be the one used.
        Assert.DoesNotContain(paths.CandidatesFor("defenseclaw-gateway"), c => !c.StartsWith(@"D:\next\bin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_container_reads_a_read_only_host_copy_and_an_incomplete_selection_never_half_applies()
    {
        var paths = RuntimeEnvironment.CreatePaths(RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", DataCopy));

        Assert.Equal(DataCopy, paths.DataDirectory);
        Assert.True(paths.DataDirectoryReadOnly);

        var incomplete = RuntimeEnvironment.CreatePaths(RuntimeSelection.ForCli(null, null, null));
        Assert.True(incomplete.Runtime.IsDefault);
        Assert.Equal(new DefenseClawPaths().DataDirectory, incomplete.DataDirectory);
    }

    [Fact]
    public void The_container_wrapper_names_environment_variables_but_never_carries_a_value()
    {
        var selection = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", DataCopy);

        var plain = RuntimeLaunch.ContainerArguments(selection, "defenseclaw", ["status", "--json"], [], usesStdin: false);
        Assert.Equal(new[] { "exec", "dc-next-1", "defenseclaw", "status", "--json" }, plain);

        var withEnv = RuntimeLaunch.ContainerArguments(selection, "defenseclaw-gateway", ["status"], ["OPENAI_API_KEY", "DEFENSECLAW_X"], usesStdin: true);
        Assert.Equal(
            new[] { "exec", "-i", "-e", "OPENAI_API_KEY", "-e", "DEFENSECLAW_X", "dc-next-1", "defenseclaw-gateway", "status" },
            withEnv);

        Assert.Throws<InvalidOperationException>(() =>
            RuntimeLaunch.ContainerArguments(RuntimeSelection.Installed, "defenseclaw", [], [], false));
        Assert.True(RuntimeLaunch.RunsInContainer("defenseclaw"));
        Assert.True(RuntimeLaunch.RunsInContainer("DefenseClaw-Gateway"));
        Assert.False(RuntimeLaunch.RunsInContainer("skill-scanner"));
    }

    // ------------------------------------------------------------------ through the real CliRunner

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task A_side_by_side_cli_runs_with_its_home_in_the_environment_and_only_for_defenseclaw_binaries()
    {
        using var temp = new TempDirectory();
        var fake = Path.Combine(temp.Path, "defenseclaw.exe");
        File.Copy(CmdPath, fake);
        var home = Path.Combine(temp.Path, "home");
        Directory.CreateDirectory(home);
        var paths = RuntimeEnvironment.CreatePaths(RuntimeSelection.ForCli(fake, home, null));
        var runner = new CliRunner(paths, neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"));

        var ours = await runner.RunExecutableAsync(fake, ["/c", "set", DefenseClawPaths.HomeVariableName]);
        var other = await runner.RunExecutableAsync(CmdPath, ["/c", "set", DefenseClawPaths.HomeVariableName]);

        Assert.Equal(0, ours.ExitCode);
        Assert.Contains(ours.OutputLines, l => l.Text == $"{DefenseClawPaths.HomeVariableName}={home}");
        Assert.DoesNotContain(other.OutputLines, l => l.Text == $"{DefenseClawPaths.HomeVariableName}={home}");
    }

    [Fact]
    public async Task A_container_runs_through_docker_exec_with_environment_names_on_argv_and_values_in_docker_s_environment()
    {
        using var temp = new TempDirectory();
        var bin = Path.Combine(temp.Path, "bin");
        Directory.CreateDirectory(bin);

        // A stand-in for docker: cmd.exe under the name docker.exe. It will not understand 'exec'; the point is what it was handed.
        File.Copy(CmdPath, Path.Combine(bin, "docker.exe"));
        var data = Path.Combine(temp.Path, "data");
        Directory.CreateDirectory(data);
        var selection = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", data);
        var paths = new DefenseClawPaths(dataDirectory: data, binDirectory: bin, searchPath: Array.Empty<string>(), runtime: selection);
        var runner = new CliRunner(paths, neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"));
        const string secret = "synthetic-container-secret-424242";

        var invocation = await runner.RunAsync(
            ["status", "--json"],
            options: CliRunOptions.Default.WithEnvironment("DEFENSECLAW_TEST_SECRET", new SecretValue(secret)));

        Assert.Equal(Path.Combine(bin, "docker.exe"), invocation.Executable);
        Assert.Equal(
            new[] { "exec", "-e", "DEFENSECLAW_TEST_SECRET", "dc-next-1", "defenseclaw", "status", "--json" },
            invocation.Argv.ToArray());
        Assert.DoesNotContain(secret, string.Join(' ', invocation.Argv), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, invocation.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_container_without_docker_fails_as_a_missing_executable_not_by_running_the_host_cli()
    {
        using var temp = new TempDirectory();
        var selection = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", temp.Path);
        var paths = new DefenseClawPaths(dataDirectory: temp.Path, binDirectory: Path.Combine(temp.Path, "none"), searchPath: Array.Empty<string>(), runtime: selection);
        var runner = new CliRunner(paths, neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"));

        var ex = await Assert.ThrowsAsync<CliNotFoundException>(() => runner.RunAsync(["status"]));

        Assert.Equal("docker", ex.ExecutableName);
    }

    [Fact]
    public void A_container_keeps_the_wizard_timeout_because_it_is_worked_out_from_the_original_argv()
    {
        Assert.Equal(CliRunner.LongRunningTimeout, CliRunner.ResolveTimeout("defenseclaw", ["setup", "redaction"]));
    }

    // ------------------------------------------------------------------ the gateway token and the selected port

    private sealed class NoOwner : IPortOwnerInspector
    {
        public PortOwner? FindListener(int port) => null;
    }

    [Fact]
    public void In_container_mode_only_the_selected_gateway_port_is_trusted_with_the_token()
    {
        var selection = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", DataCopy);
        var container = new GatewayPeerVerifier(
            new DefenseClawPaths(dataDirectory: DataCopy, runtime: selection), new NoOwner());
        var installed = new GatewayPeerVerifier(new DefenseClawPaths(dataDirectory: DataCopy), new NoOwner());

        Assert.Equal(PortOwnerTrust.Gateway, container.ForPort(18971)());
        Assert.Equal(PortOwnerTrust.Unknown, container.ForPort(18970)());
        Assert.Equal(PortOwnerTrust.Unknown, installed.ForPort(18971)());
    }
}
