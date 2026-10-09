using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Observability;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="ObservabilityPlanParser"/> and the labels built from a plan (CUST-272), over the document <c>defenseclaw observability plan --format json</c>
/// prints. Two fixtures: the newer runtime's capture of a fresh install (one destination), and a synthetic plan written from the 0.8.10 emitter's code
/// (<c>commands/cmd_observability.py</c>) over invented destinations - enabled, disabled, advanced routes, metrics only - because a fresh install
/// has only the local store. The emitter's plan code is the same in both runtimes (the newer one differs in <c>destination test</c> and in a note under
/// the custody table), so one parser reads both by presence.
/// </summary>
public class ObservabilityPlanParserTests
{
    private const string PinnedPlan = "runtime-95159fd/cli/observability-plan.json";
    private const string DestinationsPlan = "runtime-0.8.10/cli/observability-plan.destinations.synthetic.json";

    private static ObservabilityPlan Plan(string fixture)
    {
        var result = ObservabilityPlanParser.Parse(FixtureFiles.ReadText(fixture));
        Assert.True(result.IsOk, result.Error);
        return result.Plan!;
    }

    // ---- the fixtures ----

    [Fact]
    public void The_newer_runtimes_fresh_plan_is_one_local_store_that_every_bucket_reaches_unredacted()
    {
        var plan = Plan(PinnedPlan);

        Assert.Equal("canonical_go_compiled_routes", plan.Basis);
        Assert.Equal(8, plan.ConfigVersion);
        Assert.Equal("9a00b320fd5da3ca54327fdf91e8ef9e04e2c88a57308c8540b73bdbdc313259", plan.PlanDigest);
        Assert.Equal(14, plan.BucketCount);
        Assert.Equal(0, plan.UnreadableRows);

        var local = Assert.Single(plan.Destinations);
        Assert.Equal("local-sqlite", local.Name);
        Assert.True(local.Enabled);
        Assert.Equal(new[] { "logs" }, local.Signals);
        Assert.Equal(14, local.RoutedBuckets);
        Assert.Equal(new[] { "none" }, local.RedactionProfiles);
        Assert.Equal("unredacted (none)", local.RedactionLabel);
        Assert.False(local.HasConditionalRoutes);
        Assert.Null(local.Limits);
        Assert.Equal("not-applicable", local.LimitsLabel);
        Assert.Equal(string.Empty, local.Kind);

        Assert.Equal("per-route · unredacted", plan.RedactionSummary);
    }

    [Fact]
    public void The_0_8_10_shaped_plan_reads_each_destination_off_its_rows_and_its_delivery_limits()
    {
        var plan = Plan(DestinationsPlan);

        Assert.Equal(14, plan.BucketCount);
        Assert.Equal(
            new[] { "local-sqlite", "example-otlp", "example-splunk", "example-archive", "example-metrics" },
            plan.Destinations.Select(d => d.Name).ToArray());

        // The local store: the mandatory floor reaches the two buckets that collect no logs (a conditional "floor_only" row), so all fourteen.
        var local = plan.Destination("local-sqlite")!;
        Assert.True(local.Enabled);
        Assert.Equal(new[] { "logs" }, local.Signals);
        Assert.Equal(14, local.RoutedBuckets);
        Assert.Equal("unredacted (none)", local.RedactionLabel);
        Assert.False(local.HasConditionalRoutes);

        // An OTLP destination that takes everything: every collected bucket but the one that collects nothing, redacted, with its batch limits.
        var otlp = plan.Destination("example-otlp")!;
        Assert.True(otlp.Enabled);
        Assert.Equal("otlp", otlp.Kind);
        Assert.Equal(new[] { "logs", "traces", "metrics" }, otlp.Signals);
        Assert.Equal(13, otlp.RoutedBuckets);
        Assert.Equal("redacted: sensitive", otlp.RedactionLabel);
        Assert.Equal("queue=2048 items/64.0 MiB; batch=256 items/8.0 MiB; delay=1000ms", otlp.LimitsLabel);
        Assert.Equal(new DeliveryLimits(2048, 67108864, 256, 8388608, 1000), otlp.Limits);

        // Advanced routes: two send, three depend on the event's severity (the plan cannot say), seven match no route at all.
        var splunk = plan.Destination("example-splunk")!;
        Assert.True(splunk.Enabled);
        Assert.Equal("splunk_hec", splunk.Kind);
        Assert.Equal(new[] { "logs" }, splunk.Signals);
        Assert.Equal(5, splunk.RoutedBuckets);
        Assert.True(splunk.HasConditionalRoutes);
        Assert.Equal(new[] { "none", "strict" }, splunk.RedactionProfiles);
        Assert.Equal("mixed: none, strict (+ conditional routes)", splunk.RedactionLabel);
        Assert.Equal("queue=1024 items/32.0 MiB; batch=100 items/1.0 MiB; delay=500ms", splunk.LimitsLabel);

        // Off by policy: every row says so, so nothing is routed, no signal is known and no redaction applies; its limits are still configured.
        var archive = plan.Destination("example-archive")!;
        Assert.False(archive.Enabled);
        Assert.Empty(archive.Signals);
        Assert.Equal(0, archive.RoutedBuckets);
        Assert.Empty(archive.RedactionProfiles);
        Assert.Equal("not-applicable", archive.RedactionLabel);
        Assert.Equal("http_jsonl", archive.Kind);
        Assert.Equal("queue=2048 items/64.0 MiB; batch=100 items/8.0 MiB; delay=1000ms", archive.LimitsLabel);

        // Metrics are not redacted: the destination is on and routed, and has no profile to show.
        var metrics = plan.Destination("example-metrics")!;
        Assert.True(metrics.Enabled);
        Assert.Equal(new[] { "metrics" }, metrics.Signals);
        Assert.Equal(13, metrics.RoutedBuckets);
        Assert.Equal("not-applicable", metrics.RedactionLabel);
        Assert.Equal("not-applicable", metrics.LimitsLabel);

        Assert.Equal(new[] { "none", "sensitive", "strict" }, plan.RedactionProfiles);
        Assert.Equal("per-route · none,sensitive,strict", plan.RedactionSummary);
    }

