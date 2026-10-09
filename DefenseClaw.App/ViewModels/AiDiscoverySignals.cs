using System.Globalization;
using System.Text.Json;
using DefenseClaw.Core.Inventory;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// A signal's lifecycle state as the sidecar words it - <c>new</c>, <c>seen</c> (the quiet "still there, unchanged"), <c>changed</c>,
/// <c>gone</c>; the older Go TUI also said <c>active</c> - and what the panel does with it: the order the TUI sorts by
/// (<c>state_weight</c>: new, changed, active, seen, gone, anything else) and a tone for the pill. A state this app has not heard of
/// sorts last and is shown as it is, in the neutral tone; a signal with no state at all gets no pill.
/// <para>
/// <b>Where each state can be seen.</b> <c>ai_discovery_state.json</c> and <c>inventory.db</c> only ever hold the signals still present
/// (new, changed, seen): the scanner drops a <c>gone</c> signal when it writes either file, so a gone signal exists only in the
/// gateway's report of the scan that noticed it (<c>GET /api/v1/ai-usage</c>). The panel reads that report (CUST-310) only to add the
/// model fields a newer runtime sends to the signals the files list, so it takes no gone signal from it: a gone count can come only from
/// a scan summary, and no card or model row is gone; the ordering and the pill handle every state anyway.
/// </para>
/// </summary>
public static class DiscoveryStates
{
    public const string New = "new";
    public const string Changed = "changed";
    public const string Active = "active";
    public const string Seen = "seen";
    public const string Gone = "gone";

    /// <summary>The weight of a state that is none of the five (the TUI's 9): after all of them.</summary>
    public const int UnknownWeight = 9;

    /// <summary>The state trimmed and lower-cased, or empty when there is none.</summary>
    public static string Normalize(string? state) =>
        string.IsNullOrWhiteSpace(state) ? string.Empty : state.Trim().ToLowerInvariant();

    /// <summary>The TUI's <c>state_weight</c>: new 0, changed 1, active 2, seen 3, gone 4, anything else 9.</summary>
    public static int Weight(string? state) => Normalize(state) switch
    {
        New => 0,
        Changed => 1,
        Active => 2,
        Seen => 3,
        Gone => 4,
        _ => UnknownWeight,
    };

    /// <summary>The design system's tone key for a state's pill: news is the accent, a change is amber, active is green, the rest are quiet.</summary>
    public static string Tone(string? state) => Normalize(state) switch
    {
        New => "Medium",
        Changed => "Warn",
        Active => "Ok",
        Gone => "Low",
        _ => "Neutral",
    };

    /// <summary>The strongest of several states (lowest weight; the first on a tie), or empty when none of them says anything.</summary>
    public static string Strongest(IEnumerable<string?> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var best = string.Empty;
        foreach (var state in states)
        {
            var normalized = Normalize(state);
            if (normalized.Length > 0 && (best.Length == 0 || Weight(normalized) < Weight(best)))
            {
                best = normalized;
            }
        }

        return best;
    }
}

/// <summary>The process a discovery signal was found in (<c>runtime</c> of a <c>process</c> signal). The gateway never sends the command line.</summary>
/// <param name="Pid">The process id; the TUI shows the line only for a positive one.</param>
/// <param name="UptimeSeconds">Seconds since the process started, 0 when the scanner did not say.</param>
/// <param name="User">The account the process runs as.</param>
/// <param name="Command">The executable name (<c>comm</c>).</param>
public sealed record DiscoveryRuntimeInfo(int Pid, long UptimeSeconds, string User, string Command)
{
    /// <summary>The TUI's <c>runtime: pid=N user=U up=3h12m comm=X</c> line, or empty when there is no process id to show.</summary>
    public string Detail()
    {
        if (Pid <= 0)
        {
            return string.Empty;
        }

        var parts = new List<string> { "runtime: pid=" + Pid.ToString(CultureInfo.InvariantCulture) };
        if (User.Length > 0)
        {
            parts.Add("user=" + User);
        }

        if (UptimeSeconds > 0)
        {
            parts.Add("up=" + DiscoveryFormat.Age(TimeSpan.FromSeconds(UptimeSeconds)));
        }

        if (Command.Length > 0)
        {
            parts.Add("comm=" + Command);
        }

        return string.Join(' ', parts);
    }
}

