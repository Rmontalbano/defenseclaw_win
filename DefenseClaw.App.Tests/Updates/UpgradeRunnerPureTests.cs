using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.Tests.Updates;

public class UpgradeRunnerPureTests
{
    private const string Sha1 = "8f434346648f6b96df89dda901c5176b10a6d83961dd3c1ac88b59b2dc327aa4";
    private const string Sha2 = "e830172b08c86a62991d8f3fa916dffd3e9ad90fca809f5e685e38d8b4555c2c";

    // ------------------------------------------------------------------ checksums.txt

    [Fact]
    public void A_plain_entry_is_found_by_the_asset_name()
    {
        var text = $"{Sha1}  DefenseClawSetup-x64.exe\n{Sha2}  defenseclaw-upgrade.ps1\n";

        Assert.Equal(Sha1, UpgradeRunner.FindChecksumEntry(text, "DefenseClawSetup-x64.exe"));
        Assert.Equal(Sha2, UpgradeRunner.FindChecksumEntry(text, "defenseclaw-upgrade.ps1"));
    }

    [Fact]
    public void The_binary_mode_marker_is_ignored()
    {
        var text = $"{Sha1} *DefenseClawSetup-x64.exe\n";

        Assert.Equal(Sha1, UpgradeRunner.FindChecksumEntry(text, "DefenseClawSetup-x64.exe"));
    }

    [Theory]
    [InlineData("dist/windows/DefenseClawSetup-x64.exe")]
    [InlineData("dist\\windows\\DefenseClawSetup-x64.exe")]
    [InlineData("./DefenseClawSetup-x64.exe")]
    [InlineData(".\\DefenseClawSetup-x64.exe")]
    [InlineData("*dist/DefenseClawSetup-x64.exe")]
    [InlineData("/abs/path/DefenseClawSetup-x64.exe")]
    public void A_path_in_front_of_the_name_is_stripped_in_either_slash_direction(string listedName)
    {
        var text = $"{Sha1}  {listedName}\n";

        Assert.Equal(Sha1, UpgradeRunner.FindChecksumEntry(text, "DefenseClawSetup-x64.exe"));
    }

    [Theory]
    [InlineData("DEFENSECLAWSETUP-X64.EXE")]
    [InlineData("defenseclawsetup-x64.exe")]
    [InlineData("DefenseClawSetup-X64.exe")]
    public void The_name_is_matched_ignoring_case(string listedName)
    {
        Assert.Equal(Sha1, UpgradeRunner.FindChecksumEntry($"{Sha1}  {listedName}\n", "DefenseClawSetup-x64.exe"));
    }

    [Fact]
    public void The_hash_is_returned_lower_case_however_the_file_spells_it()
    {
        var text = $"{Sha1.ToUpperInvariant()}  DefenseClawSetup-x64.exe\n";

        Assert.Equal(Sha1, UpgradeRunner.FindChecksumEntry(text, "DefenseClawSetup-x64.exe"));
    }

    [Fact]
    public void Windows_line_endings_comments_and_blank_lines_are_tolerated()
    {
        var text = "# checksums for v0.8.10\r\n\r\n" +
                   $"{Sha2}  defenseclaw-upgrade.ps1\r\n" +
                   "   \r\n" +
                   $"\t{Sha1}\t*DefenseClawSetup-x64.exe\r\n";

        Assert.Equal(Sha1, UpgradeRunner.FindChecksumEntry(text, "DefenseClawSetup-x64.exe"));
        Assert.Equal(Sha2, UpgradeRunner.FindChecksumEntry(text, "defenseclaw-upgrade.ps1"));
    }

    [Theory]
    [InlineData("DefenseClawSetup-x64.exe.sig")]
    [InlineData("xDefenseClawSetup-x64.exe")]
    [InlineData("DefenseClawSetup-x64.exe.partial")]
    [InlineData("DefenseClawSetup-arm64.exe")]
    public void A_name_that_only_contains_the_asset_name_is_a_different_file(string listedName)
    {
        Assert.Null(UpgradeRunner.FindChecksumEntry($"{Sha1}  {listedName}\n", "DefenseClawSetup-x64.exe"));
    }

