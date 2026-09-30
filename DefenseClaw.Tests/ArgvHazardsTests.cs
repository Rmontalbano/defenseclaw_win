using System.Diagnostics;
using DefenseClaw.Core.Cli;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The Click-on-Windows argument expansion the reviewed argv has to be compared with. The parity theory replays
/// what the real Python 3.13 / Click 8.4.2 that ships with DefenseClaw 0.8.10 returned for the same tokens, in the
/// same environment, against the same files (<see cref="ArgvHazardsParityData"/>).
/// </summary>
public sealed class ArgvHazardsTests : IDisposable
{
    private readonly TempDirectory _cwd = new("dcw-glob");

    public ArgvHazardsTests()
    {
        foreach (var relative in ArgvHazardsParityData.Files)
        {
            var full = Path.Combine(_cwd.Path, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
        }
    }

    public void Dispose() => _cwd.Dispose();

    private static string? Env(string name) => ArgvHazardsParityData.Environment.GetValueOrDefault(name);

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void Expands_exactly_as_click_does_on_windows(string token, string[] expected)
    {
        var actual = ArgvHazards.Expand(token, _cwd.Path, Env);

        // Python lists a directory in the order the file system returns it; compare as sets so a volume that
        // orders differently does not fail a test about *what* is matched.
        Assert.Equal(
            expected.OrderBy(e => e, StringComparer.OrdinalIgnoreCase),
            actual.OrderBy(e => e, StringComparer.OrdinalIgnoreCase));
    }

    public static IEnumerable<object[]> ParityCases() => ArgvHazardsParityData.Cases();

    [Theory]
    [InlineData("config.yaml", false)]
    [InlineData("skill-name_1.2", false)]
    [InlineData("", false)]
    [InlineData("--reason", false)]
    [InlineData("x*", true)]
    [InlineData("x?", true)]
    [InlineData("[ab]", true)]
    [InlineData("%TEMP%", true)]
    [InlineData("$HOME", true)]
    [InlineData("${HOME}", true)]
    [InlineData("~", true)]
    [InlineData("~/x", true)]
    [InlineData("a~b", false)]
    [InlineData("https://example.com/a", false)]
    public void Only_a_token_with_a_wildcard_a_variable_marker_or_a_leading_tilde_is_hazardous(string token, bool expected) =>
        Assert.Equal(expected, ArgvHazards.IsHazardous(token));

    [Fact]
    public void Find_reports_each_hazardous_token_with_its_position_including_targets_after_the_terminator()
    {
        var found = ArgvHazards.Find(
            new[] { "skill", "block", "--reason", "50% off?", "--", "config.y?ml", "plain", "$FOO" },
            _cwd.Path,
            Env);

        Assert.Equal(new[] { 3, 5, 7 }, found.Select(h => h.Index));
        Assert.Equal(new[] { "50% off?", "config.y?ml", "$FOO" }, found.Select(h => h.Token));

        // A reason that merely contains "?" or "%" expands to itself; the target and the variable do not.
        Assert.Equal(new[] { false, true, true }, found.Select(h => h.Changes));
        Assert.Equal(new[] { "config.yaml" }, found[1].Expansion);
        Assert.Equal(new[] { "bar" }, found[2].Expansion);
    }

    [Fact]
    public void FindChanges_keeps_only_arguments_whose_expansion_differs()
    {
        var changes = ArgvHazards.FindChanges(new[] { "mcp", "block", "--", "config.y?ml" }, _cwd.Path, Env);
        var change = Assert.Single(changes);
        Assert.Equal(3, change.Index);
        Assert.Contains("“config.y?ml” would arrive as “config.yaml”", change.Describe(), StringComparison.Ordinal);

        Assert.Empty(ArgvHazards.FindChanges(new[] { "mcp", "block", "--", "nothing-matches-*" }, _cwd.Path, Env));
        Assert.Empty(ArgvHazards.FindChanges(new[] { "skill", "list", "--json" }, _cwd.Path, Env));
    }

    [Fact]
    public void A_wildcard_that_matches_several_files_says_how_many_arguments_it_becomes()
    {
        var change = Assert.Single(ArgvHazards.FindChanges(new[] { "--", "conf*" }, _cwd.Path, Env));

        Assert.Equal(3, change.Expansion.Count);
        Assert.Contains("(3 arguments)", change.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_directory_matches_nothing_so_a_wildcard_is_left_alone()
    {
        using var empty = new TempDirectory("dcw-empty");

        Assert.Empty(ArgvHazards.FindChanges(new[] { "--", "config.y?ml", "*", "**" }, empty.Path, Env));
    }

    [Fact]
    public void Reads_the_process_environment_when_none_is_supplied()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        Assert.NotNull(systemRoot);

        var hazard = Assert.Single(ArgvHazards.Find(new[] { "%SystemRoot%" }));

        Assert.Equal(new[] { systemRoot }, hazard.Expansion);
    }

    [Fact]
    public void An_absolute_pattern_is_matched_wherever_it_points()
    {
        var pattern = Path.Combine(_cwd.Path, "sub", "*.txt");

        var expansion = ArgvHazards.Expand(pattern, workingDirectory: Path.GetTempPath(), Env);

        Assert.Equal(new[] { Path.Combine(_cwd.Path, "sub", "a.txt") }, expansion, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_network_path_is_never_listed_so_a_review_cannot_stall_on_a_dead_share()
    {
        // 192.0.2.1 is TEST-NET-1: nothing answers. Listing it would take tens of seconds.
        var timer = Stopwatch.StartNew();

        var found = ArgvHazards.Find(new[] { @"\\192.0.2.1\tools\*", "//192.0.2.1/tools/*.exe" }, _cwd.Path, Env);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
        Assert.All(found, h => Assert.False(h.Changes));
    }

    [Fact]
    public void A_pattern_that_would_walk_a_whole_drive_gives_up_after_a_moment()
    {
        // Python would list every file on the drive; a review is built on the UI thread and must not.
        var everything = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "**");
        var timer = Stopwatch.StartNew();

        var found = ArgvHazards.Find(new[] { everything }, _cwd.Path, Env);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
        Assert.Single(found);
    }

    [Theory]
    [InlineData("defenseclaw", true)]
    [InlineData("defenseclaw.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Programs\DefenseClaw\bin\defenseclaw.exe", true)]
    [InlineData("DefenseClaw.EXE", true)]
    [InlineData("defenseclaw-gateway", false)]
    [InlineData(@"C:\bin\defenseclaw-gateway.exe", false)]
    [InlineData("cmd.exe", false)]
    [InlineData("powershell.exe", false)]
    public void Only_the_python_cli_expands_its_arguments(string executable, bool expected) =>
        Assert.Equal(expected, ArgvHazards.AppliesTo(executable));
}
