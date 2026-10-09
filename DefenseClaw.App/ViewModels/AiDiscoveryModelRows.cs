using System.Globalization;

namespace DefenseClaw.App.ViewModels;

/// <summary>The classes the Models view sorts a model's modality into (the Mac's <c>AIModelModality</c>, the TUI's <c>_classify_model_modality</c>).</summary>
public enum DiscoveryModality
{
    Generative,
    Speech,
    Vision,
    Embedding,
    Audio,
    Unknown,
}

/// <summary>
/// The classes a model's relevance falls into (the Mac's <c>AIModelRelevance</c>, the TUI's <c>_classify_model_relevance</c>): how central
/// the model is to the application that owns it. The order is the order of preference when two detectors disagree about one model.
/// </summary>
public enum DiscoveryRelevance
{
    Primary,
    Supporting,
    Embedded,
    Unknown,
}

/// <summary>The confidence filter's choices: no limit, 80% and up, or the models under it.</summary>
public enum DiscoveryConfidenceBand
{
    Any,
    High,
    Low,
}

/// <summary>Names and classifies the values of <see cref="DiscoveryModality"/> and <see cref="DiscoveryRelevance"/>.</summary>
public static class DiscoveryClasses
{
    /// <summary>A scanner's modality word as one of the six classes; the words the Mac folds together (<c>text</c>, <c>chat</c>, <c>stt</c> ...) are folded here too.</summary>
    public static DiscoveryModality ClassifyModality(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "generative" or "text" or "chat" or "language" or "llm" => DiscoveryModality.Generative,
        "speech" or "speech_to_text" or "speech-to-text" or "stt" or "transcription" => DiscoveryModality.Speech,
        "vision" or "image" or "computer_vision" or "computer-vision" => DiscoveryModality.Vision,
        "embedding" or "embeddings" => DiscoveryModality.Embedding,
        "audio" => DiscoveryModality.Audio,
        _ => DiscoveryModality.Unknown,
    };

    /// <summary>A scanner's relevance word as one of the four classes: <c>primary</c>, <c>supporting</c>, <c>embedded</c>, and anything else (or nothing) <c>unknown</c>.</summary>
    public static DiscoveryRelevance ClassifyRelevance(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "primary" => DiscoveryRelevance.Primary,
        "supporting" => DiscoveryRelevance.Supporting,
        "embedded" => DiscoveryRelevance.Embedded,
        _ => DiscoveryRelevance.Unknown,
    };

    public static string Label(DiscoveryModality modality) => modality.ToString();

    public static string Label(DiscoveryRelevance relevance) => relevance.ToString();

    /// <summary>The lower-case key a filter choice and a search match use ("speech").</summary>
    public static string Key(DiscoveryModality modality) => modality.ToString().ToLowerInvariant();

    /// <summary>The lower-case key a filter choice and a search match use ("supporting").</summary>
    public static string Key(DiscoveryRelevance relevance) => relevance.ToString().ToLowerInvariant();
}

