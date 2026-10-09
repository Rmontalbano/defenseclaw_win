using System.Text;
using System.Text.Json;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using static DefenseClaw.App.Tests.Discovery.DiscoveryData;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// The gateway's <c>GET /api/v1/ai-usage</c> as the panel reads it (CUST-310): the report read into a snapshot, every way the read can end,
/// and the one thing the report is used for - adding a newer runtime's model members to the signals the files list. The answers are the
/// fixtures of <c>runtime-95159fd/rest</c> and a DefenseClaw 0.8.10 answer built from the 0.8.10 state fixture the way its code builds one
/// (<see cref="DiscoveryData.ReportOf0810"/>); no gateway is asked.
/// </summary>
public sealed class AiDiscoveryUsageTests
{
    private static AiUsageSnapshot Snapshot(string json, DiscoveryReadOptions options = default)
    {
        var read = AiUsageReader.ParseText(json, options);
        Assert.True(read.IsOk, read.Message);
        Assert.Equal(AiUsageReadStatus.Ok, read.Status);
        return read.Snapshot!;
    }

    // ---- the report ----

    [Fact]
    public void The_disabled_answer_the_pin_gives_is_a_report_that_says_the_service_is_off()
    {
        var snapshot = Snapshot(Rest(ReportDisabled));

        Assert.False(snapshot.Enabled);
        Assert.False(snapshot.LookupModelProvenanceOnline);
        Assert.Empty(snapshot.Signals);
        Assert.Null(snapshot.ScannedAt);
        Assert.Equal(0, snapshot.SignalsNotRead);
        Assert.Equal(0, snapshot.UnreadableEntries);
    }

    [Fact]
    public void The_populated_report_gives_the_gateways_signals_with_the_newer_model_members_the_scan_time_and_the_lookup_setting()
    {
        var snapshot = Snapshot(Rest(ReportPopulated));

        Assert.True(snapshot.Enabled);
        Assert.False(snapshot.LookupModelProvenanceOnline);
        Assert.Equal(new DateTimeOffset(2030, 1, 15, 10, 4, 30, TimeSpan.Zero).AddTicks(1234568), snapshot.ScannedAt);
        Assert.Equal(13, snapshot.Signals.Count);

        var chat = snapshot.Signals.Single(s => s.SignalId == "ai-0000000000000201").Model!;
        Assert.Equal("Example-Chat-3B-Q4", chat.Id);
        Assert.Equal("Example Notes", chat.OwnerApplication);
        Assert.Equal("primary", chat.Relevance);
        Assert.Equal(0.95, chat.DiscoveryConfidence);
        Assert.Equal("Example Labs", chat.Provenance!.Publisher);
        Assert.Equal("US", chat.Provenance.CountryCode);
        Assert.Equal("catalog_exact", chat.Provenance.Source);

        // The same signal keeps what the state file keeps: evidence as type, basename, quality and match kind, and no path.
        var evidence = Assert.Single(snapshot.Signals.Single(s => s.SignalId == "ai-0000000000000201").Evidence);
        Assert.Equal(new DiscoveryEvidenceItem("model_file", "example-chat-3b-q4.gguf", 0.95, "exact"), evidence);

        // A model a server listed carries no confidence of its own; a reported zero would be one.
        var listed = snapshot.Signals.Single(s => s.SignalId == "ai-0000000000000212").Model!;
        Assert.Null(listed.DiscoveryConfidence);
        Assert.Equal(string.Empty, listed.OwnerApplication);
        Assert.NotNull(listed.Provenance);
    }

    [Fact]
    public void A_lookup_setting_is_true_false_or_unknown_and_unknown_is_not_offline()
    {
        Assert.True(Snapshot(WithLookup(Rest(ReportPopulated), true)).LookupModelProvenanceOnline);
        Assert.False(Snapshot(WithLookup(Rest(ReportPopulated), false)).LookupModelProvenanceOnline);

        // DefenseClaw 0.8.10 does not send the member at all.
        Assert.Null(Snapshot(WithLookup(Rest(ReportPopulated), null)).LookupModelProvenanceOnline);
        Assert.Null(Snapshot("""{"enabled":true,"lookup_model_provenance_online":"yes","signals":[]}""").LookupModelProvenanceOnline);
        Assert.Null(Snapshot("""{"enabled":true,"lookup_model_provenance_online":null,"signals":[]}""").LookupModelProvenanceOnline);
    }

