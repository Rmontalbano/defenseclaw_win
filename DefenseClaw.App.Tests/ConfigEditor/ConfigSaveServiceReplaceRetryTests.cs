using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The final swap onto config.yaml (<c>File.Replace</c>) fails with a sharing violation while anything — a file watcher, an
/// indexer, an antivirus scanner — has the file open. That is transient, so the save waits it out a few times; a hold that
/// outlasts the retries is still reported as a failed write with the file and the buffer untouched.
/// </summary>
public sealed class ConfigSaveServiceReplaceRetryTests : IDisposable
{
    private const string Original = "config_version: 8\nguardrail:\n  mode: observe\n";
    private const string Edited = "config_version: 8\nguardrail:\n  mode: enforce\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private ConfigSaveService Service(out string configPath, out DefenseClaw.Core.Config.FileSignature signature)
    {
        _ = _temp.WriteFile("config.yaml", Original);
        var paths = TestServices.IsolatedPaths(_temp.Path);
        configPath = paths.ConfigFilePath;

        // No CLI: the save ends after the swap (validation is skipped), which is the step under test.
        var service = new ConfigSaveService(paths, cli: null);
        signature = service.CaptureSignature();
        return service;
    }

    /// <summary>Opens config.yaml the way a scanner does: readable by others, not deletable or replaceable.</summary>
    private static FileStream HoldOpen(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public async Task A_sharing_violation_that_clears_within_the_retries_does_not_fail_the_save()
    {
        var service = Service(out var configPath, out var signature);
        var held = HoldOpen(configPath);
        var release = Task.Run(async () =>
        {
            await Task.Delay(120);
            await held.DisposeAsync();
        });

        var outcome = await service.SaveAsync(Edited, signature);
        await release;

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(SaveStage.Succeeded, outcome.Stage);
        Assert.Equal(Edited, File.ReadAllText(configPath));
    }

    [Fact]
    public async Task A_hold_that_outlasts_the_retries_is_a_failed_write_and_leaves_the_file_alone()
    {
        var service = Service(out var configPath, out var signature);

        using (HoldOpen(configPath))
        {
            var outcome = await service.SaveAsync(Edited, signature);

            Assert.False(outcome.Success);
            Assert.Equal(SaveStage.WriteFailed, outcome.Stage);
            Assert.Contains("Could not write config.yaml", outcome.Message, StringComparison.Ordinal);
        }

        Assert.Equal(Original, File.ReadAllText(configPath));
        Assert.Empty(Directory.GetFiles(_temp.Path, ".config.yaml.tmp-*"));
    }

    [Fact]
    public async Task A_restore_gets_the_same_patience()
    {
        var service = Service(out var configPath, out _);
        var backup = _temp.WriteFile("config.yaml.bak-test", Edited);
        var held = HoldOpen(configPath);
        var release = Task.Run(async () =>
        {
            await Task.Delay(120);
            await held.DisposeAsync();
        });

        var outcome = await service.RestoreFromBackupAsync(backup);
        await release;

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(Edited, File.ReadAllText(configPath));
    }
}