/// <summary>The package a signal resolved to (<c>component</c>; <c>package_manifest</c> signals): <c>openai==1.45.0</c> rather than "an AI SDK".</summary>
public sealed record DiscoveryComponentRef(string Ecosystem, string Name, string Version, string Framework)
{
    /// <summary>The TUI's component column: <c>name (ecosystem)</c>, or just the name.</summary>
    public string Label => Ecosystem.Length > 0 && Name.Length > 0 ? $"{Name} ({Ecosystem})" : Name;
}

/// <summary>
/// The <c>model</c> block of a signal: an AI model the scanner found on disk (<c>model_file</c>) or listed by a local model server
/// (<c>model_api</c>, <c>model_runtime</c>). The positional members are the fields DefenseClaw 0.8.10's own <c>AIUsageModel</c> reads. Model
/// ids live here and not in the product, because they are user-controlled and unbounded. A field the scanner did not report is empty
/// (text), 0 (size) or false (pinned) - never made up.
/// <para>
/// The four <c>init</c> members are what a newer runtime adds to the block (<c>LocalModelInfo</c> in its
/// <c>internal/inventory/ai_discovery.go</c>, DefenseClaw source commit 95159fd): the application the model belongs to, how central it
/// is to that application, how sure the scanner is of it, and its lineage. 0.8.10 sends none of them (its own reader does not know them,
/// and its gateway binary has no such member names), so on 0.8.10 they stay empty and null, which is what keeps its view as it was: a
/// column or a filter built on one appears only when some model carries it. The state file, <c>inventory.db</c> and the gateway's
/// <c>GET /api/v1/ai-usage</c> all carry the same block, and all are read the same way.
/// </para>
/// </summary>
/// <param name="Id">The model's name; a block with none is not a model this panel can list.</param>
/// <param name="Status"><c>installed</c> or <c>loaded</c>.</param>
/// <param name="Modality">As the scanner worded it (<c>text</c>, <c>speech</c> ...); see <see cref="DiscoveryModality"/> for the classes.</param>
public sealed record DiscoveryModelInfo(
    string Id,
    string Status,
    string Format,
    string Provider,
    string Recipe,
    string Modality,
    string Device,
    long SizeBytes,
    bool Pinned)
{
    /// <summary>The application the model belongs to (<c>owner_application</c>), when the scanner could attribute it. Empty when it did not.</summary>
    public string OwnerApplication { get; init; } = string.Empty;

    /// <summary>
    /// How central the model is to its owner (<c>relevance</c>): <c>primary</c>, <c>supporting</c>, <c>embedded</c> or <c>unknown</c>, as the
    /// scanner worded it. Empty when the runtime reports none; see <see cref="DiscoveryRelevance"/> for the classes.
    /// </summary>
    public string Relevance { get; init; } = string.Empty;

    /// <summary>
    /// How sure the scanner is that this is a model worth listing (<c>discovery_confidence</c>), 0..1. Null when the runtime did not say:
    /// that is not zero, and a model server's listing of its models carries none by design. An explicit 0 is 0.
    /// </summary>
    public double? DiscoveryConfidence { get; init; }

    /// <summary>The model's lineage (<c>provenance</c>); null when the runtime sent none.</summary>
    public DiscoveryModelProvenance? Provenance { get; init; }

    /// <summary>True when the block carries any of what the Mac's recommended scope acts on (<c>hasModelClassificationMetadata</c>): a discovery confidence, an owner or a relevance.</summary>
    public bool HasClassification =>
        DiscoveryConfidence is not null || OwnerApplication.Trim().Length > 0 || Relevance.Trim().Length > 0;

    /// <summary>
    /// The TUI's <c>model: id=... status=... format=...</c> line: only the fields the block has, in the TUI's order. The size is in
    /// words (<c>1.5 GiB</c>) where the TUI prints the byte count.
    /// </summary>
    public string Detail()
    {
        var parts = new List<string> { "model: id=" + (Id.Length > 0 ? Id : "(unknown)") };
        void Add(string label, string value)
        {
            if (value.Length > 0)
            {
                parts.Add(label + "=" + value);
            }
        }

        Add("status", Status);
        Add("format", Format);
        Add("provider", Provider);
        Add("recipe", Recipe);
        Add("modality", Modality);
        Add("relevance", Relevance);
        Add("owner", OwnerApplication);
        Add("device", Device);
        Add("size", DiscoveryFormat.Bytes(SizeBytes));
        if (Pinned)
        {
            parts.Add("pinned=true");
        }

        if (DiscoveryConfidence is { } confidence)
        {
            parts.Add("discovery_confidence=" + DiscoveryFormat.Percent(confidence).ToString(CultureInfo.InvariantCulture) + "%");
        }

        return string.Join(' ', parts);
    }
}

