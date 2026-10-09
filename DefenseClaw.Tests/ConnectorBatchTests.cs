using DefenseClaw.Core.Setup;

namespace DefenseClaw.Tests;

/// <summary>
/// The batch argv of <c>defenseclaw setup</c> (CUST-271) and the flags it carries, checked against the live 0.8.10 <c>setup --help</c> capture
/// (<c>Fixtures/runtime-0.8.10/setup.txt</c>).
/// </summary>
public sealed class ConnectorBatchTests
{
    private static string Help() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup.txt"));

    [Fact]
    public void Every_flag_the_batch_can_send_is_listed_by_the_live_setup_help()
    {
        var help = Help();
        foreach (var flag in ConnectorBatch.Flags)
        {
            Assert.Contains(flag, help, StringComparison.Ordinal);
        }

        Assert.Contains("--restart / --no-restart", help, StringComparison.Ordinal);
        Assert.Contains("--mode [observe|action]", help, StringComparison.Ordinal);
        Assert.Contains("-y, --yes", help, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_connectors_are_a_repeated_connector_flag_with_the_mode()
    {
        Assert.Equal(
            new[] { "setup", "--yes", "--connector", "codex", "--connector", "claudecode", "--mode", "observe" },
            ConnectorBatch.Argv(new[] { "codex", "claudecode" }, BatchExtra.None, "observe", restart: true));
    }

    [Fact]
    public void Detected_and_all_come_after_the_connectors_and_the_mode_after_those()
    {
        Assert.Equal(
            new[] { "setup", "--yes", "--connector", "codex", "--detected", "--mode", "action" },
            ConnectorBatch.Argv(new[] { "codex" }, BatchExtra.Detected, "action", restart: true));
        Assert.Equal(
            new[] { "setup", "--yes", "--all", "--mode", "observe" },
            ConnectorBatch.Argv(Array.Empty<string>(), BatchExtra.All, "observe", restart: true));
    }

    [Fact]
    public void No_restart_is_the_last_flag_and_only_when_asked_for()
    {
        var argv = ConnectorBatch.Argv(new[] { "codex" }, BatchExtra.None, "observe", restart: false)!;
        Assert.Equal("--no-restart", argv[^1]);
        Assert.DoesNotContain("--no-restart", ConnectorBatch.Argv(new[] { "codex" }, BatchExtra.None, "observe", restart: true)!);
    }

    [Fact]
    public void Nothing_selected_is_no_command_because_the_cli_would_open_its_picker()
    {
        Assert.Null(ConnectorBatch.Argv(Array.Empty<string>(), BatchExtra.None, "observe", restart: true));
        Assert.Null(ConnectorBatch.Argv(new[] { " ", "" }, BatchExtra.None, "observe", restart: true));
    }

    [Fact]
    public void Duplicates_are_dropped_and_an_unknown_mode_is_observe()
    {
        Assert.Equal(
            new[] { "setup", "--yes", "--connector", "codex", "--mode", "observe" },
            ConnectorBatch.Argv(new[] { "codex", "codex" }, BatchExtra.None, "enforce", restart: true));
    }

    [Fact]
    public void The_batch_flags_act_only_with_no_subcommand_so_no_connector_name_follows_setup()
    {
        // "(no subcommand)" in the live help: the CLI ignores these flags when a connector subcommand follows.
        Assert.Contains("(no subcommand)", Help(), StringComparison.Ordinal);
        var argv = ConnectorBatch.Argv(new[] { "codex" }, BatchExtra.None, "observe", restart: true)!;
        Assert.Equal("setup", argv[0]);
        Assert.StartsWith("--", argv[1], StringComparison.Ordinal);
    }
}
