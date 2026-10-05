namespace DefenseClaw.Tests;

/// <summary>
/// What the app says about verifying an update has to be what it does. It used to call checksums.txt "signed" when it only noticed that signature
/// files were published, and to say the download was "verified against the signed checksums.txt" when it compared it with a file from the same release.
/// These read the shipped text - the README and every window and view-model string of the update path - from the repository, so an overclaim cannot
/// come back unnoticed, and they pin the honest wording the fix put there.
/// </summary>
public sealed class UpdateClaimsTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DefenseClaw.Win.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"DefenseClaw.Win.sln was not found above {AppContext.BaseDirectory}.");
    }

    private static string Read(params string[] relative) => File.ReadAllText(Path.Combine(new[] { Root }.Concat(relative).ToArray()));

    /// <summary>Everything a user can read about the update path: the README, the two windows that describe it, and the code that builds their words.</summary>
    private static readonly (string Name, string[] Path)[] ShippedText =
    {
        ("README.md", new[] { "README.md" }),
        ("UpdatesWindow.xaml", new[] { "DefenseClaw.App", "Views", "Updates", "UpdatesWindow.xaml" }),
        ("FirstRunWindow.xaml", new[] { "DefenseClaw.App", "Views", "FirstRun", "FirstRunWindow.xaml" }),
        ("UpgradeSectionViewModel.cs", new[] { "DefenseClaw.App", "ViewModels", "Updates", "UpgradeSectionViewModel.cs" }),
        ("UpdatesWindowViewModel.cs", new[] { "DefenseClaw.App", "ViewModels", "Updates", "UpdatesWindowViewModel.cs" }),
        ("UpgradeRunner.cs", new[] { "DefenseClaw.App", "Services", "Updates", "UpgradeRunner.cs" }),
        ("ProvenanceInspector.cs", new[] { "DefenseClaw.App", "Services", "Updates", "ProvenanceInspector.cs" }),
        ("ChecksumsSignatureVerifier.cs", new[] { "DefenseClaw.App", "Services", "Updates", "ChecksumsSignatureVerifier.cs" }),
    };

    [Theory]
    [InlineData("signed checksums")]
    [InlineData("sigstore-signed")]
    [InlineData("correctly signed")]
    [InlineData("(correctly) signed")]
    [InlineData("signed with sigstore")]
    [InlineData("signed `checksums.txt`")]
    [InlineData("signed checksums.txt")]
    [InlineData("anchored to anything signed")]
    public void No_shipped_text_calls_a_checksums_file_signed_as_a_fact(string overclaim)
    {
        foreach (var (name, path) in ShippedText)
        {
            Assert.False(Read(path).Contains(overclaim, StringComparison.OrdinalIgnoreCase), $"{name} still says \"{overclaim}\"");
        }
    }

    [Fact]
    public void The_README_says_what_the_update_path_checks_and_what_it_does_not()
    {
        var readme = Read("README.md");
        var bullet = readme.Split('\n').Single(l => l.StartsWith("- **Update awareness**", StringComparison.Ordinal));

        Assert.Contains("compares the file's SHA-256 with the `checksums.txt` of the same release", bullet, StringComparison.Ordinal);
        Assert.Contains("if `cosign` is installed", bullet, StringComparison.Ordinal);
        Assert.Contains("verifies that file's sigstore signature", bullet, StringComparison.Ordinal);
        Assert.Contains("a signature cosign rejects stops the upgrade", bullet, StringComparison.Ordinal);
        Assert.Contains("published but *not verified*", bullet, StringComparison.Ordinal);
        Assert.Contains("only shows the download matches that file", bullet, StringComparison.Ordinal);
        Assert.Contains("not Authenticode-signed", bullet, StringComparison.Ordinal);
    }

    [Fact]
    public void The_updates_window_says_the_signature_is_checked_with_cosign_when_it_is_installed()
    {
        var xaml = Read("DefenseClaw.App", "Views", "Updates", "UpdatesWindow.xaml");

        Assert.Contains("compare its SHA-256 with that release's checksums.txt (and, when cosign is installed, verify that file's sigstore signature first)", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_run_window_says_the_same()
    {
        var xaml = Read("DefenseClaw.App", "Views", "FirstRun", "FirstRunWindow.xaml");

        Assert.Contains("compares its SHA-256 with the release's checksums.txt (and verifies that file's sigstore signature when cosign is installed)", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cosign_command_the_app_builds_carries_the_exact_upstream_identity_and_never_a_pattern_or_a_flag_that_turns_the_checks_off()
    {
        var verifier = Read("DefenseClaw.App", "Services", "Updates", "ChecksumsSignatureVerifier.cs");

        Assert.Contains("\"--certificate-identity\"", verifier, StringComparison.Ordinal);
        Assert.Contains("\"--certificate-oidc-issuer\"", verifier, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/release.yaml@refs/heads/main", verifier, StringComparison.Ordinal);
        Assert.Contains("https://token.actions.githubusercontent.com", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("insecure-ignore", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--certificate-identity-regexp", verifier, StringComparison.Ordinal);
    }
}
