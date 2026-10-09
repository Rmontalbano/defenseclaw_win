using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Setup;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-270: what <c>setup observability list --json</c>, <c>setup webhook list|show --json</c> and <c>setup trusted-paths list --json</c> print,
/// read the way the editors read them. The fixtures (<c>Fixtures/setup-editors/*.synthetic.json</c>) are written from the code that emits them
/// (<c>cmd_setup_observability._print_v8_destination_list</c>, <c>cmd_setup_webhook._view_to_dict</c>, <c>cmd_setup._collect_trusted_prefixes</c>), because
/// these commands are never run for a fixture: their output names real destinations. Every credential in a test is a made-up value that
/// starts with <c>synth</c>.
/// </summary>
public sealed class SetupResourceListTests
{
    private static string Fixture(string name) => FixtureFiles.ReadText("setup-editors/" + name);

    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthkey", "synthtoken", "synthfrag", "synthpath-secret" };

    // ---- observability ----

    [Fact]
    public void Every_destination_of_the_list_is_read_with_the_state_the_cli_would_print()
    {
        Assert.True(ObservabilityDestinationParser.TryParse(Fixture("observability-list.synthetic.json"), out var rows, out var error), error);

        Assert.Equal(
            new[] { "local-sqlite", "example-otlp", "example-hec", "example-jsonl", "example-local-stack" },
            rows.Select(r => r.Name));
        Assert.Equal(new[] { "enabled", "enabled", "disabled", "enabled", "unsupported" }, rows.Select(r => r.State));

        var otlp = rows[1];
        Assert.Equal("otlp", otlp.Kind);
        Assert.Equal(new[] { "logs", "traces" }, otlp.Signals);
        Assert.Equal(new[] { "logs", "traces", "metrics" }, otlp.Capabilities);
        Assert.Equal("concise", otlp.Policy);
        Assert.Equal(6, otlp.BucketCount);
        Assert.Equal("redacted: strict", otlp.Redaction);
        Assert.Equal("collector.example.test:4318", otlp.Endpoint);
        Assert.False(otlp.Generated);
        Assert.False(otlp.SendsUnredacted);
    }

    [Fact]
    public void The_destination_the_compiler_adds_is_generated_mandatory_and_local()
    {
        Assert.True(ObservabilityDestinationParser.TryParse(Fixture("observability-list.synthetic.json"), out var rows, out _));

        var local = rows.Single(r => r.Name == "local-sqlite");
        Assert.True(local.Generated);
        Assert.True(local.IsMandatory);
        Assert.True(local.IsLocal);
        Assert.Equal(@"C:\Users\synthetic\.defenseclaw\audit.db", local.Endpoint); // a path, shown as it is

        Assert.All(rows.Where(r => r.Name != "local-sqlite"), r => Assert.False(r.IsMandatory));
        Assert.All(rows.Where(r => r.Kind is "otlp" or "http_jsonl" or "splunk_hec"), r => Assert.False(r.IsLocal));
    }

    [Fact]
    public void A_destination_that_sends_content_unredacted_or_partly_is_marked_so()
    {
        Assert.True(ObservabilityDestinationParser.TryParse(Fixture("observability-list.synthetic.json"), out var rows, out _));

        Assert.True(rows.Single(r => r.Name == "example-hec").SendsUnredacted);       // unredacted (none)
        Assert.True(rows.Single(r => r.Name == "example-jsonl").SendsUnredacted);     // mixed: none, sensitive
        Assert.False(rows.Single(r => r.Name == "example-otlp").SendsUnredacted);     // redacted: strict
        Assert.False(rows.Single(r => r.Name == "local-sqlite").SendsUnredacted);     // redacted: sensitive
        Assert.False(rows.Single(r => r.Name == "example-local-stack").SendsUnredacted); // not-applicable
    }

