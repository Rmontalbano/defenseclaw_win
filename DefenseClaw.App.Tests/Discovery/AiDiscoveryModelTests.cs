using System.Globalization;
using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// AI Discovery's data model, with no window: how a signal is read from <c>ai_discovery_state.json</c> and <c>inventory.db</c>, the
/// lifecycle states and their order, the product cards and the model rows made from the signals, and the filters over the models.
/// Synthetic data only. Two payload shapes are fed through it: a DefenseClaw 0.8.10 state file (<c>ai-discovery-state.0.8.10.json</c>:
/// every key the live file has, checked key by key against one on 2026-10-08) and one in the shape of upstream source commit 95159fd
/// (<c>ai-discovery-state.95159fd.json</c>: the model fields <c>owner_application</c>, <c>relevance</c>, <c>discovery_confidence</c> and
/// <c>provenance</c> that 0.8.10 does not send, from <c>internal/inventory/ai_discovery.go</c>). The panel reads what 0.8.10 carries; the
/// newer fields are CUST-310, so the second file proves that a newer runtime's file reads, and shows exactly what 0.8.10 would have shown
/// of it - nothing invented, nothing of the new fields.
/// </summary>
public sealed class AiDiscoveryModelTests
{
    /// <summary>Three minutes after the last_active_at of the first 0.8.10 process signal.</summary>
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T15:30:04.1234567Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static IReadOnlyList<DiscoverySignalRecord> FromFixture(string fixture)
    {
        using var document = JsonDocument.Parse(PayloadFixtures.Read(fixture));
        return document.RootElement.GetProperty("signals").EnumerateObject()
            .Select(property => DiscoverySignalParser.FromState(property.Value))
            .ToList();
    }

    private static DiscoverySignalRecord Signal(
        string product = "Local Model Artifact",
        string vendor = "Local",
        string category = "local_model",
        string detector = "model_file",
        string? state = "seen",
        double confidence = 0.9,
        DiscoveryModelInfo? model = null,
        DiscoveryRuntimeInfo? runtime = null,
        DateTimeOffset? lastActive = null) =>
        new(vendor, product, category, detector, "test", confidence, state, null, null, Array.Empty<DiscoveryEvidenceItem>())
        {
            Model = model,
            Runtime = runtime,
            LastActiveAt = lastActive,
        };

    private static DiscoveryModelInfo Model(string id, string modality = "", string status = "installed", string format = "gguf") =>
        new(id, status, format, "filesystem", string.Empty, modality, string.Empty, 0, false);

    /// <summary>One model row: a single model signal of the given modality (empty: the scanner named none) and detection score.</summary>
    private static DiscoveryModelRow Row(string id, string modality, double signalConfidence) =>
        Assert.Single(DiscoveryModelRow.Build(new[] { Signal(product: "Meetily", confidence: signalConfidence, model: Model(id, modality)) }, Now));

    // ------------------------------------------------------------------------------------------ states

    [Theory]
    [InlineData("new", 0, "Medium")]
    [InlineData("changed", 1, "Warn")]
    [InlineData("active", 2, "Ok")]
    [InlineData("seen", 3, "Neutral")]
    [InlineData("gone", 4, "Low")]
    [InlineData("  NEW ", 0, "Medium")]
    [InlineData("something-new", 9, "Neutral")]
    [InlineData("", 9, "Neutral")]
    [InlineData(null, 9, "Neutral")]
    public void A_state_has_the_TUIs_weight_and_a_tone(string? state, int weight, string tone)
    {
        Assert.Equal(weight, DiscoveryStates.Weight(state));
        Assert.Equal(tone, DiscoveryStates.Tone(state));
    }

    [Fact]
    public void The_strongest_state_is_the_lowest_weight_and_nothing_when_no_signal_says_anything()
    {
        Assert.Equal("new", DiscoveryStates.Strongest(new[] { "seen", "gone", "new", "changed" }));
        Assert.Equal("changed", DiscoveryStates.Strongest(new[] { "seen", " Changed ", "gone" }));
        Assert.Equal("seen", DiscoveryStates.Strongest(new string?[] { null, "", "seen" }));
        Assert.Equal("whatever", DiscoveryStates.Strongest(new string?[] { "whatever" }));
        Assert.Equal(string.Empty, DiscoveryStates.Strongest(new string?[] { null, " " }));
        Assert.Equal(string.Empty, DiscoveryStates.Strongest(Array.Empty<string?>()));
    }

