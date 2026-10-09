using System.Text;
using System.Text.Json;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The Runtime panel's reader (CUST-309): <c>GET /api/v1/ai-usage/runtime</c> into <see cref="AiRuntimeSnapshot"/>. The fixtures are
/// <b>synthetic</b>: the disabled answer is the capture under <c>runtime-95159fd/rest</c>; the populated, degraded and planes-A-and-B ones
/// (<c>*.synthetic.json</c>) are built by hand from the emitting code (<c>internal/gateway/ai_runtime_api.go</c> lines 31-97 and the
/// <c>internal/sensor</c> strings behind the plane reasons) because no populated snapshot has been captured on Windows. Nothing here starts a
/// process or opens a socket.
/// </summary>
public class AiRuntimeReaderTests
{
    private static string Rest(string name) => RuntimeFixtures.Read("rest/" + name);

    private static AiRuntimeSnapshot Snapshot(string name)
    {
        var read = AiRuntimeReader.ParseText(Rest(name));
        Assert.True(read.IsOk, read.Message);
        return read.Snapshot!;
    }

    private static AiRuntimeRead FromJson(string json) => AiRuntimeReader.ParseText(json);

    // ------------------------------------------------------------------ the five snapshots

    [Fact]
    public void The_disabled_runtime_of_the_pin_reads_as_off_with_no_planes_and_no_findings()
    {
        var snapshot = Snapshot("ai-usage-runtime.json");

        Assert.False(snapshot.Enabled);
        Assert.False(snapshot.Polled);
        Assert.Null(snapshot.ScannedAt);
        Assert.Empty(snapshot.Planes);
        Assert.Empty(snapshot.Findings);
        Assert.False(snapshot.Degraded);

        // Nothing is being watched, and the coverage verdict says so rather than reading an empty list as good news.
        Assert.False(snapshot.Coverage.IsComplete);
        Assert.Contains("unknown", snapshot.Coverage.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void The_populated_snapshot_is_worst_first_unique_and_keeps_what_the_inspector_needs()
    {
        var snapshot = Snapshot("ai-usage-runtime.populated.synthetic.json");

        Assert.True(snapshot.Enabled);
        Assert.True(snapshot.Polled);
        Assert.Equal(new DateTimeOffset(2030, 1, 15, 10, 4, 30, TimeSpan.Zero), snapshot.ScannedAt);
        Assert.Equal(6, snapshot.Findings.Count);
        Assert.Equal(0, snapshot.FindingsNotShown);
        Assert.Equal(0, snapshot.UnreadableEntries);

        // critical, high x2 (60 before 55), medium x2 (44 before 35), low
        Assert.Equal(
            new[] { "critical", "high", "high", "medium", "medium", "low" },
            snapshot.Findings.Select(f => f.SeverityKey).ToArray());
        Assert.Equal(new[] { 100, 60, 55, 44, 35, 18 }, snapshot.Findings.Select(f => f.Score).ToArray());
        Assert.Equal(6, snapshot.Findings.Select(f => f.Id).Distinct().Count());

        var chain = snapshot.Findings[0];
        Assert.Equal(4120, chain.Pid);
        Assert.Equal("python.exe", chain.Process);
        Assert.Equal("claude-code", chain.AgentName);
        Assert.Equal(@"EXAMPLE\operator", chain.User);
        Assert.Equal("credential_access -> identity_creation -> exfiltration", chain.Chain);
        Assert.Equal(4, chain.Signals.Count);
        Assert.Equal(new[] { 28, 42, 33, 25 }, chain.Signals.Select(s => s.Weight).ToArray());
        Assert.True(chain.Correlation.IsUnaccounted);
        Assert.Equal(new DateTimeOffset(2030, 1, 15, 10, 1, 10, TimeSpan.Zero), chain.FirstSeen);
    }

    [Fact]
    public void A_finding_without_an_id_is_matched_by_its_pid_and_process_and_an_unattributed_peer_keeps_its_port()
    {
        var snapshot = Snapshot("ai-usage-runtime.populated.synthetic.json");

        Assert.Contains(snapshot.Findings, f => f.FindingId.Length == 0 && f.Id == "3320-python.exe");

        // Two ports on one host and address are two peers: neither collapses into the other.
        var egress = snapshot.Findings.Single(f => f.Pid == 7788);
        Assert.Equal(2, egress.Providers.Count);
        Assert.Equal(2, egress.Providers.Select(p => p.Identity).Distinct().Count());
        Assert.Equal("api.provider-a.example:443, api.provider-a.example:8443", egress.ProviderSummary);
        Assert.Equal("frontier, dns answer (95%)", egress.Providers[0].Detail);
        Assert.True(egress.Correlation.VerdictText == "accounted");
        Assert.Equal(new[] { "coding_assistant" }, egress.Correlation.Categories);

        // A finding with no providers shows a dash; a provider with no category says so.
        Assert.Equal("—", snapshot.Findings.Single(f => f.Pid == 9912).ProviderSummary);
        var local = snapshot.Findings.Single(f => f.Pid == 5504);
        Assert.Equal("localhost:11434", local.ProviderSummary);
        Assert.Equal("uncategorised, reverse dns (60%)", local.Providers[0].Detail);
    }

    [Fact]
    public void Unobserved_is_kept_and_named_not_dropped_or_turned_into_agreement()
    {
        var snapshot = Snapshot("ai-usage-runtime.populated.synthetic.json");

        var unobserved = snapshot.Findings.Where(f => f.Correlation.IsUnobserved).ToArray();
        Assert.Equal(2, unobserved.Length);
        Assert.All(unobserved, f =>
        {
            Assert.Equal("unobserved", f.Correlation.VerdictText);
            Assert.False(string.IsNullOrWhiteSpace(f.Correlation.Reason));
            Assert.False(f.Correlation.IsUnaccounted);
        });
    }

    [Fact]
    public void Credentials_in_a_command_line_are_masked_before_they_are_kept()
    {
        var snapshot = Snapshot("ai-usage-runtime.populated.synthetic.json");

        var cmdline = snapshot.Findings[0].Cmdline;
        Assert.Contains("--api-key [redacted]", cmdline, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-synthetic", cmdline, StringComparison.Ordinal);
        Assert.StartsWith("python.exe C:\\Users\\operator\\agents\\sync\\run.py", cmdline, StringComparison.Ordinal);
    }

    [Fact]
    public void With_all_three_planes_up_and_few_unattributed_connections_the_coverage_is_complete_and_still_says_nothing_about_a_clean_host()
    {
        var snapshot = Snapshot("ai-usage-runtime.populated.synthetic.json");

        Assert.All(snapshot.Planes, p => Assert.Equal(AiRuntimePlaneState.Up, p.State));
        Assert.Equal(new[] { "a", "b", "c" }, snapshot.Planes.Select(p => p.Id).ToArray());
        Assert.False(snapshot.HasUnattributedWarning);
        Assert.True(snapshot.Coverage.IsComplete);
        Assert.Equal(3, snapshot.Coverage.FullyUpPlanes);
        Assert.Empty(snapshot.Coverage.Gaps);
        Assert.Equal("All three planes are reporting.", snapshot.Coverage.Headline);
        Assert.Equal("212 processes, 3 not fully readable, 148 connections, 6 with no owner", snapshot.CoverageSummary);
        Assert.Equal("host plane: 57 kernel events classified, 41 excluded outside AI-agent lineage", snapshot.HostPlaneSummary);

        // The strongest thing the verdict says is that the sensors are reporting; it never says the host is clean.
        Assert.DoesNotContain("clean", snapshot.Coverage.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a clean host", AiRuntimeCoverage.NoFindingsCaveat, StringComparison.Ordinal);
    }

    [Fact]
    public void The_degraded_snapshot_states_every_plane_that_is_not_up_with_its_whole_reason()
    {
        var snapshot = Snapshot("ai-usage-runtime.degraded.synthetic.json");

        Assert.True(snapshot.Degraded);
        Assert.Equal(
            new[] { AiRuntimePlaneState.Up, AiRuntimePlaneState.Partial, AiRuntimePlaneState.Blind },
            snapshot.Planes.Select(p => p.State).ToArray());

        var egress = snapshot.Planes[1];
        Assert.Equal("partial", egress.Badge);
        Assert.Equal(
            "egress attribution is limited to this process's own sockets; run the gateway elevated for machine-wide coverage",
            egress.Reason);
        Assert.Contains(egress.Reason, egress.Summary, StringComparison.Ordinal);

        var actions = snapshot.Planes[2];
        Assert.Equal("blind", actions.Badge);
        Assert.False(actions.Available);
        Assert.False(actions.Running);
        Assert.Equal(
            "plane: Security event log unreadable: Access is denied. (the gateway needs an elevated token to read the Security channel)",
            actions.Reason);
        Assert.Contains(actions.Reason, actions.Summary, StringComparison.Ordinal);

        Assert.Equal(2, snapshot.PlanesNotUp.Count);
        Assert.Equal(2, snapshot.DegradedReasons.Count);
    }

    [Fact]
    public void The_gateways_degraded_lines_that_only_restate_a_plane_are_not_listed_twice()
    {
        var snapshot = Snapshot("ai-usage-runtime.degraded.synthetic.json");

        // Both lines are "<plane name> <state>: <the plane's reason>", so the plane strip already says all of it.
        Assert.Empty(snapshot.ExtraDegradedReasons);

        var withOther = FromJson(Rest("ai-usage-runtime.degraded.synthetic.json").Replace(
            "\"degraded_reasons\": [",
            "\"degraded_reasons\": [\"process table unreadable: Access is denied.\", ",
            StringComparison.Ordinal)).Snapshot!;
        Assert.Equal(new[] { "process table unreadable: Access is denied." }, withOther.ExtraDegradedReasons.ToArray());
        Assert.Contains("process table unreadable: Access is denied.", withOther.Coverage.Gaps);
    }

    [Fact]
    public void More_than_half_unattributed_raises_the_warning_with_the_percentage_and_makes_the_coverage_incomplete()
    {
        var snapshot = Snapshot("ai-usage-runtime.degraded.synthetic.json");

        Assert.True(snapshot.HasUnattributedWarning);
        Assert.Equal(
            "54% of connections could not be attributed to a process. Run the gateway elevated for machine-wide egress attribution.",
            snapshot.UnattributedWarning);
        Assert.Contains(snapshot.UnattributedWarning, snapshot.Coverage.Gaps);
        Assert.False(snapshot.Coverage.IsComplete);
        Assert.Equal(1, snapshot.Coverage.FullyUpPlanes);
        Assert.Equal("Partial coverage: 1 of 3 planes fully reporting.", snapshot.Coverage.Headline);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(100, 49, false)]
    [InlineData(100, 50, true)]
    [InlineData(100, 51, true)]
    [InlineData(2, 1, true)]
    [InlineData(3, 1, false)]
    [InlineData(10, 0, false)]
    public void The_unattributed_warning_starts_at_half_of_the_connections_seen(int observed, int unattributed, bool warns)
    {
        var json = $$"""{"enabled":true,"findings":[],"planes":[],"connections_observed":{{observed}},"connections_unattributed":{{unattributed}}}""";

        var snapshot = FromJson(json).Snapshot!;

        Assert.Equal(warns, snapshot.HasUnattributedWarning);
        Assert.Equal(warns, snapshot.UnattributedWarning.Length > 0);
    }

    [Fact]
    public void A_plane_that_is_not_selected_is_off_not_blind_and_a_snapshot_with_one_off_is_still_not_complete()
    {
        var snapshot = Snapshot("ai-usage-runtime.planes-ab.synthetic.json");

        Assert.Equal(
            new[] { AiRuntimePlaneState.Up, AiRuntimePlaneState.Up, AiRuntimePlaneState.Off },
            snapshot.Planes.Select(p => p.State).ToArray());
        Assert.Equal("off", snapshot.Planes[2].Badge);
        Assert.False(snapshot.Degraded);
        Assert.Empty(snapshot.Findings);

        // The gateway does not call a deselected plane degraded, and this app does not call the coverage complete.
        Assert.False(snapshot.Coverage.IsComplete);
        Assert.Equal("Partial coverage: 2 of 3 planes fully reporting.", snapshot.Coverage.Headline);
        Assert.Contains(snapshot.Planes[2].Summary, snapshot.Coverage.Gaps);
    }

    [Theory]
    [InlineData(true, true, "", AiRuntimePlaneState.Up)]
    [InlineData(true, true, "the kernel event source is missing file events", AiRuntimePlaneState.Partial)]
    [InlineData(true, false, "a source stopped", AiRuntimePlaneState.Idle)]
    [InlineData(true, false, "", AiRuntimePlaneState.Idle)]
    [InlineData(false, false, "needs an elevated token", AiRuntimePlaneState.Blind)]
    [InlineData(false, false, "", AiRuntimePlaneState.Blind)]
    [InlineData(true, false, "not selected in ai_discovery.runtime.planes", AiRuntimePlaneState.Off)]
    [InlineData(false, false, "NOT SELECTED in ai_discovery.runtime.planes", AiRuntimePlaneState.Off)]
    [InlineData(true, false, "plane c is listed in ai_discovery.runtime.planes but ai_discovery.runtime.enable_host_plane is false; the opt-in is where the privilege and privacy decision is recorded", AiRuntimePlaneState.Off)]
    [InlineData(true, true, "not selected in the poll window", AiRuntimePlaneState.Partial)]
    public void A_plane_is_one_of_five_states_and_only_up_is_fully_up(bool available, bool running, string reason, AiRuntimePlaneState expected)
    {
        var plane = new AiRuntimePlane("c", "agent actions", available, running, "ETW", reason);

        Assert.Equal(expected, plane.State);
        Assert.Equal(expected == AiRuntimePlaneState.Up, plane.IsFullyUp);
    }

    [Theory]
    [InlineData("an ETW session needs an elevated token, and this process is not elevated. Security-log events additionally need Advanced Audit Policy enabled, which elevation alone does not supply")]
    [InlineData("plane: Security event log unreadable: Access is denied. (the gateway needs an elevated token to read the Security channel)")]
    public void Both_unelevated_reasons_of_plane_c_reach_the_screen_whole_and_read_as_blind(string reason)
    {
        // The first is the capability's own sentence (internal/sensor/platform/windows.go:91-95); the second is what the host plane reports when the
        // Security channel cannot be opened (internal/sensor/plane/windows.go:147-150). "an elevated token to read" must not come out with a word blanked.
        var json = "{\"enabled\":true,\"findings\":[],\"planes\":[{\"plane\":\"c\",\"name\":\"agent actions\",\"available\":false,\"running\":false,\"reason\":" +
                   JsonSerializer.Serialize(reason) + "}]}";

        var plane = FromJson(json).Snapshot!.Planes.Single();

        Assert.Equal(reason, plane.Reason);
        Assert.Equal(AiRuntimePlaneState.Blind, plane.State);
        Assert.Contains(reason, plane.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plane_that_gives_no_reason_says_so_instead_of_staying_silent()
    {
        var blind = new AiRuntimePlane("c", "agent actions", false, false, string.Empty, string.Empty);

        Assert.Equal("C · agent actions: unavailable. no reason reported", blind.Summary);
        Assert.Equal("C · agent actions", blind.DisplayName);
        Assert.Equal("A · x: up via an unnamed mechanism", new AiRuntimePlane("a", "x", true, true, string.Empty, string.Empty).Summary);
    }

    // ------------------------------------------------------------------ nothing here is ever a clean result

    [Fact]
    public void No_findings_with_a_plane_not_up_is_an_incomplete_coverage_in_every_shape_it_can_take()
    {
        foreach (var name in new[] { "ai-usage-runtime.json", "ai-usage-runtime.planes-ab.synthetic.json" })
        {
            var snapshot = Snapshot(name);
            Assert.Empty(snapshot.Findings);
            Assert.False(snapshot.Coverage.IsComplete, name);
        }

        // Idle and blind, the two the Mac names, and a snapshot with a degraded flag and no planes at all.
        string[] bodies =
        [
            """{"enabled":true,"findings":[],"planes":[{"plane":"a","name":"x","available":true,"running":false,"reason":"not started"},{"plane":"b","name":"y","available":true,"running":true},{"plane":"c","name":"z","available":true,"running":true}]}""",
            """{"enabled":true,"findings":[],"planes":[{"plane":"a","name":"x","available":true,"running":true},{"plane":"b","name":"y","available":false,"running":false,"reason":"r"},{"plane":"c","name":"z","available":true,"running":true}]}""",
            """{"enabled":true,"findings":[],"planes":[{"plane":"a","name":"x","available":true,"running":true},{"plane":"b","name":"y","available":true,"running":true},{"plane":"c","name":"z","available":true,"running":true}],"degraded":true}""",
            """{"enabled":true,"findings":[],"planes":[]}""",
            """{"enabled":true,"findings":[],"planes":[{"plane":"a","name":"x","available":true,"running":true},{"plane":"b","name":"y","available":true,"running":true}]}""",
        ];
        foreach (var body in bodies)
        {
            Assert.False(FromJson(body).Snapshot!.Coverage.IsComplete, body);
        }
    }

    [Fact]
    public void A_plane_the_gateway_did_not_list_is_a_gap_not_good_news()
    {
        var snapshot = FromJson("""{"enabled":true,"findings":[],"planes":[{"plane":"a","name":"x","available":true,"running":true},{"plane":"b","name":"y","available":true,"running":true}]}""").Snapshot!;

        Assert.Equal(new[] { "c" }, snapshot.MissingPlaneIds.ToArray());
        Assert.Contains("Plane C was not reported by the gateway.", snapshot.Coverage.Gaps);
        Assert.False(snapshot.Coverage.IsComplete);
    }

    [Fact]
    public void The_coverage_texts_never_use_the_word_clean_except_to_deny_it()
    {
        foreach (var name in new[]
                 {
                     "ai-usage-runtime.json", "ai-usage-runtime.populated.synthetic.json", "ai-usage-runtime.degraded.synthetic.json",
                     "ai-usage-runtime.planes-ab.synthetic.json",
                 })
        {
            var snapshot = Snapshot(name);
            var texts = snapshot.Coverage.Gaps.Append(snapshot.Coverage.Headline).Append(snapshot.CoverageSummary).Append(snapshot.HostPlaneSummary)
                .Concat(snapshot.Planes.Select(p => p.Summary));
            Assert.All(texts, text => Assert.DoesNotContain("clean", text, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ------------------------------------------------------------------ how a read ends

    [Fact]
    public void A_404_and_a_405_mean_the_version_has_no_runtime_planes_and_clear_nothing_else()
    {
        foreach (var code in new[] { 404, 405 })
        {
            var read = AiRuntimeReader.FromGateway(GatewayResult<JsonDocument>.Error("gateway returned HTTP " + code, code));

            Assert.Equal(AiRuntimeReadStatus.Unsupported, read.Status);
            Assert.Null(read.Snapshot);
            Assert.False(read.KeepsLastSnapshot);
            Assert.Contains(code.ToString(System.Globalization.CultureInfo.InvariantCulture), read.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unreachable_gateway_a_refused_token_and_a_missing_subsystem_each_keep_the_last_snapshot()
    {
        var unreachable = AiRuntimeReader.FromGateway(GatewayResult<JsonDocument>.Unreachable("gateway is not listening (connection refused)"));
        var unauthorized = AiRuntimeReader.FromGateway(GatewayResult<JsonDocument>.Unauthorized());
        var notConnected = AiRuntimeReader.FromGateway(GatewayResult<JsonDocument>.NotConnected());

        Assert.Equal(AiRuntimeReadStatus.Unreachable, unreachable.Status);
        Assert.Equal(AiRuntimeReadStatus.Unauthorized, unauthorized.Status);
        Assert.Equal(AiRuntimeReadStatus.NotConnected, notConnected.Status);
        Assert.All(new[] { unreachable, unauthorized, notConnected }, read =>
        {
            Assert.False(read.IsOk);
            Assert.True(read.KeepsLastSnapshot);
            Assert.NotEmpty(read.Message);
        });
    }

    [Fact]
    public void A_server_error_is_a_failure_that_keeps_the_last_snapshot_and_does_not_echo_the_server_text()
    {
        var read = AiRuntimeReader.FromGateway(GatewayResult<JsonDocument>.Error("internal error: stack trace here", 500));

        Assert.Equal(AiRuntimeReadStatus.Failed, read.Status);
        Assert.True(read.KeepsLastSnapshot);
        Assert.Equal("The gateway returned HTTP 500.", read.Message);
    }

    [Fact]
    public void A_good_status_with_a_body_that_could_not_be_used_is_malformed_and_keeps_the_last_snapshot()
    {
        var unparseable = AiRuntimeReader.FromGateway(GatewayResult<JsonDocument>.Error("could not parse gateway response: '<' is an invalid start of a value.", 200));
        var empty = AiRuntimeReader.FromGateway(GatewayResult<JsonDocument>.Ok(null!, 200));

        Assert.Equal(AiRuntimeReadStatus.Malformed, unparseable.Status);
        Assert.Equal(AiRuntimeReadStatus.Malformed, empty.Status);
        Assert.True(unparseable.KeepsLastSnapshot);
        Assert.True(empty.KeepsLastSnapshot);
    }

    [Fact]
    public void A_read_through_the_real_client_interpretation_of_the_pins_answer_gives_the_same_snapshot()
    {
        var body = Rest("ai-usage-runtime.degraded.synthetic.json");

        var result = GatewayClient.Interpret<JsonDocument>(System.Net.HttpStatusCode.OK, body);
        var notFound = GatewayClient.Interpret<JsonDocument>(System.Net.HttpStatusCode.NotFound, "404 page not found");
        var notAllowed = GatewayClient.Interpret<JsonDocument>(System.Net.HttpStatusCode.MethodNotAllowed, "method not allowed");
        var html = GatewayClient.Interpret<JsonDocument>(System.Net.HttpStatusCode.OK, "<html>oops</html>");

        using (result.Value)
        {
            var read = AiRuntimeReader.FromGateway(result);
            Assert.True(read.IsOk);
            Assert.Equal(AiRuntimePlaneState.Blind, read.Snapshot!.Planes[2].State);
        }

        Assert.Equal(AiRuntimeReadStatus.Unsupported, AiRuntimeReader.FromGateway(notFound).Status);
        Assert.Equal(AiRuntimeReadStatus.Unsupported, AiRuntimeReader.FromGateway(notAllowed).Status);
        Assert.Equal(AiRuntimeReadStatus.Malformed, AiRuntimeReader.FromGateway(html).Status);
    }

    // ------------------------------------------------------------------ malformed answers

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"enabled":true}""")]
    [InlineData("""{"enabled":true,"planes":[]}""")]
    [InlineData("""{"enabled":true,"findings":[]}""")]
    [InlineData("""{"planes":[],"findings":[]}""")]
    [InlineData("""{"enabled":"yes","planes":[],"findings":[]}""")]
    [InlineData("""{"enabled":1,"planes":[],"findings":[]}""")]
    [InlineData("""{"enabled":true,"planes":{},"findings":[]}""")]
    [InlineData("""{"enabled":true,"planes":[],"findings":"none"}""")]
    [InlineData("""{"enabled":true,"planes":null,"findings":null}""")]
    [InlineData("{\"enabled\":true,\"planes\":[],\"findings\":[")]
    [InlineData("<html>not json</html>")]
    [InlineData("")]
    public void An_answer_without_enabled_planes_and_findings_is_malformed_never_an_empty_list(string json)
    {
        var read = FromJson(json);

        Assert.Equal(AiRuntimeReadStatus.Malformed, read.Status);
        Assert.Null(read.Snapshot);
        Assert.True(read.KeepsLastSnapshot);
        Assert.NotEmpty(read.Message);
    }

    [Fact]
    public void An_element_of_the_wrong_kind_is_skipped_and_counted_so_a_lost_entry_cannot_read_as_a_quiet_one()
    {
        const string json = """
            {"enabled":true,
             "planes":[1,"x",{"plane":"a","name":"inference heartbeat","available":true,"running":true},null],
             "findings":[null,3,"x",{"finding_id":"f1","pid":1,"process":"p","score":40,"severity":"medium"}]}
            """;

        var snapshot = FromJson(json).Snapshot!;

        Assert.Single(snapshot.Planes);
        Assert.Single(snapshot.Findings);
        Assert.Equal(6, snapshot.UnreadableEntries);
        Assert.False(snapshot.Coverage.IsComplete);
        Assert.Contains(snapshot.Coverage.Gaps, gap => gap.Contains("6 entries", StringComparison.Ordinal));
    }

    [Fact]
    public void Fields_an_older_gateway_leaves_out_read_as_zero_rather_than_failing()
    {
        const string json = """{"enabled":true,"planes":[],"findings":[{"pid":9,"process":"p"}]}""";

        var snapshot = FromJson(json).Snapshot!;
        var finding = snapshot.Findings.Single();

        Assert.Equal(0, snapshot.ProcessesObserved);
        Assert.Equal(0, snapshot.ConnectionsObserved);
        Assert.Equal(0L, snapshot.HostPlaneObservations);
        Assert.False(snapshot.Degraded);
        Assert.Empty(snapshot.DegradedReasons);
        Assert.Equal("9-p", finding.Id);
        Assert.Equal("info", finding.Severity);
        Assert.Equal(0, finding.Score);
        Assert.Empty(finding.Signals);
        Assert.Equal(string.Empty, finding.Correlation.Verdict);
        Assert.Equal("unknown", finding.Correlation.VerdictText);
        Assert.Null(finding.FirstSeen);
    }

    [Fact]
    public void Numbers_and_times_of_the_wrong_kind_read_as_nothing_not_as_an_exception()
    {
        const string json = """
            {"enabled":true,"scanned_at":"not a time","processes_observed":"many","connections_observed":1e30,"connections_unattributed":-5,
             "host_plane_observations":2.9,
             "planes":[],"findings":[{"pid":"x","process":"p","score":1.5e400,"severity":"high","first_seen":12}]}
            """;

        var snapshot = FromJson(json).Snapshot!;

        Assert.True(snapshot.Polled);
        Assert.Null(snapshot.ScannedAt);
        Assert.Equal(0, snapshot.ProcessesObserved);
        Assert.Equal(int.MaxValue, snapshot.ConnectionsObserved);
        Assert.Equal(-5, snapshot.ConnectionsUnattributed);
        Assert.Equal(2L, snapshot.HostPlaneObservations);
        Assert.Equal(0, snapshot.Findings[0].Pid);
        Assert.Equal(0, snapshot.Findings[0].Score);
        Assert.Null(snapshot.Findings[0].FirstSeen);
        Assert.Equal(0, snapshot.UnattributedShare);
    }

    // ------------------------------------------------------------------ bounds

    [Fact]
    public void Only_the_worst_thousand_findings_are_kept_and_the_snapshot_says_how_many_were_not()
    {
        var body = new StringBuilder("{\"enabled\":true,\"planes\":[],\"findings\":[");
        for (var i = 0; i < 1500; i++)
        {
            if (i > 0)
            {
                body.Append(',');
            }

            // 1 low-scored finding per index, plus a handful of critical ones at the very end of the array.
            var severity = i >= 1495 ? "critical" : "low";
            var score = i >= 1495 ? 90 : 20;
            body.Append($$"""{"finding_id":"f{{i}}","pid":{{i + 1}},"process":"p{{i}}","score":{{score}},"severity":"{{severity}}"}""");
        }

        body.Append("]}");

        var snapshot = FromJson(body.ToString()).Snapshot!;

        Assert.Equal(AiRuntimeReader.MaxFindings, snapshot.Findings.Count);
        Assert.Equal(500, snapshot.FindingsNotShown);
        Assert.All(snapshot.Findings.Take(5), f => Assert.Equal("critical", f.SeverityKey));
        Assert.Equal(1000, snapshot.Findings.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void A_repeated_id_is_one_row_and_counts_as_not_shown()
    {
        const string json = """
            {"enabled":true,"planes":[],"findings":[
              {"finding_id":"same","pid":1,"process":"a","score":50,"severity":"high"},
              {"finding_id":"same","pid":2,"process":"b","score":90,"severity":"critical"},
              {"finding_id":"other","pid":3,"process":"c","score":40,"severity":"medium"}]}
            """;

        var snapshot = FromJson(json).Snapshot!;

        Assert.Equal(2, snapshot.Findings.Count);
        Assert.Equal(1, snapshot.FindingsNotShown);
        Assert.Equal(2, snapshot.Findings.First(f => f.FindingId == "same").Pid); // the worse one wins
    }

    [Fact]
    public void Lists_and_strings_inside_a_finding_are_cut_to_their_limits()
    {
        var signals = string.Join(',', Enumerable.Range(0, 200).Select(i => $$"""{"id":"s{{i}}","weight":1}"""));
        var providers = string.Join(',', Enumerable.Range(0, 200).Select(i => $$"""{"hostname":"h{{i}}.example","port":{{i + 1}}}"""));
        var categories = string.Join(',', Enumerable.Range(0, 100).Select(i => $"\"c{i}\""));
        var long1 = new string('x', 10_000);
        var json = $$"""
            {"enabled":true,"planes":[],"degraded":true,"degraded_reasons":[{{string.Join(',', Enumerable.Range(0, 100).Select(i => $"\"reason {i}\""))}}],
             "findings":[{"finding_id":"f","pid":1,"process":"{{long1}}","cmdline":"{{long1}}","user":"{{long1}}","severity":"low",
               "signals":[{{signals}}],"providers":[{{providers}}],
               "correlation":{"verdict":"unaccounted","reason":"{{long1}}","categories":[{{categories}}]} }]}
            """;

        var snapshot = FromJson(json).Snapshot!;
        var finding = snapshot.Findings.Single();

        Assert.Equal(AiRuntimeReader.MaxSignals, finding.Signals.Count);
        Assert.Equal(AiRuntimeReader.MaxProviders, finding.Providers.Count);
        Assert.Equal(AiRuntimeReader.MaxListItems, finding.Correlation.Categories.Count);
        Assert.Equal(AiRuntimeReader.MaxDegradedReasons, snapshot.DegradedReasons.Count);
        Assert.Equal(AiRuntimeReader.NameLimit, finding.Process.Length);
        Assert.Equal(AiRuntimeReader.NameLimit, finding.User.Length);
        Assert.Equal(AiRuntimeReader.CmdlineLimit, finding.Cmdline.Length);
        Assert.Equal(AiRuntimeReader.TextLimit, finding.Correlation.Reason.Length);
    }

    // ------------------------------------------------------------------ what reaches the screen

    [Fact]
    public void Reasons_details_and_degraded_lines_are_masked_before_they_are_kept()
    {
        const string json = """
            {"enabled":true,"degraded":true,
             "degraded_reasons":["agent actions unavailable: could not open https://hooks.example.test/x?token=synthetic-synthetic"],
             "planes":[{"plane":"c","name":"agent actions","available":false,"running":false,"reason":"start failed: password=synthetic-synthetic"}],
             "findings":[{"finding_id":"f","pid":1,"process":"p","severity":"low",
               "signals":[{"id":"s","detail":"read file with api_key=synthetic-synthetic","weight":1}],
               "correlation":{"verdict":"unobserved","reason":"stale; secret: synthetic-synthetic"}}]}
            """;

        var snapshot = FromJson(json).Snapshot!;
        var shown = string.Join(
            '\n',
            snapshot.DegradedReasons
                .Concat(snapshot.Planes.Select(p => p.Reason))
                .Concat(snapshot.Findings.SelectMany(f => f.Signals.Select(s => s.Detail)))
                .Concat(snapshot.Findings.Select(f => f.Correlation.Reason)));

        Assert.DoesNotContain("synthetic-synthetic", shown, StringComparison.Ordinal);
        Assert.Contains("[redacted]", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_process_that_names_itself_with_a_bidi_override_or_a_newline_is_shown_with_those_spelled_out()
    {
        const string json = """
            {"enabled":true,"planes":[],"findings":[{"finding_id":"f","pid":1,"process":"evil\u202Eexe.txt","cmdline":"a\nb\u200Bc","user":"u\r\nv","agent_name":"x\ty","severity":"low"}]}
            """;

        var finding = FromJson(json).Snapshot!.Findings.Single();

        Assert.Equal("evil\\u202Eexe.txt", finding.Process);
        Assert.Equal("a\\nb\\u200Bc", finding.Cmdline);
        Assert.Equal("u\\r\\nv", finding.User);
        Assert.Equal("x\\ty", finding.AgentName);
        Assert.All(new[] { finding.Process, finding.Cmdline, finding.User, finding.AgentName }, text =>
        {
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('\r', text);
            Assert.DoesNotContain('\u202E', text);
        });
    }

    [Fact]
    public void An_unknown_severity_ranks_last_and_is_shown_as_sent()
    {
        const string json = """
            {"enabled":true,"planes":[],"findings":[
              {"finding_id":"a","pid":1,"process":"a","score":99,"severity":"BANANA"},
              {"finding_id":"b","pid":2,"process":"b","score":10,"severity":"Low"},
              {"finding_id":"c","pid":3,"process":"c","score":1,"severity":""}]}
            """;

        var findings = FromJson(json).Snapshot!.Findings;

        Assert.Equal(new[] { "b", "a", "c" }, findings.Select(f => f.FindingId).ToArray());
        Assert.Equal("Low", findings[0].SeverityLabel);
        Assert.Equal("Banana", findings[1].SeverityLabel);
        Assert.Equal(4, findings[1].SeverityRank);
        Assert.Equal("Info", findings[2].SeverityLabel);
    }

    [Fact]
    public void The_filter_matches_every_word_across_the_fields_the_mac_searches()
    {
        var snapshot = Snapshot("ai-usage-runtime.populated.synthetic.json");
        var egress = snapshot.Findings.Single(f => f.Pid == 7788);

        Assert.True(egress.Matches(null));
        Assert.True(egress.Matches("  "));
        Assert.True(egress.Matches("NODE"));                       // process, any case
        Assert.True(egress.Matches("assistant\\index"));           // command line
        Assert.True(egress.Matches("operator"));                   // user
        Assert.True(egress.Matches("high"));                       // severity
        Assert.True(egress.Matches("provider-a"));                 // provider host
        Assert.True(egress.Matches("203.0.113.10"));               // provider address
        Assert.True(egress.Matches("accounted"));                  // inventory verdict
        Assert.True(egress.Matches("node accounted"));             // all words, in any order
        Assert.False(egress.Matches("node unaccounted"));
        Assert.False(egress.Matches("nonexistent"));
        Assert.True(snapshot.Findings.Single(f => f.Pid == 4120).Matches("claude-code"));  // agent
    }
}
