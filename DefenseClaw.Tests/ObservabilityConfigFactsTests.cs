using System.Text.Json;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="ObservabilityConfigFacts"/> (CUST-272): what the Observability card reads from config.yaml because the plan document does not carry it -
/// the local store's retention window and files, whether the raw LLM-judge text is kept, and where each destination sends. A made-up config with a
/// credential in nearly every address; every credential starts with <c>synth</c>, and the tests hold that none of them is on anything the type
/// exposes.
/// </summary>
public class ObservabilityConfigFactsTests
{
    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthtoken", "synthkey", "synthpath-secret", "synthfrag" };

    private const string Config = """
        config_version: 8
        guardrail:
          enabled: true
          retain_judge_bodies: false
        observability:
          local:
            path: D:\evidence\audit-custom.db
            judge_bodies_path: ~/judge/bodies.db
            retention_days: 30
          destinations:
            - name: example-otlp
              kind: otlp
              endpoint: https://collector.example.test:4318/v1/logs
            - name: example-splunk
              kind: splunk_hec
              endpoint: https://synthuser:synthpass@splunk.example.test:8088/services/collector/event?token=synthtoken#synthfrag
              token_env: EXAMPLE_HEC_TOKEN
            - name: example-archive
              kind: http_jsonl
              enabled: false
              endpoint: https://archive.example.test/synthpath-secret/hook
            - name: example-metrics
              kind: prometheus
              listen: 127.0.0.1:9464
              path: /metrics
            - name: example-file
              kind: jsonl
              path: ~/.defenseclaw/gateway.jsonl
            - name: galileo
              kind: otlp
              preset: galileo
              signal_overrides:
                logs:
                  endpoint: https://synthuser:synthpass@logs.example.test/v1/logs?key=synthkey
                traces:
                  endpoint: https://traces.example.test:4318
                metrics:
                  endpoint: https://traces.example.test:4318/metrics
            - name: example-console
              kind: console
        """;

    private static string Everything(ObservabilityConfigFacts facts) => JsonSerializer.Serialize(facts);

    // ---- the local store ----

    [Fact]
    public void Retention_the_files_and_the_judge_setting_are_what_config_yaml_says()
    {
        var facts = ObservabilityConfigFacts.FromYaml(Config);

        Assert.Equal(30, facts.RetentionDays);
        Assert.Equal(@"D:\evidence\audit-custom.db", facts.LocalPath);
        Assert.Equal("~/judge/bodies.db", facts.JudgeBodiesPath);
        Assert.False(facts.JudgeCapture);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("90", 90L)]
    [InlineData("'30'", 30L)]
    [InlineData("\"7\"", 7L)]
    [InlineData("007", 7L)]
    [InlineData("106751", 106751L)]
    public void A_retention_window_is_a_whole_number_of_days_and_zero_is_without_limit(string written, long days)
    {
        var facts = ObservabilityConfigFacts.FromYaml($"observability:\n  local:\n    retention_days: {written}\n");

        Assert.Equal(days, facts.RetentionDays);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("thirty")]
    [InlineData("106752")]
    [InlineData("99999999999999999999")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("''")]
    [InlineData("~")]
    public void A_retention_window_that_is_not_a_non_negative_whole_number_is_not_a_window_and_nothing_is_guessed(string written)
    {
        var facts = ObservabilityConfigFacts.FromYaml($"observability:\n  local:\n    retention_days: {written}\n");

        Assert.Null(facts.RetentionDays);
    }

