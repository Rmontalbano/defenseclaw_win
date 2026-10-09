using System.Text.Json;
using DefenseClaw.App.ViewModels;
using static DefenseClaw.App.Tests.Discovery.DiscoveryData;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// What a newer runtime adds to a model block (CUST-310) and how it is read: the owner, the relevance, the discovery confidence and the
/// lineage, taken from the state file, <c>inventory.db</c> and the gateway's answer alike, only when they are there, with nothing made up
/// and nothing that names a place on this machine shown unless the runtime says it keeps such paths. Ported from the Mac's own tests
/// (<c>AIDiscoveryModelTests.swift</c> and <c>LocalModelDiscoveryParityTests.swift</c>) where they cover the same reading, with the
/// values changed to invented ones.
/// </summary>
public sealed class AiDiscoveryModelFieldsTests
{
    private static DiscoveryModelInfo ModelOf(string json, DiscoveryReadOptions options = default)
    {
        using var document = JsonDocument.Parse(json);
        return DiscoverySignalParser.ParseModel(document.RootElement, options)!;
    }

    private static DiscoveryModelProvenance? ProvenanceOf(string json, DiscoveryReadOptions options = default)
    {
        using var document = JsonDocument.Parse(json);
        return DiscoverySignalParser.ParseProvenance(document.RootElement, options);
    }

    // ---- the model block ----

    [Fact]
    public void A_model_block_of_a_newer_runtime_keeps_its_owner_relevance_confidence_and_lineage()
    {
        var model = ModelOf("""
            {
              "id": "mlx-community/Example-3B-Instruct-4bit", "status": "installed", "format": "safetensors", "provider": "mlx",
              "recipe": "mlx", "modality": "text", "device": "gpu", "size_bytes": 2147483648, "pinned": true,
              "owner_application": "Example Notes", "relevance": "primary", "discovery_confidence": 0.92,
              "provenance": {
                "publisher": "Example Labs", "country_code": "us", "root_model": "example-labs/Example-3B-Instruct",
                "base_models": ["example-labs/Example-3B-Instruct"], "quantized": true, "quantization": "4-bit", "distilled": false,
                "derivation": "quantized", "source": "catalog_exact", "confidence": "high"
              }
            }
            """);

        Assert.Equal("mlx-community/Example-3B-Instruct-4bit", model.Id);
        Assert.Equal(2147483648, model.SizeBytes);
        Assert.True(model.Pinned);
        Assert.Equal("Example Notes", model.OwnerApplication);
        Assert.Equal("primary", model.Relevance);
        Assert.Equal(0.92, model.DiscoveryConfidence);
        Assert.True(model.HasClassification);

        var lineage = model.Provenance!;
        Assert.Equal("Example Labs", lineage.Publisher);
        Assert.Equal("US", lineage.CountryCode);
        Assert.Equal("United States (US)", lineage.CountryDisplay);
        Assert.Equal("example-labs/Example-3B-Instruct", lineage.RootModel);
        Assert.Equal(new[] { "example-labs/Example-3B-Instruct" }, lineage.BaseModels);
        Assert.True(lineage.Quantized);
        Assert.False(lineage.Distilled);
        Assert.Equal("quantized · 4-bit", lineage.DerivationDisplay);
        Assert.Equal("catalog_exact", lineage.Source);
        Assert.Equal("high", lineage.Confidence);
    }

    [Fact]
    public void A_model_block_of_0_8_10_has_none_of_them_and_is_not_classified()
    {
        var model = ModelOf("""{ "id": "m", "status": "installed", "format": "gguf", "provider": "filesystem", "size_bytes": 12 }""");

        Assert.Equal(string.Empty, model.OwnerApplication);
        Assert.Equal(string.Empty, model.Relevance);
        Assert.Null(model.DiscoveryConfidence);
        Assert.Null(model.Provenance);
        Assert.False(model.HasClassification);
        Assert.Equal("model: id=m status=installed format=gguf provider=filesystem size=12 B", model.Detail());
    }