/// <summary>Small formatting helpers shared by the discovery view-models; the TUI's <c>format_confidence</c>, <c>humanize_age</c> and <c>format_csv_truncated</c>.</summary>
internal static class DiscoveryFormat
{
    private static readonly string[] ByteUnits = { "B", "KiB", "MiB", "GiB", "TiB" };

    /// <summary>A score on 0..1 (anything else is pulled into it).</summary>
    public static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    /// <summary>A 0..1 score as a whole percent, halves rounding up (the TUI's <c>int(score * 100 + 0.5)</c>).</summary>
    public static int Percent(double unit) => (int)Math.Floor((Clamp01(unit) * 100) + 0.5);

    /// <summary>A confidence band the way a person reads it: <c>very_high</c> becomes "very high".</summary>
    public static string Band(string? band) => (band ?? string.Empty).Trim().Replace('_', ' ');

    /// <summary>The TUI's <c>format_confidence</c>: "high (85%)", "85%" for a score with no band, empty for neither.</summary>
    public static string Confidence(double? score, string? band)
    {
        var word = Band(band);
        var value = score ?? 0;
        if (word.Length == 0 && value == 0)
        {
            return string.Empty;
        }

        var percent = Percent(value).ToString(CultureInfo.InvariantCulture);
        return word.Length == 0 ? percent + "%" : $"{word} ({percent}%)";
    }

    /// <summary>The TUI's <c>humanize_age</c>: <c>45s</c>, <c>12m</c>, <c>3h5m</c>, <c>2d4h</c>; the sign is dropped.</summary>
    public static string Age(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            delta = delta.Negate();
        }

        var seconds = (long)delta.TotalSeconds;
        string N(long value) => value.ToString(CultureInfo.InvariantCulture);

        if (seconds < 1)
        {
            return "0s";
        }

        if (seconds < 60)
        {
            return N(seconds) + "s";
        }

        var minutes = seconds / 60;
        if (minutes < 60)
        {
            return N(minutes) + "m";
        }

        var hours = minutes / 60;
        if (hours < 24)
        {
            var rest = minutes - (hours * 60);
            return rest == 0 ? N(hours) + "h" : N(hours) + "h" + N(rest) + "m";
        }

        var days = hours / 24;
        var restHours = hours % 24;
        return restHours == 0 ? N(days) + "d" : N(days) + "d" + N(restHours) + "h";
    }

    /// <summary>The TUI's <c>format_csv_truncated</c>: "a, b (+3)" - the first <paramref name="limit"/> and how many more there are.</summary>
    public static string CsvTruncated(IReadOnlyList<string> items, int limit = 2)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (limit <= 0 || limit >= items.Count)
        {
            return string.Join(", ", items);
        }

        return $"{string.Join(", ", items.Take(limit))} (+{(items.Count - limit).ToString(CultureInfo.InvariantCulture)})";
    }

    /// <summary>A byte count in words (<c>1.5 GiB</c>); empty for none, because 0 here means "the scanner did not say".</summary>
    public static string Bytes(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
            : value.ToString("0.#", CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
    }

    /// <summary>"last active: 3m ago", else "last seen: 3m ago", else empty (the TUI's last detail line of a signal).</summary>
    public static string Activity(DateTimeOffset? lastActive, DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (lastActive is { } active)
        {
            return "last active: " + Age(now - active) + " ago";
        }

        return lastSeen is { } seen ? "last seen: " + Age(now - seen) + " ago" : string.Empty;
    }
}

