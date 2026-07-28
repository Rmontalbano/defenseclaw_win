using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

public class ConfigChangeTokenTests
{
    private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void FileSignature_distinguishes_missing_from_present()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.yaml");

        var missing = FileSignature.Capture(path);
        Assert.False(missing.Exists);

        File.WriteAllText(path, "config_version: 8\n");
        var present = FileSignature.Capture(path);

        Assert.True(present.Exists);
        Assert.NotEqual(missing, present);
    }

    [Fact]
    public void FileSignature_changes_when_the_content_length_changes()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("config.yaml", "short");
        var before = FileSignature.Capture(path);

        File.WriteAllText(path, "considerably longer contents");

        Assert.NotEqual(before, FileSignature.Capture(path));
    }

    [Fact]
    public void FileSignature_changes_on_a_same_length_edit()
    {
        // mode: observe -> mode: enforce keeps the byte count, and two quick saves can
        // share a last-write timestamp. The signature must still differ.
        using var temp = new TempDirectory();
        var path = temp.Write("config.yaml", "mode: observe\n");
        var before = FileSignature.Capture(path);

        File.WriteAllText(path, "mode: enforce\n");
        var after = FileSignature.Capture(path);

        Assert.Equal(before.Length, after.Length);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Notifies_when_config_yaml_changes()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        temp.Write(".env", "UNRELATED=1\n");

        using var token = new ConfigChangeToken(new DefenseClawPaths(dataDirectory: temp.Path), FastPoll);
        using var signalled = new ManualResetEventSlim(false);
        ConfigChangedEventArgs? observed = null;

        token.Changed += (_, e) =>
        {
            observed = e;
            signalled.Set();
        };

        File.WriteAllText(temp.File("config.yaml"), "config_version: 8\nclaw:\n  mode: codex\n");

        Assert.True(signalled.Wait(Timeout), "config.yaml change was not observed");
        Assert.Equal(ConfigFileKind.Config, observed!.Kind);
        Assert.True(token.HasChanged);

        token.Reset();
        Assert.False(token.HasChanged);
    }

    [Fact]
    public void Notifies_when_the_dot_env_file_changes()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");
        temp.Write(".env", "DEFENSECLAW_GATEWAY_TOKEN=old\n");

        using var token = new ConfigChangeToken(new DefenseClawPaths(dataDirectory: temp.Path), FastPoll);
        using var signalled = new ManualResetEventSlim(false);
        ConfigChangedEventArgs? observed = null;

        using var registration = token.RegisterChangeCallback(e =>
        {
            observed = e;
            signalled.Set();
        });

        File.WriteAllText(temp.File(".env"), "DEFENSECLAW_GATEWAY_TOKEN=rotated-value\n");

        Assert.True(signalled.Wait(Timeout), ".env change was not observed");
        Assert.Equal(ConfigFileKind.DotEnv, observed!.Kind);
    }

    [Fact]
    public void Notifies_when_config_yaml_appears_for_the_first_time()
    {
        // The "installed but not initialized" → initialized transition.
        using var temp = new TempDirectory();

        using var token = new ConfigChangeToken(new DefenseClawPaths(dataDirectory: temp.Path), FastPoll);
        using var signalled = new ManualResetEventSlim(false);
        token.Changed += (_, _) => signalled.Set();

        File.WriteAllText(temp.File("config.yaml"), "config_version: 8\n");

        Assert.True(signalled.Wait(Timeout), "config.yaml creation was not observed");
    }

    [Fact]
    public void Unsubscribed_callbacks_stop_firing()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        using var token = new ConfigChangeToken(new DefenseClawPaths(dataDirectory: temp.Path), FastPoll);
        var calls = 0;
        var registration = token.RegisterChangeCallback(_ => Interlocked.Increment(ref calls));
        registration.Dispose();

        File.WriteAllText(temp.File("config.yaml"), "config_version: 9\n");
        token.Poll();

        Assert.Equal(0, calls);
        Assert.True(token.HasChanged);
    }

    [Fact]
    public void Poll_detects_changes_without_the_watcher()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        using var token = new ConfigChangeToken(new DefenseClawPaths(dataDirectory: temp.Path), TimeSpan.FromHours(1));

        File.WriteAllText(temp.File("config.yaml"), "config_version: 9\n");
        token.Poll();

        Assert.True(token.HasChanged);
    }

    [Fact]
    public void No_change_means_no_notification()
    {
        using var temp = new TempDirectory();
        temp.Write("config.yaml", "config_version: 8\n");

        using var token = new ConfigChangeToken(new DefenseClawPaths(dataDirectory: temp.Path), TimeSpan.FromHours(1));
        token.Poll();
        token.Poll();

        Assert.False(token.HasChanged);
    }
}
