using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>The Mutations tab's data side: the diff builder and the view-model over a synthetic audit database. No WPF controls are built.</summary>
public sealed class MutationsViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private MutationDatabase Database() => new(_temp.File("audit.db"));

    private ActivityMutationsViewModel Model(MutationDatabase database) => new(new MutationReader(database.Path));

    // ------------------------------------------------------------------ the diff

    private static MutationItem Item(string before = "", string after = "", string diff = "", string structured = "") =>
        new("id", MutationSource.ActivityEvent, DateTimeOffset.UtcNow, "now", "admin", "policy.update", "policy", "default", string.Empty, before, after, diff, string.Empty, string.Empty, null, string.Empty, structured);

    [Fact]
    public void The_diff_mode_prefers_before_after_then_a_diff_then_the_audit_record()
    {
        Assert.Equal(MutationDiffMode.BeforeAfter, MutationDiffBuilder.ModeOf(Item(before: "{}", diff: "[]")));
        Assert.Equal(MutationDiffMode.BeforeAfter, MutationDiffBuilder.ModeOf(Item(after: "{}")));
        Assert.Equal(MutationDiffMode.Unified, MutationDiffBuilder.ModeOf(Item(diff: "[]", structured: "{}")));
        Assert.Equal(MutationDiffMode.Recorded, MutationDiffBuilder.ModeOf(Item(structured: "{}")));
        Assert.Equal(MutationDiffMode.None, MutationDiffBuilder.ModeOf(Item()));
    }

    [Fact]
    public void Json_is_indented_and_anything_else_is_left_alone()
    {
        Assert.Equal("{\n  \"a\": 1\n}".ReplaceLineEndings(), MutationDiffBuilder.Pretty("{\"a\":1}").ReplaceLineEndings());
        Assert.Equal("not json", MutationDiffBuilder.Pretty("not json"));
        Assert.Equal(string.Empty, MutationDiffBuilder.Pretty("  "));
        Assert.Contains("<&>", MutationDiffBuilder.Pretty("{\"a\":\"<&>\"}"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_diff_of_path_before_after_entries_becomes_a_unified_diff()
    {
        var lines = MutationDiffBuilder.Unified("[{\"path\":\"guardrail.mode\",\"op\":\"replace\",\"before\":\"observe\",\"after\":\"action\"},{\"path\":\"x\",\"after\":3}]");

        Assert.Equal(
            new[]
            {
                (DiffLineKind.Context, "@@ guardrail.mode (replace)"),
                (DiffLineKind.Removed, "- observe"),
                (DiffLineKind.Added, "+ action"),
                (DiffLineKind.Context, "@@ x"),
                (DiffLineKind.Added, "+ 3"),
            },
            lines.Select(l => (l.Kind, l.Text)));
    }

    [Fact]
    public void A_text_diff_keeps_its_added_and_removed_lines_and_json_of_another_shape_is_untinted()
    {
        var text = MutationDiffBuilder.Unified("--- a\n+++ b\n@@ -1 +1 @@\n-old\n+new\n same\n");
        Assert.Equal(DiffLineKind.Removed, text.Single(l => l.Text == "-old").Kind);
        Assert.Equal(DiffLineKind.Added, text.Single(l => l.Text == "+new").Kind);
        Assert.Equal(DiffLineKind.Context, text.Single(l => l.Text == "--- a").Kind);

        var other = MutationDiffBuilder.Unified("{\"changed\":[\"a\"]}");
        Assert.All(other, l => Assert.Equal(DiffLineKind.Context, l.Kind));
        Assert.Contains(other, l => l.Text.Contains("changed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_says_what_the_table_and_the_inspector_show()
    {
        var row = new MutationRow(Item(before: "{\"a\":1}", after: "{\"a\":2}") with { VersionFrom = "3", VersionTo = "4", Connector = "codex", Reason = "line one\nline two" });

        Assert.Equal("policy/default", row.TargetText);
        Assert.Equal("3 → 4", row.VersionText);
        Assert.Equal("codex", row.ConnectorText);
        Assert.Equal("line one line two", row.ReasonText);
        Assert.True(row.ShowBeforeAfter);
        Assert.False(row.ShowUnified);
        Assert.Contains("\"a\": 1", row.BeforeText, StringComparison.Ordinal);
        Assert.Contains(row.Fields, f => f.Key == "Actor" && f.Value == "admin");
        Assert.Contains(row.Fields, f => f.Key == "Version" && f.Value == "3 → 4");
        Assert.True(row.Matches("POLICY"));
        Assert.False(row.Matches("nothing like this"));

        var bare = new MutationRow(Item() with { Actor = string.Empty, TargetType = string.Empty, Target = string.Empty });
        Assert.Equal("—", bare.Actor);
        Assert.Equal("—", bare.TargetText);
        Assert.Equal("—", bare.VersionText);
        Assert.True(bare.ShowNothing);
        Assert.Equal("(empty)", bare.BeforeText);
    }

    // ------------------------------------------------------------------ the view-model

    [Fact]
    public async Task An_empty_database_shows_the_expected_empty_state()
    {
        var model = Model(Database());

        await model.LoadAsync();

        Assert.True(model.HasLoaded);
        Assert.True(model.IsEmpty);
        Assert.Empty(model.Rows);
        Assert.Equal(
            "No mutations — Gateway configuration mutations and policy changes appear here from the audit database.",
            model.EmptyTitle + " — " + model.EmptyDetail);
        Assert.False(model.CanFilterConnectors);
        Assert.Equal(string.Empty, model.StatusNote);
    }

    [Fact]
    public async Task A_missing_database_is_the_same_empty_state()
    {
        var model = new ActivityMutationsViewModel(new MutationReader(_temp.File("no-such.db")));

        await model.LoadAsync();

        Assert.True(model.IsEmpty);
        Assert.Equal(ActivityMutationsViewModel.DefaultEmptyDetail, model.EmptyDetail);
        Assert.Equal(string.Empty, model.StatusNote);
    }

    [Fact]
    public async Task Changes_load_newest_first_with_the_connectors_they_name()
    {
        var model = Model(Database()
            .Activity("a1", reason: "r", before: "{\"a\":1}", after: "{\"a\":2}")
            .Change("c1", connector: "claudecode")
            .Change("c2", "quarantine", "enforcement.action", "codex")
            .Change("c3"));

        await model.LoadAsync();

        Assert.Equal(new[] { "a1", "c1", "c2", "c3" }, model.Rows.Select(r => r.Id));
        Assert.False(model.IsEmpty);
        Assert.Equal(new[] { ActivityMutationsViewModel.AllConnectors, "claudecode", "codex" }, model.Connectors);
        Assert.True(model.CanFilterConnectors);
        Assert.Equal("4 changes", model.Summary);
    }

    [Fact]
    public async Task The_connector_filter_hides_other_connectors_and_rows_with_none()
    {
        var model = Model(Database().Change("c1", connector: "claudecode").Change("c2", connector: "codex").Change("c3").Activity("a1"));
        await model.LoadAsync();

        model.SelectedConnector = "codex";

        Assert.Equal(new[] { "c2" }, model.Rows.Select(r => r.Id));
        Assert.Equal("1 of 4 changes", model.Summary);

        model.SelectedConnector = ActivityMutationsViewModel.AllConnectors;
        Assert.Equal(4, model.Rows.Count);
    }

    [Fact]
    public async Task Search_narrows_the_loaded_changes_and_a_filter_with_no_match_says_so()
    {
        var model = Model(Database().Activity("a1", actor: "alice", targetId: "strict").Activity("a2", actor: "bob"));
        await model.LoadAsync();

        model.SearchText = "ALICE";
        Assert.Equal(new[] { "a1" }, model.Rows.Select(r => r.Id));

        model.SearchText = "zzz";
        Assert.True(model.IsEmpty);
        Assert.Equal("No mutations match", model.EmptyTitle);
        Assert.Contains("2", model.EmptyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reload_keeps_the_inspector_on_the_same_change()
    {
        var database = Database().Activity("a1").Activity("a2");
        var model = Model(database);
        await model.LoadAsync();
        model.SelectedRow = model.Rows[1];

        _ = database.Activity("a0");
        await model.LoadAsync();

        Assert.Equal("a2", model.SelectedRow?.Id);
        Assert.True(model.HasSelection);

        model.ClearSelectionCommand.Execute(null);
        Assert.False(model.HasSelection);
    }

    [Fact]
    public async Task A_failed_read_is_a_note_and_keeps_the_rows_already_shown()
    {
        var database = Database().Activity("a1");
        var model = Model(database);
        await model.LoadAsync();
        Assert.Single(model.Rows);

        // Not a database: opening it fails.
        await File.WriteAllTextAsync(database.Path, "this is not a sqlite database");
        await model.LoadAsync();

        Assert.StartsWith("The audit database could not be read", model.StatusNote, StringComparison.Ordinal);
        Assert.True(model.HasStatusNote);
        Assert.Single(model.Rows);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task A_newer_load_abandons_the_one_running()
    {
        var model = Model(Database().Activity("a1"));

        var first = model.LoadAsync();
        var second = model.LoadAsync();
        await Task.WhenAll(first, second);

        Assert.Single(model.Rows);
        Assert.False(model.IsLoading);
        Assert.True(model.HasLoaded);
    }
}