    [Fact]
    public void The_synthetic_plan_is_what_the_emitter_could_print_no_key_the_emitter_does_not_write_and_no_credential_in_it()
    {
        var text = FixtureFiles.ReadText(DestinationsPlan);
        using var document = JsonDocument.Parse(text);

        // cmd_observability.py writes exactly these top-level keys, and a row has these keys (potential_action and condition on a conditional one).
        Assert.Equal(
            new[] { "basis", "config_version", "connector_export_custody", "delivery", "network_validation", "plan_digest", "rows" },
            document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "bucket", "collected", "decision", "destination", "redaction_profile", "reload_applicability", "route", "signal", "potential_action", "condition", "compatibility",
        };
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            Assert.All(row.EnumerateObject().Select(p => p.Name), key => Assert.Contains(key, allowed));
        }

        // A plan names destinations and never addresses: nothing in it can hold a credential.
        Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
    }

    // ---- shapes the parser is asked to survive ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("no json here")]
    [InlineData("[]")]
    [InlineData("[{\"rows\":[]}]")]
    [InlineData("{}")]
    [InlineData("{\"rows\":{}}")]
    [InlineData("{\"rows\":\"synthsecret\"}")]
    [InlineData("{\"rows\":[]}")]
    [InlineData("{\"rows\":[1,2,3]}")]
    [InlineData("{\"rows\":[{}]}")]
    [InlineData("{\"rows\":[{\"bucket\":\"b\",\"signal\":\"logs\",\"destination\":\"\",\"decision\":\"send\"}]}")]
    [InlineData("{\"rows\":[{\"bucket\":\"b\",\"signal\":\"logs\",\"destination\":7,\"decision\":\"send\"}]}")]
    [InlineData("{\"rows\":[{\"bucket\":\"b\",\"signal\":\"logs\",\"destination\":\"d\"}]}")]
    [InlineData("{\"rows\":[{\"bucket\":")]
    [InlineData("{\"rows\":[{\"bucket\":\"b\",\"signal\":\"logs\",\"destination\":\"d\",\"decision\":\"send\"}]} trailing synthsecret")]
    public void A_document_that_is_not_a_plan_is_refused_with_a_sentence_that_quotes_none_of_it(string? text)
    {
        var result = ObservabilityPlanParser.Parse(text);

        Assert.False(result.IsOk);
        Assert.Null(result.Plan);
        Assert.NotEmpty(result.Error);
        Assert.DoesNotContain("synthsecret", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_deeper_than_any_plan_is_refused_not_followed()
    {
        var deep = "{\"rows\":" + string.Concat(Enumerable.Repeat("[", 500)) + string.Concat(Enumerable.Repeat("]", 500)) + "}";

        Assert.False(ObservabilityPlanParser.Parse(deep).IsOk);
    }

    [Fact]
    public void A_document_larger_than_the_limit_is_refused_unread()
    {
        var big = "{\"rows\":[],\"x\":\"" + new string('a', ObservabilityPlanParser.MaxLength) + "\"}";

        var result = ObservabilityPlanParser.Parse(big);

        Assert.False(result.IsOk);
        Assert.Contains("larger", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_banner_before_the_json_is_skipped()
    {
        var result = ObservabilityPlanParser.Parse("DefenseClaw 0.8.10\n" + FixtureFiles.ReadText(PinnedPlan));

        Assert.True(result.IsOk, result.Error);
        Assert.Equal("local-sqlite", Assert.Single(result.Plan!.Destinations).Name);
    }

    [Fact]
    public void A_row_of_the_wrong_shape_is_skipped_and_counted_and_the_rest_is_read()
    {
        var json = """
            {"rows":[
              {"bucket":"a","signal":"logs","destination":"x","decision":"send","redaction_profile":"strict","collected":true},
              7,
              {"bucket":"b","signal":"logs","destination":"x"},
              {"bucket":"b","signal":"logs","destination":"x","decision":"send","redaction_profile":null},
              {"bucket":"c","signal":"logs","destination":"x","decision":5}
            ]}
            """;

        var plan = ObservabilityPlanParser.Parse(json).Plan!;

        Assert.Equal(3, plan.UnreadableRows);
        var x = Assert.Single(plan.Destinations);
        Assert.Equal(2, x.RoutedBuckets);
        Assert.Equal(new[] { "strict" }, x.RedactionProfiles);
        Assert.Equal(2, plan.BucketCount);
    }

    [Fact]
    public void A_decision_nobody_has_heard_of_means_on_and_not_routed()
    {
        var json = """
            {"rows":[
              {"bucket":"a","signal":"logs","destination":"x","decision":"quarantine_pending"},
              {"bucket":"a","signal":"traces","destination":"x","decision":"signal_not_selected"},
              {"bucket":"a","signal":"logs","destination":"y","decision":"destination_disabled"}
            ]}
            """;

        var plan = ObservabilityPlanParser.Parse(json).Plan!;

        var x = plan.Destination("x")!;
        Assert.True(x.Enabled);
        Assert.Equal(0, x.RoutedBuckets);
        Assert.Equal(new[] { "logs" }, x.Signals);
        Assert.False(plan.Destination("y")!.Enabled);
    }

    [Fact]
    public void A_conditional_row_counts_by_what_it_would_do()
    {
        var json = """
            {"rows":[
              {"bucket":"a","signal":"logs","destination":"x","decision":"conditional","potential_action":"send"},
              {"bucket":"b","signal":"logs","destination":"x","decision":"conditional","potential_action":"drop"},
              {"bucket":"c","signal":"logs","destination":"x","decision":"conditional"},
              {"bucket":"d","signal":"logs","destination":"x","decision":"conditional","potential_action":"floor_only"},
              {"bucket":"e","signal":"logs","destination":"floor","decision":"conditional","potential_action":"floor_only"}
            ]}
            """;

        var plan = ObservabilityPlanParser.Parse(json).Plan!;

        var x = plan.Destination("x")!;
        Assert.Equal(3, x.RoutedBuckets);
        Assert.True(x.HasConditionalRoutes);

        // The mandatory floor routes, and its profile is the store's own, so it does not make the label conditional.
        var floor = plan.Destination("floor")!;
        Assert.Equal(1, floor.RoutedBuckets);
        Assert.False(floor.HasConditionalRoutes);
    }

    [Fact]
    public void A_limit_that_is_not_a_positive_number_is_left_out_and_a_destination_with_no_row_is_not_invented_by_its_limits()
    {
        var json = """
            {"rows":[{"bucket":"a","signal":"logs","destination":"x","decision":"send"}],
             "delivery":[
               {"destination":"x","kind":"otlp","max_queue_size":"2048","max_queue_bytes":0,"max_export_batch_size":-5,"max_export_batch_bytes":1.5,"scheduled_delay_ms":0},
               {"destination":"ghost","kind":"otlp","max_queue_size":10},
               7,
               {"destination":"x","kind":"duplicate","max_queue_size":10}
             ]}
            """;

        var plan = ObservabilityPlanParser.Parse(json).Plan!;

        var x = Assert.Single(plan.Destinations);
        Assert.Equal("otlp", x.Kind);
        Assert.Equal(new DeliveryLimits(null, null, null, null, 0), x.Limits);
        Assert.Equal("delay=0ms", x.LimitsLabel);
    }

    [Fact]
    public void A_name_with_control_and_bidirectional_characters_is_written_out_and_is_the_same_name_everywhere()
    {
        var json = "{\"rows\":[{\"bucket\":\"a\",\"signal\":\"logs\",\"destination\":\"line\\nbreak \\u202Ename\",\"decision\":\"send\"}]}";

        var plan = ObservabilityPlanParser.Parse(json).Plan!;

        var name = Assert.Single(plan.Destinations).Name;
        Assert.DoesNotContain('\n', name);
        Assert.DoesNotContain('\u202E', name);
        Assert.Contains("\\n", name, StringComparison.Ordinal);
        Assert.Contains("\\u202E", name, StringComparison.Ordinal);
        Assert.Equal(name, ObservabilityPlanParser.CleanName("line\nbreak \u202Ename"));
    }

    [Fact]
    public void The_number_of_destinations_buckets_and_rows_read_is_bounded()
    {
        var rows = new StringBuilder("{\"rows\":[");
        for (var i = 0; i < ObservabilityPlanParser.MaxDestinations + 5; i++)
        {
            rows.Append(i == 0 ? "" : ",").Append("{\"bucket\":\"b").Append(i).Append("\",\"signal\":\"logs\",\"destination\":\"d").Append(i).Append("\",\"decision\":\"send\"}");
        }

        rows.Append("]}");

        var plan = ObservabilityPlanParser.Parse(rows.ToString()).Plan!;

        Assert.Equal(ObservabilityPlanParser.MaxDestinations, plan.Destinations.Count);
        Assert.Equal(5, plan.UnreadableRows);
        Assert.True(plan.BucketCount <= ObservabilityPlanParser.MaxBuckets);
    }

    [Fact]
    public void A_name_too_long_to_be_one_is_not_a_destination()
    {
        var json = "{\"rows\":[{\"bucket\":\"a\",\"signal\":\"logs\",\"destination\":\"" + new string('n', 300) + "\",\"decision\":\"send\"}]}";

        Assert.False(ObservabilityPlanParser.Parse(json).IsOk);
    }

    // ---- the words ----

    [Theory]
    [InlineData(new string[0], "per-route · unredacted")]
    [InlineData(new[] { "none" }, "per-route · unredacted")]
    [InlineData(new[] { "whole" }, "per-route · whole-content")]
    [InlineData(new[] { "strict" }, "per-route · strict")]
    [InlineData(new[] { "none", "strict" }, "per-route · none,strict")]
    [InlineData(new[] { "strict", "sensitive", "strict", "" }, "per-route · sensitive,strict")]
    [InlineData(new[] { "whole", "none" }, "per-route · none,whole")]
    public void The_aggregate_label_is_the_tuis(string[] profiles, string expected) =>
        Assert.Equal(expected, ObservabilityRedaction.Aggregate(profiles));

    [Theory]
    [InlineData(new string[0], false, "not-applicable")]
    [InlineData(new string[0], true, "per conditional route")]
    [InlineData(new[] { "none" }, false, "unredacted (none)")]
    [InlineData(new[] { "none", "strict" }, false, "mixed: none, strict")]
    [InlineData(new[] { "strict", "sensitive" }, false, "redacted: sensitive, strict")]
    [InlineData(new[] { "strict" }, true, "redacted: strict (+ conditional routes)")]
    public void A_destinations_redaction_label_is_the_tuis_with_a_note_for_routes_the_plan_cannot_settle(string[] profiles, bool conditional, string expected) =>
        Assert.Equal(expected, ObservabilityRedaction.DestinationLabel(profiles, conditional));

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KiB")]
    [InlineData(1536L, "1.5 KiB")]
    [InlineData(1048575L, "1024.0 KiB")]
    [InlineData(1048576L, "1.0 MiB")]
    [InlineData(67108864L, "64.0 MiB")]
    public void Bytes_print_with_one_decimal_in_the_units_the_tui_uses(long bytes, string expected) =>
        Assert.Equal(expected, ObservabilityFormat.Bytes(bytes));

    [Fact]
    public void Limits_say_only_what_the_plan_sets()
    {
        Assert.Equal("queue=100 items", new DeliveryLimits(100, null, null, null, null).Label);
        Assert.Equal("queue=2.0 KiB", new DeliveryLimits(null, 2048, null, null, null).Label);
        Assert.Equal("batch=5 items/512 B", new DeliveryLimits(null, null, 5, 512, null).Label);
        Assert.Equal("delay=250ms", new DeliveryLimits(null, null, null, null, 250).Label);
        Assert.Equal("not-applicable", new DeliveryLimits(null, null, null, null, null).Label);
        Assert.True(new DeliveryLimits(null, null, null, null, null).IsEmpty);
    }
}
