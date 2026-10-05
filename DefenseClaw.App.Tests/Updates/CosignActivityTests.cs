using System.Reflection;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The app's own signature check goes through the CLI runner, so it is in the Activity panel like every command. Its argv is not a DefenseClaw command
/// (<c>verify-blob …</c> is nothing the tier classifier knows, and an unknown verb is a change), so the row says what it is: a read.
/// </summary>
public sealed class CosignActivityTests
{
    private static ActivityRow Row(string executable, params string[] argv) =>
        new(
            (CliInvocation)Activator.CreateInstance(
                typeof(CliInvocation),
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                args: new object[] { executable, argv, DateTimeOffset.UtcNow, false },
                culture: null)!,
            runner: null,
            notify: _ => { });

    [Theory]
    [InlineData(@"C:\tools\cosign.exe", "version")]
    [InlineData(@"C:\tools\cosign.exe", "verify-blob", "--bundle", @"C:\s\checksums.txt.bundle", "--certificate-identity", "x", "--certificate-oidc-issuer", "y", @"C:\s\checksums.txt")]
    [InlineData(@"C:\Users\operator\AppData\Local\Microsoft\WinGet\Links\COSIGN.EXE", "verify-blob", "a")]
    public void The_apps_own_cosign_runs_are_shown_as_reads(string executable, params string[] argv)
    {
        var row = Row(executable, argv);

        Assert.Equal("Read-only", row.TierText);
        Assert.Equal("Neutral", row.TierKey);
    }

    [Theory]
    [InlineData(@"C:\tools\cosign.exe", "sign-blob", "x")]
    [InlineData(@"C:\tools\cosign.exe", "initialize")]
    [InlineData(@"C:\tools\cosign.exe")]
    [InlineData(@"C:\evil\not-cosign.exe", "verify-blob", "x")]
    [InlineData(@"C:\Users\operator\.local\bin\defenseclaw.exe", "verify-blob", "x")]
    public void Anything_else_is_classified_as_before(string executable, params string[] argv)
    {
        var row = Row(executable, argv);

        Assert.Equal("Changes state", row.TierText);
    }
}