    [Theory]
    [InlineData("0.92", 0.92)]
    [InlineData("86", 0.86)]
    [InlineData("100", 1.0)]
    [InlineData("1", 1.0)]
    [InlineData("0", 0.0)]
    [InlineData("0.0", 0.0)]
    [InlineData("-3", 0.0)]
    [InlineData("\"0.5\"", 0.5)]
    [InlineData("1.5", 0.015)]
    [InlineData("true", null)]
    [InlineData("false", null)]
    [InlineData("null", null)]
    [InlineData("\"high\"", null)]
    [InlineData("[0.9]", null)]
    [InlineData("{}", null)]
    public void A_discovery_confidence_is_a_fraction_a_whole_percent_or_nothing_and_a_zero_stays_a_zero(string json, double? expected)
    {
        var model = ModelOf("{\"id\":\"m\",\"discovery_confidence\":" + json + "}");

        if (expected is null)
        {
            Assert.Null(model.DiscoveryConfidence);
        }
        else
        {
            Assert.Equal(expected.Value, model.DiscoveryConfidence!.Value, precision: 9);
        }
    }

    [Fact]
    public void A_missing_confidence_is_not_a_zero_and_does_not_make_a_model_classified_on_its_own()
    {
        Assert.Null(ModelOf("""{"id":"m"}""").DiscoveryConfidence);
        Assert.False(ModelOf("""{"id":"m","provenance":{"publisher":"Example Labs"}}""").HasClassification);
        Assert.True(ModelOf("""{"id":"m","discovery_confidence":0}""").HasClassification);
        Assert.True(ModelOf("""{"id":"m","owner_application":"Example Notes"}""").HasClassification);
        Assert.True(ModelOf("""{"id":"m","relevance":"unknown"}""").HasClassification);
    }

    [Fact]
    public void The_detail_line_lists_the_new_members_where_the_TUI_lists_them()
    {
        var model = ModelOf("""
            {
              "id": "example-chat-3b-q4", "status": "loaded", "format": "gguf", "provider": "example-server", "recipe": "llamacpp",
              "modality": "text", "device": "gpu", "size_bytes": 2000000000, "pinned": true,
              "owner_application": "Example Notes", "relevance": "primary", "discovery_confidence": 0.93
            }
            """);

        Assert.Equal(
            "model: id=example-chat-3b-q4 status=loaded format=gguf provider=example-server recipe=llamacpp modality=text relevance=primary owner=Example Notes device=gpu size=1.9 GiB pinned=true discovery_confidence=93%",
            model.Detail());

        // The Mac's own example of the bounded inspector detail.
        Assert.Equal(
            "model: id=llama3:8b-q4 relevance=primary owner=Ollama discovery_confidence=92%",
            ModelOf("""{"id":"llama3:8b-q4","owner_application":"Ollama","relevance":"primary","discovery_confidence":92}""").Detail());
    }

    // ---- lineage ----

    [Fact]
    public void An_invalid_country_is_dropped_and_unknown_flags_stay_unknown()
    {
        var unknown = ProvenanceOf("""{"country_code":"USA","derivation":"distilled+quantized"}""")!;

        Assert.Equal(string.Empty, unknown.CountryCode);
        Assert.Equal(string.Empty, unknown.CountryDisplay);
        Assert.Null(unknown.Quantized);
        Assert.Null(unknown.Distilled);
        Assert.Equal("distilled+quantized", unknown.DerivationDisplay);
    }

    [Fact]
    public void Compatible_spellings_of_a_flag_and_a_single_base_model_are_accepted_and_no_root_is_invented()
    {
        var lineage = ProvenanceOf("""{"base_models":"publisher/base-model","quantized":"yes","quantization":"Q4_K_M","distilled":0}""")!;

        Assert.Equal(new[] { "publisher/base-model" }, lineage.BaseModels);
        Assert.Equal("ambiguous (1)", lineage.RootDisplay);
        Assert.True(lineage.Quantized);
        Assert.False(lineage.Distilled);
        Assert.Equal("quantized · Q4_K_M", lineage.DerivationDisplay);
    }