    [Fact]
    public void A_missing_entry_an_empty_file_and_a_bare_hash_line_all_find_nothing()
    {
        Assert.Null(UpgradeRunner.FindChecksumEntry($"{Sha1}  other.exe\n", "DefenseClawSetup-x64.exe"));
        Assert.Null(UpgradeRunner.FindChecksumEntry(string.Empty, "DefenseClawSetup-x64.exe"));
        Assert.Null(UpgradeRunner.FindChecksumEntry(Sha1 + "\n", "DefenseClawSetup-x64.exe"));
        Assert.Null(UpgradeRunner.FindChecksumEntry("# only a comment\n", "DefenseClawSetup-x64.exe"));
    }

    [Fact]
    public void A_commented_out_entry_is_not_an_entry_and_the_first_real_one_wins()
    {
        var text = $"# {Sha2}  DefenseClawSetup-x64.exe\n{Sha1}  DefenseClawSetup-x64.exe\n{Sha2}  DefenseClawSetup-x64.exe\n";

        Assert.Equal(Sha1, UpgradeRunner.FindChecksumEntry(text, "DefenseClawSetup-x64.exe"));
    }

    // ------------------------------------------------------------------ argv

    [Fact]
    public void The_installer_runs_quiet_without_a_reboot_and_in_user_scope()
    {
        Assert.Equal(new[] { "/quiet", "/norestart", "INSTALLSCOPE=user" }, UpgradeRunner.BuildInstallerArgv());
    }

