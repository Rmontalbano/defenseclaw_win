using DefenseClaw.App.Services.Observability;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Observability;

namespace DefenseClaw.App.Tests.Observability;

/// <summary>
/// The Agent360 address (CUST-354): the dashboard's uid and variable names, escaping, the time range, and the rule that only a loopback address the app
/// built is ever opened. Pure: nothing here touches the network, a process or the install.
/// </summary>
public sealed class Agent360LinkTests
{
    private const string Base = "http://localhost:3000";

    [Fact]
    public void Without_a_connector_the_link_is_the_dashboard_with_its_default_range()
    {
        Assert.Equal("http://localhost:3000/d/defenseclaw-agent-360?from=now-6h&to=now", Agent360Link.Build(Base));
    }

    [Fact]
    public void A_connector_becomes_the_connector_variable()
    {
        Assert.Equal(
            "http://localhost:3000/d/defenseclaw-agent-360?var-connector=claudecode&from=now-6h&to=now",
            Agent360Link.Build(Base, "claudecode"));
    }

    [Fact]
    public void An_agent_adds_the_agent_variable_and_the_scope_that_matches_it_by_id()
    {
        Assert.Equal(
            "http://localhost:3000/d/defenseclaw-agent-360?var-connector=codex&var-agent=agent-1&var-scope_label=gen_ai_agent_id&from=now-6h&to=now",
            Agent360Link.Build(Base, "codex", "agent-1"));
    }

