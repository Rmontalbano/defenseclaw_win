using System.Globalization;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// What a parser of AI Discovery data may keep of a value that names a place on this machine. The runtime keeps no literal paths unless
/// <c>ai_discovery.store_raw_local_paths</c> is on, and even then its REST answer drops them (<c>SanitizeEvidenceForWire</c>), so the
/// fields read here - an owner, a publisher, a model reference - hold none; this is what stops one that did from reaching the screen.
/// </summary>
/// <param name="ShowRawLocalPaths">
/// True when the runtime says its own store keeps raw local paths (<c>ai_discovery.store_raw_local_paths</c> in config.yaml): the operator
/// has chosen to have them, so a value that looks like one is shown as it is. False (the runtime's default) hides it.
/// </param>
internal readonly record struct DiscoveryReadOptions(bool ShowRawLocalPaths);

/// <summary>Decides when a value names a local file or folder, and what is shown instead.</summary>
internal static class DiscoveryPaths
{
    /// <summary>Shown in place of a value that is a local path the operator has not asked to see.</summary>
    public const string HiddenText = "(local path hidden)";

    /// <summary>
    /// True for a drive path (<c>C:\x</c>, <c>C:/x</c>), a UNC or any other backslashed path, <c>file:</c> URI, <c>~/x</c>, <c>/x</c>, or a path
    /// that starts with an environment variable. A model reference such as <c>org/model-name</c> is not a path, and neither is an
    /// owner or a publisher name, so those pass.
    /// </summary>
    public static bool LooksLikeLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.AsSpan().Trim();

        // None of the values guarded here (owner, publisher, root and base model, source) holds a backslash, and it is the Windows separator.
        if (text.Contains('\\'))
        {
            return true;
        }

        // file:///C:/x, /x, $HOME/x, ~/x
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            text[0] == '/' ||
            (text[0] == '$' && text.Contains('/')) ||
            text.StartsWith("~/", StringComparison.Ordinal))
        {
            return true;
        }

        // C:/x
        if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '/')
        {
            return true;
        }

        // %USERPROFILE%/x
        return text[0] == '%' && text[1..].IndexOf('%') is > 0 and var close && close + 2 < text.Length && text[close + 2] == '/';
    }

    /// <summary><paramref name="value"/>, or <see cref="HiddenText"/> when it names a local path and the runtime does not say otherwise.</summary>
    public static string Guard(string? value, DiscoveryReadOptions options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return options.ShowRawLocalPaths || !LooksLikeLocalPath(value) ? value : HiddenText;
    }
}

/// <summary>The countries a model's publisher can be placed in.</summary>
internal static class DiscoveryCountries
{
    // The countries of the runtime's reviewed publisher catalog: internal/inventory/model_provenance.go isValidModelCountryCode accepts
    // these seven and no other pair of letters. A code outside it is shown as the code alone, never as a guessed name.
    private static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AE"] = "United Arab Emirates",
        ["CA"] = "Canada",
        ["CN"] = "China",
        ["DE"] = "Germany",
        ["FR"] = "France",
        ["GB"] = "United Kingdom",
        ["US"] = "United States",
    };

    /// <summary>An ISO 3166-1 alpha-2 code as the runtime writes it (upper case): trimmed, and empty unless it is exactly two letters A to Z.</summary>
    public static string Normalize(string? raw)
    {
        var code = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return code.Length == 2 && code.All(static c => c is >= 'A' and <= 'Z') ? code : string.Empty;
    }

    /// <summary>"United States (US)" for a known code, the code alone for another, and nothing for none. The flag the other clients draw
    /// from the code is not drawn: Windows has no flag glyphs and would show the two letters boxed.</summary>
    public static string Display(string code) =>
        code.Length == 0 ? string.Empty : Names.TryGetValue(code, out var name) ? $"{name} ({code})" : code;
}

