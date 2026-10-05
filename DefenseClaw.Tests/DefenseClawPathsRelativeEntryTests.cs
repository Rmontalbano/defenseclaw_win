using DefenseClaw.Core.Paths;

namespace DefenseClaw.Tests;

/// <summary>
/// A PATH entry that is not fully qualified is resolved against the process's current directory - wherever the app was started
/// from - so an executable planted there would be run as the DefenseClaw CLI. The lookup ignores such entries (cmd.exe does not).
/// </summary>
public class DefenseClawPathsRelativeEntryTests
{
    private const string FakeBin = @"C:\fake\Programs\DefenseClaw\bin";
    private const string FakePathEntry = @"C:\fake\on-path";

    [Theory]
    [InlineData(".")]
    [InlineData("bin")]
    [InlineData(@"..\tools")]
    [InlineData(@".\tools")]
    [InlineData(@"\no-drive")]
    [InlineData("C:drive-relative")]
    public void A_relative_path_entry_is_never_searched(string entry)
    {
        var probed = new List<string>();
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { entry },
            fileExists: candidate =>
            {
                probed.Add(candidate);
                return !Path.IsPathFullyQualified(candidate);
            },
            persistedSearchPath: Array.Empty<string>);

        Assert.Null(paths.FindExecutable("defenseclaw"));
        Assert.DoesNotContain(probed, candidate => !Path.IsPathFullyQualified(candidate));
        Assert.DoesNotContain(paths.CandidatesFor("defenseclaw"), candidate => !Path.IsPathFullyQualified(candidate));
    }

    [Fact]
    public void Fully_qualified_entries_around_a_relative_one_still_work_in_order()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");

        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { ".", FakePathEntry, "bin" },
            fileExists: candidate => candidate == onPath || !Path.IsPathFullyQualified(candidate),
            persistedSearchPath: Array.Empty<string>);

        Assert.Equal(onPath, paths.FindExecutable("defenseclaw"));
    }

    [Fact]
    public void A_relative_entry_the_registry_holds_is_ignored_when_the_search_path_is_refreshed()
    {
        var paths = new DefenseClawPaths(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: candidate => !Path.IsPathFullyQualified(candidate),
            persistedSearchPath: () => new[] { ".", @"\\server\share\tools" });

        paths.InvalidateExecutableCache();

        Assert.Null(paths.FindExecutable("defenseclaw"));
        Assert.DoesNotContain(paths.CandidatesFor("defenseclaw"), candidate => !Path.IsPathFullyQualified(candidate));
        Assert.Contains(paths.CandidatesFor("defenseclaw"), candidate => candidate.StartsWith(@"\\server\share\tools", StringComparison.OrdinalIgnoreCase));
    }
}
