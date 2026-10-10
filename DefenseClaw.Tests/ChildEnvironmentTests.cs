using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// A child that is not the DefenseClaw CLI or gateway does not inherit the gateway token or provider keys (CUST-251). Synthetic variable
/// names only; the one process started is the system's cmd.exe printing its own environment.
/// </summary>
public sealed class ChildEnvironmentTests
{
    [Theory]
    [InlineData("DEFENSECLAW_GATEWAY_TOKEN")]
    [InlineData("defenseclaw_gateway_token")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("MY_SERVICE_SECRET")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("DB_PASSWORD")]
    [InlineData("TOKEN")]
    [InlineData("api_key")]
    public void Secret_looking_names_are_recognised(string name) => Assert.True(ChildEnvironment.IsSecretName(name));

    [Theory]
    [InlineData("PATH")]
    [InlineData("USERPROFILE")]
    [InlineData("DEFENSECLAW_HOME")]
    [InlineData("DEFENSECLAW_GATEWAY_HOST")]
    [InlineData("TOKENIZER_PATH")]
    [InlineData("KEYBOARD")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_names_are_kept(string? name) => Assert.False(ChildEnvironment.IsSecretName(name));

    [Fact]
    public void Stripping_removes_only_the_secrets_and_names_what_it_removed()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = "x",
            ["DEFENSECLAW_GATEWAY_TOKEN"] = "synthetic-1",
            ["FOO_API_KEY"] = "synthetic-2",
            ["DEFENSECLAW_HOME"] = "h",
        };

        var removed = ChildEnvironment.StripSecrets(env);

        Assert.Equal(new[] { "DEFENSECLAW_GATEWAY_TOKEN", "FOO_API_KEY" }, removed.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "DEFENSECLAW_HOME", "PATH" }, env.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_non_defenseclaw_child_does_not_inherit_the_secrets_but_keeps_the_rest()
    {
        const string SecretName = "DCTEST_CHILDENV_API_KEY";
        const string KeepName = "DCTEST_CHILDENV_KEEP";
        Environment.SetEnvironmentVariable(SecretName, "synthetic-secret-value");
        Environment.SetEnvironmentVariable(KeepName, "synthetic-keep-value");
        try
        {
            using var temp = new TempDirectory();
            var runner = new CliRunner(new DefenseClawPaths(
                dataDirectory: temp.Path,
                binDirectory: Path.Combine(temp.Path, "no-such-bin"),
                searchPath: Array.Empty<string>()));

            var run = await runner.RunExecutableAsync(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                new[] { "/c", "set" });

            var text = string.Join("\n", run.OutputLines.Select(line => line.Text));
            Assert.Equal(0, run.ExitCode);
            Assert.Contains(KeepName + "=synthetic-keep-value", text, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretName, text, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-secret-value", text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecretName, null);
            Environment.SetEnvironmentVariable(KeepName, null);
        }
    }
}
