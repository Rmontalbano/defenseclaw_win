using System.Text.Json;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Govern;

public class GovernJsonTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------ Flatten: every list shape 0.8.10 prints

    [Fact]
    public void A_bare_array_is_a_list_of_items_with_no_group_connector()
    {
        var root = Parse("""[{"name": "a"}, {"name": "b", "connector": "codex"}]""");

        var items = GovernJson.Flatten(root, "skills");

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.GroupConnector));
        Assert.Equal("a", GovernJson.Str(items[0].Item, "name"));
        Assert.Equal("codex", GovernJson.Str(items[1].Item, "connector"));
    }

    [Fact]
    public void An_array_of_per_connector_groups_is_flattened_and_each_item_remembers_its_group()
    {
        var root = Parse("""
            [
              {"connector": "claudecode", "skills": [{"name": "a"}, {"name": "b"}]},
              {"connector": "codex", "skills": [{"name": "a"}]}
            ]
            """);

        var items = GovernJson.Flatten(root, "skills");

        Assert.Equal(
            new[] { ("a", "claudecode"), ("b", "claudecode"), ("a", "codex") },
            items.Select(i => (GovernJson.Str(i.Item, "name")!, i.GroupConnector!)).ToArray());
    }

    [Fact]
    public void A_single_group_object_is_read_as_one_group()
    {
        var root = Parse("""{"connector": "codex", "skills": [{"name": "a"}, {"name": "b"}]}""");

        var items = GovernJson.Flatten(root, "skills");

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal("codex", i.GroupConnector));
    }

    [Fact]
    public void A_group_with_a_null_connector_is_the_source_scoped_tail_and_names_no_connector()
    {
        var root = Parse("""
            [
              {"connector": "claudecode", "tools": [{"name": "x"}]},
              {"connector": null, "scope": "source", "tools": [{"name": "y"}]}
            ]
            """);

        var items = GovernJson.Flatten(root, "tools");

        Assert.Equal("claudecode", items[0].GroupConnector);
        Assert.Null(items[1].GroupConnector);
        Assert.Equal("y", GovernJson.Str(items[1].Item, "name"));
    }

    [Fact]
    public void Only_the_items_key_the_caller_asked_for_makes_an_object_a_group()
    {
        var root = Parse("""[{"connector": "codex", "plugins": [{"id": "p"}]}]""");

        // Asked for skills: the object has no "skills" array, so it is an item (a strange one), not a group.
        var items = GovernJson.Flatten(root, "skills");

        var only = Assert.Single(items);
        Assert.Null(only.GroupConnector);
        Assert.Equal("codex", GovernJson.Str(only.Item, "connector"));
    }

    [Fact]
    public void Entries_that_are_not_objects_are_skipped_in_arrays_and_groups()
    {
        var root = Parse("""[1, "two", null, {"name": "a"}, {"connector": "codex", "skills": [3, {"name": "b"}, "x"]}]""");

        var items = GovernJson.Flatten(root, "skills");

        Assert.Equal(new[] { "a", "b" }, items.Select(i => GovernJson.Str(i.Item, "name")).ToArray());
    }

    [Fact]
    public void An_empty_array_and_an_empty_group_are_empty_lists()
    {
        Assert.Empty(GovernJson.Flatten(Parse("[]"), "skills"));
        Assert.Empty(GovernJson.Flatten(Parse("""{"connector": "codex", "skills": []}"""), "skills"));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("""{"no_items_here": true}""")]
    public void A_payload_that_is_neither_an_array_nor_a_group_is_a_format_error(string json)
    {
        Assert.Throws<FormatException>(() => GovernJson.Flatten(Parse(json), "skills"));
    }

    // ------------------------------------------------------------------ Interpret: state, scan, precedence

    [Fact]
    public void A_plugin_item_reads_its_scan_block_severity_and_findings()
    {
        var item = Parse("""
            {
              "id": "code-review", "status": "active", "enabled": true, "verdict": "warning",
              "scan": {"target": "C:\\p", "clean": false, "max_severity": "HIGH", "total_findings": 23},
              "actions": {"file": "none", "runtime": "enable", "install": "none"}
            }
            """);

        var state = GovernJson.Interpret(item);

        Assert.Equal("HIGH", state.Severity);
        Assert.Equal(23, state.Findings);
        Assert.False(state.ScanClean);
        Assert.Equal("HIGH · 23 findings", state.ScanLabel);
        Assert.Equal("High", state.ScanTone);
        Assert.True(state.NeedsAttention);
        Assert.Equal("Active", state.Label);
        Assert.Equal("Ok", state.Tone);
        Assert.Equal("file: none · runtime: enable · install: none", state.ActionsText);
    }

    [Fact]
    public void An_mcp_item_reads_a_bare_severity_string_when_it_has_no_scan_block()
    {
        var state = GovernJson.Interpret(Parse("""{"name": "srv", "severity": "MEDIUM"}"""));

        Assert.Equal("MEDIUM", state.Severity);
        Assert.Null(state.Findings);
        Assert.Equal("MEDIUM", state.ScanLabel);
        Assert.Equal("Medium", state.ScanTone);
        Assert.True(state.NeedsAttention);
    }

    [Fact]
    public void One_finding_is_singular()
    {
        var state = GovernJson.Interpret(Parse("""{"scan": {"max_severity": "LOW", "total_findings": 1}}"""));

        Assert.Equal("LOW · 1 finding", state.ScanLabel);
        Assert.False(state.NeedsAttention);
    }

    [Theory]
    [InlineData("""{"scan": {"clean": true, "max_severity": "CLEAN"}}""", "Scan clean", "Ok")]
    [InlineData("""{"scan": {"clean": true}}""", "Scan clean", "Ok")]
    [InlineData("""{"severity": "clean"}""", "Scan clean", "Ok")]
    [InlineData("""{"scan": {"max_severity": "NONE"}}""", "Not scanned", "Neutral")]
    [InlineData("""{"name": "x"}""", "Not scanned", "Neutral")]
    public void A_clean_or_absent_scan_is_labelled_as_such(string json, string label, string tone)
    {
        var state = GovernJson.Interpret(Parse(json));

        Assert.Equal(label, state.ScanLabel);
        Assert.Equal(tone, state.ScanTone);
        Assert.False(state.NeedsAttention);
    }

    [Fact]
    public void A_rejected_or_warning_verdict_needs_attention_without_any_severity()
    {
        Assert.True(GovernJson.Interpret(Parse("""{"verdict": "rejected"}""")).NeedsAttention);
        Assert.True(GovernJson.Interpret(Parse("""{"verdict": "Warning"}""")).NeedsAttention);
        Assert.False(GovernJson.Interpret(Parse("""{"verdict": "allowed"}""")).NeedsAttention);
    }

    [Theory]
    [InlineData("""{"actions": {"install": "block"}}""")]
    [InlineData("""{"verdict": "blocked"}""")]
    [InlineData("""{"status": "blocked"}""")]
    public void Every_spelling_of_blocked_is_blocked(string json)
    {
        var state = GovernJson.Interpret(Parse(json));

        Assert.True(state.Blocked);
        Assert.False(state.Allowed);
        Assert.Equal("Blocked", state.Label);
        Assert.Equal("Bad", state.Tone);
    }

    [Theory]
    [InlineData("""{"actions": {"install": "allow"}}""")]
    [InlineData("""{"verdict": "allowed"}""")]
    [InlineData("""{"status": "allowed"}""")]
    public void Every_spelling_of_allowed_is_allowed(string json)
    {
        var state = GovernJson.Interpret(Parse(json));

        Assert.True(state.Allowed);
        Assert.False(state.Blocked);
        Assert.Equal("Allowed", state.Label);
        Assert.Equal("Ok", state.Tone);
    }

    [Theory]
    [InlineData("""{"actions": {"file": "quarantine"}}""")]
    [InlineData("""{"verdict": "quarantined"}""")]
    [InlineData("""{"status": "quarantined"}""")]
    public void Every_spelling_of_quarantined_is_quarantined(string json)
    {
        var state = GovernJson.Interpret(Parse(json));

        Assert.True(state.Quarantined);
        Assert.Equal("Quarantined", state.Label);
        Assert.Equal("Bad", state.Tone);
    }

    [Theory]
    [InlineData("""{"actions": {"runtime": "disable"}}""")]
    [InlineData("""{"disabled": true}""")]
    [InlineData("""{"enabled": false}""")]
    [InlineData("""{"status": "disabled"}""")]
    public void Every_spelling_of_disabled_is_disabled(string json)
    {
        var state = GovernJson.Interpret(Parse(json));

        Assert.True(state.Disabled);
        Assert.Equal("Disabled", state.Label);
        Assert.Equal("Warn", state.Tone);
    }

    [Theory]
    [InlineData("""{"enabled": true}""")]
    [InlineData("""{"disabled": false}""")]
    [InlineData("""{"actions": {"runtime": "enable"}}""")]
    public void An_enabled_item_is_not_disabled(string json)
    {
        Assert.False(GovernJson.Interpret(Parse(json)).Disabled);
    }

    [Fact]
    public void Quarantine_beats_block_beats_disable_beats_allow_in_the_label()
    {
        var everything = GovernJson.Interpret(Parse(
            """{"actions": {"file": "quarantine", "install": "block", "runtime": "disable"}}"""));
        Assert.Equal("Quarantined", everything.Label);

        var blockedAndDisabled = GovernJson.Interpret(Parse("""{"actions": {"install": "block", "runtime": "disable"}}"""));
        Assert.Equal("Blocked", blockedAndDisabled.Label);

        var disabledAndAllowed = GovernJson.Interpret(Parse("""{"actions": {"install": "allow", "runtime": "disable"}}"""));
        Assert.Equal("Disabled", disabledAndAllowed.Label);
    }

    [Fact]
    public void An_item_that_is_both_blocked_and_allowed_is_only_blocked()
    {
        var state = GovernJson.Interpret(Parse("""{"actions": {"install": "block"}, "verdict": "allowed"}"""));

        Assert.True(state.Blocked);
        Assert.False(state.Allowed);
    }

    [Fact]
    public void An_unknown_status_is_shown_capitalized_and_no_status_is_unknown()
    {
        Assert.Equal("Pending", GovernJson.Interpret(Parse("""{"status": "pending"}""")).Label);
        Assert.Equal("Neutral", GovernJson.Interpret(Parse("""{"status": "pending"}""")).Tone);
        Assert.Equal("Unknown", GovernJson.Interpret(Parse("""{"name": "x"}""")).Label);
    }

    [Fact]
    public void An_item_with_no_actions_has_no_actions_text()
    {
        Assert.Null(GovernJson.Interpret(Parse("""{"name": "x"}""")).ActionsText);
    }

    // ------------------------------------------------------------------ the small readers

    [Fact]
    public void Str_reads_strings_numbers_and_booleans_and_treats_blank_as_absent()
    {
        var item = Parse("""{"s": "text", "n": 1.10, "t": true, "f": false, "blank": "   ", "nul": null, "obj": {}}""");

        Assert.Equal("text", GovernJson.Str(item, "s"));
        Assert.Equal("1.10", GovernJson.Str(item, "n"));
        Assert.Equal("true", GovernJson.Str(item, "t"));
        Assert.Equal("false", GovernJson.Str(item, "f"));
        Assert.Null(GovernJson.Str(item, "blank"));
        Assert.Null(GovernJson.Str(item, "nul"));
        Assert.Null(GovernJson.Str(item, "obj"));
        Assert.Null(GovernJson.Str(item, "missing"));
        Assert.Null(GovernJson.Str(Parse("[1]"), "s"));
    }

    [Fact]
    public void Bool_and_int_read_only_their_own_kinds()
    {
        var item = Parse("""{"b": true, "s": "true", "i": 7, "big": 9999999999, "f": 1.5, "str": "7"}""");

        Assert.True(GovernJson.Bool(item, "b"));
        Assert.Null(GovernJson.Bool(item, "s"));
        Assert.Equal(7, GovernJson.Int(item, "i"));
        Assert.Null(GovernJson.Int(item, "big"));
        Assert.Null(GovernJson.Int(item, "f"));
        Assert.Null(GovernJson.Int(item, "str"));
        Assert.Null(GovernJson.Int(item, "missing"));
    }

    [Fact]
    public void Obj_returns_only_objects()
    {
        var item = Parse("""{"o": {"a": 1}, "s": "x", "arr": []}""");

        Assert.NotNull(GovernJson.Obj(item, "o"));
        Assert.Null(GovernJson.Obj(item, "s"));
        Assert.Null(GovernJson.Obj(item, "arr"));
        Assert.Null(GovernJson.Obj(item, "missing"));
    }

    [Fact]
    public void An_mcp_args_array_is_joined_with_spaces_and_a_scalar_is_read_as_is()
    {
        Assert.Equal("-y @scope/pkg 3", GovernJson.JoinedArray(Parse("""{"args": ["-y", "@scope/pkg", 3]}"""), "args"));
        Assert.Null(GovernJson.JoinedArray(Parse("""{"args": []}"""), "args"));
        Assert.Equal("--flag", GovernJson.JoinedArray(Parse("""{"args": "--flag"}"""), "args"));
        Assert.Null(GovernJson.JoinedArray(Parse("""{}"""), "args"));
    }

    [Fact]
    public void Pretty_text_indents_json_and_leaves_anything_else_alone()
    {
        Assert.Contains("\n", GovernJson.PrettyText("""{"a":1}"""), StringComparison.Ordinal);
        Assert.Equal("not json at all", GovernJson.PrettyText("not json at all"));
    }
}
