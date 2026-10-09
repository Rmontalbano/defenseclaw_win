using System.Text.Json;
using DefenseClaw.App.Services.Guardrail;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Runtime;

/// <summary>
/// The fixed argv the app builds (the ones that are not generated from a <c>--help</c> screen at run time), held against the command tree of
/// DefenseClaw source commit 95159fd (<c>Fixtures/runtime-95159fd/cli/cli-tree.json</c>): every command path must still exist and every option
/// the app passes must still be one of that command's options. A flag the CLI renamed would otherwise only show up as a failed command in front
/// of an operator. The 0.8.10 half of this guarantee is that none of these argv changed.
/// </summary>
public sealed class ArgvContractTests
{
    private sealed record Option(string[] Names, bool Flag);

    private sealed record Command(string Path, bool Group, Option[] Options);

    private static readonly Command[] Tree = Load();

    private static Command[] Load()
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "cli", "cli-tree.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        return document.RootElement.GetProperty("commands").EnumerateArray().Select(c => new Command(
                c.GetProperty("path").GetString()!,
                c.GetProperty("group").GetBoolean(),
                c.GetProperty("options").EnumerateArray().Select(o => new Option(
                    o.GetProperty("names").EnumerateArray().Select(n => n.GetString()!).ToArray(),
                    o.GetProperty("flag").GetBoolean())).ToArray()))
            .ToArray();
    }

    /// <summary>Null when <paramref name="argv"/> is a command of the newer tree with only options it has; else why not.</summary>
    private static string? Check(IReadOnlyList<string> argv)
    {
        var depth = 0;
        Command? command = null;
        for (var n = Math.Min(argv.Count, 4); n >= 1; n--)
        {
            var path = string.Join(' ', argv.Take(n));
            if (Tree.FirstOrDefault(c => c.Path == path) is { } found)
            {
                command = found;
                depth = n;
                break;
            }
        }

        if (command is null)
        {
            return $"no such command: {string.Join(' ', argv)}";
        }

        for (var i = depth; i < argv.Count; i++)
        {
            var token = argv[i];
            if (token == "--")
            {
                break;
            }

            if (!token.StartsWith('-') || token.Length < 2)
            {
                continue;
            }

            var option = command.Options.FirstOrDefault(o => o.Names.Contains(token, StringComparer.Ordinal));
            if (option is null)
            {
                return $"'{command.Path}' has no option {token}";
            }

            if (!option.Flag)
            {
                i++;
            }
        }

        return null;
    }

    public static TheoryData<string> Argvs =>
        new()
        {
            // read through the app's own panels
            string.Join('\u001f', OverviewPanelViewModel.StatusArgv),
            string.Join('\u001f', ObservabilityPlanReader.Argv),
            string.Join('\u001f', CredentialsViewModel.ListArgv),
            string.Join('\u001f', OverviewPanelViewModel.DoctorArgv),
            string.Join('\u001f', new[] { "doctor", "--fix", "--dry-run" }),
            string.Join('\u001f', new[] { "doctor", "--fix", "--yes" }),
            string.Join('\u001f', new[] { "config", "validate" }),
            string.Join('\u001f', new[] { "skill", "list", "--json", "--connector", "claudecode" }),
            string.Join('\u001f', new[] { "mcp", "list", "--json", "--connector", "claudecode" }),
            string.Join('\u001f', new[] { "plugin", "list", "--json", "--connector", "claudecode" }),
            string.Join('\u001f', new[] { "tool", "list", "--json" }),
            string.Join('\u001f', new[] { "skill", "info", "--json", "--connector", "claudecode", "some-skill" }),
            string.Join('\u001f', new[] { "plugin", "info", "--json", "some-plugin" }),
            string.Join('\u001f', new[] { "tool", "status", "--json", "some-tool" }),
            string.Join('\u001f', new[] { "aibom", "scan", "--json" }),
            string.Join('\u001f', new[] { "agent", "discovery", "status", "--json" }),
            string.Join('\u001f', new[] { "agent", "discovery", "scan" }),
            string.Join('\u001f', new[] { "agent", "discover", "--refresh", "--no-emit-otel" }),
            // the Runtime panel (CUST-309): the poll, the read-only prerequisites check, enable with every option, and disable
            string.Join('\u001f', AiRuntimeCommands.PollNow),
            string.Join('\u001f', AiRuntimeCommands.ReadPermissions),
            string.Join('\u001f', AiRuntimeCommands.Enable(new AiRuntimeEnableOptions())),
            string.Join('\u001f', AiRuntimeCommands.Enable(new AiRuntimeEnableOptions(HostPlane: true, DnsCapture: false, PollIntervalSeconds: 30, MinRiskToReport: 40, Restart: false))),
            string.Join('\u001f', AiRuntimeCommands.Enable(new AiRuntimeEnableOptions(HostPlane: false, DnsCapture: true))),
            string.Join('\u001f', AiRuntimeCommands.Disable()),
            string.Join('\u001f', AiRuntimeCommands.Disable(restart: false)),
            string.Join('\u001f', new[] { "guardrail", "status" }),
            // alerts
            string.Join('\u001f', new[] { "alerts", "acknowledge", "--severity", "HIGH", "--before", "2030-01-15T00:00:00Z", "--dry-run" }),
            string.Join('\u001f', new[] { "alerts", "acknowledge", "--severity", "HIGH", "--before", "2030-01-15T00:00:00Z", "--yes" }),
            string.Join('\u001f', new[] { "alerts", "dismiss", "--severity", "HIGH", "--before", "2030-01-15T00:00:00Z", "--dry-run" }),
            string.Join('\u001f', AlertsPanelViewModel.AlertIdCommandArgs("acknowledge", new[] { "a", "b" })),
            string.Join('\u001f', AlertsPanelViewModel.AlertIdCommandArgs("dismiss", new[] { "a", "b" })),
            // registries
            string.Join('\u001f', new[] { "registry", "list", "--json" }),
            string.Join('\u001f', new[] { "registry", "sync", "--all", "--json" }),
            string.Join('\u001f', new[] { "registry", "sync", "some-source", "--json" }),
            string.Join('\u001f', new[] { "registry", "edit", "some-source", "--enabled", "--non-interactive", "--json" }),
            string.Join('\u001f', new[] { "registry", "remove", "some-source", "--non-interactive", "--json" }),
            string.Join('\u001f', new[] { "registry", "require", "--type", "skill", "--enabled", "--json" }),
            string.Join('\u001f', new[] { "registry", "approve", "some-source", "entry", "--type", "skill", "--json" }),
            string.Join('\u001f', new[] { "registry", "reject", "--type", "skill", "--json", "--", "some-source", "-entry" }),
            // guardrail controls
            string.Join('\u001f', GuardrailControlArgv.Hilt(true, "HIGH", "claudecode", restart: false)),
            string.Join('\u001f', GuardrailControlArgv.Hilt(false, null, null, restart: true)),
            string.Join('\u001f', GuardrailControlArgv.BlockMessage("Blocked.", "claudecode", restart: false)),
            string.Join('\u001f', GuardrailControlArgv.BlockMessage(null, null, restart: true)),
            string.Join('\u001f', GuardrailControlArgv.JudgeAdd("claudecode", true, 5, restart: false)),
            string.Join('\u001f', GuardrailControlArgv.JudgeRemove("claudecode", restart: false)),
            string.Join('\u001f', GuardrailControlArgv.ReadHilt()),
            string.Join('\u001f', GuardrailControlArgv.ReadBlockMessage()),
            string.Join('\u001f', GuardrailControlArgv.ReadJudge()),
        };

    [Theory]
    [MemberData(nameof(Argvs))]
    public void The_argv_is_still_a_command_of_the_newer_cli_with_options_it_has(string packed)
    {
        var argv = packed.Split('\u001f');

        Assert.Null(Check(argv));
    }

    [Fact]
    public void The_checker_notices_a_renamed_flag_and_a_removed_command()
    {
        Assert.Equal("'doctor' has no option --repair", Check(["doctor", "--repair"]));
        Assert.StartsWith("no such command", Check(["migrations", "status"]), StringComparison.Ordinal);
    }
}