    [Fact]
    public void A_0_8_10_answer_reads_like_the_files_signals_and_has_none_of_the_newer_members_or_the_lookup_setting()
    {
        var snapshot = Snapshot(ReportOf0810(State0810()));

        Assert.True(snapshot.Enabled);
        Assert.Null(snapshot.LookupModelProvenanceOnline);
        Assert.Equal(11, snapshot.Signals.Count);
        Assert.All(snapshot.Signals.Where(s => s.Model is not null), s => Assert.False(s.Model!.HasClassification || s.Model.Provenance is not null));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"report\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"signals":[]}""")]
    [InlineData("""{"enabled":"true","signals":[]}""")]
    [InlineData("""{"enabled":null}""")]
    [InlineData("""{"enabled":1}""")]
    public void An_answer_without_a_boolean_enabled_flag_is_not_a_report_and_never_the_service_being_off(string json)
    {
        var read = AiUsageReader.ParseText(json);

        Assert.Equal(AiUsageReadStatus.Malformed, read.Status);
        Assert.Null(read.Snapshot);
        Assert.Contains("no enabled flag", read.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_that_is_not_json_is_malformed()
    {
        var read = AiUsageReader.ParseText("<html>not the gateway</html>");

        Assert.Equal(AiUsageReadStatus.Malformed, read.Status);
        Assert.False(read.IsOk);
    }

    [Fact]
    public void Signals_of_the_wrong_shape_cost_only_themselves_and_are_counted()
    {
        var snapshot = Snapshot("""
            {"enabled": true, "signals": [
              {"signal_id":"a","vendor":"V","product":"P","category":"ai_cli","detector":"binary","state":"seen"},
              "not a signal", 5, null, [1, 2],
              {"signal_id":"b","vendor":"V","product":"Q","category":"ai_cli","detector":"binary","state":"seen"}
            ]}
            """);

        Assert.Equal(new[] { "a", "b" }, snapshot.Signals.Select(s => s.SignalId).ToArray());
        Assert.Equal(4, snapshot.UnreadableEntries);

        var notAList = Snapshot("""{"enabled": true, "signals": {"a": {"product": "P"}}}""");
        Assert.Empty(notAList.Signals);
        Assert.Equal(0, notAList.UnreadableEntries);
    }

    [Fact]
    public void Past_the_limit_the_signals_are_counted_and_not_parsed()
    {
        var builder = new StringBuilder("{\"enabled\":true,\"signals\":[");
        var total = AiUsageReader.MaxSignals + 7;
        for (var i = 0; i < total; i++)
        {
            _ = builder.Append(i > 0 ? "," : string.Empty).Append("{\"signal_id\":\"s").Append(i).Append("\",\"vendor\":\"V\",\"product\":\"P\",\"category\":\"ai_cli\"}");
        }

        _ = builder.Append("]}");

        var snapshot = Snapshot(builder.ToString());

        Assert.Equal(AiUsageReader.MaxSignals, snapshot.Signals.Count);
        Assert.Equal(7, snapshot.SignalsNotRead);
        Assert.Equal("s0", snapshot.Signals[0].SignalId);
    }

    [Fact]
    public void The_limits_are_the_Macs_four_megabytes_and_a_report_of_thousands_of_signals_fits_in_them()
    {
        Assert.Equal(4 * 1024 * 1024, AiUsageReader.MaxBytes);
        Assert.Equal("api/v1/ai-usage", AiUsageReader.Route);

        // On the wire a report is compact JSON. The populated fixture's 13 signals, written that way, come to about a thousand bytes each,
        // so three thousand signals like them fit in the limit.
        var compact = System.Text.Json.Nodes.JsonNode.Parse(Rest(ReportPopulated))!.ToJsonString();
        var perSignal = System.Text.Encoding.UTF8.GetByteCount(compact) / 13;
        Assert.True(perSignal * 3000 < AiUsageReader.MaxBytes, $"{perSignal} bytes a signal");
    }

    [Fact]
    public void A_path_in_the_reports_text_is_withheld_and_shown_only_when_the_runtime_keeps_raw_paths()
    {
        var json = Rest(ReportPopulated).Replace("\"owner_application\": \"Example Notes\"", "\"owner_application\": \"C:\\\\Users\\\\operator\\\\AppData\\\\Local\\\\Example Notes\\\\notes.exe\"", StringComparison.Ordinal);
        Assert.Contains("operator", json, StringComparison.Ordinal);

        var hidden = Snapshot(json).Signals.Single(s => s.SignalId == "ai-0000000000000201").Model!;
        Assert.Equal(DiscoveryPaths.HiddenText, hidden.OwnerApplication);

        var shown = Snapshot(json, new DiscoveryReadOptions(ShowRawLocalPaths: true)).Signals.Single(s => s.SignalId == "ai-0000000000000201").Model!;
        Assert.Equal(@"C:\Users\operator\AppData\Local\Example Notes\notes.exe", shown.OwnerApplication);
    }

    [Fact]
    public void A_raw_path_member_in_evidence_is_never_read_even_when_a_report_carries_one()
    {
        // The gateway clears raw_path from every evidence row before it answers (SanitizeEvidenceForWire); this proves the app would not
        // show one that slipped through, whatever the option says.
        var json = Rest(ReportPopulated).Replace(
            "\"basename\": \"example-chat-3b-q4.gguf\",",
            "\"basename\": \"example-chat-3b-q4.gguf\", \"raw_path\": \"C:\\\\Users\\\\operator\\\\models\\\\example-chat-3b-q4.gguf\",",
            StringComparison.Ordinal);
        Assert.Contains("raw_path", json, StringComparison.Ordinal);

        foreach (var options in new DiscoveryReadOptions[] { default, new(ShowRawLocalPaths: true) })
        {
            var evidence = Assert.Single(Snapshot(json, options).Signals.Single(s => s.SignalId == "ai-0000000000000201").Evidence);
            Assert.Equal("example-chat-3b-q4.gguf", evidence.Basename);
            Assert.DoesNotContain("operator", evidence.ToString(), StringComparison.Ordinal);
        }
    }

    // ---- how a read ends ----

    private static GatewayResult<JsonDocument> Doc(string json) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(json));

    [Fact]
    public void A_gateway_answer_that_is_a_report_is_read_and_the_document_is_left_to_its_owner()
    {
        using var document = JsonDocument.Parse(Rest(ReportPopulated));

        var read = AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Ok(document));

        Assert.Equal(AiUsageReadStatus.Ok, read.Status);
        Assert.Equal(13, read.Snapshot!.Signals.Count);

        // Still usable: the reader did not dispose it.
        Assert.True(document.RootElement.GetProperty("enabled").GetBoolean());
    }

    public static TheoryData<string, GatewayStatus, int?, string?, AiUsageReadStatus> Endings() => new()
    {
        { "unreachable", GatewayStatus.Unreachable, null, "gateway is not listening (connection refused)", AiUsageReadStatus.Unreachable },
        { "refused token", GatewayStatus.Unauthorized, 401, "unauthorized", AiUsageReadStatus.Unauthorized },
        { "not connected", GatewayStatus.NotConnected, 200, "gateway: not connected", AiUsageReadStatus.NotConnected },
        { "no such route", GatewayStatus.Error, 404, "gateway returned HTTP 404", AiUsageReadStatus.Unsupported },
        { "method not allowed", GatewayStatus.Error, 405, "gateway returned HTTP 405", AiUsageReadStatus.Unsupported },
        { "too large", GatewayStatus.Error, 200, "the gateway's answer is larger than 4194304 bytes, so it was not read", AiUsageReadStatus.TooLarge },
        { "2xx that is not json", GatewayStatus.Error, 200, "could not parse gateway response: boom", AiUsageReadStatus.Malformed },
        { "server error", GatewayStatus.Error, 500, "gateway returned HTTP 500", AiUsageReadStatus.Failed },
        { "error with no code", GatewayStatus.Error, null, "the request could not be sent (FormatException)", AiUsageReadStatus.Failed },
    };

    [Theory]
    [MemberData(nameof(Endings))]
    public void Every_way_a_read_can_end_is_a_state_of_its_own(string name, GatewayStatus status, int? http, string? message, AiUsageReadStatus expected)
    {
        var result = status switch
        {
            GatewayStatus.Unreachable => GatewayResult<JsonDocument>.Unreachable(message),
            GatewayStatus.Unauthorized => GatewayResult<JsonDocument>.Unauthorized(message, http ?? 401),
            GatewayStatus.NotConnected => GatewayResult<JsonDocument>.NotConnected(message, http),
            _ => GatewayResult<JsonDocument>.Error(message!, http),
        };

        var read = AiUsageReader.FromGateway(result);

        Assert.True(read.Status == expected, $"{name}: {read.Status}");
        Assert.Null(read.Snapshot);
        Assert.NotEmpty(read.Message);
        Assert.False(read.IsOk);
    }

    [Fact]
    public void What_the_gateway_said_in_an_error_never_reaches_the_message_unmasked_and_a_status_code_is_all_a_failure_says()
    {
        var malformed = AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Error("could not parse gateway response: api_key=sk-synthetic-not-a-key-0123456789", 200));
        Assert.Equal(AiUsageReadStatus.Malformed, malformed.Status);
        Assert.DoesNotContain("sk-synthetic", malformed.Message, StringComparison.Ordinal);

        var failed = AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Error("internal error: token=abc123 at C:\\Users\\operator\\x", 500));
        Assert.Equal("The gateway returned HTTP 500.", failed.Message);
    }

    [Fact]
    public void An_ok_result_with_no_document_is_malformed_and_not_an_empty_report()
    {
        var read = AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Ok(default!));

        Assert.Equal(AiUsageReadStatus.Malformed, read.Status);
    }

    // ---- the overlay ----

    private static IReadOnlyList<DiscoverySignalRecord> FilesOf(string reportJson, bool withNewerFields = false) =>
        SignalsFromState(StateFileOf(reportJson, withNewerFields));

    private static IReadOnlyList<DiscoverySignalRecord> SignalsFromState(string stateJson)
    {
        using var document = JsonDocument.Parse(stateJson);
        return document.RootElement.GetProperty("signals").EnumerateObject()
            .Select(property => DiscoverySignalParser.FromState(property.Value))
            .ToList();
    }

    [Fact]
    public void The_report_adds_owner_relevance_confidence_and_lineage_to_the_files_models_and_nothing_else()
    {
        var files = FilesOf(Rest(ReportPopulated));
        var report = SignalsOf(Rest(ReportPopulated));

        var (merged, enriched) = DiscoveryUsageOverlay.Apply(files, report);

        // The files carry no newer member; the report has them for the models the scanner classified, and a lineage for the ones a model
        // server listed (which has no owner, relevance or confidence of its own).
        Assert.Equal(10, files.Count(s => s.Model is not null));
        Assert.Equal(10, enriched);
        Assert.Equal(files.Count, merged.Count);

        var chatFile = merged.Single(s => s.SignalId == "ai-0000000000000201").Model!;
        Assert.Equal("Example Notes", chatFile.OwnerApplication);
        Assert.Equal(0.95, chatFile.DiscoveryConfidence);
        Assert.Equal("catalog_exact", chatFile.Provenance!.Source);

        // Everything else of the signal is the file's own.
        var before = files.Single(s => s.SignalId == "ai-0000000000000201");
        var after = merged.Single(s => s.SignalId == "ai-0000000000000201");
        Assert.Equal(before with { Model = null }, after with { Model = null });
        Assert.Equal(before.Model! with { OwnerApplication = string.Empty, Relevance = string.Empty, DiscoveryConfidence = null, Provenance = null },
            after.Model! with { OwnerApplication = string.Empty, Relevance = string.Empty, DiscoveryConfidence = null, Provenance = null });

        // Product signals are not touched: the report's per-signal scores are not taken.
        Assert.Null(merged.Single(s => s.SignalId == "ai-0000000000000301").IdentityBand);
        Assert.Same(files.Single(s => s.SignalId == "ai-0000000000000301"), merged.Single(s => s.SignalId == "ai-0000000000000301"));
    }

    [Fact]
    public void A_report_with_none_of_the_newer_members_changes_nothing_and_returns_the_list_it_was_given()
    {
        var files = SignalsFromState(State0810());
        var report = SignalsOf(ReportOf0810(State0810()));

        var (merged, enriched) = DiscoveryUsageOverlay.Apply(files, report);

        Assert.Equal(0, enriched);
        Assert.Same(files, merged);
    }

    [Fact]
    public void A_report_adds_no_signal_the_files_do_not_list_and_a_gone_one_stays_in_the_report()
    {
        var files = FilesOf(Rest(ReportPopulated));
        var report = SignalsOf(Rest(ReportPopulated));
        Assert.Contains(report, s => s.State == "gone");

        var (merged, _) = DiscoveryUsageOverlay.Apply(files, report);

        Assert.DoesNotContain(merged, s => s.State == "gone");
        Assert.DoesNotContain(merged, s => s.Model?.Id == "retired-model");
        Assert.Equal(files.Select(s => s.SignalId), merged.Select(s => s.SignalId));
    }

    [Fact]
    public void The_report_wins_where_it_has_a_member_and_the_file_keeps_the_ones_the_report_lacks()
    {
        var file = Signal(signalId: "s1", model: Model("m", owner: "Old Owner", relevance: "supporting", discoveryConfidence: 0.5));
        var report = Signal(signalId: "s1", model: Model("m", owner: "New Owner", provenance: Provenance(publisher: "P", source: "catalog_exact", confidence: "high")));

        var (merged, enriched) = DiscoveryUsageOverlay.Apply(new[] { file }, new[] { report });

        Assert.Equal(1, enriched);
        var model = Assert.Single(merged).Model!;
        Assert.Equal("New Owner", model.OwnerApplication);
        Assert.Equal("supporting", model.Relevance);
        Assert.Equal(0.5, model.DiscoveryConfidence);
        Assert.Equal("P", model.Provenance!.Publisher);
    }

    [Fact]
    public void A_report_signal_is_matched_by_its_id_and_its_model_and_not_by_anything_looser()
    {
        var file = Signal(signalId: "s1", model: Model("Model-A"));
        var richer = Model("model-a", owner: "Example Notes", relevance: "primary", discoveryConfidence: 0.9);

        // Same id, same model with a different case: matched.
        Assert.Equal(1, DiscoveryUsageOverlay.Apply(new[] { file }, new[] { Signal(signalId: "s1", model: richer) }).Enriched);

        // Same id, another model on it: not matched.
        Assert.Equal(0, DiscoveryUsageOverlay.Apply(new[] { file }, new[] { Signal(signalId: "s1", model: richer with { Id = "model-b" }) }).Enriched);

        // Another id, the same model: not matched. No id on either side: not matched.
        Assert.Equal(0, DiscoveryUsageOverlay.Apply(new[] { file }, new[] { Signal(signalId: "s2", model: richer) }).Enriched);
        Assert.Equal(0, DiscoveryUsageOverlay.Apply(new[] { file with { SignalId = null } }, new[] { Signal(signalId: null, model: richer) }).Enriched);

        // A file signal with no model block is never given one.
        var (merged, enriched) = DiscoveryUsageOverlay.Apply(new[] { Signal(signalId: "s1", category: "ai_cli") }, new[] { Signal(signalId: "s1", model: richer) });
        Assert.Equal(0, enriched);
        Assert.Null(Assert.Single(merged).Model);
    }

    [Fact]
    public void The_first_report_signal_with_an_id_is_the_one_used_and_applying_twice_changes_nothing_more()
    {
        var file = Signal(signalId: "s1", model: Model("m"));
        var first = Signal(signalId: "s1", model: Model("m", owner: "First"));
        var second = Signal(signalId: "s1", model: Model("m", owner: "Second"));

        var (once, _) = DiscoveryUsageOverlay.Apply(new[] { file }, new[] { first, second });
        var (twice, enrichedAgain) = DiscoveryUsageOverlay.Apply(once, new[] { first, second });

        Assert.Equal("First", Assert.Single(once).Model!.OwnerApplication);
        Assert.Equal(0, enrichedAgain);
        Assert.Same(once, twice);
    }

    [Fact]
    public void Nothing_in_nothing_is_nothing()
    {
        var some = new[] { Signal(signalId: "s1", model: Model("m")) };

        Assert.Same(some, DiscoveryUsageOverlay.Apply(some, Array.Empty<DiscoverySignalRecord>()).Signals);
        Assert.Empty(DiscoveryUsageOverlay.Apply(Array.Empty<DiscoverySignalRecord>(), some).Signals);
    }
}
