using DefenseClaw.App.ViewModels;
using static DefenseClaw.App.Tests.Discovery.DiscoveryData;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// The Models view's recommended / all scope (CUST-310): the Mac's <c>AIModelDiscoveryFilter</c>, case for case. The rows below are the
/// Mac's own (<c>appliesFocusedAndExplicitModelFilters</c> in its <c>Tests/AIDiscoveryModelTests.swift</c>, whose helper builds one signal
/// of a model from "Meetily" with the given modality, relevance, discovery confidence, detection score and detector), and each verdict is
/// the one that test asserts. The filters are named after what the Mac names them: <c>focused</c> is the default (Show all models off),
/// <c>all</c> is Show all models on, and a picker choice is a modality or a relevance.
/// </summary>
public sealed class AiDiscoveryModelFilterTests
{
    private static readonly DiscoveryModelFilter Focused = new() { RecommendedOnly = true };
    private static readonly DiscoveryModelFilter All = new();

    // The rows. (id, modality, relevance, discovery confidence, detection score, detector, owner)
    private static readonly Dictionary<string, DiscoveryModelRow> Rows = new(StringComparer.Ordinal)
    {
        ["primary generative 0.93"] = MacRow("primary-generative", "generative", "primary", 0.93),
        ["unknown high 0.88"] = MacRow("unknown-high", "unknown", "unknown", 0.88),
        ["supporting generative 0.91"] = MacRow("supporting", "generative", "supporting", 0.91),
        ["embedded generative 0.96"] = MacRow("embedded", "generative", "embedded", 0.96),
        ["primary speech 0.94"] = MacRow("speech", "speech", "primary", 0.94),
        ["supporting speech 0.92"] = MacRow("supporting-speech", "speech", "supporting", 0.92),
        ["ownerless supporting speech 0.92"] = MacRow("ownerless-supporting-speech", "speech", "supporting", 0.92, owner: string.Empty),
        ["supporting audio 0.8"] = MacRow("supporting-audio", "audio", "supporting", 0.8),
        ["supporting vision 0.9"] = MacRow("supporting-vision", "vision", "supporting", 0.9),
        ["supporting embedding 0.9"] = MacRow("supporting-embedding", "embedding", "supporting", 0.9),
        ["primary generative 0.79"] = MacRow("low", "generative", "primary", 0.79),
        ["primary generative 0.8"] = MacRow("boundary", "generative", "primary", 0.8),
        ["api primary generative reported 0"] = MacRow("explicit-zero", "generative", "primary", 0, 0.2, "model_api"),
        ["api primary generative unreported"] = MacRow("local-api-missing", "generative", "primary", null, 0.2, "model_api"),
        ["api supporting generative unreported"] = MacRow("local-api-supporting", "generative", "supporting", null, 0.2, "model_api"),
        ["api primary speech unreported"] = MacRow("local-api-speech", "speech", "primary", null, 0.2, "model_api"),
        ["legacy"] = MacRow("legacy", string.Empty, string.Empty, null, 0.9),
        ["legacy modality only"] = MacRow("legacy-modality", "text", string.Empty, null, 0.9, owner: string.Empty),
        ["mixed api row"] = MixedApiRow(),
    };

    /// <summary>
    /// One model seen twice: by a local model server that gave no confidence (0.2 detection score), and by a file scan that classified
    /// it as embedded and rated it 0.79 (the Mac's <c>mixedLocalAPIRow</c>).
    /// </summary>
    private static DiscoveryModelRow MixedApiRow()
    {
        var api = Signal(product: "Meetily", detector: "model_api", confidence: 0.2, model: Model("mixed-api-low"));
        var file = Signal(
            product: "Meetily",
            detector: "model_file",
            confidence: 0.79,
            model: Model("mixed-api-low", modality: "unknown", owner: "Chrome", relevance: "embedded", discoveryConfidence: 0.79));
        return Assert.Single(DiscoveryModelRow.Build(new[] { api, file }, Now));
    }