    [Fact]
    public void Names_are_escaped_so_they_cannot_add_a_parameter_or_end_the_query()
    {
        var link = Agent360Link.Build(Base, "a&var-agent=x#y", "b c/../d?e");

        Assert.NotNull(link);
        Assert.Contains("var-connector=a%26var-agent%3Dx%23y&", link, StringComparison.Ordinal);
        Assert.Contains("var-agent=b%20c%2F..%2Fd%3Fe&", link, StringComparison.Ordinal);
        Assert.DoesNotContain("#", link, StringComparison.Ordinal);
        Assert.Equal(1, link.Count(c => c == '?'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData("bad\u0000name")]
    public void A_name_that_is_empty_or_holds_a_control_character_is_left_off(string connector)
    {
        Assert.Equal("http://localhost:3000/d/defenseclaw-agent-360?from=now-6h&to=now", Agent360Link.Build(Base, connector));
    }

    [Fact]
    public void A_name_longer_than_128_characters_is_left_off()
    {
        Assert.DoesNotContain("var-connector", Agent360Link.Build(Base, new string('a', 129)), StringComparison.Ordinal);
        Assert.Contains("var-connector", Agent360Link.Build(Base, new string('a', 128)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_range_is_written_as_from_and_to()
    {
        var link = Agent360Link.Build(Base, "claudecode", range: Agent360Range.Last(24));

        Assert.EndsWith("&from=now-24h&to=now", link, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("now-6h", "now", true)]
    [InlineData("now-30m", "now-5m", true)]
    [InlineData("1700000000000", "1700003600000", true)]
    [InlineData("now-6h&x=1", "now", false)]
    [InlineData("now-6", "now", false)]
    [InlineData("javascript:alert(1)", "now", false)]
    [InlineData("", "now", false)]
    public void Only_ranges_grafana_reads_are_accepted(string from, string to, bool accepted)
    {
        Assert.Equal(accepted, Agent360Range.TryCreate(from, to) is not null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(10000)]
    public void An_hours_count_out_of_range_falls_back_to_the_dashboards_default(int hours)
    {
        Assert.Equal(Agent360Range.Default, Agent360Range.Last(hours));
    }

    [Theory]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("HTTP://LocalHost:3000/", "http://localhost:3000")]
    [InlineData("http://127.0.0.1:3100/some/path?x=1#frag", "http://127.0.0.1:3100")]
    [InlineData("https://localhost:8443", "https://localhost:8443")]
    [InlineData("http://[::1]:3000", "http://[::1]:3000")]
    public void A_loopback_address_is_reduced_to_scheme_host_and_port(string value, string expected)
    {
        Assert.Equal(expected, Agent360Link.TryNormalizeBase(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost:3000")]
    [InlineData("ftp://localhost:3000")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows")]
    [InlineData("http://grafana.example.test:3000")]
    [InlineData("http://10.0.0.5:3000")]
    [InlineData("http://localhost.example.test:3000")]
    [InlineData("http://user:pass@localhost:3000")]
    [InlineData("http://localhost@evil.example.test:3000")]
    public void Anything_that_is_not_a_plain_loopback_http_address_is_refused(string? value)
    {
        Assert.Null(Agent360Link.TryNormalizeBase(value));
        Assert.Null(Agent360Link.Build(value, "claudecode"));
    }

    [Fact]
    public void The_launcher_accepts_what_the_builder_makes_and_nothing_else()
    {
        var opened = new List<string>();
        var built = Agent360Link.Build(Base, "claudecode")!;

        Assert.True(Agent360Launcher.TryOpen(built, opened.Add));
        Assert.Equal(new[] { built }, opened);

        Assert.False(Agent360Launcher.TryOpen("https://example.test/d/defenseclaw-agent-360", opened.Add));
        Assert.False(Agent360Launcher.TryOpen("http://localhost:3000/login", opened.Add));
        Assert.False(Agent360Launcher.TryOpen("http://localhost:3000/d/defenseclaw-agent-360#x", opened.Add));
        Assert.False(Agent360Launcher.TryOpen("calc.exe", opened.Add));
        Assert.False(Agent360Launcher.TryOpen(null, opened.Add));
        Assert.Single(opened);
    }

    // ---- where Grafana is ----

    [Fact]
    public void The_url_command_is_a_read()
    {
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(GrafanaEndpoint.Argv));
    }

    [Fact]
    public void The_grafana_address_is_read_from_the_commands_json()
    {
        const string output = "{\"otlp_endpoint\":\"127.0.0.1:4317\",\"grafana_url\":\"http://localhost:3300\",\"loki_url\":\"http://localhost:3100\"}";

        Assert.Equal("http://localhost:3300", GrafanaEndpoint.ParseBase(output));
    }

    [Fact]
    public void A_warning_before_the_document_does_not_hide_it()
    {
        const string output = "warning: docker is not installed\n{\"grafana_url\":\"http://127.0.0.1:3000\"}\n";

        Assert.Equal("http://127.0.0.1:3000", GrafanaEndpoint.ParseBase(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"loki_url\":\"http://localhost:3100\"}")]
    [InlineData("{\"grafana_url\":42}")]
    [InlineData("{\"grafana_url\":\"https://grafana.example.test\"}")]
    public void No_usable_address_in_the_output_is_null_and_the_default_is_used(string? output)
    {
        Assert.Null(GrafanaEndpoint.ParseBase(output));
    }

    // ---- the plan ----

    private static PlanDestination Local(bool enabled = true, string[]? signals = null, int routed = 14, bool conditional = false) =>
        new("local-observability", "otlp", enabled, signals ?? new[] { "logs", "traces", "metrics" }, routed, new[] { "none" }, conditional, null);

    private static ObservabilityPlan Plan(params PlanDestination[] destinations) =>
        new("canonical_go_compiled_routes", 8, "digest", 14, destinations, 0);

    [Fact]
    public void A_plan_that_routes_everything_to_the_local_stack_says_nothing()
    {
        Assert.Equal(string.Empty, Agent360Coverage.NoteFor(Plan(Local())));
    }

    [Fact]
    public void Without_a_plan_nothing_is_claimed()
    {
        Assert.Equal(string.Empty, Agent360Coverage.NoteFor(null));
    }

    [Fact]
    public void A_missing_destination_or_a_disabled_one_is_partial_coverage()
    {
        Assert.StartsWith("Partial coverage", Agent360Coverage.NoteFor(Plan()), StringComparison.Ordinal);
        Assert.StartsWith("Partial coverage", Agent360Coverage.NoteFor(Plan(Local(enabled: false))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrowed_route_names_what_is_narrowed()
    {
        var note = Agent360Coverage.NoteFor(Plan(Local(signals: new[] { "logs" }, routed: 9, conditional: true)));

        Assert.StartsWith("Partial coverage", note, StringComparison.Ordinal);
        Assert.Contains("traces, metrics", note, StringComparison.Ordinal);
        Assert.Contains("only 9 of 14", note, StringComparison.Ordinal);
        Assert.Contains("severities, sources or connectors", note, StringComparison.Ordinal);
    }
}
