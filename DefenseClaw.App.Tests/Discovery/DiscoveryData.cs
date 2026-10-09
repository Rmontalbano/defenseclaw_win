using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// Signals, models and gateway answers for the CUST-310 tests, built from invented values only. The gateway's answers are the fixtures of
/// <c>runtime-95159fd/rest</c> (<c>ai-usage.json</c> is the capture of a disabled service, <c>ai-usage.populated.synthetic.json</c> is written
/// by hand from <c>internal/inventory/ai_discovery.go</c> and <c>internal/gateway/ai_usage.go</c>), and a DefenseClaw 0.8.10 answer is made
/// from its own state-file fixture the way its code makes one: the same signals as an array under <c>enabled</c> and <c>summary</c>,
/// without the evidence the 0.8.10 gateway keeps off the wire and without any of the model members a newer runtime adds.
/// </summary>
internal static class DiscoveryData
{
    /// <summary>A fixed "now" for ages: three minutes after the 0.8.10 fixture's newest signal.</summary>
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T15:30:04.1234567Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public const string ReportDisabled = "ai-usage.json";
    public const string ReportPopulated = "ai-usage.populated.synthetic.json";

    /// <summary>The fixtures the pin was checked with; the gateway's REST answers.</summary>
    public static string Rest(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "rest", name));

    public static DiscoverySignalRecord Signal(
        string product = "Local Model Artifact",
        string vendor = "Local",
        string category = "local_model",
        string detector = "model_file",
        string? state = "seen",
        double? confidence = 0.9,
        DiscoveryModelInfo? model = null,
        string? signalId = null) =>
        new(vendor, product, category, detector, "test", confidence, state, null, null, Array.Empty<DiscoveryEvidenceItem>())
        {
            Model = model,
            SignalId = signalId,
        };

    public static DiscoveryModelInfo Model(
        string id,
        string modality = "",
        string owner = "",
        string relevance = "",
        double? discoveryConfidence = null,
        DiscoveryModelProvenance? provenance = null,
        string status = "installed",
        string format = "gguf") =>
        new(id, status, format, "filesystem", string.Empty, modality, string.Empty, 0, false)
        {
            OwnerApplication = owner,
            Relevance = relevance,
            DiscoveryConfidence = discoveryConfidence,
            Provenance = provenance,
        };

    public static DiscoveryModelProvenance Provenance(
        string publisher = "",
        string country = "",
        string root = "",
        string[]? bases = null,
        bool? quantized = null,
        string quantization = "",
        bool? distilled = null,
        string derivation = "",
        string source = "",
        string confidence = "") =>
        new(publisher, country, root, bases ?? Array.Empty<string>(), quantized, quantization, distilled, derivation, source, confidence);

    /// <summary>
    /// The Mac's own helper for its filter tests (<c>modelRow</c> in <c>Tests/AIDiscoveryModelTests.swift</c>): one signal of the detector
    /// named, from "Meetily", with the owner "Meetily" unless the model says nothing at all or the owner is given.
    /// </summary>
    public static DiscoveryModelRow MacRow(
        string id,
        string modality,
        string relevance,
        double? confidence,
        double signalConfidence = 0.9,
        string detector = "model_file",
        string? owner = null)
    {
        var resolvedOwner = owner ?? (modality.Length == 0 && relevance.Length == 0 && confidence is null ? string.Empty : "Meetily");
        var signal = Signal(
            product: "Meetily",
            vendor: "Local",
            detector: detector,
            confidence: signalConfidence,
            model: Model(id, modality, resolvedOwner, relevance, confidence));
        return Assert.Single(DiscoveryModelRow.Build(new[] { signal }, Now));
    }

    /// <summary>The signals of a gateway answer, read the way the panel reads them.</summary>
    public static IReadOnlyList<DiscoverySignalRecord> SignalsOf(string reportJson, DiscoveryReadOptions options = default)
    {
        var read = AiUsageReader.ParseText(reportJson, options);
        Assert.True(read.IsOk, read.Message);
        return read.Snapshot!.Signals;
    }

    /// <summary>
    /// The state file a gateway would have written for the same scan as <paramref name="reportJson"/> (signals keyed by fingerprint, the
    /// gone ones left out, as the scanner leaves them out). With <paramref name="withNewerModelFields"/> false it is the file of a
    /// runtime that does not send owner, relevance, discovery confidence or lineage: those four members are taken off every model and the
    /// per-signal scores the report alone carries are dropped, so what the file lacks is exactly what the report can add.
    /// </summary>
    public static string StateFileOf(string reportJson, bool withNewerModelFields)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        var signals = new JsonObject();
        foreach (var node in report["signals"]!.AsArray())
        {
            var signal = node!.AsObject();
            if ((string?)signal["state"] == "gone")
            {
                continue;
            }

            var copy = (JsonObject)signal.DeepClone();
            foreach (var score in new[] { "identity_score", "identity_band", "presence_score", "presence_band" })
            {
                _ = copy.Remove(score);
            }

            if (!withNewerModelFields && copy["model"] is JsonObject model)
            {
                foreach (var member in new[] { "owner_application", "relevance", "discovery_confidence", "provenance" })
                {
                    _ = model.Remove(member);
                }
            }

            signals[(string)signal["fingerprint"]!] = copy;
        }

        return new JsonObject
        {
            ["version"] = 2,
            ["updated_at"] = (string?)report["summary"]!["scanned_at"],
            ["signals"] = signals,
        }.ToJsonString();
    }

    /// <summary>
    /// What a DefenseClaw 0.8.10 gateway answers for the signals of <paramref name="stateJson"/> (a 0.8.10 state file): the same signals as
    /// an array, <c>enabled</c> and a <c>summary</c>, the evidence kept off the wire, identity and presence scores on the signals that
    /// have a component, and none of the members a newer runtime adds - nor <c>lookup_model_provenance_online</c>.
    /// </summary>
    public static string ReportOf0810(string stateJson)
    {
        var state = JsonNode.Parse(stateJson)!.AsObject();
        var signals = new JsonArray();
        foreach (var (_, node) in state["signals"]!.AsObject())
        {
            var signal = (JsonObject)node!.DeepClone();
            _ = signal.Remove("evidence");
            _ = signal.Remove("evidence_hash");
            if (signal["component"] is not null)
            {
                signal["identity_score"] = 0.86;
                signal["identity_band"] = "high";
                signal["presence_score"] = 0.31;
                signal["presence_band"] = "low";
            }

            signals.Add(signal);
        }

        return new JsonObject
        {
            ["enabled"] = true,
            ["signals"] = signals,
            ["summary"] = new JsonObject
            {
                ["scan_id"] = "synthetic-scan",
                ["scanned_at"] = (string?)state["updated_at"],
                ["result"] = "ok",
                ["total_signals"] = signals.Count,
                ["active_signals"] = signals.Count,
            },
        }.ToJsonString();
    }

    /// <summary>The report of <paramref name="reportJson"/> with its <c>lookup_model_provenance_online</c> set to <paramref name="value"/> (null removes it).</summary>
    public static string WithLookup(string reportJson, bool? value)
    {
        var report = JsonNode.Parse(reportJson)!.AsObject();
        _ = report.Remove("lookup_model_provenance_online");
        if (value is { } flag)
        {
            report["lookup_model_provenance_online"] = flag;
        }

        return report.ToJsonString();
    }

    /// <summary>Reads a JSON text and checks it is valid; a helper for tests that build their own answers.</summary>
    public static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    /// <summary>The state fixtures of the 0.8.10 panel tests, as text.</summary>
    public static string State0810() => PayloadFixtures.Read("ai-discovery-state.0.8.10.json");
}
