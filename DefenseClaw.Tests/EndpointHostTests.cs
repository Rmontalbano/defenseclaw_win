using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="EndpointHost"/>: what the Setup editors add to <see cref="EndpointDisplay"/> (CUST-272, which has its own tests for reducing an address
/// and a sentence): a destination's own address, the line filter the runner applies to a command's output, and the shapes this CLI prints.
/// Every credential here is a made-up value that starts with <c>synth</c> (the convention <c>.gitleaks.toml</c> allows), and every host is under
/// <c>example.test</c>.
/// </summary>
public class EndpointHostTests
{
    /// <summary>Everything a credential-carrying address could leak: no output may contain any of these.</summary>
    private static readonly string[] Secrets =
    {
        "synthuser", "synthpass", "synthkey", "synthtoken", "synthfrag", "synthpath-secret", "api_key", "token=", "secret",
    };

    // ---- the shapes this CLI prints ----

    [Theory]
    [InlineData("https://hooks.example.test/***", "hooks.example.test")] // redact_webhook_url: the path is the secret of a chat webhook
    [InlineData("https://***@siem.example.test/***?***", "siem.example.test")] // userinfo, path and query of a generic webhook, all replaced
    [InlineData("https://webex.example.test:8443/***", "webex.example.test:8443")]
    [InlineData("https://hec.example.test:8088/services/collector/event", "hec.example.test:8088")] // redact_endpoint_for_display keeps the path
    public void What_the_cli_prints_for_a_webhook_or_a_destination_is_reduced_to_its_host(string printed, string expected)
    {
        var shown = EndpointDisplay.Host(printed);

        Assert.Equal(expected, shown);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_value_that_is_not_an_address_is_the_placeholder_and_not_the_text()
    {
        // What redact_webhook_url prints for a value that is not an absolute URL.
        Assert.Equal(EndpointDisplay.Unreadable, EndpointDisplay.Host("***"));
    }

    // ---- a destination's own address ----

    [Theory]
    [InlineData("otlp", "https://synthuser:synthpass@collector.example.test:4318/v1/logs?api_key=synthkey", "collector.example.test:4318")]
    [InlineData("splunk_hec", "https://hec.example.test:8088/services/collector/event", "hec.example.test:8088")]
    [InlineData("http_jsonl", "https://logs.example.test/ingest/synthpath-secret", "logs.example.test")]
    [InlineData("a-kind-from-the-future", "https://future.example.test/x/synthpath-secret", "future.example.test")]
    [InlineData(null, "https://unknown.example.test/x/synthpath-secret", "unknown.example.test")]
    public void A_remote_destinations_target_is_its_host(string? kind, string target, string expected)
    {
        var shown = EndpointHost.ForDestination(kind, target);

        Assert.Equal(expected, shown);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("sqlite", @"C:\Users\synthetic\.defenseclaw\audit.db")]
    [InlineData("jsonl", @"C:\synthetic\logs\events.jsonl")]
    public void A_local_destinations_path_is_shown_as_it_is_because_a_path_is_not_an_endpoint(string kind, string path) =>
        Assert.Equal(path, EndpointHost.ForDestination(kind, path));

    [Fact]
    public void A_local_kind_whose_target_is_an_address_is_still_cut_to_its_host() =>
        Assert.Equal("collector.example.test", EndpointHost.ForDestination("console", "https://synthuser:synthpass@collector.example.test/synthpath-secret"));

    [Fact]
    public void An_address_that_has_no_host_to_vouch_for_is_the_placeholder_even_for_a_local_kind() =>
        Assert.Equal(EndpointDisplay.Unreadable, EndpointHost.ForDestination("console", "https://synthuser:synthpass@:99999/x?synthkey"));

    [Fact]
    public void A_local_path_is_cut_to_a_length_a_row_can_show_and_written_on_one_line()
    {
        var shown = EndpointHost.ForDestination("sqlite", @"C:\synthetic\first" + "\n" + "second" + new string('d', 400));

        Assert.True(shown.Length <= 262, $"{shown.Length} characters");
        Assert.DoesNotContain('\n', shown);
        Assert.Contains("first\\nsecond", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void No_target_is_no_endpoint() => Assert.Equal(string.Empty, EndpointHost.ForDestination("console", "  "));

    // ---- a line of a command's output ----

    [Fact]
    public void The_line_setup_webhook_test_prints_loses_the_secret_in_the_address()
    {
        // 0.8.10 cmd_setup_webhook.test_cmd: "  Testing webhook <name> [<type>] → <url>", with the url whole.
        var printed = "  Testing webhook example-slack [slack] → https://hooks.example.test/services/synthtoken0/synthtoken1/synthpath-secret";

        var shown = EndpointHost.ScrubLine(printed);

        Assert.Equal("  Testing webhook example-slack [slack] → https://hooks.example.test", shown);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_json_line_with_an_address_keeps_its_shape_and_loses_the_path()
    {
        var shown = EndpointHost.ScrubLine("    \"target\": \"https://collector.example.test:4318/v1/logs/synthpath-secret\",");

        Assert.Equal("    \"target\": \"https://collector.example.test:4318\",", shown);
    }

    [Fact]
    public void Every_address_in_a_line_is_cut_and_a_credential_that_is_not_in_one_is_masked()
    {
        var shown = EndpointHost.ScrubLine(
            "primary https://synthuser:synthpass@one.example.test/a?x=synthkey failed, then http://two.example.test:8080/b?token=synthtoken; retry with token=synthtoken-synthtoken");

        Assert.Contains("https://one.example.test", shown, StringComparison.Ordinal);
        Assert.Contains("http://two.example.test:8080", shown, StringComparison.Ordinal);
        Assert.All(Secrets.Where(s => s != "token="), secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_address_in_a_line_that_cannot_be_read_is_the_placeholder()
    {
        var shown = EndpointHost.ScrubLine("dial https://synthuser:synthpass@:99999/x?synthkey refused");

        Assert.Contains(EndpointDisplay.Unreadable, shown, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("connection refused", "connection refused")]
    [InlineData("  tls: handshake failure  ", "  tls: handshake failure  ")]
    [InlineData("    Result:         ok (HTTP 200)", "    Result:         ok (HTTP 200)")]
    [InlineData("    Secret env:     EXAMPLE_PD_ROUTING_KEY (value not shown)", "    Secret env:     EXAMPLE_PD_ROUTING_KEY (value not shown)")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void An_ordinary_line_is_left_alone_with_its_indentation(string? line, string expected) => Assert.Equal(expected, EndpointHost.ScrubLine(line));

    [Fact]
    public void A_line_is_the_same_line_the_sentence_scrubber_would_make_of_it_apart_from_its_whitespace()
    {
        // One reduction of an address in the two places that do it: they must not drift apart.
        const string line = "Post \"https://synthuser:synthpass@collector.example.test:4318/v1/logs?api_key=synthkey\": context deadline exceeded";

        Assert.Equal(EndpointDisplay.ScrubText(line), EndpointHost.ScrubLine(line));
    }

    [Fact]
    public void Hostile_text_is_masked_whole_rather_than_hanging()
    {
        var hostile = "https://" + new string('a', 20_000) + "@" + new string('b', 20_000) + "/" + string.Concat(Enumerable.Repeat("https://x@", 2_000));

        var shown = EndpointHost.ScrubLine(hostile);

        Assert.DoesNotContain(new string('a', 100), shown, StringComparison.Ordinal);
        Assert.True(shown.Length < 1_000, $"{shown.Length} characters");
    }
}
