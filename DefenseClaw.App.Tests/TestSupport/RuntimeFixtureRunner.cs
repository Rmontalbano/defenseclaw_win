using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A probe runner that answers the runtime detector's questions from the help screens under <c>Fixtures/runtime-*</c> (shared with the Core suite),
/// so a test can give an isolated <see cref="DefenseClaw.App.Services.AppServices"/> the capabilities of 0.8.10 or of the pinned newer source
/// without starting a process. <c>set</c> is <c>0.8.10</c> or <c>95159fd</c>; a screen the set does not have fails like a CLI that does not know the command.
/// </summary>
internal static class RuntimeFixtureRunner
{
    public static RuntimeProbeRunner For(string set) => (arguments, _) =>
    {
        var file = string.Join(' ', arguments) switch
        {
            "--version-json" => "version.json",
            "--help" => "root.txt",
            "setup --help" => "setup.txt",
            "guardrail --help" => "guardrail.txt",
            "config --help" => "config.txt",
            "sandbox --help" => "sandbox.txt",
            "acp --help" => "acp.txt",
            "setup redaction --help" => "setup-redaction.txt",
            "agent discovery --help" => "agent-discovery.txt",
            "agent discovery runtime --help" => "agent-discovery-runtime.txt",
            _ => null,
        };

        var path = file is null ? null : Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-" + set, file);
        return Task.FromResult(path is not null && File.Exists(path)
            ? RuntimeProbeOutput.Ok(File.ReadAllText(path))
            : RuntimeProbeOutput.Fail("exit 2"));
    };
}
