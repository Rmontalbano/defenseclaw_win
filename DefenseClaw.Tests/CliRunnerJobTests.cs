using System.Diagnostics;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Kill-on-close job containment (<see cref="WindowsJob"/>) and Activity Clear. The children are cmd.exe, ping and a
/// short PowerShell script that starts a grandchild ping - harmless programs only; the real defenseclaw is never invoked.
/// Every process a test starts is killed in a finally.
/// </summary>
public sealed class CliRunnerJobTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static string PingPath => Path.Combine(Environment.SystemDirectory, "ping.exe");

    private static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static CliRunner Runner(string dataDirectory) =>
        new(new DefenseClawPaths(
            dataDirectory: dataDirectory,
            binDirectory: Path.Combine(dataDirectory, "no-such-bin"),
            searchPath: Array.Empty<string>()));

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillQuietly(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static Process StartPing()
    {
        var info = new ProcessStartInfo(PingPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add("60");
        info.ArgumentList.Add("127.0.0.1");
        return Process.Start(info)!;
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private static string[] GrandchildScript(string pidFile) => new[]
    {
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        // The child starts a ping of its own and records its pid, then idles: the ping is the grandchild.
        $"$p = Start-Process -FilePath '{PingPath}' -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden -PassThru; " +
        $"[IO.File]::WriteAllText('{pidFile}', [string]$p.Id); Start-Sleep -Seconds 60",
    };

    private static bool TryReadPid(string pidFile, out int pid)
    {
        pid = 0;
        try
        {
            return File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out pid) && pid > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    public void Closing_the_job_ends_a_process_assigned_to_it()
    {
        // What happens when the app is killed outright: the last handle to the job goes away with it.
        using var job = WindowsJob.TryCreate();
        Assert.NotNull(job);
        using var child = StartPing();
        try
        {
            Assert.True(job.TryAssign(child));
            Assert.Equal(1, job.ActiveProcessCount);
            Assert.False(child.HasExited);

            job.Dispose();

            Assert.True(child.WaitForExit((int)Wait.TotalMilliseconds), "the child outlived its job");
        }
        finally
        {
            KillQuietly(child.Id);
        }
    }

    [Fact]
    public void Terminate_ends_everything_in_the_job_and_a_closed_job_reports_failure()
    {
        using var job = WindowsJob.TryCreate();
        Assert.NotNull(job);
        using var child = StartPing();
        try
        {
            Assert.True(job.TryAssign(child));

            Assert.True(job.Terminate());
            Assert.True(child.WaitForExit((int)Wait.TotalMilliseconds));

            job.Dispose();
            job.Dispose();
            Assert.False(job.Terminate());
            Assert.Equal(-1, job.ActiveProcessCount);
            Assert.False(job.TryAssign(child));
        }
        finally
        {
            KillQuietly(child.Id);
        }
    }

    [Fact]
    public void A_process_that_already_ended_is_not_an_exception()
    {
        using var job = WindowsJob.TryCreate();
        Assert.NotNull(job);
        var info = new ProcessStartInfo(CmdPath) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("exit");
        info.ArgumentList.Add("0");
        using var child = Process.Start(info)!;
        try
        {
            Assert.True(child.WaitForExit((int)Wait.TotalMilliseconds));

            // Whether Windows still lets a just-exited process in is its business; the contract is only that it does not throw.
            _ = job.TryAssign(child);
        }
        finally
        {
            KillQuietly(child.Id);
        }
    }

    [Fact]
    public async Task Cancel_kills_a_grandchild_the_child_started_right_after_launch()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var pidFile = temp.File("grandchild.pid");
        var grandchild = 0;

        CliInvocation? invocation = null;
        runner.InvocationStarted += (_, i) => invocation = i;
        var run = runner.RunExecutableAsync(
            PowerShellPath,
            GrandchildScript(pidFile),
            options: CliRunOptions.Default with { Timeout = TimeSpan.FromMinutes(2) });
        try
        {
            Assert.True(WaitUntil(() => TryReadPid(pidFile, out grandchild)), "the child never reported its grandchild");
            Assert.True(IsAlive(grandchild));

            Assert.True(runner.Cancel(invocation!, out _));
            var finished = await run.WaitAsync(Wait);

            Assert.StartsWith("cancelled", finished.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(WaitUntil(() => !IsAlive(grandchild)), "the grandchild outlived the cancel");
        }
        finally
        {
            _ = runner.Shutdown(TimeSpan.FromSeconds(10));
            if (grandchild > 0)
            {
                KillQuietly(grandchild);
            }
        }
    }

    [Fact]
    public async Task Shutdown_ends_the_whole_tree_of_a_running_child()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        var pidFile = temp.File("grandchild.pid");
        var grandchild = 0;

        var run = runner.RunExecutableAsync(
            PowerShellPath,
            GrandchildScript(pidFile),
            options: CliRunOptions.Default with { Timeout = TimeSpan.FromMinutes(2) });
        try
        {
            Assert.True(WaitUntil(() => TryReadPid(pidFile, out grandchild)));

            _ = runner.Shutdown(Wait);
            _ = await run.WaitAsync(Wait);

            Assert.True(WaitUntil(() => !IsAlive(grandchild)), "the grandchild outlived shutdown");
        }
        finally
        {
            _ = runner.Shutdown(TimeSpan.FromSeconds(10));
            if (grandchild > 0)
            {
                KillQuietly(grandchild);
            }
        }
    }

    [Fact]
    public async Task Clear_keeps_the_run_still_in_flight_and_drops_the_finished_ones()
    {
        using var temp = new TempDirectory();
        var runner = Runner(temp.Path);
        _ = await runner.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "done" });
        CliInvocation? running = null;
        runner.InvocationStarted += (_, i) => running = i;
        var run = runner.RunExecutableAsync(CmdPath, new[] { "/c", "ping -n 60 127.0.0.1 >nul" });
        try
        {
            Assert.True(WaitUntil(() => running is { IsRunning: true } && runner.Activity.Count == 2));

            runner.ClearActivity();

            var kept = Assert.Single(runner.Activity);
            Assert.Same(running, kept);
            Assert.True(runner.Cancel(kept, out _));
            _ = await run.WaitAsync(Wait);

            // Once it has finished, the next Clear takes it too.
            runner.ClearActivity();
            Assert.Empty(runner.Activity);
        }
        finally
        {
            _ = runner.Shutdown(TimeSpan.FromSeconds(10));
        }
    }
}
