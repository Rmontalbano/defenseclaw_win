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

/// <summary>The confidence filter's choices: no limit, 80% and up, or the models under it.</summary>
public enum DiscoveryConfidenceBand
{
    Any,
    High,
    Low,
}

/// <summary>Names and classifies the values of <see cref="DiscoveryModality"/>.</summary>
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

    public static string Label(DiscoveryModality modality) => modality.ToString();

    /// <summary>The lower-case key a filter choice and a search match use ("speech").</summary>
    public static string Key(DiscoveryModality modality) => modality.ToString().ToLowerInvariant();
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
/// Every field comes from what DefenseClaw 0.8.10 reports about a model - an id, a status, a format, a provider, a size and, when the
/// scanner has them, a recipe, a modality, a device and a pinned flag - and is shown only when some signal has it. The confidence is the
/// strongest detection score of the model's signals (labelled as such: it is not a confidence in the model). A newer runtime reports an
/// owner, a relevance, a discovery confidence and a lineage as well; those are not read here (CUST-310).
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

    private readonly DateTimeOffset _now;
    private readonly IReadOnlyList<string> _recipes;
    private readonly IReadOnlyList<string> _devices;

    private DiscoveryModelRow(string modelId, IReadOnlyList<DiscoverySignalRecord> signals, DateTimeOffset now)
    {
        _now = now;
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

    /// <summary>
    /// The strongest detection score among the signals, 0..1: how sure the scanner was that something matched, which the Mac calls a signal
    /// confidence. It is not the scanner's confidence in the model itself, which only a newer runtime reports.
    /// </summary>
    public double Confidence { get; }

    public long SizeBytes { get; }

    public bool Pinned { get; }

    public DateTimeOffset? LastActive { get; }

    public int ObservationCount => Signals.Count;

    /// <summary>True when any signal names a modality (the Modality column and filter exist only then).</summary>
    public bool HasModalityData => RawModalities.Count > 0;

    /// <summary>The "State" column's word, as the pill shows it.</summary>
    public string StateText => State;

    public bool HasState => State.Length > 0;

    public string StateKey => DiscoveryStates.Tone(State);

    public int StateWeight => DiscoveryStates.Weight(State);

    public string ModalityDisplay => string.Join(", ", Modalities.Select(DiscoveryClasses.Label));

    /// <summary>The Mac's "Status / format" cell: the statuses, then the formats.</summary>
    public string StatusFormatDisplay => string.Join(", ", Statuses.Concat(Formats));

    public string SourcesDisplay => string.Join(", ", Detectors);

    public string SizeDisplay => DiscoveryFormat.Bytes(SizeBytes);

    /// <summary>"90% signal": the number and what it is a number of.</summary>
    public string ConfidenceLabel => DiscoveryFormat.Percent(Confidence).ToString(CultureInfo.InvariantCulture) + "% signal";

    /// <summary>The confidence in words, for a screen reader.</summary>
    public string ConfidenceAutomationLabel => "Signal confidence " + DiscoveryFormat.Percent(Confidence).ToString(CultureInfo.InvariantCulture) + " percent";

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

            Add("Status", string.Join(", ", Statuses));
            Add("Format", string.Join(", ", Formats));
            Add("Provider", string.Join(", ", Providers));
            Add("Recipe", string.Join(", ", _recipes));
            Add("Device", string.Join(", ", _devices));
            Add("Size", SizeDisplay);
            Add("Pinned", Pinned ? "yes" : string.Empty);
            Add("Products", string.Join(", ", Products));
            Add("Vendors", string.Join(", ", Vendors));
            Add("Sources", SourcesDisplay);
            Add("Last active", LastActive is { } active ? DiscoveryFormat.Age(_now - active) + " ago" : string.Empty);
            return facts;
        }
    }

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

    /// <summary>Does the row match a search (any part of it, case-insensitive)? The model, its state, status, format, provider, recipe, device, products, vendors, sources and modality are searched.</summary>
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
        foreach (var signal in signals.Where(IsModelSignal))
        {
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
            .Select(key => new DiscoveryModelRow(names[key], groups[key], now))
            .OrderBy(static row => row.StateWeight)
            .ThenBy(static row => row.NormalizedId, StringComparer.Ordinal)
            .ToList();
    }

    internal static string NormalizeId(string id) => id.Trim().ToLowerInvariant();

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
/// Which models the Models view lists: the ones of a chosen modality, and the ones above or below a confidence cut. Nothing is hidden
/// until the operator chooses: the Mac's own default, a "recommended" scope that hides the models under 80% and the ones that are not
/// primary, needs the model-level discovery confidence and relevance of a newer runtime, and with data that carries neither (every model
/// a DefenseClaw 0.8.10 reports) the Mac lists everything too. When those fields are read (CUST-310) the scope belongs here.
/// </summary>
public sealed record DiscoveryModelFilter
{
    /// <summary>The cut the confidence filter makes at: the Mac's, and the TUI's recommended minimum.</summary>
    public const double ConfidenceCut = 0.8;

    /// <summary>Only models of this class; null: any.</summary>
    public DiscoveryModality? Modality { get; init; }

    public DiscoveryConfidenceBand Confidence { get; init; }

    public bool Includes(DiscoveryModelRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Modality is { } modality && !row.Modalities.Contains(modality))
        {
            return false;
        }

        return Confidence switch
        {
            DiscoveryConfidenceBand.High => row.Confidence >= ConfidenceCut,
            DiscoveryConfidenceBand.Low => row.Confidence < ConfidenceCut,
            _ => true,
        };
    }
}
