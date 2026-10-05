using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// The Activity panel's Clear (as the TUI's) leaves the run that is still in flight: its row stays, still running and
/// still cancellable. The child is cmd.exe pinging localhost, ended by the test; the real defenseclaw is never invoked.
/// </summary>
[Collection(UiCollection.Name)]
public class ActivityClearKeepsRunningTests
{
    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public async Task Clear_leaves_exactly_the_row_of_a_run_in_flight()
    {
        using var scene = ActivityScene.Open(940, 620);
        _ = await scene.Cli.RunExecutableAsync(CmdPath, new[] { "/c", "echo", "finished" });
        CliInvocation? running = null;
        scene.Cli.InvocationStarted += (_, i) => running = i;
        var run = scene.Cli.RunExecutableAsync(CmdPath, new[] { "/c", "ping -n 60 127.0.0.1 >nul" });
        try
        {
            UiThread.WaitFor(() => running is { IsRunning: true }, "the child to start");
            UiThread.Run(() => scene.ViewModel.RefreshCommand.Execute(null));
            Assert.Equal(2, UiThread.Run(() => scene.ViewModel.Rows.Count));

            UiThread.Run(() => scene.ViewModel.ClearActivityCommand.Execute(null));

            var rows = UiThread.Run(() => scene.ViewModel.Rows.ToList());
            var kept = Assert.Single(rows);
            Assert.Same(running, kept.Invocation);
            Assert.True(kept.IsRunning);
            Assert.False(UiThread.Run(() => scene.ViewModel.IsEmpty));
        }
        finally
        {
            if (running is not null)
            {
                _ = scene.Cli.Cancel(running, out _);
            }

            _ = scene.Cli.Shutdown(TimeSpan.FromSeconds(10));
            _ = await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
