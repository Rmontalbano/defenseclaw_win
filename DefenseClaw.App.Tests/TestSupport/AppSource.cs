using System.Text.RegularExpressions;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// The <c>DefenseClaw.App</c> source tree this test assembly was built from, for the tests that read its XAML or code as text.
/// A file is found by its name and a set of views by its <c>x:Class</c> namespace, never by the folder it sits in, so moving
/// files between folders (CUST-387) cannot break such a test or, worse, leave it passing on nothing: <see cref="Find"/> throws
/// when a name matches no file or more than one.
/// </summary>
internal static partial class AppSource
{
    /// <summary>The project folder: the one holding <c>DefenseClaw.App.csproj</c>, found above the test output directory.</summary>
    public static string ProjectDirectory { get; } = FindProjectDirectory();

    /// <summary>The one source file called <paramref name="fileName"/> anywhere in the project (bin and obj excluded).</summary>
    public static string Find(string fileName)
    {
        var matches = Files(fileName).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"{fileName} was not found under {ProjectDirectory}."),
            _ => throw new InvalidOperationException($"{fileName} names {matches.Count} files under {ProjectDirectory}: {string.Join(", ", matches)}."),
        };
    }

    /// <summary>The text of the one source file called <paramref name="fileName"/>.</summary>
    public static string Read(string fileName) => File.ReadAllText(Find(fileName));

    /// <summary>Every XAML file of the project (bin and obj excluded).</summary>
    public static IEnumerable<string> Xaml() => Files("*.xaml");

    /// <summary>
    /// The XAML files whose <c>x:Class</c> is declared in one of <paramref name="namespaces"/> or a namespace below it
    /// (<c>DefenseClaw.App.Views.Panels</c> also takes <c>DefenseClaw.App.Views.Panels.Govern</c>). Namespaces stay put when
    /// files move, so this names the same views wherever their folders are.
    /// </summary>
    public static IEnumerable<string> XamlInNamespaces(params string[] namespaces) =>
        Xaml().Where(file => XClass().Match(File.ReadAllText(file)) is { Success: true } match
                             && namespaces.Any(ns => match.Groups["ns"].Value == ns || match.Groups["ns"].Value.StartsWith(ns + ".", StringComparison.Ordinal)));

    private static IEnumerable<string> Files(string pattern)
    {
        var bin = Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar;
        var obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(ProjectDirectory, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(bin, StringComparison.Ordinal) && !f.Contains(obj, StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private static string FindProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "DefenseClaw.App.csproj");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new FileNotFoundException("DefenseClaw.App\\DefenseClaw.App.csproj was not found above the test output directory.");
    }

    [GeneratedRegex(@"\bx:Class=""(?<ns>[\w.]+)\.\w+""")]
    private static partial Regex XClass();
}