    [Fact]
    public void A_config_that_says_nothing_about_the_local_store_leaves_retention_and_the_files_to_the_runtimes_defaults()
    {
        var facts = ObservabilityConfigFacts.FromYaml("config_version: 8\nobservability:\n  destinations: []\n");

        Assert.Null(facts.RetentionDays);
        Assert.Null(facts.LocalPath);
        Assert.Null(facts.JudgeBodiesPath);
        Assert.True(facts.JudgeCapture);
        Assert.Empty(facts.Destinations);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("guardrail:\n  enabled: true\n", true)]
    [InlineData("guardrail:\n  retain_judge_bodies: true\n", true)]
    [InlineData("guardrail:\n  retain_judge_bodies: True\n", true)]
    [InlineData("guardrail:\n  retain_judge_bodies: TRUE\n", true)]
    [InlineData("guardrail:\n  retain_judge_bodies: 'true'\n", true)]
    [InlineData("guardrail:\n  retain_judge_bodies: false\n", false)]
    [InlineData("guardrail:\n  retain_judge_bodies: no\n", false)]
    [InlineData("guardrail:\n  retain_judge_bodies: yes\n", false)]
    [InlineData("guardrail:\n  retain_judge_bodies: 1\n", false)]
    [InlineData("guardrail:\n  retain_judge_bodies:\n", false)]
    [InlineData("guardrail: nothing\n", true)]
    public void Judge_capture_is_on_unless_config_yaml_says_something_other_than_true(string yaml, bool expected) =>
        Assert.Equal(expected, ObservabilityConfigFacts.FromYaml(yaml).JudgeCapture);

    // ---- where destinations send ----

    [Fact]
    public void Each_destination_is_shown_by_its_host_and_port_and_nothing_else_of_its_address()
    {
        var facts = ObservabilityConfigFacts.FromYaml(Config);

        Assert.Equal("collector.example.test:4318", facts.Destination("example-otlp")!.Endpoint);
        Assert.Equal("splunk.example.test:8088", facts.Destination("example-splunk")!.Endpoint);
        Assert.Equal("archive.example.test", facts.Destination("example-archive")!.Endpoint);
        Assert.Equal("127.0.0.1:9464", facts.Destination("example-metrics")!.Endpoint);
        Assert.Equal("~/.defenseclaw/gateway.jsonl", facts.Destination("example-file")!.Endpoint);
        Assert.Equal(string.Empty, facts.Destination("example-console")!.Endpoint);
    }

    [Fact]
    public void A_destination_with_an_endpoint_for_each_signal_shows_each_host_once()
    {
        var galileo = ObservabilityConfigFacts.FromYaml(Config).Destination("galileo")!;

        Assert.Equal("logs.example.test, traces.example.test:4318", galileo.Endpoint);
        Assert.Equal("galileo", galileo.Kind);
    }

    [Fact]
    public void A_kind_is_the_preset_when_there_is_one_else_the_kind()
    {
        var facts = ObservabilityConfigFacts.FromYaml(Config);

        Assert.Equal("otlp", facts.Destination("example-otlp")!.Kind);
        Assert.Equal("splunk_hec", facts.Destination("example-splunk")!.Kind);
        Assert.Equal("prometheus", facts.Destination("example-metrics")!.Kind);
        Assert.Equal("galileo", facts.Destination("galileo")!.Kind);
    }

