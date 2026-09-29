namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>Self-deleting scratch directory, so no test writes into the real <c>~/.defenseclaw</c>.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dc-app-tests-" + Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string WriteFile(string name, string content)
    {
        var path = File(name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A watcher may still hold the folder for a moment; a stray temp directory is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
