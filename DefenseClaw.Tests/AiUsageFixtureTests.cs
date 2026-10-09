using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The two fixtures of the gateway's <c>GET /api/v1/ai-usage</c> under <c>runtime-95159fd/rest</c> (CUST-310). <c>ai-usage.json</c> is the
/// Docker capture of the pinned source's answer for a service that is off, byte for byte (line endings aside).
/// <c>ai-usage.populated.synthetic.json</c> is not a capture: no populated report exists from a Windows run, so it is written by hand from
/// the emitting code - <c>handleAIUsage</c> in <c>internal/gateway/ai_usage.go</c> (the four members, their order, the disabled answer),
/// <c>AISignal</c>, <c>LocalModelInfo</c> and <c>LocalModelProvenance</c> in <c>internal/inventory/ai_discovery.go</c> (every member and its
/// JSON name), and <c>AIDiscoverySummary</c>. Because nothing can compare it with a live answer, these tests hold it to the rules the same
/// source applies to a report it is given (<c>ValidateSanitizedAIDiscoveryReport</c> and <c>validateLocalModelProvenance</c>), so a
/// hand-written value that the source would refuse cannot sit in it. Nothing here starts a process or opens a socket.
/// </summary>
public class AiUsageFixtureTests
{
    private const string Disabled = "ai-usage.json";
    private const string Populated = "ai-usage.populated.synthetic.json";

    private static JsonDocument Open(string name) => JsonDocument.Parse(RuntimeFixtures.Read("rest/" + name));

