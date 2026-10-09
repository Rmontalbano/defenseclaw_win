using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// CUST-261: the Audit inspector's "Current state". Selecting an event about a skill, MCP server, plugin or tool looks up, in the <c>actions</c> table and
/// read-only, what is being done to that item now - and says "Current state: blocked" for a blocked skill. An event about anything else, or about an item
/// with no entry, says nothing; selecting another row drops the line at once. Synthetic rows from the real DDL.
/// </summary>
public sealed class AuditCurrentStateTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ManualClock _clock = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private string DbPath => Path.Combine(_temp.Path, "audit.db");

    private async Task<AuditPanelViewModel> OpenAsync()
    {
        AuditTestDatabase.Create(DbPath, 0, newest: _clock.GetUtcNow());

        // Events (newest first as numbered) and what the actions table says about the items they are about.
        Event("e-blocked-skill", 1, "skill-block", "evil-skill");
        Event("e-allowed-skill", 2, "skill-allow", "good-skill");
        Event("e-plugin", 3, "plugin-remove", "p-all");
        Event("e-no-entry", 4, "mcp-set", "docs-server");
        Event("e-scan-of-the-same-name", 5, "scan-finding", "evil-skill");
        Event("e-no-target", 6, "skill-block", string.Empty);
        Event("e-codex-tool", 7, "tool_invocation", "Bash", "codex");
        Event("e-claude-tool", 8, "tool_invocation", "Bash", "claudecode");
        Event("e-empty-entry", 9, "mcp-unset", "old-server");
        CorrelatedRows.AddAction(DbPath, "skill", "evil-skill", """{"install":"block"}""");
        CorrelatedRows.AddAction(DbPath, "skill", "good-skill", """{"install":"allow"}""");
        CorrelatedRows.AddAction(DbPath, "plugin", "p-all", """{"install":"block","file":"quarantine","runtime":"disable"}""");
        CorrelatedRows.AddAction(DbPath, "tool", "Bash", """{"install":"block"}""", connector: "codex");
        CorrelatedRows.AddAction(DbPath, "mcp", "old-server", "{}");

        _services = TestServices.Create(_temp);
        var panel = new AuditPanelViewModel(_services) { TimeSource = _clock, LiveTimerEnabled = false, ActionableOnly = false };
        await panel.InitializeAsync();
        return panel;
    }

    private void Event(string id, double secondsAgo, string action, string target, string connector = "claudecode") =>
        CorrelatedRows.Add(DbPath, id, _clock.GetUtcNow().AddSeconds(-secondsAgo), action, "INFO", connector, "enforcement.action", "evt", target);

    private static async Task<AuditPanelViewModel> Select(AuditPanelViewModel panel, string id)
    {
        panel.SelectedRow = panel.Rows.Single(r => r.Id == id);
        await panel.LastCurrentState;
        await panel.LastCorrelation;
        return panel;
    }

    // ------------------------------------------------------------------ the acceptance

    [Fact]
    public async Task A_blocked_skills_event_shows_current_state_blocked()
    {
        var panel = await OpenAsync();

        await Select(panel, "e-blocked-skill");

        Assert.True(panel.HasCurrentState);
        Assert.Equal("blocked", panel.CurrentStateText);
        Assert.Equal("Current state: blocked", panel.CurrentStateLabel);
        Assert.Equal("Bad", panel.CurrentStateTone);
    }

    [Fact]
    public async Task An_allowed_skill_a_plugin_with_all_three_and_an_empty_entry_read_as_the_Govern_panels_read_them()
    {
        var panel = await OpenAsync();

        await Select(panel, "e-allowed-skill");
        Assert.Equal("Current state: allowed", panel.CurrentStateLabel);
        Assert.Equal("Ok", panel.CurrentStateTone);

        await Select(panel, "e-plugin");
        Assert.Equal("Current state: blocked, quarantined, disabled", panel.CurrentStateLabel);
        Assert.Equal("Bad", panel.CurrentStateTone);

        // An entry that says nothing is being done is still an answer.
        await Select(panel, "e-empty-entry");
        Assert.Equal("Current state: none", panel.CurrentStateLabel);
        Assert.Equal("Neutral", panel.CurrentStateTone);
    }

    [Fact]
    public async Task An_item_with_no_entry_and_an_event_about_something_else_say_nothing()
    {
        var panel = await OpenAsync();

        await Select(panel, "e-no-entry");
        Assert.False(panel.HasCurrentState);
        Assert.Equal(string.Empty, panel.CurrentStateLabel);

        // The same name, but the event is about a scan, not about a skill: no lookup at all.
        var reads = panel.StateReader.ReadCount;
        await Select(panel, "e-scan-of-the-same-name");
        Assert.False(panel.HasCurrentState);
        Assert.Equal(reads, panel.StateReader.ReadCount);

        // An event with no target has nothing to look up.
        await Select(panel, "e-no-target");
        Assert.False(panel.HasCurrentState);
        Assert.Equal(reads, panel.StateReader.ReadCount);
    }

    [Fact]
    public async Task Selecting_another_row_or_none_drops_the_line()
    {
        var panel = await OpenAsync();
        await Select(panel, "e-blocked-skill");
        Assert.True(panel.HasCurrentState);

        panel.SelectedRow = panel.Rows.Single(r => r.Id == "e-scan-of-the-same-name");
        Assert.False(panel.HasCurrentState);

        await Select(panel, "e-blocked-skill");
        Assert.True(panel.HasCurrentState);
        panel.SelectedRow = null;
        Assert.False(panel.HasCurrentState);
        Assert.Equal("Neutral", panel.CurrentStateTone);
    }

    [Fact]
    public async Task A_lookup_that_is_overtaken_never_writes_over_the_row_now_selected()
    {
        var panel = await OpenAsync();

        // The first row's lookup is started and the selection moves on before it is awaited: whichever finishes last, the line is the second row's.
        panel.SelectedRow = panel.Rows.Single(r => r.Id == "e-blocked-skill");
        var first = panel.LastCurrentState;
        panel.SelectedRow = panel.Rows.Single(r => r.Id == "e-no-entry");
        await Task.WhenAll(first, panel.LastCurrentState);
        await panel.LastCorrelation;

        Assert.False(panel.HasCurrentState);
        Assert.Equal(string.Empty, panel.CurrentStateText);
    }

    [Fact]
    public async Task The_connector_of_the_event_decides_between_a_global_and_a_connectors_own_entry()
    {
        var panel = await OpenAsync();

        // The block is codex's own: an event of codex sees it, an event of claudecode does not.
        await Select(panel, "e-codex-tool");
        Assert.Equal("Current state: blocked", panel.CurrentStateLabel);
        await Select(panel, "e-claude-tool");
        Assert.False(panel.HasCurrentState);
    }

    [Fact]
    public async Task The_state_is_read_now_not_when_the_list_was_read()
    {
        var panel = await OpenAsync();
        await Select(panel, "e-blocked-skill");
        Assert.Equal("blocked", panel.CurrentStateText);

        // The skill is allowed afterwards (the CLI rewrote the entry): the next time the row is selected it says so.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """UPDATE actions SET actions_json = '{"install":"allow"}' WHERE target_name = 'evil-skill'""";
            _ = command.ExecuteNonQuery();
        }

        panel.SelectedRow = null;
        await Select(panel, "e-blocked-skill");
        Assert.Equal("allowed", panel.CurrentStateText);
    }

    [Fact]
    public async Task A_database_that_cannot_be_read_shows_no_line_and_breaks_nothing()
    {
        var panel = await OpenAsync();
        var notADatabase = _temp.File("not-a-database.db");
        File.WriteAllText(notADatabase, "this is not an sqlite file at all, but it is long enough to be read as a header");
        panel.StateReader = new EnforcementStateReader(notADatabase, TestTimeouts.Ceiling);

        await Select(panel, "e-blocked-skill");

        Assert.False(panel.HasCurrentState);
        Assert.NotNull(panel.SelectedRow);
    }

    [Theory]
    [InlineData("""{"install":"block"}""")]
    [InlineData("""{"install":"allow"}""")]
    [InlineData("""{"file":"quarantine"}""")]
    [InlineData("""{"runtime":"disable"}""")]
    [InlineData("""{"install":"block","file":"quarantine","runtime":"disable"}""")]
    [InlineData("""{"install":"allow","runtime":"disable"}""")]
    [InlineData("""{"install":"BLOCK","file":"Quarantine"}""")]
    [InlineData("""{"install":"custom"}""")]
    [InlineData("{}")]
    public async Task The_state_is_what_the_Govern_panels_make_of_the_same_actions(string json)
    {
        AuditTestDatabase.Create(DbPath, 0);
        CorrelatedRows.AddAction(DbPath, "skill", "s", json);
        var state = await new EnforcementStateReader(DbPath, TestTimeouts.Ceiling).ReadAsync("skill", "s");

        using var document = System.Text.Json.JsonDocument.Parse("{\"actions\":" + json + "}");
        var govern = GovernJson.Interpret(document.RootElement);

        // The same four facts, and the same tone for the badge, from the same actions{file,runtime,install}.
        Assert.NotNull(state);
        Assert.Equal(govern.Blocked, state.IsBlocked);
        Assert.Equal(govern.Quarantined, state.IsQuarantined);
        Assert.Equal(govern.Disabled, state.IsDisabled);
        Assert.Equal(govern.Allowed, state.IsAllowed && !state.IsBlocked);
        Assert.Equal(govern.Tone, state.Tone);
    }

    [Fact]
    public async Task A_database_without_the_table_shows_no_line()
    {
        var panel = await OpenAsync();
        panel.StateReader = new EnforcementStateReader(_temp.File("never-created.db"), TestTimeouts.Ceiling);

        await Select(panel, "e-blocked-skill");

        Assert.False(panel.HasCurrentState);
    }

    [Fact]
    public async Task The_lookup_is_bounded_like_the_inspectors_other_lookups()
    {
        var panel = await OpenAsync();

        // The composition's limit for an inspector's lookup: 8 s in the app, the suite's ceiling in a test composition (CUST-323).
        Assert.Equal(_services!.ReaderTimeouts.Audit, panel.StateReader.ReadTimeout);
        Assert.Equal(TestTimeouts.Ceiling, panel.StateReader.ReadTimeout);
    }
}