/// <summary>
/// Reads one signal from the three places the app finds them: a member of <c>ai_discovery_state.json</c>'s <c>signals</c> object, a
/// row of <c>inventory.db</c>'s <c>ai_signals</c>, or an element of the <c>signals</c> array of the gateway's <c>GET /api/v1/ai-usage</c>
/// (the same signal the state file keeps, without its stored extras). All are read as tolerantly as the TUI's
/// <c>AIUsageSignal.from_mapping</c>: a value of the wrong type is a missing value, never an exception, and a block the scanner did not
/// send (<c>model</c>, <c>runtime</c>, <c>component</c>) is null.
/// <para>
/// <b>What each source carries</b> (checked 2026-10-08 against a live 0.8.10 install and its installed TUI source): the state file has
/// every field below; <c>inventory.db</c> has the ids, the component columns, <c>last_active_at</c> and the <c>model_json</c> /
/// <c>runtime_json</c> blocks, but no <c>first_seen</c>, <c>source</c> or <c>version</c>. Neither has a <c>gone</c> signal (the scanner
/// drops those when it writes either file) or identity and presence scores: <c>inventory.db</c> has them per component, in
/// <c>ai_confidence_snapshots</c> (see <c>AiDiscoveryPanelViewModel.ApplyConfidenceBandsAsync</c>), and the gateway's answer has them
/// per signal. This panel takes neither the gone signals nor the per-signal scores from the gateway's answer (see
/// <see cref="DiscoveryUsageOverlay"/>: it adds the model fields below to the signals the files list, and nothing else).
/// </para>
/// <para>
/// <b>The model block</b> is read for what 0.8.10's <c>AIUsageModel</c> reads, plus what a newer runtime adds to it: <c>owner_application</c>,
/// <c>relevance</c>, <c>discovery_confidence</c> and <c>provenance</c>, taken when the block has them and left empty when it does not -
/// never filled in, and never taken from a version number. Text among them that names a place on this machine is withheld unless the
/// runtime says its store keeps raw local paths (<see cref="DiscoveryReadOptions"/>).
/// </para>
/// </summary>
internal static class DiscoverySignalParser
{
    /// <summary>A lineage lists at most eight base models (<c>maxModelBaseModels</c> in the runtime); a ninth is not read.</summary>
    private const int MaxBaseModels = 8;

    /// <summary>The longest text kept of an owner or a lineage field: the runtime's own longest, a model id (<c>maxLocalModelIDBytes</c>).</summary>
    private const int PinTextLimit = 512;

    // ---- the state file --------------------------------------------------------------------------------------

    public static DiscoverySignalRecord FromState(JsonElement element, DiscoveryReadOptions options = default)
    {
        var evidence = new List<DiscoveryEvidenceItem>();
        if (element.TryGetProperty("evidence", out var evidenceArray) && evidenceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in evidenceArray.EnumerateArray())
            {
                evidence.Add(new DiscoveryEvidenceItem(
                    Text(item, "type") ?? "unknown",
                    Text(item, "basename"),
                    Number(item, "quality"),
                    Text(item, "match_kind")));
            }
        }

