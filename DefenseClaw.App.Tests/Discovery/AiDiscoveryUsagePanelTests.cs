using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;
using static DefenseClaw.App.Tests.Discovery.DiscoveryData;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// What the AI Discovery view-model builds when a runtime sends the model members of source commit 95159fd (CUST-310): the recommended
/// view and its switch, the pickers, the columns, the gateway's report adding to what the files list, and - the other half of the
/// acceptance - that a DefenseClaw 0.8.10 install, whose files and whose report carry none of it, gets the panel it always had. The files
/// are the 0.8.10 state fixture and the state file the populated synthetic report implies; the report is a fixture or a script handed to the
/// view-model, so no process starts and no request is made.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryUsagePanelTests
{
    private const string StorePaths = "ai_discovery:\n  enabled: true\n  mode: enhanced\n  store_raw_local_paths: true\n";

    /// <summary>
    /// The model both a file scan and a runtime that has it loaded report, once as <c>Example-Chat-3B-Q4</c> and once in lower case: one row,
    /// named as the first signal named it (the file scan's is the first the fixture lists).
    /// </summary>
    private const string Chat = "Example-Chat-3B-Q4";

    private static string[] Models(AiDiscoveryPanelViewModel vm) => vm.ModelsView.Cast<DiscoveryModelRow>().Select(m => m.ModelId).ToArray();

    private static string[] Products(AiDiscoveryPanelViewModel vm) => vm.CardsView.Cast<DiscoveryComponentCard>().Select(c => c.Product).ToArray();

    private static DiscoveryModelRow Row(AiDiscoveryPanelViewModel vm, string id) =>
        vm.ModelsView.Cast<DiscoveryModelRow>().Single(m => m.ModelId == id);

    /// <summary>A scene over the files of a gateway that sends the newer members (the state file the populated report implies).</summary>
    private static DiscoveryScene OpenNewer(string? config = DiscoveryScene.DiscoveryOn) =>
        DiscoveryScene.Open(null, config, temp => temp.WriteFile("ai_discovery_state.json", StateFileOf(Rest(ReportPopulated), withNewerModelFields: true)));

    /// <summary>A scene over the files of a runtime that does not send them, with the same signals: what a gateway's report can add to.</summary>
    private static DiscoveryScene OpenFilesWithoutThem(string? config = DiscoveryScene.DiscoveryOn) =>
        DiscoveryScene.Open(null, config, temp => temp.WriteFile("ai_discovery_state.json", StateFileOf(Rest(ReportPopulated), withNewerModelFields: false)));

    private static void Apply(DiscoveryScene scene, string reportJson) =>
        scene.OnUi(() => scene.ViewModel.ApplyUsage(AiUsageReader.ParseText(reportJson, new DiscoveryReadOptions(scene.Services.Config.Config.AiDiscovery.StoreRawLocalPaths))));

    private static GatewayResult<JsonDocument> Doc(string json) => GatewayResult<JsonDocument>.Ok(JsonDocument.Parse(json));

    /// <summary>The recommended models of the populated scan, in the order the table lists them (the news first, then by name).</summary>
    private static readonly string[] Recommended = { Chat, "merged-model-q8", "llama-lite-8b", "speech-tiny" };

    private static readonly string[] EveryModel =
    {
        Chat, "merged-model-q8", "browser-spellcheck", "llama-lite-8b", "ownerless-speech", "speech-tiny", "supporting-chat-1b",
        "uncertain-model", "unclassified-artifact",
    };

    // ---- a runtime that sends the members ----

    [Fact]
    public void A_state_file_that_carries_the_members_lists_the_recommended_models_and_says_how_many_it_kept_off()
    {
        using var scene = OpenNewer();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.Equal(9, vm.ModelCount);
            Assert.Equal(Recommended, Models(vm));
            Assert.Equal("4 of 9", vm.ModelCaption);
            Assert.True(vm.HasRecommendedScope);
            Assert.False(vm.ShowAllModels);
            Assert.False(vm.HasActiveModelFilters);

            // Columns and pickers the data can fill.
            Assert.True(vm.HasOwnerData);
            Assert.True(vm.HasModalityFilter);
            Assert.True(vm.HasRelevanceFilter);

            Assert.True(vm.HasModelScopeNote);
            Assert.Equal("5 models hidden by the recommended view. Turn on Show all models to list them.", vm.ModelScopeNote);

            // The products are the products: models never become cards.
            Assert.Equal(new[] { "Example Agent", "Example Python SDK" }, Products(vm));
        });
    }

    [Fact]
    public void Show_all_models_lists_every_model_and_Reset_filters_puts_the_recommended_view_back()
    {
        using var scene = OpenNewer();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            vm.ShowAllModels = true;
            Assert.Equal(EveryModel, Models(vm));
            Assert.Equal("9 of 9", vm.ModelCaption);
            Assert.False(vm.HasModelScopeNote);
            Assert.True(vm.HasActiveModelFilters);

            vm.ResetModelFiltersCommand.Execute(null);
            Assert.False(vm.ShowAllModels);
            Assert.Equal(Recommended, Models(vm));
            Assert.False(vm.HasActiveModelFilters);
            Assert.True(vm.HasModelScopeNote);
        });
    }

    [Fact]
    public void A_modality_or_relevance_chosen_decides_what_is_of_that_class_and_the_floor_stays()
    {
        using var scene = OpenNewer();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            // Speech: the unowned supporting speech model the scope hid is of the class asked for.
            vm.ModalityFilter = "speech";
            Assert.Equal(new[] { "ownerless-speech", "speech-tiny" }, Models(vm));
            Assert.True(vm.HasActiveModelFilters);

            vm.ModalityFilter = "all";
            vm.RelevanceFilter = "embedded";
            Assert.Equal(new[] { "browser-spellcheck" }, Models(vm));

            vm.RelevanceFilter = "primary";
            Assert.Equal(new[] { Chat, "merged-model-q8" }, Models(vm));
            Assert.DoesNotContain("uncertain-model", Models(vm));

            // A model nobody gave a relevance, and one a server listed with no confidence, are both of the class "unknown".
            vm.RelevanceFilter = "unknown";
            Assert.Equal(new[] { "llama-lite-8b", "unclassified-artifact" }, Models(vm));

            // Show all models lifts the floor as well.
            vm.RelevanceFilter = "primary";
            vm.ShowAllModels = true;
            Assert.Equal(new[] { Chat, "merged-model-q8", "uncertain-model" }, Models(vm));
        });
    }

    [Fact]
    public void The_confidence_picker_cuts_the_number_the_column_shows_and_adds_to_the_scope()
    {
        using var scene = OpenNewer();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            vm.ShowAllModels = true;
            vm.ConfidenceFilter = "low";
            Assert.Equal(new[] { "uncertain-model" }, Models(vm));

            vm.ConfidenceFilter = "high";
            Assert.DoesNotContain("uncertain-model", Models(vm));
            Assert.Contains("llama-lite-8b", Models(vm));
            Assert.Equal(8, Models(vm).Length);

            vm.ShowAllModels = false;
            Assert.Equal(Recommended, Models(vm));
        });
    }

    [Fact]
    public void A_search_composes_with_the_scope_and_the_note_counts_only_what_the_search_leaves()
    {
        using var scene = OpenNewer();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            // By owner and by lineage.
            vm.SearchText = "Example Studio";
            Assert.Equal(new[] { "merged-model-q8" }, Models(vm));
            vm.SearchText = "Example Speech Co";
            Assert.Equal(new[] { "speech-tiny" }, Models(vm));

            // A model the recommended view keeps off: the search finds nothing, and says the scope is why.
            vm.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
            vm.SearchText = "spellcheck";
            Assert.Empty(Models(vm));
            Assert.True(vm.ShowNoMatch);
            Assert.Equal("1 model hidden by the recommended view. Turn on Show all models to list it.", vm.ModelScopeNote);
            Assert.Contains("turn on Show all models", vm.NoMatchDetail, StringComparison.Ordinal);

            vm.ShowAllModels = true;
            Assert.Equal(new[] { "browser-spellcheck" }, Models(vm));
            Assert.False(vm.ShowNoMatch);
            Assert.False(vm.HasModelScopeNote);
        });
    }

    [Fact]
    public void A_row_shows_the_models_own_confidence_API_for_a_listing_and_the_owners_and_relevance_of_all_its_detectors()
    {
        using var scene = OpenNewer();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            vm.ShowAllModels = true;

            // Seen by a file scan and by a runtime that has it loaded: one row, the file scan's confidence, owner and lineage.
            var chat = Row(vm, Chat);
            Assert.Equal(2, chat.ObservationCount);
            Assert.Equal("95%", chat.ConfidenceLabel);
            Assert.Equal("Example Notes", chat.OwnersDisplay);
            Assert.Equal("Primary", chat.RelevanceDisplay);
            Assert.Equal("Generative", chat.ModalityDisplay);
            Assert.Equal("model_file, model_runtime", chat.SourcesDisplay);
            Assert.Equal("new", chat.State);
            Assert.Equal("high", chat.Provenance!.Confidence);

            // Listed by a model server with no confidence: "API", and no owner.
            var listed = Row(vm, "llama-lite-8b");
            Assert.Equal("API", listed.ConfidenceLabel);
            Assert.Equal("—", listed.OwnersDisplay);
            Assert.Equal("Unknown", listed.RelevanceDisplay);

            var embedded = Row(vm, "browser-spellcheck");
            Assert.Equal("Embedded", embedded.RelevanceDisplay);
            Assert.Equal("Example Browser", embedded.OwnersDisplay);
        });
    }

    // ---- the gateway's report adds to files that lack the members ----

    [Fact]
    public void Files_without_the_members_are_a_legacy_snapshot_and_the_report_that_has_them_makes_it_a_classified_one()
    {
        using var scene = OpenFilesWithoutThem();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            // As 0.8.10 would show it: every model, no owner, relevance or switch.
            Assert.Equal(EveryModel, Models(vm));
            Assert.False(vm.HasRecommendedScope);
            Assert.False(vm.HasOwnerData);
            Assert.False(vm.HasRelevanceFilter);
            Assert.True(vm.HasModalityFilter);
            Assert.Equal("9 of 9", vm.ModelCaption);
            Assert.DoesNotContain(vm.HeaderChips, c => c.IsDiagnostic);
            Assert.DoesNotContain(vm.Sources, s => s.Label.StartsWith("Gateway", StringComparison.Ordinal));
        });

        Apply(scene, Rest(ReportPopulated));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.True(vm.HasRecommendedScope);
            Assert.True(vm.HasOwnerData);
            Assert.True(vm.HasRelevanceFilter);
            Assert.Equal(Recommended, Models(vm));
            Assert.Equal("4 of 9", vm.ModelCaption);
            Assert.Equal("5 models hidden by the recommended view. Turn on Show all models to list them.", vm.ModelScopeNote);

            // The report added no row: the gone model the gateway reported stays in the report.
            Assert.Equal(9, vm.ModelCount);
            vm.ShowAllModels = true;
            Assert.DoesNotContain("retired-model", Models(vm));
            Assert.Equal("Example Notes", Row(vm, "speech-tiny").OwnersDisplay);
            Assert.Equal("France (FR)", Row(vm, "speech-tiny").Provenance!.CountryDisplay);

            // The line of counts gains the lookup setting the gateway reported, as the TUI's last header part, after the counts.
            var chip = Assert.Single(vm.HeaderChips, c => c.IsDiagnostic);
            Assert.Equal("model-lookup=offline", chip.Text);
            Assert.True(chip.HasDescription);
            Assert.Same(chip, vm.HeaderChips[^1]);
            Assert.True(vm.HasHeader);
            Assert.True(vm.HasHeaderNote);
            Assert.Contains("Counted from the signals", vm.HeaderNote, StringComparison.Ordinal);

            // And Sources says what was read from where.
            var source = Assert.Single(vm.Sources, s => s.Label == "Gateway — GET /api/v1/ai-usage");
            Assert.True(source.Available);
            Assert.Equal(
                "13 signals read from the gateway. 10 model signals gained an owner, relevance, confidence or lineage from it. Online model lookup is off.",
                source.Detail);
            Assert.Equal(new DateTimeOffset(2030, 1, 15, 10, 4, 30, TimeSpan.Zero).AddTicks(1234568), source.LastUpdated);
        });
    }

    [Fact]
    public void An_online_lookup_is_said_so_and_a_report_that_does_not_say_has_no_chip()
    {
        using var scene = OpenFilesWithoutThem();

        Apply(scene, WithLookup(Rest(ReportPopulated), true));
        scene.OnUi(() =>
        {
            var chip = Assert.Single(scene.ViewModel.HeaderChips, c => c.IsDiagnostic);
            Assert.Equal("model-lookup=online", chip.Text);
            Assert.Contains("Hugging Face", chip.Description, StringComparison.Ordinal);
            Assert.Contains("Online model lookup is on.", scene.ViewModel.Sources.Single(s => s.Label.StartsWith("Gateway", StringComparison.Ordinal)).Detail, StringComparison.Ordinal);
        });

        Apply(scene, WithLookup(Rest(ReportPopulated), null));
        scene.OnUi(() =>
        {
            Assert.DoesNotContain(scene.ViewModel.HeaderChips, c => c.IsDiagnostic);
            Assert.DoesNotContain("lookup", scene.ViewModel.Sources.Single(s => s.Label.StartsWith("Gateway", StringComparison.Ordinal)).Detail, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void A_0_8_10_install_gets_exactly_the_panel_it_had_whatever_its_gateway_reports()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        string[] Observed(AiDiscoveryPanelViewModel vm) =>
            new[]
            {
                string.Join("|", Products(vm)),
                string.Join("|", Models(vm)),
                string.Join("|", vm.ModelsView.Cast<DiscoveryModelRow>().Select(m => m.ConfidenceLabel + "/" + m.OwnersDisplay + "/" + m.RelevanceDisplay + "/" + m.State)),
                string.Join("|", vm.CardsView.Cast<DiscoveryComponentCard>().Select(c => c.HeaderDisplay + "/" + c.HasBands)),
                $"{vm.ProductCount} {vm.ModelCount} {vm.ModelCaption} [{vm.StatusMessage}]",
                $"{vm.HasModalityFilter} {vm.HasOwnerData} {vm.HasRelevanceFilter} {vm.HasRecommendedScope} {vm.ShowAllModels} {vm.HasActiveModelFilters} {vm.HasModelScopeNote}",
                $"{vm.HasHeader} {vm.HasHeaderNote} {string.Join("|", vm.HeaderChips.Select(c => c.Text))} [{vm.HeaderNote}]",
                string.Join("|", vm.Sources.Select(s => s.Label + "=" + s.Detail)),
            };

        string[] before = Array.Empty<string>();
        DiscoveryComponentCard[] cardsBefore = Array.Empty<DiscoveryComponentCard>();
        DiscoveryModelRow[] rowsBefore = Array.Empty<DiscoveryModelRow>();
        scene.OnUi(() =>
        {
            before = Observed(scene.ViewModel);
            cardsBefore = scene.ViewModel.CardsView.Cast<DiscoveryComponentCard>().ToArray();
            rowsBefore = scene.ViewModel.ModelsView.Cast<DiscoveryModelRow>().ToArray();
        });

        // What the 0.8.10 code answers for these same signals: evidence off the wire, scores on the component's signal, no newer member,
        // no lookup setting.
        Apply(scene, ReportOf0810(State0810()));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            var after = Observed(vm);

            // Everything is as it was, except the one Sources row that says the report was read.
            Assert.Equal(before.Take(7), after.Take(7));
            var sourceLines = after[7].Split('|');
            Assert.Equal(before[7], string.Join("|", sourceLines.Take(sourceLines.Length - 1)));
            Assert.StartsWith("Gateway — GET /api/v1/ai-usage=11 signals read from the gateway. No model gained anything from it", sourceLines[^1], StringComparison.Ordinal);

            // The lists were not rebuilt: a card the operator had open is the same card.
            Assert.True(cardsBefore.SequenceEqual(vm.CardsView.Cast<DiscoveryComponentCard>(), ReferenceEqualityComparer.Instance));
            Assert.True(rowsBefore.SequenceEqual(vm.ModelsView.Cast<DiscoveryModelRow>(), ReferenceEqualityComparer.Instance));

            // The per-signal scores the report carries on the SDK signal were not taken: its card has no band.
            Assert.DoesNotContain(vm.CardsView.Cast<DiscoveryComponentCard>(), c => c.HasBands);
        });
    }

    [Fact]
    public void The_pickers_of_a_0_8_10_view_keep_the_words_they_had_and_a_classified_view_says_which_number_it_cuts()
    {
        using var legacy = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);
        legacy.OnUi(() =>
        {
            Assert.Equal(
                "The strongest detection score among the model's signals: how sure the scanner was that something matched, not its confidence in the model itself. Every model is listed until you choose a cut.",
                legacy.ViewModel.ConfidenceFilterHelp);
            Assert.Equal("Set the modality and confidence filters back to all.", legacy.ViewModel.ResetFiltersHelp);
        });

        using var classified = OpenNewer();
        classified.OnUi(() =>
        {
            Assert.StartsWith("The number the Confidence column shows:", classified.ViewModel.ConfidenceFilterHelp, StringComparison.Ordinal);
            Assert.Equal("Set the filters back to all and the list back to the recommended models.", classified.ViewModel.ResetFiltersHelp);
        });
    }

    [Fact]
    public void A_service_that_reports_itself_off_adds_a_sources_row_and_nothing_else()
    {
        using var scene = OpenNewer();
        string[] before = Array.Empty<string>();
        scene.OnUi(() => before = Models(scene.ViewModel));

        Apply(scene, Rest(ReportDisabled));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            Assert.Equal(before, Models(vm));
            Assert.DoesNotContain(vm.HeaderChips, c => c.IsDiagnostic);

            var source = Assert.Single(vm.Sources, s => s.Label == "Gateway — GET /api/v1/ai-usage");
            Assert.True(source.Available);
            Assert.Equal("The gateway reports AI discovery off, so it has nothing to add to the files.", source.Detail);
        });
    }

    public static TheoryData<string> Failures() => new()
    {
        "unreachable", "refused", "not connected", "no route", "too large", "not json", "not a report", "error",
    };

    private static AiUsageRead FailedRead(string name) => name switch
    {
        "unreachable" => AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Unreachable("gateway is not listening (connection refused)")),
        "refused" => AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Unauthorized("unauthorized")),
        "not connected" => AiUsageReader.FromGateway(GatewayResult<JsonDocument>.NotConnected("gateway: not connected", 200)),
        "no route" => AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Error("gateway returned HTTP 404", 404)),
        "too large" => AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Error(GatewayClient.TooLargeMessage(AiUsageReader.MaxBytes), 200)),
        "not json" => AiUsageReader.ParseText("<html>"),
        "not a report" => AiUsageReader.ParseText("[1,2,3]"),
        _ => AiUsageReader.FromGateway(GatewayResult<JsonDocument>.Error("gateway returned HTTP 500", 500)),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void A_read_that_fails_leaves_the_files_lists_as_they_are_and_says_why_under_sources(string failure)
    {
        using var scene = OpenNewer();
        string[] before = Array.Empty<string>();
        scene.OnUi(() => before = Models(scene.ViewModel));
        var read = FailedRead(failure);

        scene.OnUi(() => scene.ViewModel.ApplyUsage(read));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            Assert.Equal(before, Models(vm));
            Assert.DoesNotContain(vm.HeaderChips, c => c.IsDiagnostic);

            var source = Assert.Single(vm.Sources, s => s.Label == "Gateway — GET /api/v1/ai-usage");
            Assert.False(source.Available);
            Assert.Equal(read.Message, source.Detail);
            Assert.Equal("Warn", source.AvailabilityKey);
        });
    }

    [Fact]
    public void A_later_read_that_fails_takes_back_what_an_earlier_report_added_and_the_files_that_have_it_still_show_it()
    {
        using var scene = OpenFilesWithoutThem();

        Apply(scene, Rest(ReportPopulated));
        scene.OnUi(() => Assert.True(scene.ViewModel.HasOwnerData));

        scene.OnUi(() => scene.ViewModel.ApplyUsage(FailedRead("unreachable")));
        scene.OnUi(() =>
        {
            // These files carry no owner: a gateway that stopped answering is not believed about its models.
            Assert.False(scene.ViewModel.HasOwnerData);
            Assert.Equal(EveryModel, Models(scene.ViewModel));
            Assert.Single(scene.ViewModel.Sources, s => s.Label.StartsWith("Gateway", StringComparison.Ordinal));
        });

        using var carrying = OpenNewer();
        carrying.OnUi(() => carrying.ViewModel.ApplyUsage(FailedRead("unreachable")));
        carrying.OnUi(() =>
        {
            // These files carry the members themselves, so the view is the same with or without the gateway.
            Assert.True(carrying.ViewModel.HasOwnerData);
            Assert.Equal(Recommended, Models(carrying.ViewModel));
        });
    }

    [Fact]
    public void What_the_report_added_survives_a_refresh_of_the_files_and_a_chosen_model_stays_chosen()
    {
        using var scene = OpenFilesWithoutThem();
        Apply(scene, Rest(ReportPopulated));

        scene.OnUi(() =>
        {
            scene.ViewModel.ActiveView = AiDiscoveryPanelViewModel.ModelsViewKey;
            scene.ViewModel.SelectedModel = Row(scene.ViewModel, "speech-tiny");
            Assert.True(scene.ViewModel.HasModelSelection);
        });

        scene.Reload();

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            // The files were read again; the owner is still there while the gateway has not been asked again.
            Assert.True(vm.HasOwnerData);
            Assert.Equal(Recommended, Models(vm));
            Assert.Equal("speech-tiny", vm.SelectedModel?.ModelId);
            Assert.Equal("Example Notes", vm.SelectedModel!.OwnersDisplay);
        });

        Apply(scene, Rest(ReportPopulated));

        scene.OnUi(() =>
        {
            Assert.Equal("speech-tiny", scene.ViewModel.SelectedModel?.ModelId);
            Assert.Single(scene.ViewModel.Sources, s => s.Label.StartsWith("Gateway", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void One_classified_model_ends_the_legacy_listing_of_the_rest()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);
        scene.OnUi(() => Assert.Equal(3, Models(scene.ViewModel).Length));

        // A report that classifies a single model of the three.
        const string report = """
            {"enabled": true, "signals": [
              {"signal_id": "sig-model-llm-a", "category": "local_model", "model": {"id": "example-llm-7b-q4", "owner_application": "Example Notes", "relevance": "primary", "discovery_confidence": 0.95}}
            ]}
            """;
        Apply(scene, report);

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;

            Assert.True(vm.HasRecommendedScope);

            // The model it classified is listed; the two it did not are held to the scope like any other (nothing says they are primary).
            Assert.Equal(new[] { "example-llm-7b-q4" }, Models(vm));
            Assert.Equal("1 of 3", vm.ModelCaption);
            Assert.Equal("2 models hidden by the recommended view. Turn on Show all models to list them.", vm.ModelScopeNote);
        });
    }

    // ---- where the report comes from ----

    [Fact]
    public void The_panel_asks_through_the_script_it_is_given_and_applies_the_answer()
    {
        using var scene = OpenFilesWithoutThem();

        scene.LoadUsage(() => Doc(Rest(ReportPopulated)));

        scene.OnUi(() =>
        {
            Assert.True(scene.ViewModel.HasOwnerData);
            Assert.Equal(Recommended, Models(scene.ViewModel));
        });
    }

    [Fact]
    public void A_script_that_fails_or_answers_with_a_status_is_a_failed_read_and_never_an_exception_of_the_panel()
    {
        using var scene = OpenFilesWithoutThem();

        scene.LoadUsage(() => GatewayResult<JsonDocument>.Unauthorized("unauthorized"));
        scene.OnUi(() => Assert.Contains("refused this app's API token", scene.ViewModel.Sources.Single(s => s.Label.StartsWith("Gateway", StringComparison.Ordinal)).Detail, StringComparison.Ordinal));

        scene.LoadUsage(() => throw new InvalidOperationException("the script broke"));
        scene.OnUi(() =>
        {
            var source = scene.ViewModel.Sources.Single(s => s.Label.StartsWith("Gateway", StringComparison.Ordinal));
            Assert.False(source.Available);
            Assert.Equal("The read failed unexpectedly (InvalidOperationException).", source.Detail);
            Assert.DoesNotContain("the script broke", source.Detail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_report_read_for_a_second_time_replaces_the_sources_row_and_does_not_add_one()
    {
        using var scene = OpenFilesWithoutThem();

        scene.LoadUsage(() => Doc(Rest(ReportPopulated)));
        scene.LoadUsage(() => Doc(Rest(ReportPopulated)));
        scene.LoadUsage(() => Doc(Rest(ReportDisabled)));

        scene.OnUi(() => Assert.Single(scene.ViewModel.Sources, s => s.Label.StartsWith("Gateway", StringComparison.Ordinal)));
    }

    // ---- raw local paths ----

    private static string ReportWithPathOwner() =>
        Rest(ReportPopulated).Replace("\"owner_application\": \"Example Notes\"", "\"owner_application\": \"C:\\\\Users\\\\operator\\\\AppData\\\\Local\\\\Example Notes\\\\notes.exe\"", StringComparison.Ordinal);

    [Fact]
    public void A_path_where_an_owner_belongs_is_not_shown_by_default()
    {
        using var scene = OpenFilesWithoutThem();

        scene.LoadUsage(() => Doc(ReportWithPathOwner()));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            vm.ShowAllModels = true;

            var owners = vm.ModelsView.Cast<DiscoveryModelRow>().SelectMany(m => m.OwnerApplications).Distinct().ToArray();
            Assert.Contains(DiscoveryPaths.HiddenText, owners);
            Assert.DoesNotContain(owners, o => o.Contains("operator", StringComparison.Ordinal));

            // Nor in the lines of the observations, the search, or the facts.
            var chat = Row(vm, Chat);
            Assert.DoesNotContain(chat.Observations, o => o.DetailText.Contains("operator", StringComparison.Ordinal));
            Assert.DoesNotContain(chat.Facts, f => f.Value.Contains("operator", StringComparison.Ordinal));
            Assert.False(chat.Matches("AppData"));
        });
    }

    [Fact]
    public void The_same_path_is_shown_when_the_runtime_says_it_keeps_raw_local_paths()
    {
        using var scene = OpenFilesWithoutThem(StorePaths);

        scene.LoadUsage(() => Doc(ReportWithPathOwner()));

        scene.OnUi(() =>
        {
            var vm = scene.ViewModel;
            vm.ShowAllModels = true;

            Assert.Contains(@"C:\Users\operator\AppData\Local\Example Notes\notes.exe", Row(vm, Chat).OwnerApplications);
        });
    }

    [Fact]
    public void A_path_in_a_state_file_is_withheld_too_and_the_config_decides_for_files_as_for_the_report()
    {
        var state = StateFileOf(Rest(ReportPopulated), withNewerModelFields: true)
            .Replace("\"owner_application\":\"Example Notes\"", "\"owner_application\":\"C:\\\\Users\\\\operator\\\\Notes\\\\notes.exe\"", StringComparison.Ordinal);
        Assert.Contains("operator", state, StringComparison.Ordinal);

        using var hidden = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp => temp.WriteFile("ai_discovery_state.json", state));
        hidden.OnUi(() =>
        {
            hidden.ViewModel.ShowAllModels = true;
            Assert.DoesNotContain(hidden.ViewModel.ModelsView.Cast<DiscoveryModelRow>().SelectMany(m => m.OwnerApplications), o => o.Contains("operator", StringComparison.Ordinal));
        });

        using var shown = DiscoveryScene.Open(null, StorePaths, temp => temp.WriteFile("ai_discovery_state.json", state));
        shown.OnUi(() =>
        {
            shown.ViewModel.ShowAllModels = true;
            Assert.Contains(shown.ViewModel.ModelsView.Cast<DiscoveryModelRow>().SelectMany(m => m.OwnerApplications), o => o.Contains("operator", StringComparison.Ordinal));
        });
    }

    // ---- inventory.db carries the same blocks ----

    [Fact]
    public void The_fallback_to_inventory_db_reads_the_newer_members_from_its_model_json()
    {
        using var scene = DiscoveryScene.Open(null, DiscoveryScene.DiscoveryOn, temp => DiscoveryScene.WriteInventory(
            temp.File("inventory.db"),
            DiscoveryScene.InventoryOf(
                "INSERT INTO ai_signals (scan_id, fingerprint, signal_id, signature_id, name, vendor, product, category, detector, state, confidence, last_seen, evidence_json, model_json) " +
                "VALUES ('scan-1', 'fp-m', 'sig-m', 'local-model-artifact', 'local-model-artifact', 'Local', 'Local Model Artifact', 'local_model', 'model_file', 'seen', 0.9, " +
                "'2026-10-08 15:20:00 +0000 UTC', '[]', '{\"id\":\"db-model\",\"status\":\"installed\",\"format\":\"gguf\",\"provider\":\"filesystem\",\"owner_application\":\"Example Notes\"," +
                "\"relevance\":\"primary\",\"discovery_confidence\":0.9,\"provenance\":{\"publisher\":\"Example Labs\",\"source\":\"catalog_exact\",\"confidence\":\"high\"}}')")));

        scene.OnUi(() =>
        {
            var row = Row(scene.ViewModel, "db-model");

            Assert.Equal("Example Notes", row.OwnersDisplay);
            Assert.Equal("90%", row.ConfidenceLabel);
            Assert.Equal("Example Labs", row.Provenance!.Publisher);
            Assert.True(scene.ViewModel.HasRecommendedScope);
        });
    }
}