    // ------------------------------------------------------------------------------------------ formatting

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(45, "45s")]
    [InlineData(60, "1m")]
    [InlineData(720, "12m")]
    [InlineData(3600, "1h")]
    [InlineData(11520, "3h12m")]
    [InlineData(86400, "1d")]
    [InlineData(187200, "2d4h")]
    [InlineData(-90, "1m")]
    public void An_age_is_worded_as_the_TUI_words_it(int seconds, string expected) =>
        Assert.Equal(expected, DiscoveryFormat.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Confidence_percent_and_band_text_follow_the_TUI()
    {
        Assert.Equal(86, DiscoveryFormat.Percent(0.855));
        Assert.Equal(0, DiscoveryFormat.Percent(-3));
        Assert.Equal(100, DiscoveryFormat.Percent(7));
        Assert.Equal("very high (97%)", DiscoveryFormat.Confidence(0.97, "very_high"));
        Assert.Equal("low (31%)", DiscoveryFormat.Confidence(0.31, " low "));
        Assert.Equal("85%", DiscoveryFormat.Confidence(0.85, null));
        Assert.Equal("low (0%)", DiscoveryFormat.Confidence(null, "low"));
        Assert.Equal(string.Empty, DiscoveryFormat.Confidence(0, ""));
        Assert.Equal(string.Empty, DiscoveryFormat.Confidence(null, null));
    }

    [Fact]
    public void A_list_is_cut_to_two_with_a_count_of_the_rest_and_a_size_is_in_words()
    {
        Assert.Equal(string.Empty, DiscoveryFormat.CsvTruncated(Array.Empty<string>()));
        Assert.Equal("a, b", DiscoveryFormat.CsvTruncated(new[] { "a", "b" }));
        Assert.Equal("a, b (+2)", DiscoveryFormat.CsvTruncated(new[] { "a", "b", "c", "d" }));
        Assert.Equal(string.Empty, DiscoveryFormat.Bytes(0));
        Assert.Equal("900 B", DiscoveryFormat.Bytes(900));
        Assert.Equal("90 KiB", DiscoveryFormat.Bytes(92_160));
        Assert.Equal("4.1 GiB", DiscoveryFormat.Bytes(4_368_439_296));
    }

    // ------------------------------------------------------------------------------------------ reading signals

    [Fact]
    public void A_0_8_10_state_file_signal_carries_state_model_process_and_component_and_no_bands()
    {
        var signals = FromFixture("ai-discovery-state.0.8.10.json");

        var process = signals.Single(s => s.SignalId == "sig-agent-process-1");
        Assert.Equal("new", process.State);
        Assert.Equal("example-agent", process.SignatureId);
        Assert.Equal("example-agent", process.DisplayId);
        Assert.Equal(new DiscoveryRuntimeInfo(4242, 11520, "example-user", "example-agent.exe"), process.Runtime);
        Assert.Equal("runtime: pid=4242 user=example-user up=3h12m comm=example-agent.exe", process.Runtime!.Detail());
        Assert.NotNull(process.LastActiveAt);
        Assert.Null(process.Model);

        var sdk = signals.Single(s => s.SignalId == "sig-sdk");
        Assert.Equal(new DiscoveryComponentRef("pypi", "example-sdk", "1.4.2", "Example Python SDK"), sdk.Component);
        Assert.Equal("1.4.2", sdk.Version);
        Assert.Equal("example-sdk (pypi)", sdk.Component!.Label);

        var model = signals.Single(s => s.SignalId == "sig-model-llm-a").Model!;
        Assert.Equal("example-llm-7b-q4", model.Id);
        Assert.Equal("installed", model.Status);
        Assert.Equal("gguf", model.Format);
        Assert.Equal("filesystem", model.Provider);
        Assert.Equal(4_368_439_296, model.SizeBytes);

        // What 0.8.10 does not send is not there - not an empty "Unknown" standing in for it.
        Assert.Equal(string.Empty, model.Modality);
        Assert.Equal(string.Empty, model.Recipe);
        Assert.Equal(string.Empty, model.Device);
        Assert.False(model.Pinned);
        Assert.All(signals, s =>
        {
            Assert.Null(s.IdentityBand);
            Assert.Null(s.PresenceBand);
            Assert.Null(s.IdentityScore);
            Assert.Null(s.PresenceScore);
        });
    }

    [Fact]
    public void A_newer_runtimes_state_file_reads_for_what_0_8_10_reads_of_it_and_the_new_fields_are_left_alone()
    {
        var signals = FromFixture("ai-discovery-state.95159fd.json");

        // The model block's 0.8.10 fields are read; owner_application, relevance, discovery_confidence and provenance are CUST-310's.
        var chat = signals.Single(s => s.SignalId == "sig-chat-api").Model!;
        Assert.Equal(new DiscoveryModelInfo("example-chat-3b-q4", "loaded", "gguf", "example-server", "llamacpp", "text", "gpu", 2_000_000_000, true), chat);

        // The process block of the newer build also lists other instances; they are not read, and the rest of it is.
        var app = signals.Single(s => s.SignalId == "sig-desktop-app");
        Assert.Equal(new DiscoveryRuntimeInfo(5150, 3600, "example-user", "ExampleNotes.exe"), app.Runtime);

        // A model block with nothing but the 0.8.10 fields reads the same way.
        var unclassified = signals.Single(s => s.SignalId == "sig-unclassified").Model!;
        Assert.Equal(string.Empty, unclassified.Modality);
        Assert.Equal(12_345, unclassified.SizeBytes);

        var component = signals.Single(s => s.SignalId == "sig-sdk");
        Assert.Equal(new DiscoveryComponentRef("npm", "@example/sdk", "2.0.1", "Example JS SDK"), component.Component);
        Assert.Null(component.IdentityBand);
    }

    [Fact]
    public void A_value_of_the_wrong_type_costs_only_its_own_field()
    {
        const string json = """
            {
              "vendor": "V", "product": "P", "confidence": "high", "state": 7,
              "model": { "id": "m", "size_bytes": -5, "pinned": "maybe", "modality": 3 },
              "runtime": [1, 2],
              "component": 5
            }
            """;
        using var document = JsonDocument.Parse(json);

        var signal = DiscoverySignalParser.FromState(document.RootElement);

        Assert.Equal("P", signal.Product);
        Assert.Null(signal.Confidence);
        Assert.Null(signal.State);
        Assert.Null(signal.Runtime);
        Assert.Null(signal.Component);
        Assert.Equal("m", signal.Model!.Id);
        Assert.Equal(0, signal.Model.SizeBytes);
        Assert.False(signal.Model.Pinned);
        Assert.Equal(string.Empty, signal.Model.Modality);
    }

    [Theory]
    [InlineData("{\"id\":\"m\",\"size_bytes\":1.5}", 0L)]
    [InlineData("{\"id\":\"m\",\"size_bytes\":\"12\"}", 12L)]
    [InlineData("{\"id\":\"m\",\"size_bytes\":true}", 0L)]
    [InlineData("{\"id\":\"m\",\"size_bytes\":2048}", 2048L)]
    public void A_size_is_a_whole_non_negative_number_or_nothing(string json, long expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, DiscoverySignalParser.ParseModel(document.RootElement)!.SizeBytes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"model\"")]
    [InlineData("null")]
    public void An_empty_or_non_object_block_is_no_block(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(DiscoverySignalParser.ParseModel(document.RootElement));
        Assert.Null(DiscoverySignalParser.ParseRuntime(document.RootElement));
        Assert.Null(DiscoverySignalParser.ParseComponent(document.RootElement));
    }

    [Fact]
    public void A_database_row_reads_the_same_blocks_from_its_json_columns_and_a_bad_cell_costs_only_its_block()
    {
        var row = new Dictionary<string, object?>
        {
            ["signal_id"] = "sig-1",
            ["signature_id"] = "",
            ["name"] = "example-model",
            ["vendor"] = "Local",
            ["product"] = "Local Model Artifact",
            ["category"] = "local_model",
            ["detector"] = "model_file",
            ["state"] = "changed",
            ["confidence"] = 0.91,
            ["component_ecosystem"] = null,
            ["component_name"] = null,
            ["last_seen"] = "2026-10-08 15:27:04.7798903 +0000 UTC",
            ["last_active_at"] = "2026-10-08 15:20:04.0000000 +0000 UTC",
            ["evidence_json"] = "[{\"type\":\"model_file\",\"basename\":\"m.gguf\",\"quality\":1,\"match_kind\":\"exact\"}]",
            ["runtime_json"] = "{\"pid\":77,\"user\":\"example-user\",\"comm\":\"srv.exe\",\"uptime_sec\":61}",
            ["model_json"] = "{\"id\":\"example-model\",\"status\":\"loaded\",\"format\":\"gguf\",\"size_bytes\":1024}",
        };

        var signal = DiscoverySignalParser.FromDbRow(row);

        Assert.Equal("example-model", signal.DisplayId);
        Assert.Equal("changed", signal.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 15, 27, 4, TimeSpan.Zero).AddTicks(7798903), signal.LastSeen);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 15, 20, 4, TimeSpan.Zero), signal.LastActiveAt);
        Assert.Equal("m.gguf", Assert.Single(signal.Evidence).Basename);
        Assert.Equal("loaded", signal.Model!.Status);
        Assert.Equal(1024, signal.Model.SizeBytes);
        Assert.Equal(77, signal.Runtime!.Pid);
        Assert.Null(signal.Component);

        row["model_json"] = "{not json";
        row["runtime_json"] = "";
        var damaged = DiscoverySignalParser.FromDbRow(row);
        Assert.Null(damaged.Model);
        Assert.Null(damaged.Runtime);
        Assert.Equal("changed", damaged.State);
    }

    [Fact]
    public void A_database_row_with_a_component_names_it_and_takes_its_version()
    {
        var row = new Dictionary<string, object?>
        {
            ["signal_id"] = "sig-2",
            ["signature_id"] = "example-sdk",
            ["name"] = "example-sdk",
            ["vendor"] = "Example Corp",
            ["product"] = "Example SDK",
            ["category"] = "package_dependency",
            ["detector"] = "package_manifest",
            ["state"] = "seen",
            ["confidence"] = 0.9,
            ["component_ecosystem"] = "pypi",
            ["component_name"] = "example-sdk",
            ["component_framework"] = "Example Python SDK",
            ["component_version"] = "1.4.2",
            ["last_seen"] = "2026-10-08 15:27:04.7798903 +0000 UTC",
        };

        var signal = DiscoverySignalParser.FromDbRow(row);

        Assert.Equal(new DiscoveryComponentRef("pypi", "example-sdk", "1.4.2", "Example Python SDK"), signal.Component);
        Assert.Equal("1.4.2", signal.Version);
    }

    // ------------------------------------------------------------------------------------------ products and models

    [Fact]
    public void The_models_view_lists_one_row_per_model_with_case_variants_folded_and_the_news_first()
    {
        var rows = DiscoveryModelRow.Build(FromFixture("ai-discovery-state.0.8.10.json"), Now);

        Assert.Equal(new[] { "tiny-embed", "example-llm-7b-q4", "voice-small" }, rows.Select(r => r.ModelId).ToArray());
        Assert.Equal(new[] { "new", "seen", "seen" }, rows.Select(r => r.State).ToArray());

        var llm = rows[1];
        Assert.Equal(2, llm.ObservationCount);
        Assert.Equal(new[] { "gguf" }, llm.Formats);
        Assert.Equal(new[] { "installed" }, llm.Statuses);
        Assert.Equal(new[] { "model_file" }, llm.Detectors);
        Assert.Equal(4_368_439_296, llm.SizeBytes);
        Assert.Equal("4.1 GiB", llm.SizeDisplay);
        Assert.Equal("installed, gguf", llm.StatusFormatDisplay);
        Assert.Equal("90% signal", llm.ConfidenceLabel);
        Assert.Equal("2 observations · Signal confidence 90 percent", llm.Summary);
    }

    [Fact]
    public void A_model_row_takes_the_strongest_state_and_every_status_of_the_signals_that_name_it()
    {
        var signals = new[]
        {
            Signal(product: "Server A", detector: "model_api", state: "seen", model: Model("  Qwen3-0.6B-GGUF\n", status: "installed")),
            Signal(product: "Server B", detector: "model_runtime", state: "new", model: Model("qWEN3-0.6b-gguf", status: "loaded")),
        };

        var row = Assert.Single(DiscoveryModelRow.Build(signals, Now));

        Assert.Equal("Qwen3-0.6B-GGUF", row.ModelId);
        Assert.Equal("new", row.State);
        Assert.Equal(new[] { "installed", "loaded" }, row.Statuses);
        Assert.Equal(new[] { "Server A", "Server B" }, row.Products);
        Assert.Equal(new[] { "model_api", "model_runtime" }, row.Detectors);
    }

    [Fact]
    public void Only_an_identified_local_model_leaves_the_product_cards_and_nothing_disappears()
    {
        var signals = new List<DiscoverySignalRecord>(FromFixture("ai-discovery-state.0.8.10.json"))
        {
            // A model block on a signal that is not a local model stays a product signal.
            Signal(product: "Acme Studio", vendor: "Acme", category: "desktop_app", detector: "application", model: Model("acme/embedded-model")),

            // A local_model signal whose model block names nothing stays visible as a product, like one from an older gateway.
            Signal(product: "Malformed Model Signal", model: Model(string.Empty, status: "installed")),
        };

        var cards = AiDiscoveryPanelViewModel.BuildCards(signals, Now).ToList();
        var models = DiscoveryModelRow.Build(signals, Now);

        Assert.DoesNotContain(cards, c => c.Product == "Local Model Artifact");
        Assert.Contains(cards, c => c.Product == "Legacy Model Signal");
        Assert.Contains(cards, c => c.Product == "Acme Studio");
        Assert.Contains(cards, c => c.Product == "Malformed Model Signal");
        Assert.Equal(3, models.Count);
        Assert.DoesNotContain(models, m => m.ModelId == "acme/embedded-model");

        // Every signal is in exactly one of the two views: the cards hold the ones that are not model signals, the rows fold the ones that are.
        Assert.Equal(signals.Count, cards.Sum(c => c.Signals.Count) + models.Sum(m => m.ObservationCount));
    }

    [Fact]
    public void Cards_sort_the_news_first_then_the_strongest_detection_and_take_the_strongest_state()
    {
        var cards = AiDiscoveryPanelViewModel.BuildCards(FromFixture("ai-discovery-state.0.8.10.json"), Now).ToList();

        Assert.Equal(new[] { "Example Agent", "Example SDK", "Contoso CLI", "Legacy Model Signal" }, cards.Select(c => c.Product).ToArray());

        var agent = cards[0];
        Assert.Equal("new", agent.State);
        Assert.Equal("Medium", agent.StateKey);
        Assert.Equal("1 new, 1 changed, 2 seen", agent.StateTally);
        Assert.Contains("new,", agent.HeaderDisplay, StringComparison.Ordinal);
        Assert.False(agent.HasBands);
    }

    [Fact]
    public void A_gone_card_sorts_after_the_ones_that_are_still_there_whatever_its_confidence()
    {
        var signals = new[]
        {
            Signal(product: "Old Tool", vendor: "V", category: "ai_cli", detector: "binary", state: "gone", confidence: 0.99),
            Signal(product: "Quiet Tool", vendor: "V", category: "ai_cli", detector: "binary", state: "seen", confidence: 0.2),
            Signal(product: "Changed Tool", vendor: "V", category: "ai_cli", detector: "binary", state: "changed", confidence: 0.1),
            Signal(product: "Fresh Tool", vendor: "V", category: "ai_cli", detector: "binary", state: "new", confidence: 0.1),
            Signal(product: "Odd Tool", vendor: "V", category: "ai_cli", detector: "binary", state: "weird", confidence: 0.99),
            Signal(product: "Stateless Tool", vendor: "V", category: "ai_cli", detector: "binary", state: null, confidence: 0.99),
        };

        var order = AiDiscoveryPanelViewModel.BuildCards(signals, Now).Select(c => c.Product).ToArray();

        Assert.Equal(new[] { "Fresh Tool", "Changed Tool", "Quiet Tool", "Old Tool", "Odd Tool", "Stateless Tool" }, order);
        Assert.False(AiDiscoveryPanelViewModel.BuildCards(signals, Now).Single(c => c.Product == "Stateless Tool").HasState);
    }

    [Fact]
    public void A_card_lists_its_signals_with_their_process_and_activity_lines_new_ones_first()
    {
        var card = AiDiscoveryPanelViewModel.BuildCards(FromFixture("ai-discovery-state.0.8.10.json"), Now).First(c => c.Product == "Example Agent");

        var lines = card.SignalLines;

        Assert.Equal(4, lines.Count);
        Assert.Equal(new[] { "new", "changed", "seen", "seen" }, lines.Select(l => l.StateText).ToArray());

        var first = lines[0];
        Assert.Equal("example-agent", first.Title);
        Assert.Equal("95%", first.ConfidenceText);
        Assert.Equal(
            "detector=process source=sidecar\nruntime: pid=4242 user=example-user up=3h12m comm=example-agent.exe\nlast active: 3m ago",
            first.DetailText);

        // A signal with no process block and no last_active_at says when it was last seen, and nothing about a process.
        var config = lines.Single(l => l.DetailText.Contains("detector=config", StringComparison.Ordinal));
        Assert.DoesNotContain("runtime:", config.DetailText, StringComparison.Ordinal);
        Assert.Contains("last seen: 3m ago", config.DetailText, StringComparison.Ordinal);
        Assert.Contains("runtime: pid=4300 user=example-user up=1m comm=example-agent.exe", string.Join('\n', lines.Select(l => l.DetailText)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_runtime_line_leaves_out_the_parts_the_process_block_lacks_and_is_not_there_without_a_pid()
    {
        Assert.Equal("runtime: pid=9", new DiscoveryRuntimeInfo(9, 0, string.Empty, string.Empty).Detail());
        Assert.Equal("runtime: pid=9 user=svc", new DiscoveryRuntimeInfo(9, 0, "svc", string.Empty).Detail());
        Assert.Equal("runtime: pid=9 up=45s comm=srv.exe", new DiscoveryRuntimeInfo(9, 45, string.Empty, "srv.exe").Detail());
        Assert.Equal(string.Empty, new DiscoveryRuntimeInfo(0, 120, "svc", "srv.exe").Detail());
    }

    [Fact]
    public void A_package_signal_names_its_component_and_version_and_a_signal_without_one_does_not()
    {
        var sdk = AiDiscoveryPanelViewModel.BuildCards(FromFixture("ai-discovery-state.0.8.10.json"), Now).First(c => c.Product == "Example SDK");
        var agent = AiDiscoveryPanelViewModel.BuildCards(FromFixture("ai-discovery-state.0.8.10.json"), Now).First(c => c.Product == "Example Agent");

        Assert.Equal(
            "detector=package_manifest source=sidecar\ncomponent: example-sdk (pypi) version=1.4.2\nlast seen: 3m ago",
            Assert.Single(sdk.SignalLines).DetailText);
        Assert.DoesNotContain(agent.SignalLines, line => line.DetailText.Contains("component:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_card_with_more_than_fifty_signals_lists_fifty_and_says_how_many_are_left()
    {
        var signals = Enumerable.Range(0, 53)
            .Select(i => Signal(product: "Busy", vendor: "V", category: "active_process", detector: "process", state: i == 52 ? "new" : "seen"))
            .ToList();

        var card = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(signals, Now));

        Assert.Equal(50, card.SignalLines.Count);
        Assert.Equal("new", card.SignalLines[0].StateText);
        Assert.True(card.HasSignalOverflow);
        Assert.Equal("...and 3 more (use `defenseclaw agent usage --detail --json` for the full list)", card.SignalOverflow);
    }

    [Theory]
    [InlineData("seen", true)]
    [InlineData("new", false)]
    [InlineData("Example", true)]
    [InlineData("contoso", false)]
    [InlineData("package_manifest", true)]
    [InlineData("pypi", true)]
    [InlineData("1.4.2", true)]
    [InlineData("nothing-like-this", false)]
    public void A_card_is_found_by_its_state_vendor_product_detector_component_or_version(string query, bool matches)
    {
        var card = AiDiscoveryPanelViewModel.BuildCards(FromFixture("ai-discovery-state.0.8.10.json"), Now).First(c => c.Product == "Example SDK");

        Assert.Equal(matches, card.Matches(query));
    }

    // ------------------------------------------------------------------------------------------ identity and presence

    [Fact]
    public void A_card_shows_the_bands_of_its_component_and_a_band_it_lacks_as_a_dash()
    {
        var plain = Signal(product: "Tool", vendor: "V", category: "package_dependency", detector: "package_manifest");
        var bandedBoth = plain with { IdentityScore = 0.97, IdentityBand = "very_high", PresenceScore = 0.31, PresenceBand = "low" };
        var bandedOne = plain with { IdentityScore = 0.55, IdentityBand = "medium" };

        var none = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(new[] { plain }, Now));
        Assert.False(none.HasBands);
        Assert.Equal(string.Empty, none.IdentityDisplay);

        var both = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(new[] { bandedBoth }, Now));
        Assert.True(both.HasBands);
        Assert.Equal("very high (97%)", both.IdentityText);
        Assert.Equal("low (31%)", both.PresenceText);

        var one = Assert.Single(AiDiscoveryPanelViewModel.BuildCards(new[] { bandedOne }, Now));
        Assert.True(one.HasBands);
        Assert.Equal("medium (55%)", one.IdentityText);
        Assert.Equal("—", one.PresenceText);
    }

    // ------------------------------------------------------------------------------------------ the line of counts

    [Fact]
    public void The_counts_show_active_always_the_churn_only_when_it_is_not_zero_and_the_files_only_when_a_scan_read_some()
    {
        var quiet = new DiscoveryHeaderCounts(222, 0, 0, 0, 0, null, null, DiscoveryCountsOrigin.AuditTrail);
        Assert.Equal(new[] { "222 active" }, quiet.Chips.Select(c => c.Text).ToArray());

        var busy = new DiscoveryHeaderCounts(1220, 2, 1, 3, 1000, null, null, DiscoveryCountsOrigin.Gateway);
        Assert.Equal(new[] { "1,220 active", "2 new", "1 changed", "3 gone", "1,000 files" }, busy.Chips.Select(c => c.Text).ToArray());
        Assert.Equal(new[] { "Neutral", "Medium", "Warn", "Low", "Neutral" }, busy.Chips.Select(c => c.ToneKey).ToArray());

        // A count the source does not have is not shown as zero.
        var unknown = new DiscoveryHeaderCounts(null, null, null, null, null, null, null, DiscoveryCountsOrigin.SignalList);
        Assert.Empty(unknown.Chips);
    }

    [Fact]
    public void Counts_taken_from_the_signals_themselves_have_no_file_count_and_say_so()
    {
        var counts = DiscoveryHeaderCounts.FromSignals(FromFixture("ai-discovery-state.0.8.10.json"));

        Assert.Equal(new[] { "11 active", "2 new", "1 changed" }, counts.Chips.Select(c => c.Text).ToArray());
        Assert.Contains("Counted from the signals", counts.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void No_signals_means_no_line_of_counts_at_all()
    {
        var counts = DiscoveryHeaderCounts.FromSignals(Array.Empty<DiscoverySignalRecord>());

        Assert.Empty(counts.Chips);
    }

    [Fact]
    public void A_gone_signal_is_counted_as_gone_and_not_as_active_when_the_counts_come_from_the_signals()
    {
        var signals = new[]
        {
            Signal(product: "A", state: "seen"),
            Signal(product: "B", state: "gone"),
            Signal(product: "C", state: "changed"),
            Signal(product: "D", state: null),
        };

        var counts = DiscoveryHeaderCounts.FromSignals(signals);

        Assert.Equal(new[] { "3 active", "1 changed", "1 gone" }, counts.Chips.Select(c => c.Text).ToArray());
    }

    [Fact]
    public void The_notes_say_whose_counts_they_are_and_when_the_scan_was()
    {
        var at = new DateTimeOffset(2026, 10, 8, 15, 27, 4, TimeSpan.Zero);

        var gateway = new DiscoveryHeaderCounts(5, 0, 0, 0, 0, at, "process", DiscoveryCountsOrigin.Gateway);
        var audit = new DiscoveryHeaderCounts(5, 0, 0, 0, 0, at, null, DiscoveryCountsOrigin.AuditTrail);

        Assert.StartsWith("The gateway's last scan: process scan, ", gateway.Note, StringComparison.Ordinal);
        Assert.StartsWith("The audit trail's last scan: scan, ", audit.Note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------ labels and search

    [Fact]
    public void A_models_confidence_is_worded_as_what_it_is_a_detection_score()
    {
        var row = Row("a", "text", 0.9);

        Assert.Equal(0.9, row.Confidence);
        Assert.Equal("90% signal", row.ConfidenceLabel);
        Assert.Equal("Signal confidence 90 percent", row.ConfidenceAutomationLabel);
    }

    [Fact]
    public void A_model_is_found_by_its_id_status_format_provider_product_detector_or_modality_and_the_search_ignores_case()
    {
        var row = Assert.Single(DiscoveryModelRow.Build(new[] { Signal(model: Model("derived-model-q4", modality: "speech")) }, Now));

        foreach (var query in new[] { "derived-model", "DERIVED-MODEL-Q4", "installed", "gguf", "filesystem", "FILESYSTEM", "model_file", "Local Model", "speech", "Speech", "seen" })
        {
            Assert.True(row.Matches(query), query);
        }

        Assert.False(row.Matches("Anthropic"));
        Assert.True(row.Matches(string.Empty));
        Assert.True(row.Matches(null));
    }

    [Fact]
    public void The_inspectors_facts_leave_out_whatever_the_data_does_not_have()
    {
        var plain = DiscoveryModelRow.Build(FromFixture("ai-discovery-state.0.8.10.json"), Now).Single(r => r.ModelId == "example-llm-7b-q4");
        var plainLabels = plain.Facts.Select(f => f.Label).ToArray();

        Assert.Equal(new[] { "State", "Status", "Format", "Provider", "Size", "Products", "Vendors", "Sources" }, plainLabels);
        Assert.False(plain.HasModalityData);

        var rich = DiscoveryModelRow.Build(FromFixture("ai-discovery-state.95159fd.json"), Now).Single(r => r.ModelId == "example-chat-3b-q4");
        var richLabels = rich.Facts.Select(f => f.Label).ToArray();

        // Everything 0.8.10 reports about a model is listed when the model has it...
        Assert.Contains("Modality", richLabels);
        Assert.Contains("Recipe", richLabels);
        Assert.Contains("Device", richLabels);
        Assert.Contains("Pinned", richLabels);
        Assert.Equal("Generative", rich.Facts.Single(f => f.Label == "Modality").Value);

        // ...and what a newer runtime adds is not (CUST-310): no owner, relevance, discovery confidence or lineage anywhere.
        Assert.DoesNotContain(richLabels, label => label is "Owner" or "Relevance" or "Publisher" or "Country" or "Root model" or "Derivation" or "Confidence");
        var model = rich.Observations.Select(o => o.DetailText).First(detail => detail.Contains("model:", StringComparison.Ordinal));
        Assert.Contains(
            "model: id=example-chat-3b-q4 status=loaded format=gguf provider=example-server recipe=llamacpp modality=text device=gpu size=1.9 GiB pinned=true",
            model,
            StringComparison.Ordinal);
        Assert.DoesNotContain("owner", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("relevance", model, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("discovery_confidence", model, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_model_with_more_than_fifty_observations_lists_fifty_and_says_how_many_are_left()
    {
        var signals = Enumerable.Range(0, 52)
            .Select(i => Signal(product: "Server " + i.ToString(CultureInfo.InvariantCulture), state: i == 51 ? "new" : "seen", model: Model("busy-model")))
            .ToList();

        var row = Assert.Single(DiscoveryModelRow.Build(signals, Now));

        Assert.Equal(52, row.ObservationCount);
        Assert.Equal(50, row.Observations.Count);
        Assert.Equal("new", row.Observations[0].StateText);
        Assert.True(row.HasObservationOverflow);
        Assert.Equal("...and 2 more (use `defenseclaw agent usage --detail --json` for the full list)", row.ObservationOverflow);
    }

    // ------------------------------------------------------------------------------------------ the filter

    private static bool Shown(DiscoveryModelFilter filter, DiscoveryModelRow row) => filter.Includes(row);

    [Fact]
    public void Nothing_is_hidden_until_the_operator_chooses_a_filter()
    {
        // 0.8.10's data has no model-level confidence or relevance for the Mac's "recommended" default to act on, so every model is listed -
        // a very low detection score, a model of no known modality, one nobody named a format for.
        var everything = new DiscoveryModelFilter();
        var rows = new[]
        {
            Row("strong", "text", 0.95),
            Row("weak", "text", 0.05),
            Row("unclassified", string.Empty, 0.5),
            Row("zero", "speech", 0),
        };

        Assert.All(rows, row => Assert.True(Shown(everything, row), row.ModelId));
    }

    [Fact]
    public void The_confidence_cut_is_eighty_percent_and_exactly_eighty_counts_as_high()
    {
        var high = Row("high", "text", 0.93);
        var boundary = Row("boundary", "text", 0.8);
        var low = Row("low", "text", 0.79);
        var zero = Row("zero", "text", 0);

        var atLeast = new DiscoveryModelFilter { Confidence = DiscoveryConfidenceBand.High };
        Assert.True(Shown(atLeast, high));
        Assert.True(Shown(atLeast, boundary));
        Assert.False(Shown(atLeast, low));
        Assert.False(Shown(atLeast, zero));

        var under = new DiscoveryModelFilter { Confidence = DiscoveryConfidenceBand.Low };
        Assert.False(Shown(under, high));
        Assert.False(Shown(under, boundary));
        Assert.True(Shown(under, low));
        Assert.True(Shown(under, zero));
    }

    [Fact]
    public void A_modality_choice_keeps_only_models_of_that_class_and_combines_with_the_confidence_cut()
    {
        var chat = Row("chat", "text", 0.93);
        var lowChat = Row("low-chat", "chat", 0.5);
        var speech = Row("speech", "stt", 0.92);
        var unclassified = Row("unclassified", string.Empty, 0.9);

        var generative = new DiscoveryModelFilter { Modality = DiscoveryModality.Generative };
        Assert.True(Shown(generative, chat));
        Assert.True(Shown(generative, lowChat));
        Assert.False(Shown(generative, speech));
        Assert.False(Shown(generative, unclassified));

        var unknown = new DiscoveryModelFilter { Modality = DiscoveryModality.Unknown };
        Assert.True(Shown(unknown, unclassified));
        Assert.False(Shown(unknown, chat));

        var strongGenerative = new DiscoveryModelFilter { Modality = DiscoveryModality.Generative, Confidence = DiscoveryConfidenceBand.High };
        Assert.True(Shown(strongGenerative, chat));
        Assert.False(Shown(strongGenerative, lowChat));
        Assert.False(Shown(strongGenerative, speech));
    }

    [Fact]
    public void The_strongest_signal_confidence_of_the_models_signals_is_the_models_confidence()
    {
        var weak = Signal(product: "Server A", detector: "model_api", confidence: 0.2, model: Model("two-signals"));
        var strong = Signal(product: "Server B", detector: "model_file", confidence: 0.85, model: Model("TWO-signals"));

        var row = Assert.Single(DiscoveryModelRow.Build(new[] { weak, strong }, Now));

        Assert.Equal(0.85, row.Confidence);
        Assert.True(Shown(new DiscoveryModelFilter { Confidence = DiscoveryConfidenceBand.High }, row));
    }

    // ------------------------------------------------------------------------------------------ classes

    [Fact]
    public void Classes_of_modality_fold_the_scanners_synonyms()
    {
        foreach (var (raw, expected) in new[]
        {
            ("text", DiscoveryModality.Generative), ("chat", DiscoveryModality.Generative), ("LLM", DiscoveryModality.Generative),
            ("stt", DiscoveryModality.Speech), ("speech-to-text", DiscoveryModality.Speech), ("transcription", DiscoveryModality.Speech),
            ("image", DiscoveryModality.Vision), ("computer-vision", DiscoveryModality.Vision),
            ("embeddings", DiscoveryModality.Embedding), ("audio", DiscoveryModality.Audio),
            ("multimodal", DiscoveryModality.Unknown), (string.Empty, DiscoveryModality.Unknown),
        })
        {
            Assert.Equal(expected, DiscoveryClasses.ClassifyModality(raw));
        }
    }

    [Fact]
    public void A_model_that_names_two_modalities_is_in_both_classes_and_a_model_naming_none_is_unknown()
    {
        var both = Assert.Single(DiscoveryModelRow.Build(new[]
        {
            Signal(detector: "model_file", model: Model("multi", modality: "text")),
            Signal(detector: "model_api", model: Model("multi", modality: "speech")),
        }, Now));

        Assert.Equal(new[] { DiscoveryModality.Generative, DiscoveryModality.Speech }, both.Modalities);
        Assert.Equal(DiscoveryModality.Generative, both.EffectiveModality);
        Assert.Equal("Generative, Speech", both.ModalityDisplay);
        Assert.True(both.HasModalityData);

        var none = Row("none", string.Empty, 0.9);
        Assert.Equal(new[] { DiscoveryModality.Unknown }, none.Modalities);
        Assert.False(none.HasModalityData);
    }
}
