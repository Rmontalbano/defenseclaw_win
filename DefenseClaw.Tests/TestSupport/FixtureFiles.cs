namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// Locates the canned payloads copied next to the test assembly. Everything the suite
/// reads comes from here — no test ever touches the live <c>~/.defenseclaw</c> install.
/// </summary>
public static class FixtureFiles
{
    public static string Directory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string PathTo(string fileName)
    {
        var path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture '{fileName}' was not copied to the output directory ({Directory}).", path);
        }

        return path;
    }

    public static string ReadText(string fileName) => File.ReadAllText(PathTo(fileName));

    public const string Health = "health.json";
    public const string Status = "status.json";
    public const string Alerts = "alerts.json";
    public const string Skills = "skills.json";
    public const string Mcps = "mcps.json";
    public const string ToolsCatalog = "tools-catalog.json";
    public const string EnforceBlocked = "enforce-blocked.json";
    public const string EnforceAllowed = "enforce-allowed.json";
    public const string AuditSchema = "audit-schema.sql";
    public const string ConfigYaml = "config.yaml";
}

/// <summary>Self-deleting scratch directory for tests that need real files on disk.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "dcw-test")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():n}");
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, string contents)
    {
        var path = File(name);
        System.IO.File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Path))
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A watcher or SQLite pool may still hold a handle; leaving a temp dir behind
            // is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