        return new DiscoverySignalRecord(
            Text(element, "vendor") ?? string.Empty,
            Text(element, "product") ?? string.Empty,
            Text(element, "category"),
            Text(element, "detector"),
            Text(element, "source"),
            Number(element, "confidence"),
            Text(element, "state"),
            Time(Text(element, "first_seen")),
            Time(Text(element, "last_seen")),
            evidence)
        {
            SignalId = Text(element, "signal_id"),
            SignatureId = Text(element, "signature_id"),
            Name = Text(element, "name"),
            Version = Text(element, "version"),
            Component = ParseComponent(Object(element, "component")),
            Model = ParseModel(Object(element, "model"), options),
            Runtime = ParseRuntime(Object(element, "runtime")),
            LastActiveAt = Time(Text(element, "last_active_at")),
        };
    }

    // ---- inventory.db ------------------------------------------------------------------------------------------

    public static DiscoverySignalRecord FromDbRow(IReadOnlyDictionary<string, object?> row, DiscoveryReadOptions options = default)
    {
        var evidence = new List<DiscoveryEvidenceItem>();
        if (Cell(row, "evidence_json") is { Length: > 0 } evidenceJson && TryParse(evidenceJson, out var evidenceDocument))
        {
            using (evidenceDocument)
            {
                if (evidenceDocument.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in evidenceDocument.RootElement.EnumerateArray())
                    {
                        evidence.Add(new DiscoveryEvidenceItem(
                            Text(item, "type") ?? "unknown",
                            Text(item, "basename"),
                            Number(item, "quality"),
                            Text(item, "match_kind")));
                    }
                }
            }
        }

        DiscoveryComponentRef? component = null;
        if (Cell(row, "component_name") is { Length: > 0 } componentName)
        {
            component = new DiscoveryComponentRef(
                Cell(row, "component_ecosystem") ?? string.Empty,
                componentName,
                Cell(row, "component_version") ?? string.Empty,
                Cell(row, "component_framework") ?? string.Empty);
        }

        return new DiscoverySignalRecord(
            Cell(row, "vendor") ?? string.Empty,
            Cell(row, "product") ?? string.Empty,
            Cell(row, "category"),
            Cell(row, "detector"),
            null,
            CellNumber(row, "confidence"),
            Cell(row, "state"),
            null,
            Time(Cell(row, "last_seen")),
            evidence)
        {
            SignalId = Cell(row, "signal_id"),
            SignatureId = Cell(row, "signature_id"),
            Name = Cell(row, "name"),
            Version = component?.Version is { Length: > 0 } version ? version : null,
            Component = component,
            Model = Block(Cell(row, "model_json"), element => ParseModel(element, options)),
            Runtime = Block(Cell(row, "runtime_json"), ParseRuntime),
            LastActiveAt = Time(Cell(row, "last_active_at")),
        };
    }

    // ---- blocks ----------------------------------------------------------------------------------------------

    /// <summary>The <c>model</c> block, or null when there is none (an absent, empty or non-object block).</summary>
    public static DiscoveryModelInfo? ParseModel(JsonElement element, DiscoveryReadOptions options = default)
    {
        if (!IsPopulatedObject(element))
        {
            return null;
        }

        return new DiscoveryModelInfo(
            Text(element, "id") ?? string.Empty,
            Text(element, "status") ?? string.Empty,
            Text(element, "format") ?? string.Empty,
            Text(element, "provider") ?? string.Empty,
            Text(element, "recipe") ?? string.Empty,
            Text(element, "modality") ?? string.Empty,
            Text(element, "device") ?? string.Empty,
            NonNegativeLong(element, "size_bytes"),
            Flag(element, "pinned") == true)
        {
            OwnerApplication = PinText(Text(element, "owner_application"), options),
            Relevance = PinText(Text(element, "relevance"), options),
            DiscoveryConfidence = UnitScore(element, "discovery_confidence"),
            Provenance = ParseProvenance(Object(element, "provenance"), options),
        };
    }

    /// <summary>
    /// The <c>provenance</c> block of a model, or null when there is none. Country is the two-letter code or nothing; a base model that is
    /// not a string is dropped, a single string stands for a list of one; <c>quantized</c> and <c>distilled</c> are true, false or - when
    /// the runtime did not say or said something else - unknown.
    /// </summary>
    public static DiscoveryModelProvenance? ParseProvenance(JsonElement element, DiscoveryReadOptions options = default)
    {
        if (!IsPopulatedObject(element))
        {
            return null;
        }

        var baseModels = new List<string>();
        if (element.TryGetProperty("base_models", out var bases))
        {
            if (bases.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in bases.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && baseModels.Count < MaxBaseModels
                        && PinText(item.GetString()?.Trim(), options) is { Length: > 0 } baseModel
                        && !baseModels.Contains(baseModel, StringComparer.OrdinalIgnoreCase))
                    {
                        baseModels.Add(baseModel);
                    }
                }
            }
            else if (bases.ValueKind == JsonValueKind.String && PinText(bases.GetString()?.Trim(), options) is { Length: > 0 } single)
            {
                baseModels.Add(single);
            }
        }

        return new DiscoveryModelProvenance(
            PinText(Text(element, "publisher"), options),
            DiscoveryCountries.Normalize(Text(element, "country_code")),
            PinText(Text(element, "root_model"), options),
            baseModels,
            Flag(element, "quantized"),
            PinText(Text(element, "quantization"), options),
            Flag(element, "distilled"),
            PinText(Text(element, "derivation"), options),
            PinText(Text(element, "source"), options),
            PinText(Text(element, "confidence"), options));
    }


    /// <summary>The <c>runtime</c> block of a process signal, or null when there is none.</summary>
    public static DiscoveryRuntimeInfo? ParseRuntime(JsonElement element)
    {
        if (!IsPopulatedObject(element))
        {
            return null;
        }

        return new DiscoveryRuntimeInfo(
            (int)Math.Clamp(NonNegativeLong(element, "pid"), 0, int.MaxValue),
            NonNegativeLong(element, "uptime_sec"),
            Text(element, "user") ?? string.Empty,
            Text(element, "comm") ?? string.Empty);
    }

    /// <summary>The <c>component</c> block, or null when there is none.</summary>
    public static DiscoveryComponentRef? ParseComponent(JsonElement element)
    {
        if (!IsPopulatedObject(element))
        {
            return null;
        }

        return new DiscoveryComponentRef(
            Text(element, "ecosystem") ?? string.Empty,
            Text(element, "name") ?? string.Empty,
            Text(element, "version") ?? string.Empty,
            Text(element, "framework") ?? string.Empty);
    }

    // ---- value coercion --------------------------------------------------------------------------------------

    private static bool IsPopulatedObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Any();

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    /// <summary>
    /// A text the runtime wrote into the model fields a newer build adds (owner, relevance, lineage), made safe to show: a value that names
    /// a local path is withheld (<see cref="DiscoveryPaths"/>), the rest is cut to the longest the runtime writes (512, a model id) and its
    /// control and bidirectional characters are spelled out so it stays on one line and reads in the order it was written.
    /// </summary>
    private static string PinText(string? value, DiscoveryReadOptions options)
    {
        var guarded = DiscoveryPaths.Guard(value, options);
        return DisplayNames.Visible(guarded.Length > PinTextLimit ? guarded[..PinTextLimit] : guarded);
    }

    private static string? Text(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static double? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// An optional score on 0..1: the fraction the runtime writes or, from a build that wrote a whole percent, that percent (above 1, so 86
    /// is 0.86); null when it is absent, not a number, or a boolean. An explicit 0 stays 0 - it is a score, not the lack of one.
    /// </summary>
    private static double? UnitScore(JsonElement parent, string name) =>
        Number(parent, name) is { } value ? DiscoveryFormat.Clamp01(value > 1 ? value / 100 : value) : null;

    private static long NonNegativeLong(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) && number >= 0 => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    /// <summary>true / false from a boolean, a 0 or 1, or the words true, yes, on, 1 / false, no, off, 0; anything else is unknown (null).</summary>
    private static bool? Flag(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1:
                return number == 1;
            case JsonValueKind.String:
                return value.GetString()?.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on" => true,
                    "false" or "0" or "no" or "off" => false,
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static DateTimeOffset? Time(string? raw) =>
        InventoryTimestamps.TryParse(raw, out var parsed) ? parsed : null;

    // ---- database cells --------------------------------------------------------------------------------------

    private static string? Cell(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static double? CellNumber(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        try
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number) ? number : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryParse(string json, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    /// <summary>A JSON text cell (<c>model_json</c>, <c>runtime_json</c>) read by <paramref name="read"/>; bad JSON is no block, not a failed row.</summary>
    private static T? Block<T>(string? json, Func<JsonElement, T?> read)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json) || !TryParse(json, out var document))
        {
            return null;
        }

        using (document)
        {
            return read(document.RootElement);
        }
    }
}