/// <summary>One entry of the models filter row (a combo box item): a stable key and the words shown.</summary>
public sealed record DiscoveryFilterChoice(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A label and its value in the model inspector; a fact the data does not have is not made into one.</summary>
public sealed record DiscoveryFact(string Label, string Value)
{
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// One signal as a block of text lines (the TUI's detail lines for it): who it is, its state, how sure the scanner was, and the
/// component, model, process and last-activity lines it has. A line the signal has no data for is not there.
/// </summary>
public sealed record DiscoverySignalLine(string Title, string StateText, string StateKey, string ConfidenceText, string DetailText)
{
    public bool HasState => StateText.Length > 0;

    public bool HasConfidence => ConfidenceText.Length > 0;

    public bool HasDetail => DetailText.Length > 0;

    /// <summary>What a screen reader says for the block.</summary>
    public override string ToString() =>
        string.Join(". ", new[] { Title, StateText, ConfidenceText.Length > 0 ? ConfidenceText + " confidence" : string.Empty, DetailText.Replace('\n', ' ') }
            .Where(static part => part.Length > 0));

    /// <summary>
    /// The block for <paramref name="signal"/>. <paramref name="forModel"/> titles it "product · detector" (a model's observation) and
    /// leaves the detector out of the lines; otherwise the title is the signal's id and the first line is <c>detector=... source=...</c>.
    /// </summary>
    public static DiscoverySignalLine For(DiscoverySignalRecord signal, DateTimeOffset now, bool forModel)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var lines = new List<string>();
        if (!forModel && (!string.IsNullOrEmpty(signal.Detector) || !string.IsNullOrEmpty(signal.Source)))
        {
            lines.Add(string.Join(' ', new[]
            {
                string.IsNullOrEmpty(signal.Detector) ? string.Empty : "detector=" + signal.Detector,
                string.IsNullOrEmpty(signal.Source) ? string.Empty : "source=" + signal.Source,
            }.Where(static part => part.Length > 0)));
        }

        if (signal.Component is { Name.Length: > 0 } component)
        {
            lines.Add("component: " + component.Label + (component.Version.Length > 0 ? " version=" + component.Version : string.Empty));
        }

        if (signal.Model is { } model)
        {
            lines.Add(model.Detail());
        }

        if (signal.Runtime?.Detail() is { Length: > 0 } runtime)
        {
            lines.Add(runtime);
        }

        var activity = DiscoveryFormat.Activity(signal.LastActiveAt, signal.LastSeen, now);
        if (activity.Length > 0)
        {
            lines.Add(activity);
        }

        var state = DiscoveryStates.Normalize(signal.State);
        var title = forModel
            ? string.Join(" · ", new[] { signal.Product, signal.Detector ?? string.Empty }.Where(static part => part.Length > 0))
            : signal.DisplayId;

        return new DiscoverySignalLine(
            title.Length > 0 ? title : signal.DisplayId,
            state,
            DiscoveryStates.Tone(state),
            signal.Confidence is { } confidence ? DiscoveryFormat.Percent(confidence).ToString(CultureInfo.InvariantCulture) + "%" : string.Empty,
            string.Join('\n', lines));
    }
}

/// <summary>
/// One local model, as the Models view lists it (the Mac's <c>AIModelDiscoveryRow</c>): every <c>local_model</c> signal that names the same
/// model - the file on disk, the entry a local model server lists, the one it has loaded - folded into one row, with the most
/// actionable state among them. The id is compared without case or surrounding space, so <c>Qwen3-0.6B-GGUF</c> and
/// <c>qwen3-0.6b-gguf</c> are one model.
/// <para>
/// Every field comes from what the runtime reports about a model and is shown only when some signal has it. DefenseClaw 0.8.10 reports an
/// id, a status, a format, a provider, a size and, when the scanner has them, a recipe, a modality, a device and a pinned flag. A newer
/// runtime adds the application that owns the model (<see cref="OwnerApplications"/>), how central it is to it
/// (<see cref="Relevances"/>), how sure the scanner is of it (<see cref="ReportedDiscoveryConfidence"/>) and where it came from
/// (<see cref="Provenance"/>); a row built from 0.8.10's data has none of them and looks as it always did.
/// </para>
/// <para>
/// <b>Confidence.</b> <see cref="Confidence"/> is the strongest detection score of the model's signals: how sure the scanner was that
/// something matched, which is not a confidence in the model. A model that has a <see cref="ReportedDiscoveryConfidence"/> shows that
/// instead, and the two are never mixed up: the column says "93%" for the model's own and "90% signal" for the score of the match.
/// </para>
/// </summary>
public sealed class DiscoveryModelRow
{
    private const int MaxObservations = 50;

    private static readonly DiscoveryModality[] ModalityPreference =
    {
        DiscoveryModality.Generative, DiscoveryModality.Speech, DiscoveryModality.Vision,
        DiscoveryModality.Embedding, DiscoveryModality.Audio, DiscoveryModality.Unknown,
    };

    private static readonly DiscoveryRelevance[] RelevancePreference =
    {
        DiscoveryRelevance.Primary, DiscoveryRelevance.Supporting, DiscoveryRelevance.Embedded, DiscoveryRelevance.Unknown,
    };

    private readonly DateTimeOffset _now;
    private readonly IReadOnlyList<string> _recipes;
    private readonly IReadOnlyList<string> _devices;

    /// <summary>True when some model among the signals this row was built with carries a discovery confidence, an owner or a relevance: a runtime that classifies its models.</summary>
    private readonly bool _classifiedSnapshot;

    private DiscoveryModelRow(string modelId, IReadOnlyList<DiscoverySignalRecord> signals, DateTimeOffset now, bool classifiedSnapshot)
    {
        _now = now;
        _classifiedSnapshot = classifiedSnapshot;
        ModelId = modelId;
        NormalizedId = NormalizeId(modelId);
        Signals = signals;

        var models = signals.Select(static s => s.Model).OfType<DiscoveryModelInfo>().ToList();
        State = DiscoveryStates.Strongest(signals.Select(static s => s.State));
        Statuses = Unique(models.Select(static m => m.Status));
        Formats = Unique(models.Select(static m => m.Format));
        Providers = Unique(models.Select(static m => m.Provider));
        Products = Unique(signals.Select(static s => s.Product));
        Vendors = Unique(signals.Select(static s => s.Vendor));
        Detectors = Unique(signals.Select(static s => s.Detector));
        RawModalities = Unique(models.Select(static m => m.Modality));
        _recipes = Unique(models.Select(static m => m.Recipe));
        _devices = Unique(models.Select(static m => m.Device));

        var modalities = models.Select(static m => DiscoveryClasses.ClassifyModality(m.Modality)).Where(static m => m != DiscoveryModality.Unknown).Distinct().ToList();
        Modalities = modalities.Count > 0 ? modalities : new List<DiscoveryModality> { DiscoveryModality.Unknown };
        EffectiveModality = ModalityPreference.First(Modalities.Contains);

        OwnerApplications = Unique(models.Select(static m => m.OwnerApplication));
        RawRelevances = Unique(models.Select(static m => m.Relevance));
        var relevances = models.Select(static m => DiscoveryClasses.ClassifyRelevance(m.Relevance)).Where(static r => r != DiscoveryRelevance.Unknown).Distinct().ToList();
        Relevances = relevances.Count > 0 ? relevances : new List<DiscoveryRelevance> { DiscoveryRelevance.Unknown };
        EffectiveRelevance = RelevancePreference.First(Relevances.Contains);

        ReportedDiscoveryConfidence = models.Select(static m => m.DiscoveryConfidence).Max();
        HasClassificationMetadata = models.Any(static m => m.HasClassification);
        HasLocalModelApiSignal = Detectors.Any(IsApiDetector);
        HasLocalModelApiSignalWithoutDiscoveryConfidence = signals.Any(static s => IsApiDetector(s.Detector) && s.Model?.DiscoveryConfidence is null);

        DiscoveryModelProvenance? provenance = null;
        foreach (var model in models)
        {
            if (DiscoveryModelProvenance.Prefers(model.Provenance, provenance))
            {
                provenance = model.Provenance;
            }
        }

        Provenance = provenance;

        Confidence = signals.Select(static s => s.Confidence ?? 0).DefaultIfEmpty(0).Max();
        SizeBytes = models.Select(static m => m.SizeBytes).DefaultIfEmpty(0).Max();
        Pinned = models.Any(static m => m.Pinned);
        LastActive = signals.Select(static s => s.LastActiveAt).OfType<DateTimeOffset>().Cast<DateTimeOffset?>().Max();
    }

    /// <summary>The model's name as the first signal gave it (trimmed).</summary>
    public string ModelId { get; }

    /// <summary>The id folded to lower case: what two signals are compared by.</summary>
    public string NormalizedId { get; }

    /// <summary>The most actionable state among the signals (<see cref="DiscoveryStates.Strongest"/>), or empty.</summary>
    public string State { get; }

    public IReadOnlyList<DiscoverySignalRecord> Signals { get; }

    public IReadOnlyList<string> Statuses { get; }

    public IReadOnlyList<string> Formats { get; }

    public IReadOnlyList<string> Providers { get; }

    public IReadOnlyList<string> Products { get; }

    public IReadOnlyList<string> Vendors { get; }

    public IReadOnlyList<string> Detectors { get; }

    /// <summary>The modality words the scanner used, as it wrote them (empty when none said anything).</summary>
    public IReadOnlyList<string> RawModalities { get; }

    /// <summary>The known classes the signals report, or just Unknown.</summary>
    public IReadOnlyList<DiscoveryModality> Modalities { get; }

    public DiscoveryModality EffectiveModality { get; }

    /// <summary>The applications the signals say own the model, each once whatever its case (empty when the runtime attributed it to none).</summary>
    public IReadOnlyList<string> OwnerApplications { get; }

    /// <summary>The relevance words the scanner used, as it wrote them (empty when none said anything).</summary>
    public IReadOnlyList<string> RawRelevances { get; }

    /// <summary>The known relevance classes the signals report, or just Unknown.</summary>
    public IReadOnlyList<DiscoveryRelevance> Relevances { get; }

    /// <summary>The most actionable relevance (primary before supporting before embedded) when a file scan and a model server disagree about one model.</summary>
    public DiscoveryRelevance EffectiveRelevance { get; }

    /// <summary>
    /// The highest discovery confidence any signal of the model reports, 0..1; null when none does - which is not 0, and is what a model
    /// server's own listing sends. An explicit 0 is 0.
    /// </summary>
    public double? ReportedDiscoveryConfidence { get; }

    /// <summary>True when a detector reading a local model server (<c>model_api</c>) found the model.</summary>
    public bool HasLocalModelApiSignal { get; }

    /// <summary>True when one of the model's <c>model_api</c> signals carries no discovery confidence: the server named the model and the runtime has nothing to rate it by.</summary>
    public bool HasLocalModelApiSignalWithoutDiscoveryConfidence { get; }

    /// <summary>True when any of the model's signals carries a discovery confidence, an owner or a relevance.</summary>
    public bool HasClassificationMetadata { get; }

    /// <summary>The lineage the runtime gave the model: when its signals carry several, the surer and fuller one.</summary>
    public DiscoveryModelProvenance? Provenance { get; }

    /// <summary>
    /// The strongest detection score among the signals, 0..1: how sure the scanner was that something matched, which the Mac calls a signal
    /// confidence. It is not the scanner's confidence in the model itself, which is <see cref="ReportedDiscoveryConfidence"/> and which
    /// only a newer runtime reports.
    /// </summary>
    public double Confidence { get; }

    /// <summary>The number the confidence column stands for: the model's own discovery confidence when it has one, else the strongest detection score.</summary>
    public double EffectiveDiscoveryConfidence => ReportedDiscoveryConfidence ?? Confidence;

    public long SizeBytes { get; }

    public bool Pinned { get; }

    public DateTimeOffset? LastActive { get; }

    public int ObservationCount => Signals.Count;

    /// <summary>True when any signal names a modality (the Modality column and filter exist only then).</summary>
    public bool HasModalityData => RawModalities.Count > 0;

    /// <summary>True when any signal names an owner (the Owners column exists only then).</summary>
    public bool HasOwnerData => OwnerApplications.Count > 0;

    /// <summary>True when any signal names a relevance (the Relevance column and filter exist only then).</summary>
    public bool HasRelevanceData => RawRelevances.Count > 0;

    /// <summary>The "State" column's word, as the pill shows it.</summary>
    public string StateText => State;

    public bool HasState => State.Length > 0;

    public string StateKey => DiscoveryStates.Tone(State);

    public int StateWeight => DiscoveryStates.Weight(State);

    public string ModalityDisplay => string.Join(", ", Modalities.Select(DiscoveryClasses.Label));

    /// <summary>The "Owners" cell: every owner, or a dash for a model nobody attributed when others are.</summary>
    public string OwnersDisplay => OwnerApplications.Count == 0 ? "—" : string.Join(", ", OwnerApplications);

    /// <summary>The "Relevance" cell: every known class ("Primary, Embedded") or Unknown.</summary>
    public string RelevanceDisplay => string.Join(", ", Relevances.Select(DiscoveryClasses.Label));

    /// <summary>The order the Relevance column sorts by: primary first.</summary>
    public int RelevanceRank => (int)EffectiveRelevance;

    /// <summary>The Mac's "Status / format" cell: the statuses, then the formats.</summary>
    public string StatusFormatDisplay => string.Join(", ", Statuses.Concat(Formats));

    public string SourcesDisplay => string.Join(", ", Detectors);

    public string SizeDisplay => DiscoveryFormat.Bytes(SizeBytes);

    /// <summary>
    /// What the confidence column says, and of what: "93%" for the model's own discovery confidence; "API" for a model a local model server
    /// listed that the runtime has no confidence for; "90% signal" for the score of the match, which is not a confidence in the model.
    /// "API" is said only where the runtime classifies its models at all (<c>_classifiedSnapshot</c>): a 0.8.10 install sends no
    /// confidence for any model, and its view of a server-listed model has always been "NN% signal".
    /// </summary>
    public string ConfidenceLabel =>
        ReportedDiscoveryConfidence is { } reported
            ? DiscoveryFormat.Percent(reported).ToString(CultureInfo.InvariantCulture) + "%"
            : ShowsApiLabel
                ? "API"
                : DiscoveryFormat.Percent(Confidence).ToString(CultureInfo.InvariantCulture) + "% signal";

    /// <summary>The confidence in words, for a screen reader.</summary>
    public string ConfidenceAutomationLabel =>
        ReportedDiscoveryConfidence is { } reported
            ? "Discovery confidence " + DiscoveryFormat.Percent(reported).ToString(CultureInfo.InvariantCulture) + " percent"
            : ShowsApiLabel
                ? "Local model API; discovery confidence not reported"
                : "Signal confidence " + DiscoveryFormat.Percent(Confidence).ToString(CultureInfo.InvariantCulture) + " percent";

    private bool ShowsApiLabel => _classifiedSnapshot && HasLocalModelApiSignal;

    /// <summary>"3 observations · Signal confidence 90 percent".</summary>
    public string Summary =>
        $"{ObservationCount.ToString(CultureInfo.InvariantCulture)} observation{(ObservationCount == 1 ? string.Empty : "s")} · {ConfidenceAutomationLabel}";

    /// <summary>The facts the inspector lists first; each is there only when the data has it.</summary>
    public IReadOnlyList<DiscoveryFact> Facts
    {
        get
        {
            var facts = new List<DiscoveryFact>();
            void Add(string label, string value)
            {
                if (value.Length > 0)
                {
                    facts.Add(new DiscoveryFact(label, value));
                }
            }

            Add("State", State);
            if (HasModalityData)
            {
                Add("Modality", ModalityDisplay);
            }

            if (HasRelevanceData)
            {
                Add("Relevance", RelevanceDisplay);
            }

            if (HasOwnerData)
            {
                Add("Owners", string.Join(", ", OwnerApplications));
            }

            Add("Status", string.Join(", ", Statuses));
            Add("Format", string.Join(", ", Formats));
            Add("Provider", string.Join(", ", Providers));
            Add("Recipe", string.Join(", ", _recipes));
            Add("Device", string.Join(", ", _devices));
            Add("Size", SizeDisplay);
            Add("Pinned", Pinned ? "yes" : string.Empty);
            if (ReportedDiscoveryConfidence is { } reported)
            {
                Add("Discovery confidence", DiscoveryFormat.Percent(reported).ToString(CultureInfo.InvariantCulture) + "%");
            }

            Add("Products", string.Join(", ", Products));
            Add("Vendors", string.Join(", ", Vendors));
            Add("Sources", SourcesDisplay);
            Add("Last active", LastActive is { } active ? DiscoveryFormat.Age(_now - active) + " ago" : string.Empty);
            return facts;
        }
    }

    /// <summary>The lineage rows the inspector's Provenance block lists (publisher, country, root and base models, derivation, source ...); empty when the runtime sent none.</summary>
    public IReadOnlyList<DiscoveryFact> ProvenanceFacts => Provenance?.Facts ?? Array.Empty<DiscoveryFact>();

    /// <summary>True when there is a Provenance block to show.</summary>
    public bool HasProvenance => ProvenanceFacts.Count > 0;

    /// <summary>The signals behind the row, newest news first, as text blocks (the first 50; see <see cref="ObservationOverflow"/>).</summary>
    public IReadOnlyList<DiscoverySignalLine> Observations =>
        Signals
            .Select((signal, index) => (signal, index))
            .OrderBy(static pair => DiscoveryStates.Weight(pair.signal.State))
            .ThenBy(static pair => pair.index)
            .Take(MaxObservations)
            .Select(pair => DiscoverySignalLine.For(pair.signal, _now, forModel: true))
            .ToList();

    /// <summary>"...and 12 more (use `defenseclaw agent usage --detail --json` for the full list)" past the 50th observation, else empty.</summary>
    public string ObservationOverflow =>
        Signals.Count > MaxObservations
            ? $"...and {(Signals.Count - MaxObservations).ToString(CultureInfo.InvariantCulture)} more (use `defenseclaw agent usage --detail --json` for the full list)"
            : string.Empty;

    public bool HasObservationOverflow => Signals.Count > MaxObservations;

    /// <summary>
    /// Does the row match a search (any part of it, case-insensitive)? The model, its state, status, format, provider, recipe, device,
    /// products, vendors, sources, modality, owners, relevance and the lineage (publisher, country, root and base models, quantization,
    /// derivation, source) are searched.
    /// </summary>
    public bool Matches(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var parts = new List<string> { State, ModelId };
        parts.AddRange(Statuses);
        parts.AddRange(Formats);
        parts.AddRange(Providers);
        parts.AddRange(_recipes);
        parts.AddRange(_devices);
        parts.AddRange(Products);
        parts.AddRange(Vendors);
        parts.AddRange(Detectors);
        parts.AddRange(RawModalities);
        parts.AddRange(Modalities.Select(DiscoveryClasses.Key));
        parts.AddRange(OwnerApplications);
        parts.AddRange(RawRelevances);
        parts.AddRange(Relevances.Select(DiscoveryClasses.Key));
        if (Provenance is { } provenance)
        {
            parts.AddRange(provenance.SearchTerms);
        }

        return string.Join(' ', parts).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{ModelId}, {(State.Length > 0 ? State + ", " : string.Empty)}{ConfidenceAutomationLabel}";

    /// <summary>True for a signal the Models view lists: a <c>local_model</c> signal whose model block names a model. One without a name stays a product (older builds), so nothing disappears.</summary>
    public static bool IsModelSignal(DiscoverySignalRecord signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return string.Equals(signal.Category, "local_model", StringComparison.OrdinalIgnoreCase) && signal.Model is { Id.Length: > 0 };
    }

    /// <summary>The model rows of <paramref name="signals"/>, most actionable state first, then by model id.</summary>
    public static IReadOnlyList<DiscoveryModelRow> Build(IEnumerable<DiscoverySignalRecord> signals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var order = new List<string>();
        var groups = new Dictionary<string, List<DiscoverySignalRecord>>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var classified = false;
        foreach (var signal in signals.Where(IsModelSignal))
        {
            classified |= signal.Model!.HasClassification;
            var id = signal.Model!.Id.Trim();
            var key = NormalizeId(id);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new List<DiscoverySignalRecord>();
                groups[key] = group;
                names[key] = id;
                order.Add(key);
            }

            group.Add(signal);
        }

        return order
            .Select(key => new DiscoveryModelRow(names[key], groups[key], now, classified))
            .OrderBy(static row => row.StateWeight)
            .ThenBy(static row => row.NormalizedId, StringComparer.Ordinal)
            .ToList();
    }

    internal static string NormalizeId(string id) => id.Trim().ToLowerInvariant();

    /// <summary>The detector that reads a local model server's own listing of its models (<c>model_api</c>), whatever its case.</summary>
    private static bool IsApiDetector(string? detector) => string.Equals(detector?.Trim(), "model_api", StringComparison.OrdinalIgnoreCase);

    /// <summary>Distinct non-empty values in the order first seen; "A" and "a" are the same value.</summary>
    private static List<string> Unique(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}

/// <summary>
/// Which models the Models view lists: the Mac's <c>AIModelDiscoveryFilter</c>, with one control of the Windows panel kept beside it.
/// <para>
/// <b>The recommended scope</b> (<see cref="RecommendedOnly"/>, the Mac's "Show all models" switched off; the TUI's <c>is_recommended_model</c>,
/// minimum <see cref="ConfidenceCut"/>): a model is listed when it is worth a person's attention by default.
/// A model a local model server listed whose own confidence is missing is always listed (the server named it; the runtime has nothing to
/// rate it by). Otherwise a model's reported discovery confidence under 80% hides it, and with none reported - and no model-server signal
/// - so does a strongest detection score under 80%. Then, while neither Modality nor Relevance is chosen: a primary model is listed; a
/// supporting one only when an application owns it and it is a speech, audio, vision or embedding model; an embedded or unknown one is not.
/// <b>Choosing a Modality or a Relevance lifts the recommended scope's classification</b> - the choice alone decides what is of that
/// class, so picking Speech lists the supporting speech models the scope would have hidden - and the choice is then applied as asked.
/// The 80% floor above is not part of that classification and stays while "Show all models" is off, exactly as the Mac's code has it
/// (its test for the choice uses models above the floor); "Show all models" lifts all of it.
/// </para>
/// <para>
/// <b>A legacy snapshot is never narrowed</b> (<see cref="PreservingLegacySnapshot"/>): when no model carries a discovery confidence, an
/// owner or a relevance - every model a 0.8.10 install reports - nothing separates a primary model from an embedded artifact, and hiding
/// by a rule that cannot be applied would hide everything. The snapshot keeps its historical all-models listing; the explicit choices still
/// apply.
/// </para>
/// <para>
/// <see cref="Confidence"/> is the 80% picker the Windows panel has always had; it is an explicit choice of the operator and composes with
/// the rest. It cuts the number the confidence column shows (<see cref="DiscoveryModelRow.EffectiveDiscoveryConfidence"/>).
/// </para>
/// <para>
/// A filter built with nothing set lists every model; the panel builds the recommended one.
/// </para>
/// </summary>
public sealed record DiscoveryModelFilter
{
    /// <summary>The cut the recommended scope and the confidence picker make at: the Mac's <c>focusedMinimumConfidence</c> and the TUI's recommended minimum.</summary>
    public const double ConfidenceCut = 0.8;

    /// <summary>The classes a supporting model must be one of to be listed by default (and have an owner): the TUI's <c>_SUPPORTING_MODEL_MODALITIES</c>.</summary>
    private static readonly DiscoveryModality[] SupportingModalities =
    {
        DiscoveryModality.Speech, DiscoveryModality.Audio, DiscoveryModality.Vision, DiscoveryModality.Embedding,
    };

    /// <summary>The recommended scope is on: "Show all models" is off. The panel starts here and <see cref="PreservingLegacySnapshot"/> takes it off for a snapshot that cannot be narrowed.</summary>
    public bool RecommendedOnly { get; init; }

    /// <summary>Only models of this class; null: any. A choice lifts the recommended scope.</summary>
    public DiscoveryModality? Modality { get; init; }

    /// <summary>Only models of this relevance; null: any. A choice lifts the recommended scope.</summary>
    public DiscoveryRelevance? Relevance { get; init; }

    public DiscoveryConfidenceBand Confidence { get; init; }

    /// <summary>True for models that carry none of what the recommended scope acts on, all of them: a snapshot from a runtime that does not classify its models.</summary>
    public static bool IsLegacySnapshot(IReadOnlyCollection<DiscoveryModelRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count > 0 && !rows.Any(static row => row.HasClassificationMetadata);
    }

    /// <summary>This filter, with the recommended scope taken off when <paramref name="rows"/> are a legacy snapshot (the Mac's <c>preservingLegacySnapshot</c>).</summary>
    public DiscoveryModelFilter PreservingLegacySnapshot(IReadOnlyCollection<DiscoveryModelRow> rows) =>
        RecommendedOnly && IsLegacySnapshot(rows) ? this with { RecommendedOnly = false } : this;

    public bool Includes(DiscoveryModelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (RecommendedOnly && !IsRecommended(row))
        {
            return false;
        }

        if (Modality is { } modality && !row.Modalities.Contains(modality))
        {
            return false;
        }

        if (Relevance is { } relevance && !row.Relevances.Contains(relevance))
        {
            return false;
        }

        return Confidence switch
        {
            DiscoveryConfidenceBand.High => row.EffectiveDiscoveryConfidence >= ConfidenceCut,
            DiscoveryConfidenceBand.Low => row.EffectiveDiscoveryConfidence < ConfidenceCut,
            _ => true,
        };
    }

    /// <summary>The recommended scope's verdict on one model (see the type's remarks).</summary>
    private bool IsRecommended(DiscoveryModelRow row)
    {
        if (row.HasLocalModelApiSignalWithoutDiscoveryConfidence)
        {
            return true;
        }

        if (row.ReportedDiscoveryConfidence is { } reported)
        {
            if (reported < ConfidenceCut)
            {
                return false;
            }
        }
        else if (!row.HasLocalModelApiSignal && row.Confidence < ConfidenceCut)
        {
            return false;
        }

        // The recommended classification applies only while neither picker expresses intent: once either is explicit, its "all" peer
        // means unrestricted, so choosing Speech alone can reveal supporting speech models.
        if (Modality is not null || Relevance is not null)
        {
            return true;
        }

        return row.EffectiveRelevance switch
        {
            DiscoveryRelevance.Primary => true,
            DiscoveryRelevance.Supporting => row.OwnerApplications.Count > 0 && Array.IndexOf(SupportingModalities, row.EffectiveModality) >= 0,
            _ => false,
        };
    }
}
