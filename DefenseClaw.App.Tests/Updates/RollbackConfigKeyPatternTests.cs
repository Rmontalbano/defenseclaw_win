using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The rollback note and review warning with the runtime's list of keys a release refuses (upgrade_shim.py@1.0.10:89-181): keys set since an upgrade to 1.0.10,
/// the note's bounded list, and the owner's shape (a 1.0.10 config with none of the new keys says nothing). Synthetic values only.
/// </summary>
public sealed class RollbackConfigKeyPatternTests
{
    private const string CascadeAndContext = "guardrail:\n  cascade:\n    judge:\n      max_action: block\nrouting:\n  context: {}\n";

    [Fact]
    public void Keys_set_since_1_0_10_are_named_when_going_back_to_1_0_9()
    {
        var result = RollbackConfigKeys.Evaluate(CascadeAndContext, "1.0.9");

        Assert.NotNull(result);
        Assert.Equal(["guardrail.cascade", "routing.context"], result.Keys);
        Assert.Contains("DefenseClaw 1.0.9 does not accept guardrail.cascade, routing.context, so those settings", result.Note, StringComparison.Ordinal);
        Assert.StartsWith("Rollback puts back the config.yaml saved when you upgraded.", result.Warning, StringComparison.Ordinal);
        Assert.EndsWith(result.Note, result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Going_back_to_1_0_10_says_nothing_about_those_keys()
    {
        Assert.Null(RollbackConfigKeys.Evaluate(CascadeAndContext, "1.0.10"));
    }

    [Fact]
    public void A_1_0_10_config_with_none_of_the_new_keys_says_nothing()
    {
        const string owner = """
            gateway:
              host: 127.0.0.1
              port: 18970
            guardrail:
              enabled: true
              mode: observe
              judge:
                enabled: false
            observability:
              local:
                enabled: true
            claw:
              mode: codex
            """;

        Assert.Null(RollbackConfigKeys.Evaluate(owner, "1.0.9"));
        Assert.Null(RollbackConfigKeys.Evaluate(owner, "1.0.7"));
    }

    [Fact]
    public void The_1_0_7_note_for_a_1_0_8_key_keeps_its_exact_words()
    {
        var result = RollbackConfigKeys.Evaluate("guardrail:\n  judge:\n    extra_body:\n      k: v\n", "1.0.7");

        Assert.NotNull(result);
        Assert.Equal(
            "DefenseClaw 1.0.7 does not accept guardrail.judge.extra_body, so those settings are not in the config.yaml it gets back. " +
            "Today's config.yaml is kept in previous\\data. Run 'defenseclaw rollback' again to bring it back.",
            result.Note);
    }

    [Fact]
    public void A_note_lists_ten_keys_and_counts_the_rest()
    {
        const string yaml = "guardrail:\n  cascade: {}\nrouting:\n  admin: 1\n  allow_user_override: 1\n  categories: 1\n  classifier: 1\n  context: 1\n  default_alias: 1\n" +
                            "  min_confidence: 1\n  on_classifier_error: 1\n  route_only: 1\n  sensitive: 1\n  systemone: 1\n";

        var result = RollbackConfigKeys.Evaluate(yaml, "1.0.9");

        Assert.NotNull(result);
        Assert.Equal(12, result.Keys.Count);
        Assert.Contains("guardrail.cascade, routing.admin, routing.allow_user_override, routing.categories, routing.classifier, routing.context, " +
                        "routing.default_alias, routing.min_confidence, routing.on_classifier_error, routing.route_only, and 2 more, so those settings", result.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("routing.sensitive", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_profile_name_that_is_not_safe_to_show_is_reported_under_the_pattern()
    {
        var result = RollbackConfigKeys.Evaluate("guardrail:\n  profiles:\n    \"bad\\nname\": {routing: 1}\n", "1.0.9");

        Assert.NotNull(result);
        Assert.Equal(["guardrail.profiles.*.routing"], result.Keys);
        Assert.DoesNotContain('\n', result.Note);
    }
}
