using System.Runtime.CompilerServices;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Install;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// The gateway's fixed verbs in the command palette (CUST-264): <c>watchdog start | stop | status</c>, <c>connector verify | list-backups | teardown</c>,
/// <c>policy reload | domains</c>. Their rows come from the TUI registry like any other (CUST-282); what these tests hold is what is specific to them: the
/// exact argv on <c>defenseclaw-gateway</c> (never a bare one), the tier each shows, which run as they are and which are reviewed first (in the words
/// of the gateway's own help), and that <c>policy reload</c>, which needs the running sidecar, says so when there is none. The help screens the list
/// is held to are checked in the Core suite (<c>GatewayVerbTierTests</c>). Nothing starts a process: the review and the toast are seams, and the
/// runner is the isolated one (no CLI on its PATH).
/// </summary>
public sealed class GatewayVerbRowsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;
    private readonly ShellActions _actions;
    private readonly List<string> _toasts = new();
    private readonly List<CommandReview> _reviews = new();

    public GatewayVerbRowsTests()
    {
        _services = TestServices.Create(_temp);
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        _actions = new ShellActions(_services, new PanelCatalog(_services), tray, () => null)
        {
            Toast = (title, message) => _toasts.Add(title + ": " + message),
            Confirmer = review =>
            {
                _reviews.Add(review);
                return false;
            },
        };
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    /// <summary>Each verb: its TUI name, its tier, and whether it may run with no review.</summary>
    public static IEnumerable<object[]> Verbs() => new[]
    {
        new object[] { "watchdog start", new[] { "watchdog", "start" }, CommandTier.StateChanging, false },
        new object[] { "watchdog stop", new[] { "watchdog", "stop" }, CommandTier.StateChanging, false },
        new object[] { "watchdog status", new[] { "watchdog", "status" }, CommandTier.ReadOnly, true },
        new object[] { "connector verify", new[] { "connector", "verify" }, CommandTier.ReadOnly, true },
        new object[] { "connector list-backups", new[] { "connector", "list-backups" }, CommandTier.ReadOnly, true },
        new object[] { "connector teardown", new[] { "connector", "teardown" }, CommandTier.Destructive, false },
        new object[] { "policy reload", new[] { "policy", "reload" }, CommandTier.StateChanging, false },
        new object[] { "policy domains", new[] { "policy", "domains" }, CommandTier.ReadOnly, true },
    };

    private static CuratedCommand Command(string name) =>
        CuratedCommand.FromRegistry(TuiRegistryCatalogues.Baseline.Find(name) ?? throw new InvalidOperationException($"No entry '{name}'."));

    private IReadOnlyList<ShellCommand> Rows() => ShellCommandRegistry.BuildCliCommands(CuratedCommandCatalog.For(null).Commands, _actions);

    // ------------------------------------------------------------------ the list

    [Fact]
    public void The_list_is_the_eight_verbs_and_each_is_two_words_on_the_gateway_never_a_bare_one()
    {
        Assert.Equal(8, GatewayVerbs.All.Count);
        Assert.Equal(
            Verbs().Select(v => string.Join(' ', (string[])v[1])).Order(StringComparer.Ordinal),
            GatewayVerbs.All.Select(v => string.Join(' ', v.Argv)).Order(StringComparer.Ordinal));
        Assert.All(GatewayVerbs.All, v =>
        {
            Assert.Equal(2, v.Argv.Count);
            Assert.All(v.Argv, word => Assert.Matches("^[a-z][a-z-]*$", word));
            Assert.StartsWith("defenseclaw-gateway ", v.CommandText, StringComparison.Ordinal);
            Assert.NotEmpty(v.Summary);
        });

        // Only the exact two words are a verb of this list: nothing shorter, nothing longer, nothing after a terminator.
        Assert.Null(GatewayVerbs.Find(Array.Empty<string>()));
        Assert.Null(GatewayVerbs.Find(new[] { "watchdog" }));
        Assert.Null(GatewayVerbs.Find(new[] { "connector", "verify", "--json" }));
        Assert.Null(GatewayVerbs.Find(new[] { "connector", "verify", "--", "x" }));
        Assert.Null(GatewayVerbs.Find(new[] { "Connector", "verify" }));
        Assert.Null(GatewayVerbs.Find(new[] { "start" }));
        Assert.NotNull(GatewayVerbs.Find(new[] { "connector", "verify" }));
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public void Every_verb_is_a_row_with_the_exact_argv_on_the_gateway_and_the_classifiers_tier(string name, string[] argv, CommandTier tier, bool unreviewed)
    {
        var row = Assert.Single(Rows(), r => r.Title == name);
        var command = row.Cli!;

        Assert.Equal("defenseclaw-gateway", command.Executable);
        Assert.Equal(argv, command.Argv);
        Assert.Equal("defenseclaw-gateway " + string.Join(' ', argv), command.CommandLineText);
        Assert.False(command.NeedsArguments);
        Assert.False(command.NeedsTerminal);
        Assert.Null(command.LifecycleAction);
        Assert.Equal(argv, command.GatewayVerb!.Argv);

        Assert.Equal(tier, command.Tier);
        Assert.Equal(tier, CommandTiers.Classify(argv));
        Assert.Equal(unreviewed, command.RunsWithoutReview);
    }

    [Fact]
    public void The_defenseclaw_cli_s_own_policy_commands_are_not_mistaken_for_the_gateways()
    {
        var python = new CuratedCommand(new[] { "policy", "reload" }, "Policy", "x", string.Empty, Array.Empty<string>(), "defenseclaw");

        Assert.Null(python.GatewayVerb);
        Assert.Equal("defenseclaw policy reload", python.CommandLineText);
    }

    // ------------------------------------------------------------------ running them

    [Theory]
    [InlineData("watchdog status")]
    [InlineData("connector verify")]
    [InlineData("connector list-backups")]
    [InlineData("policy domains")]
    public async Task A_read_runs_as_it_is_without_a_review_and_on_the_gateway(string name)
    {
        await _actions.RunCuratedAsync(Command(name));

        Assert.Empty(_reviews);
        Assert.Contains("Could not run it", Assert.Single(_toasts), StringComparison.Ordinal);
        Assert.Empty(_services.Cli.Activity);
    }

    [Theory]
    [InlineData("watchdog start", false, "background daemon")]
    [InlineData("watchdog stop", false, "Stops the running watchdog daemon")]
    [InlineData("policy reload", false, "reload its OPA policies")]
    [InlineData("connector teardown", true, "pristine backup")]
    public async Task A_verb_that_changes_something_is_reviewed_first_in_the_words_of_the_gateways_help(string name, bool destructive, string words)
    {
        await _actions.RunCuratedAsync(Command(name));

        var review = Assert.Single(_reviews);
        var step = Assert.Single(review.Steps);
        Assert.Equal("defenseclaw-gateway", step.Executable);
        Assert.Equal(Command(name).Argv, step.Argv);
        Assert.Equal(destructive, review.IsDestructive);
        Assert.Equal(destructive ? "Run destructive command" : "Run command", review.ConfirmLabel);
        Assert.Equal(Command(name).GatewayVerb!.Summary, review.Summary);
        Assert.Contains(words, review.Summary, StringComparison.Ordinal);
        Assert.False(review.RestartsGateway);
        Assert.Empty(_toasts);
        Assert.Empty(_services.Cli.Activity);
    }

    [Fact]
    public async Task Teardown_says_what_it_restores_and_what_it_leaves_alone()
    {
        await _actions.RunCuratedAsync(Command("connector teardown"));

        var summary = Assert.Single(_reviews).Summary;
        Assert.Contains("restores the agent framework's config from its pristine backup", summary, StringComparison.Ordinal);
        Assert.Contains("removes the hook scripts", summary, StringComparison.Ordinal);
        Assert.Contains("clears the environment shims", summary, StringComparison.Ordinal);
        Assert.Contains("does not touch the gateway's own service, its token or the audit database", summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ gating

    [Fact]
    public void Policy_reload_needs_the_running_gateway_and_the_other_verbs_do_not()
    {
        Assert.Equal(new[] { "policy reload" }, GatewayVerbs.All.Where(v => v.NeedsRunningGateway).Select(v => string.Join(' ', v.Argv)));
    }

    [Fact]
    public void Policy_reload_is_disabled_with_the_reason_until_the_gateway_is_known_to_be_running()
    {
        // The isolated composition has not polled, so the install state is not known: the sentence the Gateway rows say too.
        var rows = Rows();
        var reload = Assert.Single(rows, r => r.Title == "policy reload");

        Assert.False(reload.IsEnabled);
        Assert.Equal("Still checking the gateway; try again in a moment.", reload.DisabledReason);

        foreach (var name in Verbs().Select(v => (string)v[0]).Where(n => n != "policy reload"))
        {
            Assert.True(Assert.Single(rows, r => r.Title == name).IsEnabled, name);
        }
    }

    [Fact]
    public void Availability_follows_the_install_and_the_gateway_state()
    {
        var reload = new[] { "policy", "reload" };
        string? Reason(GatewaySnapshot snapshot) => GatewayVerbs.Availability(reload, snapshot).Reason;

        Assert.Equal("Still checking the gateway; try again in a moment.", Reason(new GatewaySnapshot()));
        Assert.Equal("DefenseClaw is not installed on this machine.", Reason(new GatewaySnapshot { Install = InstallState.NotInstalled }));
        Assert.Equal("DefenseClaw is not initialized yet. Run 'defenseclaw init' first.", Reason(new GatewaySnapshot { Install = InstallState.InstalledNotInitialized }));
        Assert.Equal(
            "The gateway is not running, so there is no sidecar to reload the policies in.",
            Reason(new GatewaySnapshot { Install = InstallState.GatewayStopped, State = AppGatewayState.GatewayStopped }));

        var running = new GatewaySnapshot { Install = InstallState.Running, State = AppGatewayState.Running };
        Assert.Equal((true, (string?)null), GatewayVerbs.Availability(reload, running));

        // Anything that is not that verb is not gated by it, whatever the state.
        Assert.Equal((true, (string?)null), GatewayVerbs.Availability(new[] { "watchdog", "stop" }, new GatewaySnapshot()));
        Assert.Equal((true, (string?)null), GatewayVerbs.Availability(new[] { "policy", "domains" }, new GatewaySnapshot { Install = InstallState.NotInstalled }));
    }

    [Fact]
    public void The_reload_row_is_the_only_one_a_stopped_gateway_turns_off_and_the_lifecycle_rows_keep_their_own_rule()
    {
        var rows = Rows();

        // start | stop | restart still follow the tray's rule (they are not these verbs).
        foreach (var title in new[] { "start", "stop", "restart", "restart gateway" })
        {
            var row = Assert.Single(rows, r => r.Title == title);
            Assert.Equal(GatewayControl.Availability(row.Cli!.LifecycleAction!.Value, _actions.Snapshot).Reason, row.DisabledReason);
        }

        Assert.Null(Assert.Single(rows, r => r.Title == "gateway status").DisabledReason);
    }
}
