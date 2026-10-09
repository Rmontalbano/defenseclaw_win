using System.Reflection;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="ConfigDiskSignature"/> (CUST-312): path, length and last-write time of <c>config.yaml</c> and <c>.env</c>, taken with a stat.
/// <c>.env</c> holds secrets, so the properties that matter most are negative: no content in the type, and no file opened to take one.
/// </summary>
public sealed class ConfigDiskSignatureTests
{
    private static readonly DateTime T0 = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DefenseClawPaths Paths(string directory) =>
        new(dataDirectory: directory, binDirectory: Path.Combine(directory, "no-such-bin"), searchPath: Array.Empty<string>());

    [Fact]
    public void A_file_that_is_not_there_has_a_stamp_that_says_so()
    {
        using var temp = new TempDirectory();

        var stamp = FileStamp.Capture(temp.File("config.yaml"));

        Assert.False(stamp.Exists);
        Assert.Equal(0, stamp.Length);
        Assert.Equal(default, stamp.LastWriteUtc);
        Assert.Equal(temp.File("config.yaml"), stamp.Path);
    }

    [Fact]
    public void A_directory_of_that_name_is_not_a_file_either()
    {
        using var temp = new TempDirectory();
        _ = Directory.CreateDirectory(temp.File(".env"));

        Assert.False(FileStamp.Capture(temp.File(".env")).Exists);
    }

    [Fact]
    public void A_stamp_is_the_path_the_length_and_the_last_write_time_and_only_those()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("config.yaml", "mode: observe\n");
        File.SetLastWriteTimeUtc(path, T0);

        var stamp = FileStamp.Capture(path);

        Assert.True(stamp.Exists);
        Assert.Equal(path, stamp.Path);
        Assert.Equal(14, stamp.Length);
        Assert.Equal(T0, stamp.LastWriteUtc);

        // Nothing that could carry a byte of the content, or a hash of it: the whole shape of both types.
        Assert.Equal(
            new[] { "Exists", "LastWriteUtc", "Length", "Path" },
            typeof(FileStamp).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).Order().ToArray());
        Assert.Equal(
            new[] { "Config", "Env" },
            typeof(ConfigDiskSignature).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).Order().ToArray());
        Assert.All(
            typeof(FileStamp).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            f => Assert.DoesNotContain(f.FieldType, new[] { typeof(byte[]), typeof(string[]), typeof(ulong), typeof(Stream) }));
    }

    [Fact]
    public void A_stamp_changes_when_the_length_changes_when_the_time_changes_and_when_the_file_comes_or_goes()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.yaml");
        var missing = FileStamp.Capture(path);

        File.WriteAllText(path, "mode: observe\n");
        File.SetLastWriteTimeUtc(path, T0);
        var first = FileStamp.Capture(path);
        Assert.NotEqual(missing, first);
        Assert.Equal(first, FileStamp.Capture(path)); // looking again changes nothing

        // Same length, same content, a later write: the time alone is a change.
        File.SetLastWriteTimeUtc(path, T0.AddSeconds(1));
        var touched = FileStamp.Capture(path);
        Assert.NotEqual(first, touched);
        Assert.Equal(first.Length, touched.Length);

        // Another length, the old time.
        File.WriteAllText(path, "mode: observe\n# longer\n");
        File.SetLastWriteTimeUtc(path, T0);
        var longer = FileStamp.Capture(path);
        Assert.NotEqual(first, longer);
        Assert.True(longer.Length > first.Length);

        File.Delete(path);
        Assert.Equal(missing, FileStamp.Capture(path));
    }

    [Fact]
    public void The_signature_of_an_installation_is_its_config_yaml_and_its_env()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp.Path);
        _ = temp.Write("config.yaml", "mode: observe\n");

        var before = ConfigDiskSignature.Capture(paths);
        Assert.Equal(paths.ConfigFilePath, before.Config.Path);
        Assert.Equal(paths.EnvFilePath, before.Env.Path);
        Assert.True(before.Config.Exists);
        Assert.False(before.Env.Exists);
        Assert.Equal(before, ConfigDiskSignature.Capture(paths.ConfigFilePath, paths.EnvFilePath));
        Assert.Equal(before, ConfigDiskSignature.Capture(paths));

        // Either file moving moves the signature; a file beside them does not.
        _ = temp.Write("audit.db-notes.txt", "unrelated");
        Assert.Equal(before, ConfigDiskSignature.Capture(paths));

        var env = temp.Write(".env", "EXAMPLE_NAME=1\n");
        File.SetLastWriteTimeUtc(env, T0);
        var withEnv = ConfigDiskSignature.Capture(paths);
        Assert.NotEqual(before, withEnv);
        Assert.Equal(before.Config, withEnv.Config);

        File.SetLastWriteTimeUtc(paths.ConfigFilePath, T0.AddMinutes(1));
        var edited = ConfigDiskSignature.Capture(paths);
        Assert.NotEqual(withEnv, edited);
        Assert.Equal(withEnv.Env, edited.Env);
    }

    [Fact]
    public void Taking_a_stamp_never_opens_the_file_so_a_locked_env_is_looked_at_without_being_read()
    {
        using var temp = new TempDirectory();
        var path = temp.Write(".env", "EXAMPLE_NAME=1\n");

        // Held open for exclusive use: an open for reading, a hash, a copy would all fail on the sharing violation. A stat does not.
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => File.ReadAllBytes(path));

        var stamp = FileStamp.Capture(path);

        Assert.True(stamp.Exists);
        Assert.Equal(15, stamp.Length); // "EXAMPLE_NAME=1" and the newline
        Assert.Equal(FileStamp.Capture(path), stamp);
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("")]
    public void A_path_the_file_system_will_not_take_is_a_stamp_not_an_exception(string path)
    {
        var stamp = FileStamp.Capture(path);

        Assert.False(stamp.Exists);
        Assert.Equal(-1, stamp.Length);
        Assert.Equal(stamp, FileStamp.Capture(path)); // and it is the same answer every time, so it is not a change
    }
}
