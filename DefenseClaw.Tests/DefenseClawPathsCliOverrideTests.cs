using DefenseClaw.Core.Paths;

namespace DefenseClaw.Tests;

/// <summary>
/// "Use this defenseclaw.exe" (Settings → Connection, CUST-203): an override that replaces the PATH and install-directory lookup for the CLI
/// — and only the CLI — everywhere the paths answer, while the file is there, and never leaves the app without a CLI when it is not.
/// </summary>
public class DefenseClawPathsCliOverrideTests
{
    private const string FakeBin = @"C:\fake\Programs\DefenseClaw\bin";
    private const string FakePathEntry = @"C:\fake\on-path";
    private const string Chosen = @"D:\tools\dc\defenseclaw.exe";

    private static DefenseClawPaths Create(ISet<string> existing) =>
        new(
            binDirectory: FakeBin,
            searchPath: new[] { FakePathEntry },
            fileExists: existing.Contains);

    [Fact]
    public void Without_an_override_nothing_changes()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var paths = Create(new HashSet<string> { onPath });

        Assert.Null(paths.CliPathOverride);
        Assert.Equal(onPath, paths.CliPath);
    }

    [Fact]
    public async Task An_override_that_exists_wins_over_path_and_the_install_directory_for_every_kind_of_lookup()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var inBin = Path.Combine(FakeBin, "defenseclaw.exe");
        var paths = Create(new HashSet<string> { onPath, inBin, Chosen });

        paths.SetCliPathOverride(Chosen);

        Assert.Equal(Chosen, paths.CliPathOverride);
        Assert.Equal(Chosen, paths.CliPath);
        Assert.Equal(Chosen, paths.FindExecutable("defenseclaw"));
        Assert.Equal(Chosen, paths.FindExecutable("defenseclaw.exe"));
        Assert.Equal(Chosen, await paths.FindExecutableAsync("defenseclaw"));
        Assert.True(paths.TryGetKnownExecutable("defenseclaw", out var known));
        Assert.Equal(Chosen, known);
    }

    [Fact]
    public void Setting_it_takes_effect_at_once_even_after_the_old_answer_was_remembered()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var paths = Create(new HashSet<string> { onPath, Chosen });

        // The PATH answer is remembered for a minute ...
        Assert.Equal(onPath, paths.CliPath);

        // ... and the operator's choice does not wait for it to expire, nor does releasing the choice.
        paths.SetCliPathOverride(Chosen);
        Assert.Equal(Chosen, paths.CliPath);

        paths.SetCliPathOverride(null);
        Assert.Null(paths.CliPathOverride);
        Assert.Equal(onPath, paths.CliPath);

        paths.SetCliPathOverride("   ");
        Assert.Null(paths.CliPathOverride);
    }

    [Fact]
    public void An_override_whose_file_is_gone_is_ignored_so_the_app_is_never_left_without_a_cli()
    {
        var onPath = Path.Combine(FakePathEntry, "defenseclaw.exe");
        var existing = new HashSet<string> { onPath, Chosen };
        var paths = Create(existing);
        paths.SetCliPathOverride(Chosen);
        Assert.Equal(Chosen, paths.CliPath);

        // The drive is unplugged: the remembered answer is not trusted (the file is checked on every use), and the lookup carries on.
        _ = existing.Remove(Chosen);
        Assert.Equal(onPath, paths.CliPath);

        // A choice that never existed behaves the same way.
        var other = Create(new HashSet<string> { onPath });
        other.SetCliPathOverride(Chosen);
        Assert.Equal(onPath, other.CliPath);
        Assert.Equal(Chosen, other.CliPathOverride);
    }

    [Fact]
    public void Only_the_cli_is_affected_the_gateway_and_the_scanners_are_still_found_by_name()
    {
        var gateway = Path.Combine(FakeBin, "defenseclaw-gateway.exe");
        var scanner = Path.Combine(FakeBin, "skill-scanner.exe");
        var paths = Create(new HashSet<string> { gateway, scanner, Chosen });

        paths.SetCliPathOverride(Chosen);

        Assert.Equal(gateway, paths.GatewayCliPath);
        Assert.Equal(scanner, paths.SkillScannerPath);
        Assert.Null(paths.McpScannerPath);
    }

    [Fact]
    public void The_candidate_list_a_missing_cli_error_prints_starts_with_the_override()
    {
        var paths = Create(new HashSet<string>());
        paths.SetCliPathOverride(Chosen);

        var candidates = paths.CandidatesFor("defenseclaw").ToList();

        Assert.Equal(Chosen, candidates[0]);
        Assert.Equal(Path.Combine(FakePathEntry, "defenseclaw.exe"), candidates[1]);

        // Another executable's list is not touched.
        Assert.DoesNotContain(Chosen, paths.CandidatesFor("defenseclaw-gateway"));
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_choice_is_valid_it_clears_the_override(string? path) =>
        Assert.Null(DefenseClawPaths.CheckCliPathOverride(path, _ => false));

    [Theory]
    [InlineData(@"D:\tools\dc\defenseclaw.exe")]
    [InlineData(@"D:\tools\dc\DefenseClaw.EXE")]
    [InlineData(@"D:\tools\dc\defenseclaw")]
    [InlineData(@"  D:\tools\dc\defenseclaw.exe  ")]
    [InlineData(@"\\server\share\dc\defenseclaw.exe")]
    public void An_existing_file_named_defenseclaw_is_accepted(string path) =>
        Assert.Null(DefenseClawPaths.CheckCliPathOverride(path, _ => true));

    [Fact]
    public void A_file_that_is_not_there_is_refused()
    {
        var problem = DefenseClawPaths.CheckCliPathOverride(Chosen, _ => false);

        Assert.Equal("That file does not exist.", problem);
    }

    [Theory]
    [InlineData(@"D:\tools\dc\notepad.exe", "notepad.exe")]
    [InlineData(@"D:\tools\dc\defenseclaw-gateway.exe", "defenseclaw-gateway.exe")]
    [InlineData(@"D:\tools\dc\defenseclaw.exe.bak", "defenseclaw.exe.bak")]
    [InlineData(@"D:\tools\dc\defenseclaw.cmd", "defenseclaw.cmd")]
    public void Any_other_name_is_refused_and_the_message_names_it(string path, string name)
    {
        var problem = DefenseClawPaths.CheckCliPathOverride(path, _ => true);

        Assert.NotNull(problem);
        Assert.Contains("defenseclaw.exe", problem, StringComparison.Ordinal);
        Assert.Contains(name, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"defenseclaw.exe")]
    [InlineData(@"tools\defenseclaw.exe")]
    [InlineData(@"\tools\defenseclaw.exe")]
    public void A_path_that_is_not_absolute_is_refused(string path)
    {
        var problem = DefenseClawPaths.CheckCliPathOverride(path, _ => true);

        Assert.NotNull(problem);
        Assert.Contains("full path", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"D:\tools\dc\")]
    [InlineData(@"D:\")]
    public void A_folder_is_refused_with_a_message_that_says_so(string path)
    {
        var problem = DefenseClawPaths.CheckCliPathOverride(path, _ => true);

        Assert.NotNull(problem);
        Assert.Contains("folder", problem, StringComparison.Ordinal);
    }
}