/// <summary>
/// The lineage of a local model (<c>provenance</c> of its <c>model</c> block; <c>LocalModelProvenance</c> in the runtime's
/// <c>internal/inventory/ai_discovery.go</c>): who published it, what it was made from and how, and how the runtime came to say so.
/// Every field is optional on the wire. <see cref="Quantized"/> and <see cref="Distilled"/> are three-valued on purpose: the runtime
/// leaves them out when it cannot tell, and <c>false</c> is reserved for metadata that positively names an original artifact, so an
/// absent flag is never shown as "no".
/// </summary>
public sealed record DiscoveryModelProvenance(
    string Publisher,
    string CountryCode,
    string RootModel,
    IReadOnlyList<string> BaseModels,
    bool? Quantized,
    string Quantization,
    bool? Distilled,
    string Derivation,
    string Source,
    string Confidence)
{
    /// <summary>"United States (US)": the publisher's country, empty when the runtime named none.</summary>
    public string CountryDisplay => DiscoveryCountries.Display(CountryCode);

    /// <summary>The root model, else "ambiguous (2)" for a model with base models and no single root (a merge), else empty.</summary>
    public string RootDisplay =>
        RootModel.Length > 0 ? RootModel : BaseModels.Count > 0 ? $"ambiguous ({BaseModels.Count.ToString(CultureInfo.InvariantCulture)})" : string.Empty;

    /// <summary>
    /// How the model was derived, in words: the runtime's own derivation word, else what its flags say (<c>distilled+quantized</c>,
    /// <c>quantized</c>, <c>distilled</c>, or <c>base</c> when both are positively no), then the quantization unless the derivation
    /// already says it ("quantized · Q4_K_M").
    /// </summary>
    public string DerivationDisplay
    {
        get
        {
            var parts = new List<string>();
            if (Derivation.Length > 0)
            {
                parts.Add(Derivation);
            }
            else if (Quantized == true && Distilled == true)
            {
                parts.Add("distilled+quantized");
            }
            else if (Quantized == true)
            {
                parts.Add("quantized");
            }
            else if (Distilled == true)
            {
                parts.Add("distilled");
            }
            else if (Quantized == false && Distilled == false)
            {
                parts.Add("base");
            }

            if (Quantization.Length > 0 && !parts.Exists(part => string.Equals(part, Quantization, StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(Quantization);
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>The lineage rows the inspector shows: only what the runtime said, in the order the TUI prints them.</summary>
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

            Add("Publisher", Publisher);
            Add("Country", CountryDisplay);
            Add("Root model", RootDisplay);
            Add("Base models", string.Join(", ", BaseModels));
            Add("Derivation", DerivationDisplay);
            Add("Quantized", Quantized is { } quantized ? (quantized ? "yes" : "no") : string.Empty);
            Add("Distilled", Distilled is { } distilled ? (distilled ? "yes" : "no") : string.Empty);
            Add("Source", Source);
            Add("Lineage confidence", Confidence);
            return facts;
        }
    }

    /// <summary>True when the runtime said something the inspector can show (a block of nothing is not a lineage).</summary>
    public bool HasFacts => Facts.Count > 0;

    /// <summary>The words a search matches: publisher, country (code and name), root and base models, quantization, derivation, source, confidence.</summary>
    public IEnumerable<string> SearchTerms =>
        new[] { Publisher, CountryCode, CountryDisplay, RootModel, Quantization, Derivation, Source, Confidence }.Concat(BaseModels).Where(static t => t.Length > 0);

    /// <summary>How sure the runtime is of the lineage: high 3, medium 2, low 1, anything else (or nothing) 0.</summary>
    public int ConfidenceRank => Confidence.Trim().ToLowerInvariant() switch
    {
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0,
    };

    /// <summary>How many of publisher, country, root, base models, quantization, derivation and source it states.</summary>
    public int PopulatedFields =>
        new[] { Publisher.Length > 0, CountryCode.Length > 0, RootModel.Length > 0, BaseModels.Count > 0, Quantization.Length > 0, Derivation.Length > 0, Source.Length > 0 }
            .Count(static populated => populated);

    /// <summary>
    /// Is <paramref name="candidate"/> a better account of the same model than <paramref name="current"/>? The more sure one wins, then the
    /// fuller one; a tie keeps the one already held. (The Mac's <c>prefersModelProvenance</c> and the TUI's <c>_prefer_model_provenance</c>:
    /// one model seen by a file scan and by a model server can carry two lineages, and the row shows the stronger.)
    /// </summary>
    public static bool Prefers(DiscoveryModelProvenance? candidate, DiscoveryModelProvenance? current)
    {
        if (candidate is null)
        {
            return false;
        }

        if (current is null)
        {
            return true;
        }

        return (candidate.ConfidenceRank, candidate.PopulatedFields).CompareTo((current.ConfidenceRank, current.PopulatedFields)) > 0;
    }

    // A record compares its members, and a list compares by reference: two readings of one lineage must be equal.
    public bool Equals(DiscoveryModelProvenance? other) =>
        other is not null
        && Publisher == other.Publisher
        && CountryCode == other.CountryCode
        && RootModel == other.RootModel
        && BaseModels.SequenceEqual(other.BaseModels)
        && Quantized == other.Quantized
        && Quantization == other.Quantization
        && Distilled == other.Distilled
        && Derivation == other.Derivation
        && Source == other.Source
        && Confidence == other.Confidence;

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Publisher);
        hash.Add(CountryCode);
        hash.Add(RootModel);
        foreach (var baseModel in BaseModels)
        {
            hash.Add(baseModel);
        }

        hash.Add(Quantized);
        hash.Add(Quantization);
        hash.Add(Distilled);
        hash.Add(Derivation);
        hash.Add(Source);
        hash.Add(Confidence);
        return hash.ToHashCode();
    }
}