    private static readonly Dictionary<string, DiscoveryModelFilter> Filters = new(StringComparer.Ordinal)
    {
        ["focused"] = Focused,
        ["show all"] = All,
        ["focused, speech"] = Focused with { Modality = DiscoveryModality.Speech },
        ["focused, supporting"] = Focused with { Relevance = DiscoveryRelevance.Supporting },
        ["focused, embedded"] = Focused with { Relevance = DiscoveryRelevance.Embedded },
        ["show all, speech"] = All with { Modality = DiscoveryModality.Speech },
    };

    // ---- the cases ----

    public static TheoryData<string, string, bool> Verdicts() => new()
    {
        // The recommended scope with nothing chosen.
        { "focused", "primary generative 0.93", true },
        { "focused", "unknown high 0.88", false },
        { "focused", "supporting generative 0.91", false },
        { "focused", "embedded generative 0.96", false },
        { "focused", "primary speech 0.94", true },

        // A supporting model is listed when an application owns it and it is speech, audio, vision or embedding...
        { "focused", "supporting speech 0.92", true },
        { "focused", "supporting audio 0.8", true },
        { "focused", "supporting vision 0.9", true },
        { "focused", "supporting embedding 0.9", true },

        // ...and not when nobody owns it.
        { "focused", "ownerless supporting speech 0.92", false },

        // The 0.8 floor: 0.79 is under it, 0.8 is on it, and a reported zero is a zero.
        { "focused", "primary generative 0.79", false },
        { "focused", "primary generative 0.8", true },
        { "focused", "api primary generative reported 0", false },

        // A model a local model server listed with no confidence of its own is always listed, whatever else it is.
        { "focused", "api primary generative unreported", true },
        { "focused", "api supporting generative unreported", true },
        { "focused", "api primary speech unreported", true },
        { "focused", "mixed api row", true },

        // Choosing a modality: the choice decides what is of that class, so the supporting-speech rule no longer applies.
        { "focused, speech", "primary speech 0.94", true },
        { "focused, speech", "supporting speech 0.92", true },
        { "focused, speech", "ownerless supporting speech 0.92", true },
        { "focused, speech", "api primary speech unreported", true },
        { "focused, speech", "primary generative 0.93", false },
        { "focused, speech", "mixed api row", false },

        // Choosing a relevance: likewise, and an unowned or generative supporting model is then of the class asked for.
        { "focused, supporting", "supporting generative 0.91", true },
        { "focused, supporting", "supporting speech 0.92", true },
        { "focused, supporting", "primary generative 0.93", false },
        { "focused, embedded", "mixed api row", true },

        // Show all models lifts the scope, and the pickers still narrow.
        { "show all", "primary generative 0.79", true },
        { "show all", "embedded generative 0.96", true },
        { "show all", "unknown high 0.88", true },
        { "show all", "legacy", true },
        { "show all, speech", "primary speech 0.94", true },
        { "show all, speech", "embedded generative 0.96", false },
    };

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void The_scope_lists_what_the_Macs_filter_lists(string filter, string row, bool listed) =>
        Assert.Equal(listed, Filters[filter].Includes(Rows[row]));

    [Fact]
    public void A_choice_of_modality_or_relevance_lifts_the_primary_and_supporting_rules_and_not_the_confidence_floor()
    {
        // The Mac's code applies the 0.8 floor before it looks at the pickers, so a low-confidence speech model stays off the list under
        // "Speech" until Show all models is on.
        var lowSpeech = MacRow("low-speech", "speech", "supporting", 0.7);

        Assert.False((Focused with { Modality = DiscoveryModality.Speech }).Includes(lowSpeech));
        Assert.False((Focused with { Relevance = DiscoveryRelevance.Supporting }).Includes(lowSpeech));
        Assert.True((All with { Modality = DiscoveryModality.Speech }).Includes(lowSpeech));
    }

