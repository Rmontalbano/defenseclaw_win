using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The save's drift check compares signatures, then a moment later reads the bytes it backs up and replaces. A write that
/// lands in that gap (the CLI's <c>defenseclaw config set</c>, say) passed the first check and was then backed up as if
/// it were the original and overwritten. The backup step now checks the very bytes it read against the signature taken
/// at load.
/// </summary>
public sealed class ConfigSaveServiceDriftTests : IDisposable
{
    private const string Original = "config_version: 8\nguardrail:\n  mode: observe\n";
    private const string Edited = "config_version: 8\nguardrail:\n  mode: enforce\n";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private ConfigSaveService Service(out string configPath, out FileSignature signature)
    {
        _ = _temp.WriteFile("config.yaml", Original);
        var paths = TestServices.IsolatedPaths(_temp.Path);
        configPath = paths.ConfigFilePath;

        var service = new ConfigSaveService(paths, cli: null);
        signature = service.CaptureSignature();
        return service;
    }

    private string[] Backups() => Directory.GetFiles(_temp.Path, "config.yaml.bak-*");

    [Fact]
    public async Task A_write_between_the_drift_check_and_the_backup_is_refused_and_left_alone()
    {
        var service = Service(out var configPath, out var signature);
        const string CliWrite = "config_version: 8\nguardrail:\n  mode: observe\n  extra_key: written-by-the-cli\n";
        service.AfterDriftCheck = () => File.WriteAllText(configPath, CliWrite);

        var outcome = await service.SaveAsync(Edited, signature);

        Assert.False(outcome.Success);
        Assert.Equal(SaveStage.DriftDetected, outcome.Stage);
        Assert.Contains("while the save was starting", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(CliWrite, File.ReadAllText(configPath));
        Assert.Empty(Backups());
        Assert.Empty(Directory.GetFiles(_temp.Path, ".config.yaml.tmp-*"));
        Assert.Null(outcome.BackupPath);
    }

    [Fact]
    public async Task A_same_length_write_in_that_gap_is_caught_too()
    {
        // mode: observe -> mode: enforce would keep the byte count; the hash is what tells them apart.
        var service = Service(out var configPath, out var signature);
        const string SameLength = "config_version: 8\nguardrail:\n  mode: monitor\n";
        Assert.Equal(Original.Length, SameLength.Length);
        service.AfterDriftCheck = () => File.WriteAllText(configPath, SameLength);

        var outcome = await service.SaveAsync(Edited, signature);

        Assert.Equal(SaveStage.DriftDetected, outcome.Stage);
        Assert.Equal(SameLength, File.ReadAllText(configPath));
        Assert.Empty(Backups());
    }

    [Fact]
    public async Task With_nothing_written_in_the_gap_the_save_goes_through_and_backs_up_the_original()
    {
        var service = Service(out var configPath, out var signature);
        var seamRan = false;
        service.AfterDriftCheck = () => seamRan = true;

        var outcome = await service.SaveAsync(Edited, signature);

        Assert.True(seamRan);
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(Edited, File.ReadAllText(configPath));
        var backup = Assert.Single(Backups());
        Assert.Equal(Original, File.ReadAllText(backup));
        Assert.Equal(backup, outcome.BackupPath);
    }

    [Fact]
    public async Task A_change_before_the_save_is_still_caught_by_the_first_check()
    {
        var service = Service(out var configPath, out var signature);
        File.WriteAllText(configPath, "config_version: 8\nsomething: else\n");

        var outcome = await service.SaveAsync(Edited, signature);

        Assert.Equal(SaveStage.DriftDetected, outcome.Stage);
        Assert.DoesNotContain("while the save was starting", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(Backups());
    }
}