    [Fact]
    public void A_model_with_several_parents_has_no_root_and_says_how_many()
    {
        var lineage = ProvenanceOf("""{"base_models":["publisher/base-a","publisher/base-b"],"source":"huggingface_hub","confidence":"medium"}""")!;

        Assert.Equal("ambiguous (2)", lineage.RootDisplay);
        Assert.Equal(string.Empty, lineage.RootModel);
    }

    [Fact]
    public void A_base_model_that_is_empty_or_not_text_is_dropped_and_the_list_is_bounded_and_unique()
    {
        var odd = ProvenanceOf("""{"country_code":" us ","base_models":["Llama 3","",42,null,"  "],"quantized":"yes","quantization":"4-bit","distilled":false,"source":"catalog","confidence":"high"}""")!;
        Assert.Equal(new[] { "Llama 3" }, odd.BaseModels);
        Assert.Equal("US", odd.CountryCode);
        Assert.Equal("quantized · 4-bit", odd.DerivationDisplay);

        var many = ProvenanceOf("""{"base_models":["a/1","a/2","a/3","a/4","a/5","a/6","a/7","a/8","a/9","a/10"]}""")!;
        Assert.Equal(8, many.BaseModels.Count);
        Assert.Equal("a/8", many.BaseModels[^1]);

        var duplicated = ProvenanceOf("""{"base_models":["Org/Base","org/base"," ORG/BASE "]}""")!;
        Assert.Equal(new[] { "Org/Base" }, duplicated.BaseModels);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"lineage\"")]
    [InlineData("null")]
    [InlineData("7")]
    public void A_lineage_that_is_not_an_object_with_something_in_it_is_no_lineage(string json) => Assert.Null(ProvenanceOf(json));

    [Theory]
    [InlineData("", "", null, null, "")]
    [InlineData("quantized", "", null, null, "quantized")]
    [InlineData("", "", true, true, "distilled+quantized")]
    [InlineData("", "", true, null, "quantized")]
    [InlineData("", "", null, true, "distilled")]
    [InlineData("", "", false, false, "base")]
    [InlineData("", "", false, null, "")]
    [InlineData("", "", true, false, "quantized")]
    [InlineData("", "Q4_K_M", null, null, "Q4_K_M")]
    [InlineData("quantized", "4-bit", null, null, "quantized · 4-bit")]
    [InlineData("quantized", "QUANTIZED", null, null, "quantized")]
    [InlineData("", "Q8_0", true, true, "distilled+quantized · Q8_0")]
    public void How_a_model_was_derived_is_worded_from_the_runtimes_word_or_its_flags_and_never_from_a_guess(
        string derivation, string quantization, bool? quantized, bool? distilled, string expected) =>
        Assert.Equal(expected, Provenance(derivation: derivation, quantization: quantization, quantized: quantized, distilled: distilled).DerivationDisplay);

    [Theory]
    [InlineData("US", "United States (US)")]
    [InlineData("AE", "United Arab Emirates (AE)")]
    [InlineData("CN", "China (CN)")]
    [InlineData("GB", "United Kingdom (GB)")]
    [InlineData("XX", "XX")]
    [InlineData("", "")]
    public void A_country_is_shown_as_its_name_and_code_when_the_catalogs_list_has_it_and_as_the_code_alone_otherwise(string code, string expected) =>
        Assert.Equal(expected, DiscoveryCountries.Display(code));

    [Theory]
    [InlineData("us", "US")]
    [InlineData(" fr ", "FR")]
    [InlineData("US", "US")]
    [InlineData("USA", "")]
    [InlineData("U", "")]
    [InlineData("1A", "")]
    [InlineData("é", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_country_code_is_two_letters_or_nothing(string? raw, string expected) => Assert.Equal(expected, DiscoveryCountries.Normalize(raw));

    [Fact]
    public void The_provenance_rows_are_only_what_the_runtime_said_and_a_block_of_nothing_has_no_rows()
    {
        var full = Provenance(
            publisher: "Example Labs", country: "US", root: "example-labs/chat-3b", bases: new[] { "example-labs/chat-3b" },
            quantized: true, quantization: "Q4_K_M", distilled: false, derivation: "quantized", source: "catalog_exact", confidence: "high");

        Assert.Equal(
            new[]
            {
                ("Publisher", "Example Labs"), ("Country", "United States (US)"), ("Root model", "example-labs/chat-3b"),
                ("Base models", "example-labs/chat-3b"), ("Derivation", "quantized · Q4_K_M"), ("Quantized", "yes"), ("Distilled", "no"),
                ("Source", "catalog_exact"), ("Lineage confidence", "high"),
            },
            full.Facts.Select(f => (f.Label, f.Value)).ToArray());

        var sparse = Provenance(publisher: "Example Labs", source: "model_id", confidence: "low");
        Assert.Equal(new[] { "Publisher", "Source", "Lineage confidence" }, sparse.Facts.Select(f => f.Label).ToArray());

        // A flag the runtime did not give is not a row: absent is not "no".
        Assert.DoesNotContain(sparse.Facts, f => f.Label is "Quantized" or "Distilled");

        Assert.False(Provenance().HasFacts);
        Assert.Empty(Provenance().Facts);
    }

    [Fact]
    public void Two_readings_of_one_lineage_are_equal_and_a_list_of_base_models_does_not_get_in_the_way()
    {
        var a = Provenance(publisher: "P", bases: new[] { "x/1", "x/2" }, quantized: true);
        var b = Provenance(publisher: "P", bases: new[] { "x/1", "x/2" }, quantized: true);
        var c = Provenance(publisher: "P", bases: new[] { "x/2", "x/1" }, quantized: true);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void The_surer_and_fuller_lineage_wins_and_a_tie_keeps_the_first()
    {
        var high = Provenance(publisher: "P", source: "catalog_exact", confidence: "high");
        var mediumFull = Provenance(publisher: "P", country: "US", root: "r", source: "mixed", confidence: "medium", quantization: "Q4", derivation: "quantized", bases: new[] { "b" });
        var mediumSparse = Provenance(publisher: "P", confidence: "medium");
        var low = Provenance(publisher: "P", source: "model_id", confidence: "LOW");
        var unrated = Provenance(publisher: "P", country: "US", root: "r", source: "s");

        Assert.True(DiscoveryModelProvenance.Prefers(high, mediumFull));
        Assert.False(DiscoveryModelProvenance.Prefers(mediumFull, high));
        Assert.True(DiscoveryModelProvenance.Prefers(mediumFull, mediumSparse));
        Assert.True(DiscoveryModelProvenance.Prefers(mediumSparse, low));
        Assert.True(DiscoveryModelProvenance.Prefers(low, unrated));
        Assert.False(DiscoveryModelProvenance.Prefers(mediumSparse, Provenance(publisher: "Q", confidence: "medium")));
        Assert.True(DiscoveryModelProvenance.Prefers(low, null));
        Assert.False(DiscoveryModelProvenance.Prefers(null, low));
    }

    // ---- what names a place on this machine ----

    [Theory]
    [InlineData(@"C:\Users\operator\models\x.gguf", true)]
    [InlineData("c:/models/x.gguf", true)]
    [InlineData(@"\\fileserver\share\models", true)]
    [InlineData(@"\\?\C:\models", true)]
    [InlineData(@"models\x.gguf", true)]
    [InlineData("/Users/operator/models", true)]
    [InlineData("/opt/models", true)]
    [InlineData("/", true)]
    [InlineData("~/models", true)]
    [InlineData("file:///C:/models/x.gguf", true)]
    [InlineData("FILE:///x", true)]
    [InlineData("%USERPROFILE%/models", true)]
    [InlineData(@"%LOCALAPPDATA%\Models", true)]
    [InlineData("$HOME/models", true)]
    [InlineData("  C:\\x  ", true)]
    [InlineData("Example Notes", false)]
    [InlineData("example-labs/Example-3B-Instruct", false)]
    [InlineData("mlx-community/Example-3B-Instruct-4bit", false)]
    [InlineData("llama3:8b-q4", false)]
    [InlineData("Q4_K_M", false)]
    [InlineData("catalog_exact", false)]
    [InlineData("AT&T Labs", false)]
    [InlineData("https://huggingface.co/example-labs/chat-3b", false)]
    [InlineData("D:", false)]
    [InlineData("a:b", false)]
    [InlineData("%", false)]
    [InlineData("100%", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void A_value_that_names_a_local_path_is_told_from_a_name_or_a_model_reference(string? value, bool isPath) =>
        Assert.Equal(isPath, DiscoveryPaths.LooksLikeLocalPath(value));

    [Fact]
    public void A_path_in_an_owner_or_a_lineage_field_is_withheld_unless_the_runtime_keeps_raw_paths()
    {
        const string json = """
            {
              "id": "example-model", "owner_application": "C:\\Users\\operator\\AppData\\Local\\Example Notes\\notes.exe",
              "relevance": "primary", "discovery_confidence": 0.9,
              "provenance": {
                "publisher": "/Users/operator/hub", "root_model": "C:\\models\\root.gguf", "base_models": ["~/models/base", "org/real-base"],
                "quantization": "Q4_K_M", "source": "file:///C:/models/x", "confidence": "high"
              }
            }
            """;

        var hidden = ModelOf(json);
        Assert.Equal(DiscoveryPaths.HiddenText, hidden.OwnerApplication);
        Assert.Equal(DiscoveryPaths.HiddenText, hidden.Provenance!.Publisher);
        Assert.Equal(DiscoveryPaths.HiddenText, hidden.Provenance.RootModel);
        Assert.Equal(new[] { DiscoveryPaths.HiddenText, "org/real-base" }, hidden.Provenance.BaseModels);
        Assert.Equal(DiscoveryPaths.HiddenText, hidden.Provenance.Source);
        Assert.Equal("Q4_K_M", hidden.Provenance.Quantization);
        Assert.Equal("high", hidden.Provenance.Confidence);
        Assert.DoesNotContain("Users", hidden.Detail(), StringComparison.Ordinal);
        Assert.DoesNotContain(hidden.Provenance.Facts, f => f.Value.Contains("operator", StringComparison.Ordinal));

        // The runtime says its store keeps raw local paths (ai_discovery.store_raw_local_paths): the operator chose to have them.
        var shown = ModelOf(json, new DiscoveryReadOptions(ShowRawLocalPaths: true));
        Assert.Equal(@"C:\Users\operator\AppData\Local\Example Notes\notes.exe", shown.OwnerApplication);
        Assert.Equal("/Users/operator/hub", shown.Provenance!.Publisher);
    }

    [Fact]
    public void The_fields_the_scanner_already_sent_and_the_raw_path_members_of_evidence_are_never_touched_by_the_guard()
    {
        // 0.8.10's fields are shown as they always were (an id is the model's identity), and evidence is read only for its type, its
        // basename, its quality and its match kind: a raw_path member is not read at all, with or without the option.
        const string json = """
            {
              "vendor": "Local", "product": "Local Model Artifact", "category": "local_model", "detector": "model_file", "state": "seen",
              "model": { "id": "C:\\models\\odd-id.gguf", "provider": "D:\\models" },
              "evidence": [ { "type": "model_file", "basename": "odd-id.gguf", "raw_path": "C:\\Users\\operator\\models\\odd-id.gguf", "quality": 1, "match_kind": "exact" } ]
            }
            """;
        using var document = JsonDocument.Parse(json);

        foreach (var options in new DiscoveryReadOptions[] { default, new(ShowRawLocalPaths: true) })
        {
            var signal = DiscoverySignalParser.FromState(document.RootElement, options);

            Assert.Equal(@"C:\models\odd-id.gguf", signal.Model!.Id);
            Assert.Equal(@"D:\models", signal.Model.Provider);
            var evidence = Assert.Single(signal.Evidence);
            Assert.Equal(new DiscoveryEvidenceItem("model_file", "odd-id.gguf", 1, "exact"), evidence);
        }
    }

    [Fact]
    public void Text_from_a_host_is_cut_and_kept_on_one_line_with_its_bidirectional_controls_spelled_out()
    {
        var model = ModelOf("{\"id\":\"m\",\"owner_application\":\"Notes\\u202Eexe.txt\\n(hidden)\",\"provenance\":{\"publisher\":\"" + new string('P', 600) + "\"}}");

        Assert.DoesNotContain('\u202E', model.OwnerApplication);
        Assert.DoesNotContain('\n', model.OwnerApplication);
        Assert.Contains("\\u202E", model.OwnerApplication, StringComparison.Ordinal);
        Assert.Equal(512, model.Provenance!.Publisher.Length);
    }

    // ---- what a row makes of them ----

    private static DiscoveryModelRow RowOf(params DiscoverySignalRecord[] signals) => Assert.Single(DiscoveryModelRow.Build(signals, Now));

    [Fact]
    public void Owners_and_relevance_are_gathered_across_detectors_each_once_and_the_most_actionable_relevance_leads()
    {
        var row = RowOf(
            Signal(detector: "model_file", model: Model("Shared-Model", owner: "Example Notes", relevance: "Embedded")),
            Signal(detector: "model_runtime", product: "Ollama", model: Model("shared-model", owner: " example notes ", relevance: "primary")),
            Signal(detector: "model_api", product: "Ollama", model: Model("SHARED-MODEL", owner: "Example Studio", relevance: "")));

        Assert.Equal(new[] { "Example Notes", "Example Studio" }, row.OwnerApplications);
        Assert.Equal("Example Notes, Example Studio", row.OwnersDisplay);
        Assert.Equal(new[] { DiscoveryRelevance.Embedded, DiscoveryRelevance.Primary }, row.Relevances);
        Assert.Equal(DiscoveryRelevance.Primary, row.EffectiveRelevance);
        Assert.Equal("Embedded, Primary", row.RelevanceDisplay);
        Assert.Equal(new[] { "Embedded", "primary" }, row.RawRelevances);
        Assert.True(row.HasOwnerData);
        Assert.True(row.HasRelevanceData);
        Assert.Equal(0, row.RelevanceRank);
    }

    [Fact]
    public void A_model_nobody_classified_has_a_dash_for_its_owners_and_unknown_for_its_relevance()
    {
        var row = RowOf(Signal(model: Model("plain")));

        Assert.Equal("—", row.OwnersDisplay);
        Assert.False(row.HasOwnerData);
        Assert.Equal("Unknown", row.RelevanceDisplay);
        Assert.Equal(DiscoveryRelevance.Unknown, row.EffectiveRelevance);
        Assert.False(row.HasRelevanceData);
        Assert.False(row.HasClassificationMetadata);
        Assert.Null(row.ReportedDiscoveryConfidence);
        Assert.Null(row.Provenance);
        Assert.False(row.HasProvenance);
    }

    [Theory]
    [InlineData("primary", DiscoveryRelevance.Primary)]
    [InlineData(" Supporting ", DiscoveryRelevance.Supporting)]
    [InlineData("EMBEDDED", DiscoveryRelevance.Embedded)]
    [InlineData("unknown", DiscoveryRelevance.Unknown)]
    [InlineData("central", DiscoveryRelevance.Unknown)]
    [InlineData("", DiscoveryRelevance.Unknown)]
    [InlineData(null, DiscoveryRelevance.Unknown)]
    public void A_relevance_word_is_one_of_four_classes(string? raw, DiscoveryRelevance expected) =>
        Assert.Equal(expected, DiscoveryClasses.ClassifyRelevance(raw));

    [Fact]
    public void The_highest_reported_confidence_of_the_models_signals_is_the_models_and_a_row_keeps_the_stronger_lineage()
    {
        var weak = Provenance(publisher: "Mirror", source: "model_id", confidence: "low");
        var strong = Provenance(publisher: "Example Labs", country: "US", root: "example-labs/chat-3b", source: "catalog_exact", confidence: "high");

        var row = RowOf(
            Signal(detector: "model_api", product: "Ollama", model: Model("m", discoveryConfidence: 0.7, provenance: weak)),
            Signal(detector: "model_runtime", product: "Lemonade", model: Model("M", discoveryConfidence: 0.9, provenance: strong)),
            Signal(detector: "model_file", model: Model("m")));

        Assert.Equal(0.9, row.ReportedDiscoveryConfidence);
        Assert.Equal(strong, row.Provenance);
        Assert.True(row.HasProvenance);
        Assert.Equal("Example Labs", row.ProvenanceFacts[0].Value);
    }

    [Fact]
    public void The_inspectors_facts_gain_the_new_rows_only_where_the_data_has_them_and_in_a_stable_place()
    {
        var row = RowOf(Signal(
            model: Model("m", modality: "text", owner: "Example Notes", relevance: "primary", discoveryConfidence: 0.93) with { Pinned = true, SizeBytes = 2_000_000_000 }));

        Assert.Equal(
            new[] { "State", "Modality", "Relevance", "Owners", "Status", "Format", "Provider", "Size", "Pinned", "Discovery confidence", "Products", "Vendors", "Sources" },
            row.Facts.Select(f => f.Label).ToArray());
        Assert.Equal("93%", row.Facts.Single(f => f.Label == "Discovery confidence").Value);
        Assert.Equal("Example Notes", row.Facts.Single(f => f.Label == "Owners").Value);
        Assert.Equal("Primary", row.Facts.Single(f => f.Label == "Relevance").Value);
    }

    [Fact]
    public void A_model_is_found_by_the_words_of_its_lineage_and_its_owner_whatever_their_case()
    {
        var row = RowOf(Signal(model: Model(
            "derived-model-q4",
            owner: "Example Notes",
            relevance: "supporting",
            provenance: Provenance(
                publisher: "Mistral AI", country: "FR", root: "mistralai/Mistral-7B-v0.3", bases: new[] { "mistralai/Mistral-7B-v0.3" },
                quantized: true, quantization: "Q4_K_M", derivation: "quantized", source: "model_id", confidence: "medium"))));

        foreach (var query in new[] { "FR", "fr", "France", "Mistral AI", "mistral ai", "Mistral-7B", "Q4_K_M", "q4_k_m", "model_id", "medium", "quantized", "example notes", "supporting", "filesystem" })
        {
            Assert.True(row.Matches(query), query);
        }

        Assert.False(row.Matches("Anthropic"));
        Assert.False(row.Matches("Germany"));
    }

    [Fact]
    public void A_country_the_catalog_names_is_found_by_its_name_too_and_one_it_does_not_by_its_code_alone()
    {
        var row = RowOf(Signal(model: Model("m", provenance: Provenance(publisher: "P", country: "US", root: "r/m", source: "catalog_exact", confidence: "high"))));

        Assert.True(row.Matches("United States"));
        Assert.True(row.Matches("US"));
        Assert.False(row.Matches("Canada"));

        var unlisted = RowOf(Signal(model: Model("n", provenance: Provenance(publisher: "P", country: "ZZ", root: "r/n", source: "catalog_exact", confidence: "high"))));
        Assert.True(unlisted.Matches("ZZ"));
    }
}
