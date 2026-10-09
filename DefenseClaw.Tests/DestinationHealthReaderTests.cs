using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Observability;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="DestinationHealthReader"/> (CUST-272): what crosses over from a destination's <c>/health</c> entry. Only closed tokens, counters and timestamps do:
/// the gateway builds a free-text <c>last_error</c> from the failing request, and a request carries its credential. The fixtures are the two runtimes'
/// <c>/health</c> documents; the synthetic entries are written from the Go gateway's health rendering (<c>renderObservabilityV8Health</c>) and the
/// older delivery block the TUI also reads.
/// </summary>
public class DestinationHealthReaderTests
{
    private static JsonElement Destination(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement TelemetryDetails(string fixture)
    {
        using var document = JsonDocument.Parse(FixtureFiles.ReadText(fixture));
        return document.RootElement.GetProperty("telemetry").GetProperty("details").Clone();
    }

    private static string Clock(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // ---- the two runtimes' /health ----

    [Fact]
    public void The_newer_runtimes_destinations_give_a_state_a_queue_and_nothing_to_say_about_delivery_yet()
    {
        var details = TelemetryDetails("runtime-95159fd/rest/health.json");
        var destinations = details.GetProperty("destinations").EnumerateArray().Select(DestinationHealthReader.Read).ToArray();

        var local = destinations.Single(d => d!.Name == "local-sqlite")!;
        Assert.Equal("healthy", local.State);
        Assert.Equal("activated", local.Reason);
        Assert.Equal("0 dropped", local.QueueLabel);
        Assert.Equal("unavailable", local.LastResultLabel(Clock));
        Assert.False(local.LastResultFailed);

        var otlp = destinations.Single(d => d!.Name == "example-otlp")!;
        Assert.Equal("healthy", otlp.State);
        Assert.Equal("0/2048 items, 0 B/64.0 MiB, 0 dropped", otlp.QueueLabel);
    }

    [Fact]
    public void The_installed_0_8_10_destination_has_a_state_and_the_counter_it_counts()
    {
        var details = TelemetryDetails("health-0.8.10.json");

        var local = DestinationHealthReader.Read(details.GetProperty("destinations")[0])!;

        Assert.Equal("local-sqlite", local.Name);
        Assert.Equal("healthy", local.State);
        Assert.Equal("0 dropped", local.QueueLabel);
    }

    [Theory]
    [InlineData("health-0.8.10.json", "healthy", 90L, "healthy")]
    [InlineData("runtime-95159fd/rest/health.json", "healthy", 7L, "healthy")]
    public void The_retention_reaper_and_its_window_are_read_from_the_telemetry_block(string fixture, string state, long days, string label)
    {
        var retention = DestinationHealthReader.ReadRetention(TelemetryDetails(fixture));

        Assert.Equal(state, retention.State);
        Assert.Equal(days, retention.Days);
        Assert.Equal(label, retention.ControllerLabel);
        Assert.False(retention.NeedsAttention);
    }

    // ---- the reaper ----

    [Theory]
    [InlineData("{\"retention_state\":\"degraded\",\"retention_failure\":\"sqlite_busy\",\"retention_days\":30}", "degraded", "sqlite_busy", 30L, "degraded (sqlite_busy)", true)]
    [InlineData("{\"retention_state\":\"stopped\"}", "stopped", "", null, "stopped", true)]
    [InlineData("{\"retention_state\":\"waiting_for_readiness\"}", "waiting_for_readiness", "", null, "waiting_for_readiness", false)]
    [InlineData("{\"retention_state\":\"disabled\",\"retention_days\":0}", "disabled", "", 0L, "disabled", false)]
    [InlineData("{\"retention_state\":\"HEALTHY\",\"failure\":\"x\"}", "healthy", "x", null, "healthy (x)", false)]
    [InlineData("{\"retention_state\":\"exploding\",\"retention_days\":9}", "", "", 9L, "unavailable", false)]
    [InlineData("{\"retention_state\":5}", "", "", null, "unavailable", false)]
    [InlineData("{\"retention_state\":\"degraded\",\"retention_failure\":\"has spaces and https://synthuser:synthpass@h.example.test\"}", "degraded", "", null, "degraded", true)]
    [InlineData("{\"retention_days\":-3}", "", "", null, "unavailable", false)]
    [InlineData("{}", "", "", null, "unavailable", false)]
    public void The_reapers_state_is_one_of_the_names_it_has_and_its_failure_is_a_plain_token(
        string json, string state, string failure, long? days, string label, bool attention)
    {
        var retention = DestinationHealthReader.ReadRetention(Destination(json));

        Assert.Equal(state, retention.State);
        Assert.Equal(failure, retention.Failure);
        Assert.Equal(days, retention.Days);
        Assert.Equal(label, retention.ControllerLabel);
        Assert.Equal(attention, retention.NeedsAttention);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("7")]
    public void A_telemetry_block_that_is_not_an_object_reports_nothing(string json) =>
        Assert.Equal(RetentionHealth.None, DestinationHealthReader.ReadRetention(Destination(json)));

    // ---- one destination ----

    [Fact]
    public void A_destination_that_failed_says_when_how_and_what_it_last_delivered()
    {
        var entry = Destination("""
            {"name":"example-otlp","kind":"otlp","enabled":true,"state":"degraded","reason":"retrying",
             "last_failure_class":"timeout","last_success_at":"2030-01-15T09:17:58.497333253Z","last_failure_at":"2030-01-15T09:18:40.5+00:00",
             "queue":{"items":12,"bytes":2048,"max_items":2048,"max_bytes":67108864,"dropped":3}}
            """);

        var health = DestinationHealthReader.Read(entry)!;

        Assert.Equal("degraded", health.State);
        Assert.Equal("retrying", health.Reason);
        Assert.Equal("timeout", health.FailureClass);
        Assert.Equal("12/2048 items, 2.0 KiB/64.0 MiB, 3 dropped", health.QueueLabel);
        Assert.Equal("ok 09:17:58; error 09:18:40 (timeout)", health.LastResultLabel(Clock));
        Assert.True(health.LastResultFailed);
    }

    [Fact]
    public void A_destination_whose_last_word_was_success_is_not_failed_even_if_it_once_failed()
    {
        var entry = Destination("""
            {"name":"d","state":"healthy","last_failure_at":"2030-01-15T09:00:00Z","last_success_at":"2030-01-15T09:05:00Z"}
            """);

        var health = DestinationHealthReader.Read(entry)!;

        Assert.False(health.LastResultFailed);
        Assert.Equal("ok 09:05:00; error 09:00:00", health.LastResultLabel(Clock));
    }

    [Theory]
    [InlineData("""{"name":"d","failure":"auth_rejected"}""", "error auth_rejected", true)]
    [InlineData("""{"name":"d","last_error_class":"tls","last_failure_code":"ignored_because_the_class_came_first"}""", "error tls", true)]
    [InlineData("""{"name":"d","last_failure_code":"http_503"}""", "error http_503", true)]
    [InlineData("""{"name":"d","warning":"slow_endpoint"}""", "error slow_endpoint", true)]
    [InlineData("""{"name":"d"}""", "unavailable", false)]
    public void A_failure_class_is_the_first_plain_token_among_the_names_the_gateways_use(string json, string label, bool failed)
    {
        var health = DestinationHealthReader.Read(Destination(json))!;

        Assert.Equal(label, health.LastResultLabel(Clock));
        Assert.Equal(failed, health.LastResultFailed);
    }

    [Fact]
    public void The_older_delivery_block_says_only_that_it_failed_and_when()
    {
        var entry = Destination("""
            {"name":"example-otlp","state":"degraded",
             "delivery":{"last_error":"Post \"https://synthuser:synthpass@collector.example.test/v1?api_key=synthkey\": refused","last_attempt_at":"2030-01-15T09:30:00Z","last_success_at":"2030-01-15T09:00:00Z"}}
            """);

        var health = DestinationHealthReader.Read(entry)!;

        Assert.Equal("details_redacted", health.FailureClass);
        Assert.Equal("ok 09:00:00; error 09:30:00 (details_redacted)", health.LastResultLabel(Clock));
        Assert.DoesNotContain("synth", health.LastResultLabel(Clock), StringComparison.Ordinal);
    }

    [Fact]
    public void Free_text_in_any_field_never_comes_through()
    {
        var entry = Destination("""
            {"name":"example-otlp",
             "state":"failing because https://synthuser:synthpass@collector.example.test/?api_key=synthkey refused",
             "reason":"Bearer synthtoken-synthtoken",
             "last_error":"https://synthuser:synthpass@collector.example.test/?api_key=synthkey",
             "last_error_class":"has spaces",
             "failure":"UPPER_but_https://x",
             "last_failure_at":"yesterday around noon",
             "endpoint":"https://synthuser:synthpass@collector.example.test/?api_key=synthkey",
             "headers":{"Authorization":"Bearer synthtoken-synthtoken"}}
            """);

        var health = DestinationHealthReader.Read(entry)!;

        Assert.Equal(string.Empty, health.State);
        Assert.Equal(string.Empty, health.Reason);
        Assert.Equal(string.Empty, health.FailureClass);
        Assert.Null(health.LastFailure);
        Assert.Equal("unavailable", health.QueueLabel);
        Assert.Equal("unavailable", health.LastResultLabel(Clock));
        Assert.DoesNotContain("synth", JsonSerializer.Serialize(health), StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_is_lower_cased_and_the_flat_queue_fields_are_read_too()
    {
        var entry = Destination("""
            {"name":"d","health_state":"HEALTHY","state":"ignored",
             "queue_items":5,"queue_bytes":1048576,"max_queue_items":100,"max_queue_bytes":2097152,"queue_dropped":2}
            """);

        var health = DestinationHealthReader.Read(entry)!;

        Assert.Equal("healthy", health.State);
        Assert.Equal("5/100 items, 1.0 MiB/2.0 MiB, 2 dropped", health.QueueLabel);
    }

    [Fact]
    public void A_count_that_is_negative_fractional_or_text_is_not_a_count()
    {
        var entry = Destination("""
            {"name":"d","queue":{"items":-1,"bytes":1.5,"max_items":"100","max_bytes":null,"dropped":true},"counters":{"dropped":-4}}
            """);

        Assert.Equal("unavailable", DestinationHealthReader.Read(entry)!.QueueLabel);
    }

    [Fact]
    public void The_dropped_counter_falls_back_to_the_destinations_counters()
    {
        var entry = Destination("""{"name":"d","counters":{"accepted":9,"dropped":4}}""");

        Assert.Equal("4 dropped", DestinationHealthReader.Read(entry)!.QueueLabel);
    }

    [Theory]
    [InlineData("2030-01-15T09:17:58Z", true)]
    [InlineData("2030-01-15T09:17:58.1Z", true)]
    [InlineData("2030-01-15T09:17:58.123456789Z", true)]
    [InlineData("2030-01-15T09:17:58.123456789+05:30", true)]
    [InlineData("2030-01-15T09:17:58-08:00", true)]
    [InlineData("2030-01-15 09:17:58Z", false)]
    [InlineData("2030-01-15T09:17:58", false)]
    [InlineData("2030-01-15T25:17:58Z", false)]
    [InlineData("2030-13-15T09:17:58Z", false)]
    [InlineData("0001-01-01T00:00:00Z", false)]
    [InlineData("2030-01-15T09:17:58+24:00", false)]
    [InlineData("2030-01-15T09:17:58.1234567890Z", false)]
    [InlineData("", false)]
    public void Only_a_timestamp_in_the_form_the_gateway_writes_is_a_time(string stamp, bool valid)
    {
        var health = DestinationHealthReader.Read(Destination("{\"name\":\"d\",\"last_success_at\":\"" + stamp + "\"}"))!;

        Assert.Equal(valid, health.LastSuccess is not null);
    }

    [Fact]
    public void A_time_keeps_its_instant_whatever_zone_it_was_written_in()
    {
        var health = DestinationHealthReader.Read(Destination("""{"name":"d","last_success_at":"2030-01-15T14:47:58.5+05:30"}"""))!;

        Assert.Equal(new DateTimeOffset(2030, 1, 15, 9, 17, 58, 500, TimeSpan.Zero), health.LastSuccess!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"d\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"\"}")]
    [InlineData("{\"name\":5}")]
    public void An_entry_that_is_not_a_named_object_is_nothing(string json) =>
        Assert.Null(DestinationHealthReader.Read(Destination(json)));

    [Fact]
    public void A_name_is_cleaned_the_way_the_plans_is_so_the_two_merge()
    {
        var health = DestinationHealthReader.Read(Destination("{\"name\":\"line\\nbreak \\u202Ename\"}"))!;

        Assert.Equal(ObservabilityPlanParser.CleanName("line\nbreak \u202Ename"), health.Name);
    }

    [Fact]
    public void The_destination_key_is_a_name_too()
    {
        Assert.Equal("d", DestinationHealthReader.Read(Destination("{\"destination\":\"d\"}"))!.Name);
    }
}
