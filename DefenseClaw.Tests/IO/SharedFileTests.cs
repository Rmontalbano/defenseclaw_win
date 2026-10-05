using System.Text;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.IO;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests.IO;

/// <summary>
/// The CLI and the gateway rewrite config.yaml, .env and the discovery files while the app may be reading them. A reader that opens with
/// File.ReadAllText's FileShare.Read makes such a save fail with a sharing violation (seen on CI as a test's own write of config.yaml failing
/// while the config watcher was reloading it). SharedFile must never be the reason a writer fails.
/// </summary>
public sealed class SharedFileTests
{
    [Fact]
    public void A_writer_can_rewrite_or_delete_the_file_while_it_is_open_for_reading()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("config.yaml", "gateway:\n  api_port: 18970\n");

        using (var reading = SharedFile.Open(path, FileOptions.None))
        {
            // A save that rewrites in place (Python's open(path, "w")) goes through.
            File.WriteAllText(path, "gateway:\n  api_port: 18971\n");
        }

        Assert.Contains("18971", SharedFile.ReadAllText(path), StringComparison.Ordinal);

        using (var reading = SharedFile.Open(path, FileOptions.None))
        {
            // So does a delete (it completes when the last handle closes). A rename *over* the file is the one thing Windows refuses
            // while any handle is open, whatever the sharing, which is why every SharedFile read opens, reads to the end and closes.
            File.Delete(path);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_plain_read_handle_blocks_the_same_writer_which_is_why_the_shared_one_exists()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("config.yaml", "a: 1\n");

        using var reading = File.OpenRead(path);
        Assert.Throws<IOException>(() => File.WriteAllText(path, "a: 2\n"));
    }

    [Fact]
    public async Task Text_reads_match_File_ReadAllText_with_and_without_a_byte_order_mark()
    {
        using var temp = new TempDirectory();
        var plain = temp.Write("plain.yaml", "naïve: ✓\n");
        var withBom = temp.File("bom.yaml");
        File.WriteAllText(withBom, "naïve: ✓\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        foreach (var path in new[] { plain, withBom })
        {
            Assert.Equal(File.ReadAllText(path), SharedFile.ReadAllText(path));
            Assert.Equal(File.ReadAllText(path), await SharedFile.ReadAllTextAsync(path));
            Assert.Equal(File.ReadAllBytes(path), SharedFile.ReadAllBytes(path));
            Assert.Equal(File.ReadAllBytes(path), await SharedFile.ReadAllBytesAsync(path));
        }
    }

    [Fact]
    public void A_missing_file_fails_the_way_File_ReadAllText_does()
    {
        using var temp = new TempDirectory();
        Assert.Throws<FileNotFoundException>(() => SharedFile.ReadAllText(temp.File("absent.yaml")));
    }

    [Fact]
    public void The_config_store_reads_through_the_shared_handle()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("config.yaml", "gateway:\n  api_port: 18970\n");

        // Held open by "the CLI" for writing while the store reads: the read must still succeed.
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var document = new ConfigStore(new DefenseClaw.Core.Paths.DefenseClawPaths(dataDirectory: temp.Path)).Load();
        Assert.Contains("18970", document.RawText, StringComparison.Ordinal);
    }
}