    [Fact]
    public void An_address_with_credentials_in_it_is_reduced_to_its_host_and_the_row_has_nowhere_to_keep_the_rest()
    {
        var json = """
            [
              {"name": "example-otlp", "kind": "otlp", "enabled": true, "generated": false, "signals": ["logs"], "capabilities": ["logs"],
               "policy": "concise", "bucket_count": 1, "redaction": "redacted: strict",
               "target": "https://synthuser:synthpass@collector.example.test:4318/v1/synthpath-secret?api_key=synthkey#synthfrag",
               "platform_status": "supported"}
            ]
            """;

        Assert.True(ObservabilityDestinationParser.TryParse(json, out var rows, out var error), error);

        var row = Assert.Single(rows);
        Assert.Equal("collector.example.test:4318", row.Endpoint);
        var everything = row.ToString() + string.Join('|', row.GetType().GetProperties().Select(p => p.GetValue(row)?.ToString()));
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));
        Assert.DoesNotContain(row.GetType().GetProperties(), p => p.Name.Contains("Target", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Output_after_a_warning_line_is_still_read_even_when_the_warning_starts_with_a_bracket()
    {
        var text = "[warn] config drift detected\r\n" + Fixture("observability-list.synthetic.json").Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.True(ObservabilityDestinationParser.TryParse(text, out var rows, out var error), error);

        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void An_empty_list_is_a_list_and_nothing_printed_is_not()
    {
        Assert.True(ObservabilityDestinationParser.TryParse("[]", out var none, out var noError));
        Assert.Empty(none);
        Assert.Equal(string.Empty, noError);

        foreach (var nothing in new[] { string.Empty, "   \r\n  ", null })
        {
            Assert.False(ObservabilityDestinationParser.TryParse(nothing, out var rows, out var error));
            Assert.Empty(rows);
            Assert.Contains("printed nothing", error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"name": "x"}""")]
    [InlineData("[ {unterminated")]
    [InlineData("""[ "a string" ]""")]
    [InlineData("""[ {"kind": "otlp", "enabled": true} ]""")]
    [InlineData("""[ {"name": "x", "kind": "otlp"} ]""")]
    [InlineData("""[ {"name": "x", "kind": "otlp", "enabled": "yes"} ]""")]
    [InlineData("""[ {"name": "  ", "kind": "otlp", "enabled": true} ]""")]
    [InlineData("""[ {"name": "ok", "enabled": true}, {"enabled": true} ]""")]
    public void Output_that_is_not_the_list_the_cli_documents_is_a_failed_read_with_no_rows(string text)
    {
        Assert.False(ObservabilityDestinationParser.TryParse(text, out var rows, out var error));

        Assert.Empty(rows);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void A_name_is_kept_exactly_as_printed_so_a_later_command_acts_on_the_same_row()
    {
        Assert.True(ObservabilityDestinationParser.TryParse("""[ {"name": " padded ", "kind": "otlp", "enabled": true} ]""", out var rows, out _));

        Assert.Equal(" padded ", Assert.Single(rows).Name);
        Assert.False(SetupResourceArgv.IsSafeName(rows[0].Name));
    }

    [Fact]
    public void A_field_the_cli_leaves_out_is_empty_and_never_invented()
    {
        Assert.True(ObservabilityDestinationParser.TryParse("""[ {"name": "bare", "enabled": false} ]""", out var rows, out var error), error);

        var row = Assert.Single(rows);
        Assert.Equal(string.Empty, row.Kind);
        Assert.Empty(row.Signals);
        Assert.Equal(0, row.BucketCount);
        Assert.Equal(string.Empty, row.Endpoint);
        Assert.Equal("disabled", row.State);
        Assert.False(row.IsLocal);
        Assert.False(row.Generated);
    }

    // ---- webhooks ----

    [Fact]
    public void Every_webhook_of_the_list_is_read_without_an_address_or_a_secret()
    {
        Assert.True(NotifierWebhookParser.TryParse(Fixture("webhook-list.synthetic.json"), out var rows, out var error), error);

        Assert.Equal(new[] { "example-slack", "example-pagerduty", "example-webex", "example-hmac" }, rows.Select(r => r.Name));
        Assert.Equal(new[] { "slack", "pagerduty", "webex", "generic" }, rows.Select(r => r.Type));
        Assert.Equal(new[] { "enabled", "disabled", "enabled", "enabled" }, rows.Select(r => r.State));
        Assert.Equal(new[] { "hooks.example.test", "events.example.test", "webex.example.test:8443", "siem.example.test" }, rows.Select(r => r.Endpoint));

        var pager = rows[1];
        Assert.Equal("EXAMPLE_PD_ROUTING_KEY", pager.SecretEnv); // the NAME of the variable, never a value
        Assert.Equal("CRITICAL", pager.MinSeverity);
        Assert.Equal(new[] { "block", "scan" }, pager.Events);
        Assert.Equal("block, scan", pager.EventsText);
        Assert.Equal(15, pager.TimeoutSeconds);
        Assert.Equal(0, pager.CooldownSeconds);
        Assert.Equal("none: every matching event is delivered", pager.CooldownText);

        Assert.Equal("all events", rows[0].EventsText);
        Assert.Null(rows[0].CooldownSeconds);
        Assert.Equal("runtime default (300 s)", rows[0].CooldownText);
        Assert.Equal("Y2lzY29zeW50aGV0aWM", rows[2].RoomId);
        Assert.Equal("600 s", rows[2].CooldownText);

        Assert.DoesNotContain(typeof(NotifierWebhook).GetProperties(), p => p.Name.Equals("Url", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_address_a_cli_failed_to_cut_is_cut_here()
    {
        // The CLI redacts a webhook's address in `list` and `show`; this does not rely on it.
        var json = """
            [ {"name": "leaky", "type": "slack", "enabled": true,
               "url": "https://synthuser:synthpass@hooks.example.test/services/synthpath-secret?token=synthtoken#synthfrag",
               "secret_env": "", "room_id": "", "min_severity": "high", "events": [], "timeout_seconds": 10, "cooldown_seconds": null} ]
            """;

        Assert.True(NotifierWebhookParser.TryParse(json, out var rows, out var error), error);

        var row = Assert.Single(rows);
        Assert.Equal("hooks.example.test", row.Endpoint);
        Assert.Equal("HIGH", row.MinSeverity);
        var everything = row.ToString() + string.Join('|', row.GetType().GetProperties().Select(p => p.GetValue(row)?.ToString()));
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));
    }

    [Fact]
    public void An_address_that_is_not_one_is_the_placeholder_not_the_text()
    {
        // What redact_webhook_url prints for a value that is not an absolute URL.
        Assert.True(NotifierWebhookParser.TryParse("""[ {"name": "odd", "type": "slack", "enabled": true, "url": "***"} ]""", out var rows, out _));
        Assert.Equal(EndpointDisplay.Unreadable, Assert.Single(rows).Endpoint);

        Assert.True(NotifierWebhookParser.TryParse("""[ {"name": "none", "type": "slack", "enabled": true} ]""", out rows, out _));
        Assert.Equal(string.Empty, Assert.Single(rows).Endpoint);
    }

    [Fact]
    public void The_one_webhook_show_prints_is_read_as_a_row()
    {
        Assert.True(NotifierWebhookParser.TryParseOne(Fixture("webhook-show.synthetic.json"), out var webhook, out var error), error);

        Assert.NotNull(webhook);
        Assert.Equal("example-pagerduty", webhook!.Name);
        Assert.Equal("events.example.test", webhook.Endpoint);
        Assert.False(webhook.Enabled);
        Assert.Equal(new[] { "block", "scan" }, webhook.Events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("""{"type": "slack", "enabled": true}""")]
    [InlineData("""{"name": "x", "type": "slack"}""")]
    [InlineData("error: no webhook named 'x'")]
    public void A_show_that_printed_no_webhook_is_a_failed_read(string text)
    {
        Assert.False(NotifierWebhookParser.TryParseOne(text, out var webhook, out var error));

        Assert.Null(webhook);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void A_webhook_list_that_printed_nothing_is_unknown_and_an_empty_one_is_empty()
    {
        Assert.False(NotifierWebhookParser.TryParse(string.Empty, out _, out var error));
        Assert.Contains("printed nothing", error, StringComparison.Ordinal);

        Assert.True(NotifierWebhookParser.TryParse("[]", out var rows, out _));
        Assert.Empty(rows);
    }

    // ---- trusted paths ----

    [Fact]
    public void Every_trusted_path_is_read_with_its_source_status_and_whether_it_is_owned()
    {
        Assert.True(TrustedPathParser.TryParse(Fixture("trusted-paths-list.synthetic.json"), out var rows, out var error), error);

        Assert.Equal(6, rows.Count);
        Assert.Equal(new[] { "default", "default", "config", "legacy .env", "config", "env" }, rows.Select(r => r.Source));
        Assert.Equal(new[] { "ok", "ok", "ok", "missing", "unsafe-permissions", "ok" }, rows.Select(r => r.Status));
        Assert.Equal(new[] { "-", "-", "yes", "yes", "yes", "-" }, rows.Select(r => r.Owned));

        var configured = rows[2];
        Assert.Equal(@"~\.synthetic-tools\bin", configured.Path);
        Assert.Equal(@"C:\Users\synthetic\.synthetic-tools\bin", configured.Resolved); // what remove is given
        Assert.True(configured.Removable);
        Assert.True(configured.IsOk);
        Assert.False(rows[3].IsOk);
    }

    [Fact]
    public void A_built_in_is_protected_and_an_entry_only_the_environment_has_is_not_removable_here()
    {
        Assert.True(TrustedPathParser.TryParse(Fixture("trusted-paths-list.synthetic.json"), out var rows, out _));

        Assert.All(rows.Where(r => r.IsBuiltIn), r => Assert.False(r.Removable));
        Assert.Equal(2, rows.Count(r => r.IsBuiltIn));
        var env = rows.Single(r => r.IsEnvironmentOnly);
        Assert.False(env.Removable);
        Assert.False(env.IsBuiltIn);
    }

    [Fact]
    public void Removable_is_true_only_when_the_cli_says_so()
    {
        var json = """
            [ {"resolved": "C:\\a", "source": "config", "status": "ok"},
              {"resolved": "C:\\b", "source": "config", "status": "ok", "removable": "yes"},
              {"resolved": "C:\\c", "source": "config", "status": "ok", "removable": 1},
              {"resolved": "C:\\d", "source": "config", "status": "ok", "removable": true} ]
            """;

        Assert.True(TrustedPathParser.TryParse(json, out var rows, out var error), error);

        Assert.Equal(new[] { false, false, false, true }, rows.Select(r => r.Removable));
    }

    [Fact]
    public void A_trusted_path_list_that_printed_nothing_is_unknown_not_empty()
    {
        Assert.False(TrustedPathParser.TryParse(string.Empty, out var rows, out var error));
        Assert.Empty(rows);
        Assert.Contains("printed nothing", error, StringComparison.Ordinal);

        Assert.True(TrustedPathParser.TryParse("[]", out var empty, out _));
        Assert.Empty(empty);
    }

    [Theory]
    [InlineData("""[ {"source": "default", "status": "ok"} ]""")]
    [InlineData("""[ {"resolved": "C:\\a", "status": "ok"} ]""")]
    [InlineData("""[ "C:\\a" ]""")]
    [InlineData("not json")]
    public void A_row_without_a_path_or_a_source_fails_the_read(string text)
    {
        Assert.False(TrustedPathParser.TryParse(text, out var rows, out var error));
        Assert.Empty(rows);
        Assert.NotEqual(string.Empty, error);
    }

    // ---- connectors outside a trusted directory ----

    [Fact]
    public void The_connectors_the_last_scan_skipped_for_their_directory_are_listed_with_the_directory_to_trust()
    {
        var report = UntrustedConnectorScan.Read(Fixture("agent-discovery.synthetic.json"));

        Assert.True(report.Read);
        Assert.Equal(string.Empty, report.Note);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 17, 30, 0, TimeSpan.Zero), report.ScannedAt);
        Assert.Equal(
            new[] { ("claudecode", @"C:\Users\synthetic\.local\bin"), ("cursor", @"D:\agents\cursor\bin") },
            report.Connectors.Select(c => (c.Connector, c.Directory)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"scanned_at": "2026-10-08T17:30:00Z"}""")]
    [InlineData("""{"agents": []}""")]
    public void A_discovery_file_that_cannot_be_read_says_so_and_never_reads_as_no_connectors(string json)
    {
        var report = UntrustedConnectorScan.Read(json);

        Assert.False(report.Read);
        Assert.Empty(report.Connectors);
        Assert.NotEqual(string.Empty, report.Note);
    }

    [Fact]
    public void A_scan_that_found_nothing_to_report_is_read_and_empty()
    {
        var report = UntrustedConnectorScan.Read("""{"scanned_at": "bad", "agents": {"codex": {"name": "codex", "binary_path": "C:\\x\\codex.cmd", "error": ""}}}""");

        Assert.True(report.Read);
        Assert.Empty(report.Connectors);
        Assert.Null(report.ScannedAt);
    }

    [Fact]
    public void A_connector_whose_directory_could_not_be_suggested_is_left_out_of_the_scan_and_the_others_are_kept()
    {
        // A relative path, one that would be read as an option, one with a control character and one with a direction override: none is a folder to suggest.
        var report = UntrustedConnectorScan.Read(
            """
            {"agents": {
              "a": {"name": "a", "binary_path": "tools\\a.exe", "error": "binary path is not in a trusted install prefix"},
              "b": {"name": "b", "binary_path": "-rf\\b.exe", "error": "binary path is not in a trusted install prefix"},
              "c": {"name": "c", "binary_path": "C:\\x\ty\\c.exe", "error": "binary path is not in a trusted install prefix"},
              "d": {"name": "d", "binary_path": "C:\\x\u202Ey\\d.exe", "error": "binary path is not in a trusted install prefix"},
              "e": {"name": "e", "binary_path": "C:\\Users\\synthetic\\tools\\e.exe", "error": "binary path is not in a trusted install prefix"}
            }}
            """);

        Assert.True(report.Read);
        var kept = Assert.Single(report.Connectors);
        Assert.Equal(("e", @"C:\Users\synthetic\tools"), (kept.Connector, kept.Directory));
    }

    // ---- which folders are suggested ----

    [Theory]
    [InlineData(@"C:\Users\synthetic\.local\bin", true)]
    [InlineData(@"C:\Program Files\Synthetic Tools\bin", true)]
    [InlineData(@"C:\Users\synthetic\.local\bin\", true)]
    [InlineData(@"\\server\share\tools", true)]
    [InlineData(@"D:\a", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("C:", false)]
    [InlineData(@"tools\bin", false)]
    [InlineData(@"~\.local\bin", false)]
    [InlineData(@"-C:\x", false)]
    [InlineData("--help", false)]
    [InlineData("C:\\x\tbin", false)]
    [InlineData("C:\\x\nbin", false)]
    [InlineData("C:\\x\u202Ebin", false)]
    public void A_folder_is_suggested_only_when_it_is_an_absolute_path_the_operator_could_have_typed(string? folder, bool expected) =>
        Assert.Equal(expected, TrustedFolders.IsOffered(folder));

    [Fact]
    public void A_folder_is_suggested_only_up_to_the_length_a_path_can_have()
    {
        Assert.True(TrustedFolders.IsOffered(@"C:\" + new string('d', TrustedFolders.MaxOfferedLength - 3)));
        Assert.False(TrustedFolders.IsOffered(@"C:\" + new string('d', TrustedFolders.MaxOfferedLength - 2)));
    }
}