    [Fact]
    public void The_resolver_runs_under_powershell_with_no_profile_a_bypassed_policy_and_the_yes_flag_last()
    {
        var argv = UpgradeRunner.BuildArgv("C:\\stage\\defenseclaw-upgrade.ps1");

        Assert.Equal(
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\stage\\defenseclaw-upgrade.ps1", "-Yes" },
            argv);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_resolver_needs_a_script_path(string path)
    {
        Assert.Throws<ArgumentException>(() => UpgradeRunner.BuildArgv(path));
    }

    [Fact]
    public void The_installer_command_is_shown_with_its_path_quoted_and_the_same_flags_that_run()
    {
        var text = UpgradeRunner.DescribeCommand(UpgradeChannel.SetupInstaller, "C:\\Users\\a b\\DefenseClawSetup-x64.exe");

        Assert.Equal("\"C:\\Users\\a b\\DefenseClawSetup-x64.exe\" /quiet /norestart INSTALLSCOPE=user", text);
    }

    [Fact]
    public void The_resolver_command_is_shown_through_powershell_with_the_same_argv_that_runs()
    {
        var text = UpgradeRunner.DescribeCommand(UpgradeChannel.ResolverScript, "C:\\stage dir\\defenseclaw-upgrade.ps1");

        Assert.Contains("powershell.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("-NoProfile -ExecutionPolicy Bypass -File \"C:\\stage dir\\defenseclaw-upgrade.ps1\" -Yes", text, StringComparison.Ordinal);
        Assert.Equal(text, UpgradeRunner.DescribeCommand("C:\\stage dir\\defenseclaw-upgrade.ps1"));
    }

    [Fact]
    public void Asset_urls_point_at_the_release_download_and_escape_the_tag()
    {
        Assert.Equal(
            "https://github.com/cisco-ai-defense/defenseclaw/releases/download/v0.8.10/DefenseClawSetup-x64.exe",
            UpgradeRunner.AssetUrl("v0.8.10", "DefenseClawSetup-x64.exe").AbsoluteUri);
        Assert.Equal(
            "https://github.com/cisco-ai-defense/defenseclaw/releases/download/v1.2.3-rc.1/checksums.txt",
            UpgradeRunner.AssetUrl(" v1.2.3-rc.1 ", "checksums.txt").AbsoluteUri);
    }

    [Theory]
    [InlineData("v0.8.10")]
    [InlineData("0.8.10")]
    [InlineData("1.0.0-rc.1")]
    [InlineData("v1.2.3+build.5")]
    [InlineData("12")]
    public void A_plain_version_is_accepted(string version) => Assert.True(UpgradeRunner.IsValidReleaseVersion(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v1/../x")]
    [InlineData("v1%2f..%2fx")]
    [InlineData("v1\\x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("v1.2.3 ; rm")]
    [InlineData("v1.2.3-")]
    [InlineData("v1.2.3?x=1")]
    [InlineData("v1.2.3#frag")]
    [InlineData("..")]
    [InlineData("v1.2.3-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Anything_else_is_not_a_version(string? version)
    {
        Assert.False(UpgradeRunner.IsValidReleaseVersion(version));
        if (!string.IsNullOrWhiteSpace(version))
        {
            Assert.Throws<ArgumentException>(() => UpgradeRunner.AssetUrl(version, "checksums.txt"));
        }
    }

    // ------------------------------------------------------------------ which channel to recommend

    [Fact]
    public void A_machine_with_the_venv_the_resolver_needs_is_recommended_the_resolver()
    {
        var probed = new List<string>();

        var layout = UpgradeRunner.DetectResolverLayout(
            fileExists: path =>
            {
                probed.Add(path);
                return true;
            },
            directoryExists: _ => throw new InvalidOperationException("the runtime directory is not needed once the venv exists"));

        Assert.Equal(UpgradeChannel.ResolverScript, layout.RecommendedChannel);
        Assert.True(layout.ResolverLayoutPresent);
        Assert.Equal(new[] { UpgradeRunner.ResolverVenvPythonPath() }, probed);
        Assert.Contains(UpgradeRunner.ResolverVenvPythonPath(), layout.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_setup_install_without_the_venv_is_recommended_the_installer()
    {
        var layout = UpgradeRunner.DetectResolverLayout(fileExists: _ => false, directoryExists: _ => true);

        Assert.Equal(UpgradeChannel.SetupInstaller, layout.RecommendedChannel);
        Assert.False(layout.ResolverLayoutPresent);
        Assert.Contains("Managed Python not found", layout.Detail, StringComparison.Ordinal);
        Assert.Contains("the managed runtime is under " + UpgradeRunner.SetupRuntimeDirectory(), layout.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_venv_and_without_a_runtime_directory_the_installer_is_still_the_channel_and_the_detail_says_where_it_would_be()
    {
        var layout = UpgradeRunner.DetectResolverLayout(fileExists: _ => false, directoryExists: _ => false);

        Assert.Equal(UpgradeChannel.SetupInstaller, layout.RecommendedChannel);
        Assert.Contains("native Setup installs put the managed runtime under " + UpgradeRunner.SetupRuntimeDirectory(), layout.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_venv_python_path_decides_the_channel()
    {
        var layout = UpgradeRunner.DetectResolverLayout(
            fileExists: path => path.EndsWith("cosign.exe", StringComparison.OrdinalIgnoreCase),
            directoryExists: _ => true);

        Assert.Equal(UpgradeChannel.SetupInstaller, layout.RecommendedChannel);
    }

    [Fact]
    public void The_two_probe_paths_are_the_ones_the_resolver_and_the_setup_installer_use()
    {
        Assert.EndsWith(Path.Combine(".defenseclaw", ".venv", "Scripts", "python.exe"), UpgradeRunner.ResolverVenvPythonPath(), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("Programs", "DefenseClaw", "runtime"), UpgradeRunner.SetupRuntimeDirectory(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ cosign

    [Fact]
    public void Cosign_on_the_path_a_child_would_inherit_is_usable()
    {
        var status = UpgradeRunner.DetectCosign(
            processPath: new[] { "C:\\tools", "C:\\bin" },
            offPathDirectories: Array.Empty<string>(),
            fileExists: path => path == Path.Combine("C:\\bin", "cosign.exe"));

        Assert.Equal(CosignAvailability.OnPath, status.Availability);
        Assert.True(status.IsUsable);
        Assert.Equal(Path.Combine("C:\\bin", "cosign.exe"), status.Path);
    }

    [Fact]
    public void Cosign_installed_after_the_app_started_is_found_off_path_and_says_to_restart()
    {
        var status = UpgradeRunner.DetectCosign(
            processPath: new[] { "C:\\tools" },
            offPathDirectories: new[] { "C:\\winget\\links" },
            fileExists: path => path == Path.Combine("C:\\winget\\links", "cosign.exe"));

        Assert.Equal(CosignAvailability.FoundOffPath, status.Availability);
        Assert.False(status.IsUsable);
        Assert.Contains("Restart", status.InstallGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Cosign_missing_everywhere_says_how_to_install_it()
    {
        var status = UpgradeRunner.DetectCosign(
            processPath: new[] { "C:\\tools", "", "  " },
            offPathDirectories: new[] { "C:\\winget\\links" },
            fileExists: _ => false);

        Assert.Equal(CosignAvailability.Missing, status.Availability);
        Assert.False(status.IsUsable);
        Assert.Contains("winget install Sigstore.Cosign", status.InstallGuidance, StringComparison.Ordinal);
    }
}
