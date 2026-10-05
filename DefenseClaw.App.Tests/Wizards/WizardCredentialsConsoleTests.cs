using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// The console the "type your key here" button opens is where the operator enters an API key, so the program that hosts it must
/// be the system's own <c>cmd.exe</c> - not whatever <c>%ComSpec%</c> or a PATH entry says. Nothing here starts a process: the start
/// info is built and read, never run.
/// </summary>
public sealed class WizardCredentialsConsoleTests
{
    private static string SystemCmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void The_console_is_the_system_cmd_exe_by_absolute_path()
    {
        var info = WizardCredentials.KeysSetStartInfo(@"C:\Tools\defenseclaw.exe", "DEFENSECLAW_LLM_KEY", @"C:\Users\operator\.defenseclaw");

        Assert.True(Path.IsPathFullyQualified(info.FileName));
        Assert.Equal(SystemCmd, info.FileName, ignoreCase: true);
        Assert.True(File.Exists(info.FileName), info.FileName);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void The_interpreter_does_not_come_from_COMSPEC()
    {
        var before = Environment.GetEnvironmentVariable("ComSpec");
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dc-comspec-" + Guid.NewGuid().ToString("n"))).FullName;
        try
        {
            // A working copy of cmd.exe somewhere else (so a test running in parallel that starts a child is not given a broken interpreter for the moment
            // the variable is changed): were the variable read, the start info would name this one.
            var decoy = Path.Combine(directory, "cmd.exe");
            File.Copy(SystemCmd, decoy);
            Environment.SetEnvironmentVariable("ComSpec", decoy);

            Assert.Equal(SystemCmd, WizardCredentials.CommandInterpreterPath(), ignoreCase: true);
            Assert.Equal(SystemCmd, WizardCredentials.KeysSetStartInfo(@"C:\Tools\defenseclaw.exe", "NAME", @"C:\").FileName, ignoreCase: true);

            Environment.SetEnvironmentVariable("ComSpec", string.Empty);
            Assert.Equal(SystemCmd, WizardCredentials.CommandInterpreterPath(), ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ComSpec", before);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A scratch copy of cmd.exe; harmless if it stays.
            }
        }
    }

    [Fact]
    public void The_command_line_runs_keys_set_for_that_name_and_pauses_so_the_result_can_be_read()
    {
        var info = WizardCredentials.KeysSetStartInfo(@"C:\Program Files\DefenseClaw\defenseclaw.exe", "SPLUNK_HEC_TOKEN", @"C:\Users\operator\.defenseclaw");

        Assert.Equal(
            "/d /s /c \"\"C:\\Program Files\\DefenseClaw\\defenseclaw.exe\" keys set SPLUNK_HEC_TOKEN & echo. & pause\"",
            info.Arguments);
        Assert.Equal(@"C:\Users\operator\.defenseclaw", info.WorkingDirectory);
    }
}