    [Fact]
    public void No_credential_in_an_address_survives_anywhere_on_what_the_type_exposes()
    {
        var facts = ObservabilityConfigFacts.FromYaml(Config);

        var everything = Everything(facts);

        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase));
        Assert.All(facts.Destinations, d => Assert.All(Secrets, secret => Assert.DoesNotContain(secret, d.ToString(), StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain("://", string.Join(" ", facts.Destinations.Select(d => d.Endpoint)), StringComparison.Ordinal);
        Assert.DoesNotContain("@", string.Join(" ", facts.Destinations.Select(d => d.Endpoint)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_that_cannot_be_read_as_a_host_is_the_placeholder_and_not_the_text()
    {
        var yaml = """
            observability:
              destinations:
                - name: a
                  kind: otlp
                  endpoint: https://synthuser:synthpass@:99999/x?synthkey
                - name: b
                  kind: otlp
                  endpoint: https://collector.example.test/synthpath-secret@elsewhere.example.test/
            """;

        var facts = ObservabilityConfigFacts.FromYaml(yaml);

        Assert.Equal(EndpointDisplay.Unreadable, facts.Destination("a")!.Endpoint);
        Assert.Equal(EndpointDisplay.Unreadable, facts.Destination("b")!.Endpoint);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, Everything(facts), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_destination_is_found_by_its_name_without_case_and_a_stranger_is_not()
    {
        var facts = ObservabilityConfigFacts.FromYaml(Config);

        Assert.Equal("example-otlp", facts.Destination("EXAMPLE-OTLP")!.Name);
        Assert.Null(facts.Destination("not-a-destination"));
        Assert.Null(facts.Destination(""));
    }

    [Fact]
    public void An_entry_that_is_not_a_destination_is_skipped_and_the_rest_is_read()
    {
        var yaml = """
            observability:
              destinations:
                - just-a-string
                - 7
                - kind: otlp
                  endpoint: https://nameless.example.test
                - name: ""
                - name: good
                  kind: otlp
                  endpoint: https://good.example.test:4318
                -
            """;

        var facts = ObservabilityConfigFacts.FromYaml(yaml);

        var good = Assert.Single(facts.Destinations);
        Assert.Equal("good", good.Name);
        Assert.Equal("good.example.test:4318", good.Endpoint);
    }

    [Fact]
    public void The_number_of_destinations_read_is_bounded()
    {
        var yaml = "observability:\n  destinations:\n" + string.Concat(
            Enumerable.Range(0, ObservabilityConfigFacts.MaxDestinations + 20).Select(i => $"    - name: d{i}\n      kind: otlp\n"));

        Assert.Equal(ObservabilityConfigFacts.MaxDestinations, ObservabilityConfigFacts.FromYaml(yaml).Destinations.Count);
    }

    // ---- text that is not config ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("- a\n- b\n")]
    [InlineData("just a sentence")]
    [InlineData("observability: [unclosed")]
    [InlineData("a: 1\na: 2\n")]
    [InlineData("observability: nothing\n")]
    [InlineData("observability:\n  local: nothing\n  destinations: nothing\n")]
    [InlineData("observability:\n  destinations:\n    key: value\n")]
    public void Text_that_is_not_a_config_says_nothing_and_never_throws(string? yaml)
    {
        var facts = ObservabilityConfigFacts.FromYaml(yaml);

        Assert.Null(facts.RetentionDays);
        Assert.Null(facts.LocalPath);
        Assert.Null(facts.JudgeBodiesPath);
        Assert.Empty(facts.Destinations);
    }

    [Fact]
    public void A_config_larger_than_any_config_is_not_read()
    {
        var yaml = "observability:\n  local:\n    retention_days: 5\n# " + new string('x', ObservabilityConfigFacts.MaxLength);

        Assert.Same(ObservabilityConfigFacts.Empty, ObservabilityConfigFacts.FromYaml(yaml));
    }

    [Fact]
    public void A_path_that_is_not_text_or_is_absurdly_long_is_not_a_path()
    {
        var yaml = $"observability:\n  local:\n    path: [a, b]\n    judge_bodies_path: {new string('p', 5000)}\n";

        var facts = ObservabilityConfigFacts.FromYaml(yaml);

        Assert.Null(facts.LocalPath);
        Assert.Null(facts.JudgeBodiesPath);
    }

    // ---- from the app's own document ----

    [Fact]
    public void The_apps_parsed_config_gives_the_same_facts_as_its_text()
    {
        var document = ConfigStore.Parse(Config);

        Assert.Equal(ObservabilityConfigFacts.FromYaml(Config), ObservabilityConfigFacts.FromConfig(document), FactsComparer.Instance);
        Assert.Same(ObservabilityConfigFacts.Empty, ObservabilityConfigFacts.FromConfig(null));
    }

    /// <summary>The destinations are lists, which a record compares by reference: compare what they say.</summary>
    private sealed class FactsComparer : IEqualityComparer<ObservabilityConfigFacts>
    {
        public static FactsComparer Instance { get; } = new();

        public bool Equals(ObservabilityConfigFacts? x, ObservabilityConfigFacts? y) =>
            x is not null && y is not null && Everything(x) == Everything(y);

        public int GetHashCode(ObservabilityConfigFacts obj) => Everything(obj).GetHashCode(StringComparison.Ordinal);
    }
}
