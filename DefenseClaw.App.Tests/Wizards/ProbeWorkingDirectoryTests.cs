using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// CUST-248: the two probes that start a child outside the CLI runner (the setup <c>--help</c> read and the <c>docker</c> look) run it in the same
/// neutral, empty working directory the runner gives the CLI, not in whatever directory the app was started from. Only start infos are built here;
/// nothing is started.
/// </summary>
public sealed class ProbeWorkingDirectoryTests
{
    [Fact]
    public void The_help_probe_runs_in_the_runners_neutral_directory()
    {
        var info = SetupHelpProbe.CreateStartInfo(@"C:\fake\bin\defenseclaw.exe", new[] { "setup", "--help" });

        Assert.Equal(CliWorkingDirectory.DefaultPath, info.WorkingDirectory);
        Assert.Equal(new[] { "setup", "--help" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.Equal("80", info.Environment["COLUMNS"]);
    }

    [Fact]
    public void The_docker_probe_runs_in_the_runners_neutral_directory()
    {
        var info = DockerProbe.CreateStartInfo(@"C:\fake\docker\docker.exe", new[] { "compose", "version" });

        Assert.Equal(CliWorkingDirectory.DefaultPath, info.WorkingDirectory);
        Assert.Equal(new[] { "compose", "version" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.RedirectStandardInput);
    }
}
