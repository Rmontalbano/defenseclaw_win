using System.Diagnostics;
using System.Globalization;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// Kill-on-close job containment (<see cref="WindowsJob"/>) and Activity Clear. The children are cmd.exe, ping and a
/// short PowerShell script that starts a grandchild ping - harmless programs only; the real defenseclaw is never invoked.
/// Every process a test starts is killed in a finally.
/// <para>
/// <b>No test here asserts how long anything takes.</b> A test waits for a condition (the grandchild has reported, the process is
/// gone) up to <see cref="Wait"/>, a ceiling that only bounds a hang. It is generous because starting <c>powershell.exe</c> and having
/// it start a process took 0.9 s on a quiet machine and 23 to 32 s with sixteen busy processes sharing two cores (measured), the kind
/// of squeeze a CI runner is in while both test projects and xunit's parallel classes run on it. A fixed 30 s bound failed on CI twice
/// in a row (and fails on that emulated load), each time while the child was still starting, before anything was cancelled (CUST-301).
/// The children also live well past the ceiling, so a slow machine never finds one gone by itself.
/// </para>
/// </summary>
public sealed class CliRunnerJobTests
{
    private static readonly TimeSpan Wait = TestTimeouts.Ceiling;

    /// <summary>How long the PowerShell child, and the ping it starts, live if nobody ends them: two ceilings and a minute.</summary>
    private static readonly TimeSpan ChildLifetime = Wait + Wait + TimeSpan.FromMinutes(1);

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

    /// <summary>Polls <paramref name="condition"/> every 50 ms until it holds or <see cref="Wait"/> has passed (measured on the monotonic clock).</summary>
    private static bool WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Wait)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return condition();
    }

    private static string[] GrandchildScript(string pidFile)
    {
        var seconds = ((int)ChildLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var pidPath = pidFile.Replace("'", "''", StringComparison.Ordinal);
        return new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            // The child starts a ping of its own and records its pid, then idles: the ping is the grandchild. Any failure ends the child
            // on the spot (Stop), so a test waiting for the pid is told why instead of waiting for one that is never coming.
            "$ErrorActionPreference = 'Stop'; " +
            $"$p = Start-Process -FilePath '{PingPath}' -ArgumentList '-n','{seconds}','127.0.0.1' -WindowStyle Hidden -PassThru; " +
            $"[IO.File]::WriteAllText('{pidPath}', [string]$p.Id); Start-Sleep -Seconds {seconds}",
        };
    }

    private static bool TryReadPid(string pidFile, out int pid)
    {
        pid = 0;
        try
        {
            return File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The child is writing it this very moment (or a scanner has it open): not there yet.
            return false;
        }
    }

    /// <summary>What an invocation did, for a failure message: how it ended and the last thing it printed.</summary>
    private static string Describe(CliInvocation? invocation) =>
        invocation is null
            ? "the run never started"
            : $"exit code {invocation.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}, " +
              $"{invocation.FailureReason ?? "no failure reason"}, " +
              $"output: [{string.Join(" | ", invocation.OutputLines.TakeLast(10).Select(l => l.Text))}]";

    /// <summary>
    /// A runner and the PowerShell child it runs, which starts a ping of its own and writes the ping's pid to a file: a tree of three
    /// processes below the test. Disposing it ends whatever is left of the tree, whether the test passed or not.
    /// </summary>
    private sealed class ChildWithGrandchild : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly string _pidFile;

        public ChildWithGrandchild()
        {
            Runner = CliRunnerJobTests.Runner(_temp.Path);
            _pidFile = _temp.File("grandchild.pid");
            Runner.InvocationStarted += (_, i) => Invocation = i;
            Run = Runner.RunExecutableAsync(
                PowerShellPath,
                GrandchildScript(_pidFile),
                options: CliRunOptions.Default with { Timeout = ChildLifetime + TimeSpan.FromMinutes(1) });
        }

        public CliRunner Runner { get; }

        /// <summary>The run, finished when the child is.</summary>
        public Task<CliInvocation> Run { get; }

        /// <summary>The live invocation; the runner announces it synchronously when the run starts, before the run's task is handed back.</summary>
        public CliInvocation? Invocation { get; private set; }

        /// <summary>The ping's pid, once <see cref="WaitUntilReported"/> has it.</summary>
        public int Grandchild { get; private set; }

        /// <summary>
        /// Waits for the child to say it has started its grandchild, then holds the pid. If the run ends first there is nothing left to wait
        /// for (the script failed, or the child was killed): that is a failure now, with what the child said, not after the ceiling.
        /// </summary>
        public void WaitUntilReported()
        {
            var pid = 0;
            _ = WaitUntil(() => TryReadPid(_pidFile, out pid) || Run.IsCompleted);
            if (pid == 0 && !TryReadPid(_pidFile, out pid))
            {
                Assert.Fail(Run.IsCompleted
                    ? $"the child ended without reporting its grandchild: {Describe(Invocation)}"
                    : $"the child did not report its grandchild within {Wait.TotalSeconds:0} s: {Describe(Invocation)}");
            }

            Grandchild = pid;
        }

        public void Dispose()
        {
            _ = Runner.Shutdown(TimeSpan.FromSeconds(10));
            if (Grandchild > 0)
            {
                KillQuietly(Grandchild);
            }

            _temp.Dispose();
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
        using var tree = new ChildWithGrandchild();
        tree.WaitUntilReported();
        Assert.True(IsAlive(tree.Grandchild), "the grandchild is gone before anything was cancelled");

        Assert.True(tree.Runner.Cancel(tree.Invocation!, out _));
        var finished = await tree.Run.WaitAsync(Wait);

        Assert.StartsWith("cancelled", finished.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(WaitUntil(() => !IsAlive(tree.Grandchild)), "the grandchild outlived the cancel");
    }

    [Fact]
    public async Task Shutdown_ends_the_whole_tree_of_a_running_child()
    {
        using var tree = new ChildWithGrandchild();
        tree.WaitUntilReported();

        _ = tree.Runner.Shutdown(Wait);
        _ = await tree.Run.WaitAsync(Wait);

        Assert.True(WaitUntil(() => !IsAlive(tree.Grandchild)), "the grandchild outlived shutdown");
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
