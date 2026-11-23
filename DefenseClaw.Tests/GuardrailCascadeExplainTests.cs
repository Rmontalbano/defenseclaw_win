using DefenseClaw.Core.Policy;

namespace DefenseClaw.Tests;

/// <summary>
/// The JSON of <c>defenseclaw guardrail cascade explain --json</c> (runtime 1.0.10) read into lines in the runtime's own terms
/// (<see cref="GuardrailCascadeExplain"/>): a full synthetic fixture of the shape cascade_explain.go prints, an invalid cascade, ignored values, and
/// every refusal (not JSON, wrong shape, too long, a word that is not a string). Strings are cut; entries are capped.
/// </summary>
public sealed class GuardrailCascadeExplainTests
{
    private const string Full = """
        {
          "cascade": {
            "connector": "codex",
            "valid": true,
            "rules": { "mode": "action", "pack": "default", "tier": "default", "block_at": "CRITICAL", "alert_at": "MEDIUM", "source": "default", "hilt": false },
            "system_one": { "runs": "with_judge", "protocol": "system-one", "allow_threshold": 0.3, "block_threshold": 0.75, "on_unsure": "confirm" },
            "judge": { "runs": "escalation", "max_action": "alert", "block_needs_system_one": false, "no_verdict": "rules" },
            "unknown": { "setting": "block", "action": "block" },
            "lanes": [
              { "name": "alignment", "mode": "off", "default": "off" },
              { "name": "effects", "mode": "shadow", "default": "shadow", "shadow_only": "GAP-0191, GAP-0192" },
              { "name": "nonshell", "mode": "on", "default": "on" }
            ],
            "policy": { "active": true, "module": "synthetic.rego", "digest": "abc", "lowest": "alert", "highest": "block", "timeout_ms": 5 },
            "critical_floor": "alert",
            "hook_fail_mode": "open",
            "sources": { "unknown": "guardrail.cascade.unknown" }
          }
        }
        """;

    [Fact]
    public void A_full_explanation_reads_into_the_runtimes_terms()
    {
        var explain = GuardrailCascadeExplain.Parse(Full);

        Assert.NotNull(explain);
        Assert.True(explain!.Valid);
        Assert.Equal("codex", explain.Connector);
        Assert.Equal(
            new[]
            {
                "Scope: codex",
                "Rules: default pack, action mode, block at CRITICAL, alert at MEDIUM",
                "System One: runs with the judge (protocol system-one)",
                "LLM judge: on escalation",
                "  can at most: alert; no verdict: rules",
                "Unknown tier: block (block)",
                "Rego policy: on (lowest alert, highest block)",
                "CRITICAL floor: alert",
                "Lanes: alignment off, effects shadow (shadow only), nonshell on",
                "Hook fail mode: open",
            },
            explain.Lines);
    }

    [Theory]
    [InlineData("off", "System One: off")]
    [InlineData("with_judge", "System One: runs with the judge")]
    [InlineData("alone", "System One: runs alone")]
    public void System_one_runs_in_the_three_ways_the_runtime_names(string runs, string line)
    {
        var json = "{\"cascade\":{\"valid\":true,\"rules\":{},\"system_one\":{\"runs\":\"" + runs + "\"}}}";

        Assert.Contains(line, GuardrailCascadeExplain.Parse(json)!.Lines);
    }

    [Theory]
    [InlineData("off", "LLM judge: off")]
    [InlineData("escalation", "LLM judge: on escalation")]
    [InlineData("always", "LLM judge: always")]
    public void The_judge_runs_in_the_three_ways_the_runtime_names(string runs, string line)
    {
        var json = "{\"cascade\":{\"valid\":true,\"rules\":{},\"judge\":{\"runs\":\"" + runs + "\"}}}";

        Assert.Contains(line, GuardrailCascadeExplain.Parse(json)!.Lines);
    }

    [Fact]
    public void An_inactive_policy_says_off_and_no_connector_says_global()
    {
        var explain = GuardrailCascadeExplain.Parse("{\"cascade\":{\"valid\":true,\"rules\":{\"pack\":\"strict\"},\"policy\":{\"active\":false}}}")!;

        Assert.Equal("Scope: global default", explain.Lines[0]);
        Assert.Contains("Rego policy: off", explain.Lines);
        Assert.Contains("Rules: strict pack", explain.Lines);
    }

    [Fact]
    public void An_invalid_cascade_says_what_the_gateway_decides_with_and_what_is_weaker()
    {
        var json = """
            {"cascade":{"connector":"","valid":false,
              "invalid":{"error":"unknown: sideways is not one of pack, alert, confirm, block","fallback":"pack_defaults","weaker":["judge.max_action","unknown"]},
              "rules":{"mode":"observe","pack":"default"},
              "ignored":[{"path":"guardrail.connectors.codex.cascade.admin","reason":"the administrator's value applies"}]}}
            """;

        var explain = GuardrailCascadeExplain.Parse(json)!;

        Assert.False(explain.Valid);
        Assert.Contains("Invalid: unknown: sideways is not one of pack, alert, confirm, block; the gateway decides with the rule pack defaults", explain.Lines);
        Assert.Contains("This is weaker than what the configuration says: judge.max_action; unknown", explain.Lines);
        Assert.Contains("Not applied: guardrail.connectors.codex.cascade.admin: the administrator's value applies", explain.Lines);
    }