    private static IEnumerable<JsonElement> Signals(JsonElement root) => root.GetProperty("signals").EnumerateArray();

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static readonly Regex Digest = new("^(sha256|hmac-sha256):[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    // ---- the capture ----

    [Fact]
    public void The_capture_is_the_answer_of_a_service_that_is_off_with_the_members_in_the_order_the_handler_writes_them()
    {
        var text = RuntimeFixtures.Read("rest/" + Disabled);

        // A Go map is written with its keys sorted, and the encoder ends the answer with a newline.
        Assert.Equal("""{"enabled":false,"lookup_model_provenance_online":false,"signals":[],"summary":{"result":"disabled"}}""", text.TrimEnd('\r', '\n'));

        using var document = Open(Disabled);
        Assert.Equal(new[] { "enabled", "lookup_model_provenance_online", "signals", "summary" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("signals").ValueKind);
        Assert.Equal(0, document.RootElement.GetProperty("signals").GetArrayLength());
    }

    // ---- the populated fixture: the shape of the handler's answer ----

    [Fact]
    public void The_populated_answer_has_the_four_members_of_an_enabled_service_and_its_summary_agrees_with_its_signals()
    {
        using var document = Open(Populated);
        var root = document.RootElement;

        Assert.Equal(new[] { "enabled", "lookup_model_provenance_online", "signals", "summary" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.True(root.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.False, root.GetProperty("lookup_model_provenance_online").ValueKind);

        // AIDiscoverySummary, in the struct's order; the ones the answer has are the ones with no omitempty plus the maps it fills.
        var summary = root.GetProperty("summary");
        Assert.Equal(
            new[]
            {
                "scan_id", "scanned_at", "duration_ms", "privacy_mode", "source", "result", "total_signals", "active_signals", "new_signals",
                "changed_signals", "gone_signals", "files_scanned", "dedupe_suppressed", "errors", "detector_durations_ms",
            },
            summary.EnumerateObject().Select(p => p.Name).ToArray());

        var signals = Signals(root).ToArray();
        var states = signals.Select(s => Text(s, "state")).ToArray();
        Assert.Equal(signals.Length, summary.GetProperty("total_signals").GetInt32());
        Assert.Equal(signals.Length - states.Count(s => s == "gone"), summary.GetProperty("active_signals").GetInt32());
        Assert.Equal(states.Count(s => s == "new"), summary.GetProperty("new_signals").GetInt32());
        Assert.Equal(states.Count(s => s == "changed"), summary.GetProperty("changed_signals").GetInt32());
        Assert.Equal(states.Count(s => s == "gone"), summary.GetProperty("gone_signals").GetInt32());
        Assert.True(DateTimeOffset.TryParse(Text(summary, "scanned_at"), out _));
    }

    [Fact]
    public void The_signals_are_in_the_order_the_scanner_sorts_them_and_each_has_the_members_it_always_writes()
    {
        using var document = Open(Populated);
        var signals = Signals(document.RootElement).ToArray();

        // sortAISignals: category, vendor, product, fingerprint, joined.
        var keys = signals.Select(s => Text(s, "category") + Text(s, "vendor") + Text(s, "product") + Text(s, "fingerprint")).ToArray();
        Assert.Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), keys);

        foreach (var signal in signals)
        {
            foreach (var member in new[]
                     {
                         "fingerprint", "signal_id", "signature_id", "name", "vendor", "product", "category", "confidence", "state", "detector",
                         "source", "first_seen", "last_seen",
                     })
            {
                Assert.True(signal.TryGetProperty(member, out _), $"{Text(signal, "signal_id")} lacks {member}");
            }

            Assert.Matches("^ai-[0-9a-f]{16}$", Text(signal, "signal_id")!);
            Assert.Matches(Digest, Text(signal, "fingerprint")!);
            Assert.Contains(Text(signal, "state"), new[] { "new", "seen", "changed", "gone" });
            Assert.InRange(signal.GetProperty("confidence").GetDouble(), 0, 1);
            Assert.True(DateTimeOffset.TryParse(Text(signal, "first_seen"), out _));
            Assert.True(DateTimeOffset.TryParse(Text(signal, "last_seen"), out _));
        }

        Assert.Equal(signals.Length, signals.Select(s => Text(s, "signal_id")).Distinct().Count());
        Assert.Equal(signals.Length, signals.Select(s => Text(s, "fingerprint")).Distinct().Count());
    }

    // ---- the rules the emitting source applies to a report it is given ----

    private static readonly string[] Categories =
    {
        "supported_connector", "ai_cli", "active_process", "editor_extension", "mcp_server", "skill", "rule", "plugin", "package_dependency",
        "env_var_name", "shell_history_match", "provider_domain", "workspace_artifact", "desktop_app", "local_ai_endpoint", "local_model",
    };

    [Fact]
    public void Every_signal_passes_what_ValidateSanitizedAIDiscoveryReport_asks_of_it()
    {
        using var document = Open(Populated);
        var signals = Signals(document.RootElement).ToArray();

        Assert.True(signals.Length <= 4096);
        foreach (var signal in signals)
        {
            var category = Text(signal, "category")!;
            Assert.Contains(category, Categories);

            // A local_model signal carries a model block and nothing else does.
            Assert.Equal(category == "local_model", signal.TryGetProperty("model", out _));

            foreach (var hash in signal.TryGetProperty("path_hashes", out var hashes) ? hashes.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                Assert.Matches(Digest, hash.GetString()!);
            }

            if (Text(signal, "workspace_hash") is { Length: > 0 } workspace)
            {
                Assert.Matches(Digest, workspace);
            }

            foreach (var basename in signal.TryGetProperty("basenames", out var basenames) ? basenames.EnumerateArray() : Enumerable.Empty<JsonElement>())
            {
                Assert.DoesNotMatch(@"[/\\]", basename.GetString()!);
            }

            var evidence = signal.TryGetProperty("evidence", out var rows) ? rows.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            Assert.True(evidence.Length <= 256);
            foreach (var row in evidence)
            {
                foreach (var digest in new[] { "path_hash", "value_hash", "workspace_hash" })
                {
                    if (Text(row, digest) is { Length: > 0 } value)
                    {
                        Assert.Matches(Digest, value);
                    }
                }

                if (Text(row, "basename") is { } basename)
                {
                    Assert.DoesNotMatch(@"[/\\]", basename);
                }

                // The gateway clears raw_path from every row before it answers (SanitizeEvidenceForWire), whatever the config says.
                Assert.False(row.TryGetProperty("raw_path", out _));
                Assert.Contains(Text(row, "match_kind"), new[] { "exact", "substring", "heuristic" });
            }

            if (signal.TryGetProperty("model", out var model))
            {
                ValidateModel(model);
            }
        }
    }

    private static void ValidateModel(JsonElement model)
    {
        var id = Text(model, "id")!;
        Assert.InRange(id.Trim().Length, 1, 512);
        Assert.DoesNotMatch(@"\p{Cc}", id);
        Assert.Contains(Text(model, "status"), new[] { "installed", "loaded" });

        foreach (var (member, max) in new[] { ("format", 64), ("provider", 96), ("recipe", 128), ("modality", 64), ("device", 128), ("owner_application", 96) })
        {
            if (Text(model, member) is { } value)
            {
                Assert.True(value.Length <= max, member);
                Assert.DoesNotMatch(@"\p{Cc}", value);
            }
        }

        if (model.TryGetProperty("size_bytes", out var size))
        {
            Assert.True(size.GetInt64() >= 0);
        }

        // owner_application is an application's name, never a place on the disk.
        Assert.DoesNotMatch(@"[/\\]", Text(model, "owner_application") ?? string.Empty);
        if (Text(model, "relevance") is { } relevance)
        {
            Assert.Contains(relevance, new[] { "primary", "supporting", "embedded", "unknown" });
        }

        if (model.TryGetProperty("discovery_confidence", out var confidence))
        {
            Assert.InRange(confidence.GetDouble(), 0, 1);
        }

        if (model.TryGetProperty("provenance", out var provenance))
        {
            ValidateProvenance(provenance);
        }
    }

    /// <summary>The checks of <c>validateLocalModelProvenance</c>, in the order the source makes them.</summary>
    private static void ValidateProvenance(JsonElement provenance)
    {
        foreach (var (member, max) in new[] { ("publisher", 128), ("root_model", 512), ("quantization", 64), ("derivation", 64), ("source", 64) })
        {
            if (Text(provenance, member) is { } value)
            {
                Assert.True(value.Length <= max, member);
            }
        }

        var country = Text(provenance, "country_code") ?? string.Empty;
        if (country.Length > 0)
        {
            // Only the countries of the embedded, reviewed publisher catalog, and only with a publisher and a root model.
            Assert.Contains(country, new[] { "AE", "CA", "CN", "DE", "FR", "GB", "US" });
            Assert.False(string.IsNullOrEmpty(Text(provenance, "publisher")));
            Assert.False(string.IsNullOrEmpty(Text(provenance, "root_model")));
        }

        var bases = provenance.TryGetProperty("base_models", out var list) ? list.EnumerateArray().Select(b => b.GetString()!).ToArray() : Array.Empty<string>();
        Assert.True(bases.Length <= 8);
        Assert.Equal(bases.Length, bases.Select(b => b.ToLowerInvariant()).Distinct().Count());
        Assert.All(bases, b => Assert.False(string.IsNullOrWhiteSpace(b)));

        // A lineage states a claim, a source and a confidence...
        Assert.NotEmpty(provenance.EnumerateObject());
        Assert.Contains(Text(provenance, "source"), new[] { "catalog_exact", "catalog_family", "hf_cache", "gguf_metadata", "model_config", "ollama_metadata", "checkpoint", "model_id", "huggingface_hub", "mixed" });
        Assert.Contains(Text(provenance, "confidence"), new[] { "high", "medium", "low" });

        // ...and its derivation agrees with its flags (modelDerivation: "distilled", "quantized", or "distilled+quantized").
        bool? Flag(string name) => provenance.TryGetProperty(name, out var value) ? value.GetBoolean() : null;
        var expected = string.Join('+', new[] { Flag("distilled") == true ? "distilled" : null, Flag("quantized") == true ? "quantized" : null }.OfType<string>());
        Assert.Equal(expected, Text(provenance, "derivation") ?? string.Empty);
    }

    [Fact]
    public void The_fixture_shows_each_case_the_panel_has_a_rule_for()
    {
        using var document = Open(Populated);
        var models = Signals(document.RootElement).Where(s => s.TryGetProperty("model", out _)).Select(s => (Signal: s, Model: s.GetProperty("model"))).ToArray();

        // A model seen by two detectors under two spellings of its id.
        Assert.Contains(models, m => Text(m.Model, "id") == "Example-Chat-3B-Q4");
        Assert.Contains(models, m => Text(m.Model, "id") == "example-chat-3b-q4");

        // Listed by a model server with no confidence of its own (a reported zero would be one), and by files with one.
        Assert.Contains(models, m => Text(m.Signal, "detector") == "model_api" && !m.Model.TryGetProperty("discovery_confidence", out _));
        Assert.Contains(models, m => m.Model.TryGetProperty("discovery_confidence", out _));

        // Every relevance, the supporting speech model with an owner and the one without, the one under the floor and on it.
        foreach (var relevance in new[] { "primary", "supporting", "embedded", "unknown" })
        {
            Assert.Contains(models, m => Text(m.Model, "relevance") == relevance);
        }

        Assert.Contains(models, m => Text(m.Model, "modality") == "speech" && Text(m.Model, "relevance") == "supporting" && Text(m.Model, "owner_application") is { Length: > 0 });
        Assert.Contains(models, m => Text(m.Model, "modality") == "speech" && Text(m.Model, "relevance") == "supporting" && !m.Model.TryGetProperty("owner_application", out _));
        Assert.Contains(models, m => m.Model.TryGetProperty("discovery_confidence", out var c) && c.GetDouble() < 0.8);
        Assert.Contains(models, m => m.Model.TryGetProperty("discovery_confidence", out var c) && c.GetDouble() == 0.8);

        // Lineage: complete, a merge with no single root, the flags positively "no", and absent.
        Assert.Contains(models, m => m.Model.TryGetProperty("provenance", out var p) && Text(p, "source") == "catalog_exact" && Text(p, "confidence") == "high");
        Assert.Contains(models, m => m.Model.TryGetProperty("provenance", out var p) && !p.TryGetProperty("root_model", out _) && p.GetProperty("base_models").GetArrayLength() == 2);
        Assert.Contains(models, m => m.Model.TryGetProperty("provenance", out var p) && p.TryGetProperty("distilled", out var d) && d.ValueKind == JsonValueKind.False);
        Assert.Contains(models, m => !m.Model.TryGetProperty("provenance", out _));

        // A signal the gateway reports gone, which the state file would not carry.
        Assert.Contains(Signals(document.RootElement), s => Text(s, "state") == "gone");
    }
}
