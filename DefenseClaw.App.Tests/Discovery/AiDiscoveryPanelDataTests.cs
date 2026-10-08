using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// What the AI Discovery view-model builds from the files it reads: the product cards, the model rows, the filters the data can answer,
/// the line of counts for the latest scan, the confidence bands, the empty states. Synthetic files in a scratch directory (the state
/// file from <c>Fixtures/CliPayloads</c>; <c>inventory.db</c> and <c>audit.db</c> built from invented rows); only the disk phase of a
/// load runs, so no process starts and no request is made.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryPanelDataTests
{
    private const string State0810 = "ai-discovery-state.0.8.10.json";
    private const string State95159fd = "ai-discovery-state.95159fd.json";
    private const string BandsLabel = "Confidence bands — inventory.db (ai_confidence_snapshots, latest scan)";

    private static string[] Products(AiDiscoveryPanelViewModel vm) =>
        vm.CardsView.Cast<DiscoveryComponentCard>().Select(c => c.Product).ToArray();

    private static string[] Models(AiDiscoveryPanelViewModel vm) =>
        vm.ModelsView.Cast<DiscoveryModelRow>().Select(m => m.ModelId).ToArray();

    private static List<DiscoveryComponentCard> Cards(AiDiscoveryPanelViewModel vm) => vm.CardsView.Cast<DiscoveryComponentCard>().ToList();

    // ------------------------------------------------------------------------------------------ 0.8.10

    [Fact]
    public void A_0_8_10_state_file_gives_products_and_models_and_only_the_filters_the_data_can_answer()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.Equal(4, vm.ProductCount);
            Assert.Equal(3, vm.ModelCount);
            Assert.Equal(new[] { "Example Agent", "Example SDK", "Contoso CLI", "Legacy Model Signal" }, Products(vm));
            Assert.Equal(new[] { "tiny-embed", "example-llm-7b-q4", "voice-small" }, Models(vm));
            Assert.StartsWith("4 products and 3 local models from 11 signals", vm.StatusMessage, StringComparison.Ordinal);

            // 0.8.10 names no modality for these models, so the Modality column and picker are not offered; every model is listed, since
            // there is no model-level confidence or relevance for a "recommended" default to act on.
            Assert.False(vm.HasModalityFilter);
            Assert.Equal("3 of 3", vm.ModelCaption);
            Assert.False(vm.HasActiveModelFilters);
            Assert.Equal("all", vm.ModalityFilter);
            Assert.Equal("any", vm.ConfidenceFilter);
            Assert.True(vm.IsProductsView);

            // No inventory.db in this directory: nothing reads bands, and the page does not claim a source it did not use.
            Assert.DoesNotContain(vm.Sources, s => s.Label == BandsLabel);
            Assert.DoesNotContain(Cards(vm), c => c.HasBands);
        });
    }

    [Fact]
    public void The_line_of_counts_is_taken_from_the_signals_when_no_scan_summary_can_be_read()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            Assert.True(scene.ViewModel.HasHeader);
            Assert.Equal(new[] { "11 active", "2 new", "1 changed" }, scene.ViewModel.HeaderChips.Select(c => c.Text).ToArray());
            Assert.Contains("Counted from the signals", scene.ViewModel.HeaderNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_confidence_filter_cuts_the_models_at_eighty_percent_and_reset_puts_it_back()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            vm.ConfidenceFilter = "high";
            Assert.Equal(new[] { "tiny-embed", "example-llm-7b-q4" }, Models(vm));
            Assert.Equal("2 of 3", vm.ModelCaption);
            Assert.True(vm.HasActiveModelFilters);

            vm.ConfidenceFilter = "low";
            Assert.Equal(new[] { "voice-small" }, Models(vm));

            vm.ResetModelFiltersCommand.Execute(null);
            Assert.Equal("any", vm.ConfidenceFilter);
            Assert.Equal(3, Models(vm).Length);
            Assert.False(vm.HasActiveModelFilters);
        });
    }

    [Fact]
    public void One_search_box_filters_whichever_view_is_on_screen_and_says_why_nothing_is_shown()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            vm.SearchText = "tiny";
            Assert.Empty(Products(vm));
            Assert.True(vm.ShowNoMatch);
            Assert.Equal("No component matches the search", vm.NoMatchTitle);
            Assert.False(vm.ShowModelTable);

            vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
            Assert.Equal(new[] { "tiny-embed" }, Models(vm));
            Assert.False(vm.ShowNoMatch);
            Assert.True(vm.ShowModelTable);
            Assert.Equal("1 of 3", vm.ModelCaption);

            vm.SearchText = "tiny";
            vm.ConfidenceFilter = "low";
            Assert.Empty(Models(vm));
            Assert.True(vm.ShowNoMatch);
            Assert.Equal("No model matches", vm.NoMatchTitle);
            Assert.Contains("clear or change the search text", vm.NoMatchDetail, StringComparison.Ordinal);
            Assert.Contains("reset the filters", vm.NoMatchDetail, StringComparison.Ordinal);

            vm.SearchText = string.Empty;
            vm.ResetModelFiltersCommand.Execute(null);
            Assert.Equal(3, Models(vm).Length);
        });
    }

    [Fact]
    public void A_selected_model_is_kept_across_a_refresh_closed_by_a_view_switch_and_dropped_when_a_filter_hides_it()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn);
        var vm = scene.ViewModel;

        scene.OnUi(() =>
        {
            vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
            vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "example-llm-7b-q4");
            Assert.True(vm.HasModelSelection);
        });

        scene.Reload();

        scene.OnUi(() =>
        {
            Assert.Equal("example-llm-7b-q4", vm.SelectedModel?.ModelId);

            vm.ActiveView = AiDiscoveryPanelViewModel.ProductsViewKey;
            Assert.False(vm.HasModelSelection);

            vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
            vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "example-llm-7b-q4");
            vm.ConfidenceFilter = "low";
            Assert.False(vm.HasModelSelection);

            vm.ConfidenceFilter = "any";
            vm.SelectedModel = vm.ModelsView.Cast<DiscoveryModelRow>().First();
            vm.ClearModelSelectionCommand.Execute(null);
            Assert.Null(vm.SelectedModel);
        });
    }

    // ------------------------------------------------------------------------------------------ an upstream-shaped payload

    [Fact]
    public void A_newer_runtimes_payload_is_read_for_what_0_8_10_carries_and_every_model_is_listed()
    {
        using var scene = DiscoveryScene.Open(State95159fd, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.Equal(new[] { "Example JS SDK", "Example Notes" }, Products(vm));
            Assert.Equal(6, vm.ModelCount);

            // Nothing is hidden by default: the owner, relevance and discovery confidence the Mac's recommended scope would act on are
            // not read here (CUST-310), so the model it would hide - the embedded spell checker, the one rated under 80% by the newer
            // runtime - is listed with the rest. The new news first, then by name.
            Assert.Equal(
                new[] { "example-chat-3b-q4", "browser-spellcheck", "server-listed-model", "speech-tiny", "uncertain-model", "unclassified-artifact" },
                Models(vm));
            Assert.Equal("6 of 6", vm.ModelCaption);
            Assert.False(vm.HasActiveModelFilters);

            // This payload names modalities, so the Modality column and picker exist.
            Assert.True(vm.HasModalityFilter);
            vm.ModalityFilter = "speech";
            Assert.Equal(new[] { "speech-tiny" }, Models(vm));

            vm.ModalityFilter = "generative";
            Assert.Equal(new[] { "example-chat-3b-q4", "browser-spellcheck", "server-listed-model", "uncertain-model" }, Models(vm));

            // The Confidence picker cuts the detection score: the model the newer runtime rates at 79% has a 85% signal and stays high.
            vm.ModalityFilter = "all";
            vm.ConfidenceFilter = "low";
            Assert.Equal(new[] { "server-listed-model" }, Models(vm));
            vm.ConfidenceFilter = "high";
            Assert.Contains("uncertain-model", Models(vm));
        });
    }

    [Fact]
    public void A_newer_runtimes_card_and_model_show_the_process_and_model_lines_and_none_of_the_fields_that_are_not_read()
    {
        using var scene = DiscoveryScene.Open(State95159fd, DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            var sdk = Cards(vm).Single(c => c.Product == "Example JS SDK");
            var app = Cards(vm).Single(c => c.Product == "Example Notes");

            // The state file carries no identity or presence band, whichever runtime wrote it.
            Assert.False(sdk.HasBands);
            Assert.False(app.HasBands);
            Assert.Equal("changed", sdk.State);
            Assert.Contains("component: @example/sdk (npm) version=2.0.1", string.Join('\n', sdk.SignalLines.Select(l => l.DetailText)), StringComparison.Ordinal);
            Assert.Contains("runtime: pid=5150 user=example-user up=1h comm=ExampleNotes.exe", string.Join('\n', app.SignalLines.Select(l => l.DetailText)), StringComparison.Ordinal);

            var chat = vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == "example-chat-3b-q4");
            Assert.Equal("new", chat.State);
            Assert.Equal(new[] { "loaded", "installed" }, chat.Statuses);
            Assert.Equal("Generative", chat.ModalityDisplay);
            Assert.True(chat.Pinned);
        });
    }

    // ------------------------------------------------------------------------------------------ inventory.db

    [Fact]
    public void Without_a_state_file_the_cards_and_models_come_from_inventory_db_with_their_model_and_process_blocks()
    {
        using var scene = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteInventory(
            temp.File("inventory.db"),
            DiscoveryScene.InventoryOf(
                "INSERT INTO ai_signals (scan_id, fingerprint, signal_id, signature_id, name, vendor, product, category, detector, state, confidence, last_seen, last_active_at, evidence_json, runtime_json) " +
                "VALUES ('scan-1', 'fp-p', 'sig-p', 'example-agent', 'agent-process', 'Example Corp', 'Example Agent', 'active_process', 'process', 'new', 0.95, " +
                "'2026-10-08 15:20:00 +0000 UTC', '2026-10-08 15:19:00 +0000 UTC', '[]', '{\"pid\":77,\"user\":\"example-user\",\"comm\":\"agent.exe\",\"uptime_sec\":120}')",
                "INSERT INTO ai_signals (scan_id, fingerprint, signal_id, signature_id, name, vendor, product, category, detector, state, confidence, last_seen, evidence_json, model_json) " +
                "VALUES ('scan-1', 'fp-m', 'sig-m', 'local-model-artifact', 'local-model-artifact', 'Local', 'Local Model Artifact', 'local_model', 'model_file', 'seen', 0.9, " +
                "'2026-10-08 15:20:00 +0000 UTC', '[]', '{\"id\":\"db-model\",\"status\":\"installed\",\"format\":\"gguf\",\"provider\":\"filesystem\",\"size_bytes\":2048}')")));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.Contains("inventory.db", vm.Sources[0].Label, StringComparison.Ordinal);
            Assert.Equal(new[] { "Example Agent" }, Products(vm));
            Assert.Equal(new[] { "db-model" }, Models(vm));
            Assert.Equal("new", Cards(vm).Single().State);
            Assert.Contains("runtime: pid=77 user=example-user up=2m comm=agent.exe", Cards(vm).Single().SignalLines[0].DetailText, StringComparison.Ordinal);

            var model = vm.ModelsView.Cast<DiscoveryModelRow>().Single();
            Assert.Equal(2048, model.SizeBytes);
            Assert.Equal("installed, gguf", model.StatusFormatDisplay);
        });
    }

    [Fact]
    public void A_component_takes_the_identity_and_presence_band_of_its_snapshot_in_inventory_db_and_other_cards_stay_without()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteInventory(
            temp.File("inventory.db"),
            DiscoveryScene.InventoryOf(DiscoveryScene.SdkSignalRow, DiscoveryScene.SdkSnapshotRow)));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            var sdk = Cards(vm).Single(c => c.Product == "Example SDK");
            Assert.True(sdk.HasBands);
            Assert.Equal("high (86%)", sdk.IdentityDisplay);
            Assert.Equal("low (31%)", sdk.PresenceDisplay);
            Assert.DoesNotContain(Cards(vm).Where(c => c.Product != "Example SDK"), c => c.HasBands);

            // The page says where the bands came from, right after the signal cache, and how many signals they reached.
            var source = Assert.Single(vm.Sources, s => s.Label == BandsLabel);
            Assert.True(source.Available);
            Assert.Equal("1 component with an identity and presence band, shown on 1 signal.", source.Detail);
            Assert.Equal(1, vm.Sources.IndexOf(source));
        });
    }

    [Fact]
    public void An_inventory_db_that_cannot_give_the_bands_leaves_the_cards_as_they_were_and_says_why_under_sources()
    {
        // A database from a layout without the snapshot table.
        using var missingTable = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteInventory(
            temp.File("inventory.db"),
            new[] { "DROP TABLE ai_confidence_snapshots" }.Concat(DiscoveryScene.InventoryOf(DiscoveryScene.SdkSignalRow)).ToArray()));

        missingTable.OnUi(() =>
        {
            var vm = missingTable.ViewModel;

            Assert.Equal(new[] { "Example Agent", "Example SDK", "Contoso CLI", "Legacy Model Signal" }, Products(vm));
            Assert.DoesNotContain(Cards(vm), c => c.HasBands);
            var source = Assert.Single(vm.Sources, s => s.Label == BandsLabel);
            Assert.False(source.Available);
            Assert.Contains("does not have the confidence snapshot tables", source.Detail, StringComparison.Ordinal);
        });

        // A file that is not a database at all.
        using var unreadable = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn, temp => temp.WriteFile("inventory.db", "this is not a database"));

        unreadable.OnUi(() =>
        {
            var vm = unreadable.ViewModel;

            Assert.Equal(4, vm.ProductCount);
            var source = Assert.Single(vm.Sources, s => s.Label == BandsLabel);
            Assert.False(source.Available);
            Assert.StartsWith("Could not read inventory.db:", source.Detail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_snapshot_for_a_component_no_signal_names_adds_no_band_and_a_database_with_no_snapshot_row_adds_no_line()
    {
        using var unmatched = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteInventory(
            temp.File("inventory.db"),
            DiscoveryScene.InventoryOf(
                DiscoveryScene.SdkSignalRow,
                "INSERT INTO ai_confidence_snapshots (scan_id, ecosystem, name, identity_score, identity_band, presence_score, presence_band, policy_version) " +
                "VALUES ('scan-1', 'npm', 'some-other-package', 0.5, 'medium', 0.5, 'medium', 1)")));

        unmatched.OnUi(() =>
        {
            // The rollup only lists components some signal of the scan names, so the other package's snapshot never reaches the cards, and
            // the SDK has no snapshot of its own.
            Assert.DoesNotContain(Cards(unmatched.ViewModel), c => c.HasBands);
            Assert.DoesNotContain(unmatched.ViewModel.Sources, s => s.Label == BandsLabel);
        });
    }

    // ------------------------------------------------------------------------------------------ audit.db and the gateway

    private static DiscoveryScene.AuditSpec Scan(int minutesAgo, string source, int active, int added, int changed, int gone, int files) =>
        new(
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            "ai.discovery.completed",
            "{\"defenseclaw.ai.discovery.signals_total\":20,\"defenseclaw.ai.discovery.active_signals\":" + active +
            ",\"defenseclaw.ai.discovery.new_signals\":" + added + ",\"defenseclaw.ai.discovery.changed_signals\":" + changed +
            ",\"defenseclaw.ai.discovery.gone_signals\":" + gone + ",\"defenseclaw.ai.discovery.files_scanned\":" + files +
            ",\"defenseclaw.ai.discovery.duration_ms\":150,\"defenseclaw.ai.discovery.source\":\"" + source + "\",\"defenseclaw.ai.discovery.result\":\"ok\"}");

    private static DiscoveryScene.AuditSpec ComponentEvent(int minutesAgo, string name) =>
        new(
            DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
            name,
            "{\"defenseclaw.ai.component.id\":\"x\",\"defenseclaw.ai.component.product\":\"Example Agent\",\"defenseclaw.ai.discovery.detector\":\"process\"}");

    [Fact]
    public void The_line_of_counts_is_the_newest_scan_event_and_a_component_event_is_not_a_scan()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteAudit(
            temp.File("audit.db"),
            ComponentEvent(0, "ai_component.removed"),
            Scan(1, "process", active: 222, added: 0, changed: 0, gone: 3, files: 0),
            ComponentEvent(2, "ai_component.discovered"),
            Scan(6, "scheduled", active: 220, added: 1, changed: 0, gone: 0, files: 1000)));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            // The history lists the two scans, not the two component events.
            Assert.Equal(2, vm.ScanHistory.Count);
            Assert.All(vm.ScanHistory, scan => Assert.True(scan.IsSummary));

            // The files read are not shown when the newest scan read none; the gone count is, which no signal file can say.
            Assert.Equal(new[] { "222 active", "3 gone" }, vm.HeaderChips.Select(c => c.Text).ToArray());
            Assert.StartsWith("The audit trail's last scan: process scan,", vm.HeaderNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_gateways_own_summary_is_preferred_to_the_audit_trail_and_the_audit_trail_comes_back_when_it_is_gone()
    {
        using var scene = DiscoveryScene.Open(State0810, DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteAudit(
            temp.File("audit.db"),
            Scan(1, "process", active: 222, added: 0, changed: 0, gone: 3, files: 0)));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            var live = new LiveDiscoveryStatus(
                Reachable: true,
                Enabled: true,
                Error: null,
                Drift: false,
                ScannedAt: DateTimeOffset.UtcNow,
                Source: "scheduled",
                Result: "ok",
                DurationMs: 900,
                FilesScanned: 1000,
                TotalSignals: 220,
                ActiveSignals: 220,
                NewSignals: 1,
                ChangedSignals: 0,
                GoneSignals: 0,
                Errors: 0);

            vm.ApplyLiveStatus(live, null);
            Assert.Equal(new[] { "220 active", "1 new", "1,000 files" }, vm.HeaderChips.Select(c => c.Text).ToArray());
            Assert.StartsWith("The gateway's last scan: scheduled scan,", vm.HeaderNote, StringComparison.Ordinal);

            // A gateway that did not answer, or answered "off" with no scan, leaves the audit trail's account in place.
            vm.ApplyLiveStatus(null, "'defenseclaw agent discovery status' did not complete.");
            Assert.Equal(new[] { "222 active", "3 gone" }, vm.HeaderChips.Select(c => c.Text).ToArray());

            vm.ApplyLiveStatus(live with { ScannedAt = null, ActiveSignals = null, NewSignals = null, ChangedSignals = null, GoneSignals = null, FilesScanned = null }, null);
            Assert.Equal(new[] { "222 active", "3 gone" }, vm.HeaderChips.Select(c => c.Text).ToArray());
        });
    }

    // ------------------------------------------------------------------------------------------ nothing to show

    [Fact]
    public void An_empty_list_says_whether_discovery_is_off_has_not_written_a_result_or_found_nothing()
    {
        // Off: nothing is being scanned, and nothing was ever written.
        using (var off = DiscoveryScene.Open(null))
        {
            off.OnUi(() =>
            {
                Assert.True(off.ViewModel.HasNoComponents);
                Assert.False(off.ViewModel.HasHeader);
                Assert.Equal("AI Discovery is turned off", off.ViewModel.EmptyTitle);
                Assert.True(off.ViewModel.ShowEmpty);
                off.ViewModel.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                Assert.True(off.ViewModel.HasNoModels);
                Assert.False(off.ViewModel.HasNoComponents);
                Assert.Equal("AI Discovery is turned off", off.ViewModel.EmptyTitle);
            });
        }

        // On, but no result on disk yet.
        using (var waiting = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn))
        {
            waiting.OnUi(() => Assert.Equal("No scan result on disk yet", waiting.ViewModel.EmptyTitle));
        }

        // On, a result that holds nothing: found none, in each view's own words.
        using (var none = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp => temp.WriteFile("ai_discovery_state.json", "{\"version\":2,\"signals\":{}}")))
        {
            none.OnUi(() =>
            {
                Assert.Equal("Nothing detected in the latest scan", none.ViewModel.EmptyTitle);
                none.ViewModel.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
                Assert.Equal("No local models identified", none.ViewModel.EmptyTitle);
                Assert.False(none.ViewModel.ShowModelTable);
            });
        }
    }

    [Fact]
    public void A_machine_with_only_local_models_says_the_products_are_none_and_points_at_the_models()
    {
        const string onlyModels = """
            { "version": 2, "signals": { "fp-m": {
                "category": "local_model", "confidence": 0.9, "detector": "model_file", "state": "seen",
                "vendor": "Local", "product": "Local Model Artifact",
                "model": { "id": "only-model", "status": "installed", "format": "gguf" } } } }
            """;
        using var scene = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp => temp.WriteFile("ai_discovery_state.json", onlyModels));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.Equal(0, vm.ProductCount);
            Assert.Equal(1, vm.ModelCount);
            Assert.True(vm.HasNoComponents);
            Assert.Equal("No AI products detected", vm.EmptyTitle);
            Assert.Contains("1 local model", vm.EmptyDetail, StringComparison.Ordinal);

            vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
            Assert.False(vm.ShowEmpty);
            Assert.True(vm.ShowModelTable);
        });
    }

    [Fact]
    public void A_signal_that_is_not_an_object_loses_only_itself()
    {
        const string oneBad = """
            { "version": 2, "signals": {
                "fp-bad": "not an object",
                "fp-ok": { "category": "ai_cli", "confidence": 0.7, "detector": "binary", "state": "new", "vendor": "Contoso Tools", "product": "Contoso CLI" } } }
            """;
        using var scene = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp => temp.WriteFile("ai_discovery_state.json", oneBad));

        scene.OnUi(() =>
        {
            Assert.Equal(new[] { "Contoso CLI" }, Products(scene.ViewModel));
            Assert.Equal(0, scene.ViewModel.ModelCount);
        });
    }

    [Fact]
    public void A_state_file_that_is_not_json_falls_back_to_inventory_db_with_its_signals_and_its_bands()
    {
        using var scene = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp =>
        {
            temp.WriteFile("ai_discovery_state.json", "{ this is not json");
            DiscoveryScene.WriteInventory(
                temp.File("inventory.db"),
                DiscoveryScene.InventoryOf(DiscoveryScene.SdkSignalRow, DiscoveryScene.SdkSnapshotRow));
        });

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.Equal(new[] { "Example SDK" }, Products(vm));
            Assert.Contains("falling back to inventory.db", vm.Sources[0].Detail, StringComparison.Ordinal);
            Assert.Equal("high (86%)", Cards(vm).Single().IdentityDisplay);
        });
    }
}
