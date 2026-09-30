using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.ConfigEditor;
using CrashLog = DefenseClaw.App.App.CrashLog;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The last-resort paths in <c>App.xaml.cs</c> that cannot be reached through an <c>App</c> (it is never built in a test:
/// its start-up would signal the running copy): the crash log's directory and pruning, and the config-editor half of the
/// tray Exit. Both are static, so this class touches the log's process-wide state and must stay the only one that does.
/// </summary>
public sealed class AppFaultHandlingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string LogDirectory => _temp.File("logs");

    private string[] CrashFiles() =>
        Directory.Exists(LogDirectory) ? Directory.GetFiles(LogDirectory, "crash-*.log") : Array.Empty<string>();

    private string PlantCrashFile(string name, TimeSpan age)
    {
        _ = Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, name);
        File.WriteAllText(path, "an earlier crash\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    // ---- CrashLog: where it writes ----

    [Fact]
    public void A_fault_is_written_under_the_directory_the_test_seam_names()
    {
        using (CrashLog.UseDirectory(LogDirectory))
        {
            CrashLog.Write("DispatcherUnhandledException", new InvalidOperationException("the binding blew up"));

            var file = Assert.Single(CrashFiles());
            Assert.Equal(file, CrashLog.CurrentFilePath);
            Assert.Equal(LogDirectory, CrashLog.DirectoryPath);
            var text = File.ReadAllText(file);
            Assert.Contains("DispatcherUnhandledException", text, StringComparison.Ordinal);
            Assert.Contains("the binding blew up", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_seam_puts_the_previous_state_back()
    {
        var before = CrashLog.CurrentFilePath;
        var directoryBefore = CrashLog.DirectoryPath;

        using (CrashLog.UseDirectory(LogDirectory))
        {
            CrashLog.Write("test", new InvalidOperationException("x"));
            Assert.NotEqual(before, CrashLog.CurrentFilePath);
        }

        Assert.Equal(before, CrashLog.CurrentFilePath);
        Assert.Equal(directoryBefore, CrashLog.DirectoryPath);
    }

    [Fact]
    public void The_environment_variable_redirects_the_directory()
    {
        var previous = Environment.GetEnvironmentVariable(CrashLog.DirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CrashLog.DirectoryEnvironmentVariable, LogDirectory);

            Assert.Equal(LogDirectory, CrashLog.DirectoryPath);

            // The seam still wins over it, so a test is never at the mercy of the machine's environment.
            using (CrashLog.UseDirectory(_temp.File("elsewhere")))
            {
                Assert.Equal(_temp.File("elsewhere"), CrashLog.DirectoryPath);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(CrashLog.DirectoryEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Without_the_variable_the_directory_is_the_apps_own_under_local_app_data()
    {
        var previous = Environment.GetEnvironmentVariable(CrashLog.DirectoryEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CrashLog.DirectoryEnvironmentVariable, null);

            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DefenseClaw.App", "logs"),
                CrashLog.DirectoryPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CrashLog.DirectoryEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void A_directory_that_cannot_be_created_is_swallowed()
    {
        // A file where the directory should be: CreateDirectory throws, and a crash handler must not.
        var blocker = _temp.WriteFile("logs", "not a directory");

        using (CrashLog.UseDirectory(blocker))
        {
            Assert.Null(Record.Exception(() => CrashLog.Write("test", new InvalidOperationException("x"))));
            Assert.Null(CrashLog.CurrentFilePath);
        }
    }

    // ---- CrashLog: pruning ----

    [Fact]
    public void Files_older_than_the_age_limit_are_deleted_and_recent_ones_are_kept_however_many_there_are()
    {
        var recent = Enumerable.Range(0, 25)
            .Select(i => PlantCrashFile($"crash-20260929-1{i:D3}-{i}.log", TimeSpan.FromMinutes(5 + i)))
            .ToArray();
        var old = new[]
        {
            PlantCrashFile("crash-20260101-000000-1.log", CrashLog.MaxAge + TimeSpan.FromDays(1)),
            PlantCrashFile("crash-20250601-000000-2.log", TimeSpan.FromDays(400)),
        };
        var notALog = PlantCrashFile("notes.txt", TimeSpan.FromDays(400));

        using (CrashLog.UseDirectory(LogDirectory))
        {
            CrashLog.Write("test", new InvalidOperationException("a new fault"));
        }

        Assert.All(old, path => Assert.False(File.Exists(path), path));
        Assert.All(recent, path => Assert.True(File.Exists(path), path));
        Assert.True(File.Exists(notALog));
        Assert.Equal(recent.Length + 1, CrashFiles().Length);
    }

    [Fact]
    public void A_burst_of_new_files_no_longer_evicts_older_real_history()
    {
        // The count cap this replaced (10) deleted every genuine crash once ten harness crashes had landed.
        var history = PlantCrashFile("crash-20260901-090000-100.log", TimeSpan.FromDays(28));
        var burst = Enumerable.Range(0, 12)
            .Select(i => PlantCrashFile($"crash-20260929-155{i:D3}-{i}.log", TimeSpan.FromMinutes(i)))
            .ToArray();

        using (CrashLog.UseDirectory(LogDirectory))
        {
            CrashLog.Write("test", new InvalidOperationException("a new fault"));
        }

        Assert.True(File.Exists(history));
        Assert.All(burst, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void The_count_ceiling_keeps_a_crash_loop_from_filling_the_disk()
    {
        var planted = Enumerable.Range(0, CrashLog.MaxFiles + 20)
            .Select(i => PlantCrashFile($"crash-loop-{i:D4}.log", TimeSpan.FromMinutes(i + 1)))
            .ToArray();

        using (CrashLog.UseDirectory(LogDirectory))
        {
            CrashLog.Write("test", new InvalidOperationException("a new fault"));
        }

        Assert.Equal(CrashLog.MaxFiles, CrashFiles().Length);
        Assert.True(File.Exists(planted[0]), "the newest planted file survives");
        Assert.False(File.Exists(planted[^1]), "the oldest planted file is the one dropped");
    }

    // ---- the tray Exit's config-editor half ----

    [Fact]
    public async Task A_closed_editor_clears_the_exit_without_a_toast()
    {
        var clearance = await App.ClearEditorForExitAsync(() => Task.FromResult(ConfigEditorExitResult.Closed));

        Assert.True(clearance.MayExit);
        Assert.Null(clearance.Notice);
    }

    [Fact]
    public async Task A_declined_close_abandons_the_exit_without_a_toast_because_the_operator_just_answered()
    {
        var clearance = await App.ClearEditorForExitAsync(() => Task.FromResult(ConfigEditorExitResult.Declined));

        Assert.False(clearance.MayExit);
        Assert.Null(clearance.Notice);
    }

    [Fact]
    public async Task A_busy_editor_abandons_the_exit_with_a_toast_instead_of_silently()
    {
        var clearance = await App.ClearEditorForExitAsync(() => Task.FromResult(ConfigEditorExitResult.Busy));

        Assert.False(clearance.MayExit);
        Assert.StartsWith("Could not exit: ", clearance.Notice, StringComparison.Ordinal);
        Assert.Contains("config editor", clearance.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_close_that_throws_abandons_the_exit_with_the_reason_and_is_logged()
    {
        // A faulted save the editor is still holding: CloseForExitAsync rethrows it. As an async void handler that was
        // a dispatcher fault (a dialog, the dashboard hidden) and the exit dropped.
        using (CrashLog.UseDirectory(LogDirectory))
        {
            var clearance = await App.ClearEditorForExitAsync(
                () => Task.FromException<ConfigEditorExitResult>(new IOException("the save could not finish")));

            Assert.False(clearance.MayExit);
            Assert.Equal("Could not exit: the save could not finish", clearance.Notice);

            var file = Assert.Single(CrashFiles());
            Assert.Contains("the save could not finish", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_close_that_throws_before_returning_a_task_is_caught_too()
    {
        using (CrashLog.UseDirectory(LogDirectory))
        {
            var clearance = await App.ClearEditorForExitAsync(
                () => throw new InvalidOperationException("thrown synchronously"));

            Assert.False(clearance.MayExit);
            Assert.Equal("Could not exit: thrown synchronously", clearance.Notice);
        }
    }
}
