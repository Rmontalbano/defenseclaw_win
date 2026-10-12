using System.Diagnostics;
using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin.Tests;

/// <summary>
/// The reading half of the real file system, on a scratch folder under the temp directory. It reads owners and access lists and opens files;
/// it changes nothing outside the scratch folder and needs no rights (a directory junction is made with <c>mklink /J</c>, which does not elevate).
/// </summary>
public sealed class RealReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-gwadmin-real-" + Guid.NewGuid().ToString("N"));
    private readonly ReadOnlyAdminFileSystem _fs = new();

    public RealReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            // A junction is removed as a link, so the target's contents are never touched.
            foreach (var dir in Directory.EnumerateDirectories(_root))
            {
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(dir);
                }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_file_reports_its_owner_an_access_list_and_a_length()
    {
        var path = Path.Combine(_root, "a.bin");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var node = _fs.Stat(path);

        Assert.True(node.Exists);
        Assert.False(node.IsDirectory);
        Assert.False(node.IsReparsePoint);
        Assert.Equal(3, node.Length);
        Assert.NotNull(node.Security);
        Assert.True(node.Security!.HasDacl);
        Assert.False(string.IsNullOrEmpty(node.Security.OwnerSid));
        Assert.NotEmpty(node.Security.Entries);
        Assert.All(node.Security.Entries, e => Assert.True(AdminLayout.IsValidSid(e.Sid), e.Sid));
    }

    [Fact]
    public void A_missing_path_is_missing_and_a_folder_is_a_folder()
    {
        Assert.False(_fs.Stat(Path.Combine(_root, "nope")).Exists);
        var folder = _fs.Stat(_root);
        Assert.True(folder.Exists);
        Assert.True(folder.IsDirectory);
    }

    [Fact]
    public void A_file_can_be_read_exclusively_and_nobody_can_write_to_it_meanwhile()
    {
        var path = Path.Combine(_root, "b.bin");
        File.WriteAllBytes(path, new byte[] { 9, 9, 9 });

        using var stream = _fs.OpenReadExclusive(path);

        Assert.Equal(Hex.HashOf(new MemoryStream(new byte[] { 9, 9, 9 })), Hex.HashOf(stream));
        Assert.Throws<IOException>(() => File.WriteAllBytes(path, new byte[] { 1 }));
        Assert.Throws<IOException>(() => File.Delete(path));
    }

    [Fact]
    public void A_junction_is_reported_as_a_reparse_point_and_is_not_read_through()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "x.bin"), new byte[] { 5 });
        var link = Path.Combine(_root, "link");
        if (!TryMakeJunction(link, target))
        {
            return; // Junctions are unavailable here (policy); the fake-based tests cover the logic.
        }

        var node = _fs.Stat(link);

        Assert.True(node.IsReparsePoint);
        Assert.Null(node.Security);
        Assert.Throws<IOException>(() => _fs.OpenReadExclusive(Path.Combine(link, "x.bin")));
    }

    [Fact]
    public void The_text_read_is_bounded()
    {
        var path = Path.Combine(_root, "t.json");
        File.WriteAllText(path, new string('x', 100));

        Assert.Equal(100, _fs.ReadText(path, 200)!.Length);
        Assert.Null(_fs.ReadText(path, 50));
        Assert.Null(_fs.ReadText(Path.Combine(_root, "none.json"), 50));
    }

    [Fact]
    public void The_apps_file_system_refuses_every_change()
    {
        Assert.Throws<NotSupportedException>(() => _fs.EnsureAdminFolder(Path.Combine(_root, "new")));
        Assert.Throws<NotSupportedException>(() => _fs.WriteText(Path.Combine(_root, "w.txt"), "x"));
        Assert.Throws<NotSupportedException>(() => _fs.DeleteFile(Path.Combine(_root, "w.txt")));
        Assert.Throws<NotSupportedException>(() => _fs.Replace("a", "b"));
        Assert.Throws<NotSupportedException>(() => _fs.CreateTemp(_root, out _));
        Assert.False(Directory.Exists(Path.Combine(_root, "new")));
    }

    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