    [Fact]
    public void The_last_known_good_fallback_is_named()
    {
        var json = "{\"cascade\":{\"valid\":false,\"invalid\":{\"error\":\"bad\",\"fallback\":\"last_known_good\"},\"rules\":{}}}";

        Assert.Contains("Invalid: bad; the gateway decides with the last-known-good cascade", GuardrailCascadeExplain.Parse(json)!.Lines);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"cascade\":3}")]
    [InlineData("{\"cascade\":{}}")]
    [InlineData("{\"cascade\":{\"valid\":\"yes\",\"rules\":{}}}")]
    [InlineData("{\"cascade\":{\"valid\":true}}")]
    [InlineData("{\"cascade\":{\"valid\":true,\"rules\":[]}}")]
    [InlineData("{\"cascade\":{\"valid\":true,\"rules\":{\"pack\":7}}}")]
    [InlineData("{\"cascade\":{\"valid\":true,\"rules\":{},\"lanes\":{\"a\":1}}}")]
    [InlineData("{\"cascade\":{\"valid\":true,\"rules\":{},\"lanes\":[3]}}")]
    [InlineData("{\"cascade\":{\"valid\":true,\"rules\":{},\"ignored\":\"x\"}}")]
    [InlineData("{\"cascade\":{\"valid\":false,\"invalid\":{\"weaker\":[1]},\"rules\":{}}}")]
    [InlineData("{\"cascade\":{\"valid\":true,\"rules\":{},\"policy\":{\"active\":\"yes\"}}}")]
    [InlineData("{\"cascade\":{\"valid\":true,\"rules\":{}")]
    public void A_shape_problem_is_null_so_the_caller_falls_back_to_the_config_lines(string? json)
    {
        Assert.Null(GuardrailCascadeExplain.Parse(json));
    }

    [Fact]
    public void Input_over_256_KiB_is_refused()
    {
        var padding = new string('x', GuardrailCascadeExplain.MaxInputChars);
        var json = "{\"cascade\":{\"valid\":true,\"rules\":{},\"connector\":\"" + padding + "\"}}";

        Assert.Null(GuardrailCascadeExplain.Parse(json));
    }

    [Fact]
    public void Every_string_is_cut_to_200_characters_and_made_fit_to_draw()
    {
        var long200 = new string('a', 500);
        var json = "{\"cascade\":{\"connector\":\"" + long200 + "\",\"valid\":true,\"rules\":{\"pack\":\"line1\\nline2\\u202eend\"}}}";

        var explain = GuardrailCascadeExplain.Parse(json)!;

        Assert.Equal(GuardrailCascadeExplain.MaxStringChars, explain.Connector.Length);
        Assert.All(explain.Lines, l => Assert.DoesNotContain('\n', l));
        Assert.DoesNotContain(explain.Lines, l => l.Contains('‮'));
    }

    [Fact]
    public void At_most_64_lanes_and_64_ignored_values_are_read()
    {
        var lanes = string.Join(",", Enumerable.Range(0, 200).Select(i => "{\"name\":\"lane" + i + "\",\"mode\":\"on\"}"));
        var ignored = string.Join(",", Enumerable.Range(0, 200).Select(i => "{\"path\":\"p" + i + "\",\"reason\":\"r\"}"));
        var json = "{\"cascade\":{\"valid\":true,\"rules\":{},\"lanes\":[" + lanes + "],\"ignored\":[" + ignored + "]}}";

        var explain = GuardrailCascadeExplain.Parse(json)!;

        Assert.Equal(64, explain.Lines.Count(l => l.StartsWith("Not applied:", StringComparison.Ordinal)));
        var laneLine = explain.Lines.Single(l => l.StartsWith("Lanes:", StringComparison.Ordinal));
        Assert.Equal(64, laneLine.Split(',').Length);
    }

    [Fact]
    public void The_error_line_is_the_first_line_only_and_bounded()
    {
        Assert.Equal(
            "Could not run defenseclaw guardrail cascade explain: the gateway binary was not found",
            GuardrailCascadeExplain.ErrorLine("the gateway binary was not found\nsecond line\nthird"));
        Assert.Equal("Could not run defenseclaw guardrail cascade explain: no reason given", GuardrailCascadeExplain.ErrorLine(null));
        Assert.True(GuardrailCascadeExplain.ErrorLine(new string('z', 5000)).Length <= "Could not run defenseclaw guardrail cascade explain: ".Length + 200);
    }
}