    [Fact]
    public void A_snapshot_with_no_classification_anywhere_is_legacy_and_keeps_its_all_models_listing()
    {
        var legacy = Rows["legacy"];
        var modalityOnly = Rows["legacy modality only"];

        // Entirely legacy: the recommended scope would hide everything (nothing is primary), so it is taken off.
        Assert.True(Focused.PreservingLegacySnapshot(new[] { legacy }).Includes(legacy));

        // The modality a 0.8.10 scanner already sends does not make a snapshot classified.
        Assert.True(Focused.PreservingLegacySnapshot(new[] { modalityOnly }).Includes(modalityOnly));
        Assert.True(DiscoveryModelFilter.IsLegacySnapshot(new[] { legacy, modalityOnly }));

        // One classified model anywhere and legacy rows are held to the scope like the rest.
        var classified = Rows["primary generative 0.93"];
        var mixed = new[] { legacy, classified };
        Assert.False(DiscoveryModelFilter.IsLegacySnapshot(mixed));
        Assert.False(Focused.PreservingLegacySnapshot(mixed).Includes(legacy));
        Assert.True(Focused.PreservingLegacySnapshot(mixed).Includes(classified));
    }

    [Fact]
    public void A_legacy_snapshot_still_obeys_what_the_operator_chose()
    {
        var rows = new[] { Rows["legacy"], MacRow("legacy-speech", "speech", string.Empty, null, 0.9, owner: string.Empty) };
        var speech = Focused with { Modality = DiscoveryModality.Speech };

        var effective = speech.PreservingLegacySnapshot(rows);

        Assert.False(effective.RecommendedOnly);
        Assert.False(effective.Includes(rows[0]));
        Assert.True(effective.Includes(rows[1]));
    }

    [Fact]
    public void An_empty_snapshot_is_not_legacy_and_a_filter_that_is_not_recommended_is_left_as_it_is()
    {
        Assert.False(DiscoveryModelFilter.IsLegacySnapshot(Array.Empty<DiscoveryModelRow>()));

        var shown = All with { Modality = DiscoveryModality.Vision };
        Assert.Same(shown, shown.PreservingLegacySnapshot(new[] { Rows["legacy"] }));
    }

    [Fact]
    public void A_filter_with_nothing_set_lists_every_model_so_a_0_8_10_view_is_what_it_was()
    {
        // The filter the panel built before the recommended scope existed, and the one it still builds for a legacy snapshot.
        Assert.All(Rows.Values, row => Assert.True(new DiscoveryModelFilter().Includes(row), row.ModelId));
    }

    [Fact]
    public void The_confidence_picker_composes_with_the_scope_and_cuts_the_number_the_column_shows()
    {
        // A reported 0.93 beats a 0.2 detection score for the cut, as it does in the column.
        var reportedHigh = MacRow("reported-high", "generative", "primary", 0.93, signalConfidence: 0.2);
        var signalOnlyHigh = MacRow("signal-high", string.Empty, string.Empty, null, signalConfidence: 0.95);

        var atLeast = All with { Confidence = DiscoveryConfidenceBand.High };
        var under = All with { Confidence = DiscoveryConfidenceBand.Low };

        Assert.True(atLeast.Includes(reportedHigh));
        Assert.False(under.Includes(reportedHigh));
        Assert.True(atLeast.Includes(signalOnlyHigh));
        Assert.False(under.Includes(signalOnlyHigh));

        // Under the recommended scope it is one more condition, not a replacement for it.
        var recommendedAndHigh = Focused with { Confidence = DiscoveryConfidenceBand.High };
        Assert.True(recommendedAndHigh.Includes(Rows["primary generative 0.93"]));
        Assert.False(recommendedAndHigh.Includes(Rows["embedded generative 0.96"]));
    }

    // ---- the row facts the verdicts rest on ----

    [Fact]
    public void A_mixed_row_keeps_the_file_scans_classification_and_the_servers_missing_confidence()
    {
        var row = Rows["mixed api row"];

        Assert.Equal(0.79, row.ReportedDiscoveryConfidence);
        Assert.True(row.HasLocalModelApiSignalWithoutDiscoveryConfidence);
        Assert.True(row.HasLocalModelApiSignal);
        Assert.Equal(DiscoveryRelevance.Embedded, row.EffectiveRelevance);
        Assert.Equal(new[] { "Chrome" }, row.OwnerApplications);
    }

    [Fact]
    public void A_reported_zero_is_a_confidence_and_a_missing_one_is_not()
    {
        Assert.Equal(0d, Rows["api primary generative reported 0"].ReportedDiscoveryConfidence);
        Assert.False(Rows["api primary generative reported 0"].HasLocalModelApiSignalWithoutDiscoveryConfidence);
        Assert.Null(Rows["api primary generative unreported"].ReportedDiscoveryConfidence);
        Assert.True(Rows["api primary generative unreported"].HasLocalModelApiSignalWithoutDiscoveryConfidence);
    }

    // ---- what the confidence column says ----

    [Fact]
    public void The_confidence_column_says_the_models_own_confidence_API_or_a_signal_score_and_what_it_is_a_number_of()
    {
        var reported = Rows["primary generative 0.93"];
        Assert.Equal("93%", reported.ConfidenceLabel);
        Assert.Equal("Discovery confidence 93 percent", reported.ConfidenceAutomationLabel);

        var api = Rows["api primary generative unreported"];
        Assert.Equal("API", api.ConfidenceLabel);
        Assert.Equal("Local model API; discovery confidence not reported", api.ConfidenceAutomationLabel);

        var legacy = Rows["legacy"];
        Assert.Equal("90% signal", legacy.ConfidenceLabel);
        Assert.Equal("Signal confidence 90 percent", legacy.ConfidenceAutomationLabel);
    }

    [Fact]
    public void A_server_listed_model_is_API_only_where_the_runtime_classifies_its_models_and_a_0_8_10_view_keeps_its_signal_score()
    {
        var api = Signal(product: "Ollama", detector: "model_api", confidence: 0.85, model: Model("server-model"));

        // 0.8.10: no model in the snapshot carries a confidence, an owner or a relevance. The label has always been the score.
        var legacy = Assert.Single(DiscoveryModelRow.Build(new[] { api }, Now));
        Assert.Equal("85% signal", legacy.ConfidenceLabel);
        Assert.Equal("Signal confidence 85 percent", legacy.ConfidenceAutomationLabel);

        // A runtime that classifies its models: the same listing has no confidence of its own, and says so.
        var classified = Signal(model: Model("owned-model", owner: "Example Notes", relevance: "primary", discoveryConfidence: 0.9));
        var rows = DiscoveryModelRow.Build(new[] { api, classified }, Now);
        Assert.Equal("API", rows.Single(r => r.ModelId == "server-model").ConfidenceLabel);
    }

    [Fact]
    public void The_confidence_that_sorts_is_the_one_shown_and_percent_rounds_half_up()
    {
        Assert.Equal(0.93, Rows["primary generative 0.93"].EffectiveDiscoveryConfidence);
        Assert.Equal(0.9, Rows["legacy"].EffectiveDiscoveryConfidence);
        Assert.Equal("93%", MacRow("a", "generative", "primary", 0.925).ConfidenceLabel);
        Assert.Equal("0%", MacRow("b", "generative", "primary", 0).ConfidenceLabel);
    }

    // ---- search over the new members, as the Mac's matches() ----

    [Fact]
    public void A_model_is_found_by_its_owner_its_modality_and_its_relevance()
    {
        var row = Rows["primary generative 0.93"];

        Assert.True(row.Matches("Meetily"));
        Assert.True(row.Matches("meetily"));
        Assert.True(row.Matches("generative"));
        Assert.True(row.Matches("primary"));
        Assert.False(row.Matches("supporting"));
        Assert.False(row.Matches("Anthropic"));
    }
}
