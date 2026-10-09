using System.IO;
using System.Text.Json;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>One provider of the runtime's model catalogue.</summary>
/// <param name="Name">The provider id the CLI takes (<c>anthropic</c>, <c>vertex_ai</c>).</param>
/// <param name="Label">The words the catalogue gives it ("AWS Bedrock").</param>
/// <param name="Models">The model ids it suggests, in the catalogue's order.</param>
public sealed record ModelCatalogueProvider(string Name, string Label, IReadOnlyList<string> Models);

/// <summary>
/// The model ids the installed runtime suggests, read from the file it ships - never from a provider.
/// <para>
/// <b>Where the TUI gets them.</b> Its model picker (<c>tui/screens/model_picker.py</c>) lists what <c>llm_catalog_models(provider, instance)</c>
/// returns (<c>tui/panels/setup.py</c>): the <c>models</c> of that provider in <c>_data/llm/model_catalog.json</c>, a JSON file packaged with the
/// runtime and "hand-maintained" (its own comment; <c>commands/_llm_picker.py</c> reads it with <c>importlib.resources</c> and caches it), or, for a
/// custom-provider instance, the <c>available_models</c> the operator gave <c>setup provider add</c> in <c>~/.defenseclaw/custom-providers.json</c>.
/// Both are static data on this machine. Nothing here asks a provider, LiteLLM or any network service which models exist; the picker is
/// "not exhaustive" by the file's own account, which is why a typed id is always accepted.
/// </para>
/// <para>
/// <b>Where this app finds it.</b> Beside the CLI it runs: <c>…\DefenseClaw\bin\defenseclaw.exe</c> sits next to
/// <c>…\DefenseClaw\runtime\python\Lib\site-packages\defenseclaw\_data\llm\model_catalog.json</c> in the Windows Setup layout, and a virtual
/// environment keeps it under <c>Lib\site-packages</c> beside <c>Scripts</c>. A runtime that is not laid out like either (a container, a
/// managed layout nobody has looked at) simply has no catalogue, and the model box is a plain text box. A file that cannot be read, is larger
/// than a megabyte or is not what it should be is no catalogue either: this never throws and never guesses.
/// </para>
/// </summary>
public sealed class ModelCatalogue
{
    /// <summary>The most the app reads of either file; the real catalogue is under 6 KB.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private const int MaxProviders = 200;
    private const int MaxModelsPerProvider = 1000;
    private const int MaxModelLength = 200;

    private static readonly string[] CatalogueFolders =
    {
        Path.Combine("runtime", "python", "Lib", "site-packages", "defenseclaw", "_data", "llm"),
        Path.Combine("Lib", "site-packages", "defenseclaw", "_data", "llm"),
    };

    private readonly IReadOnlyDictionary<string, ModelCatalogueProvider> _providers;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _instances;

    private ModelCatalogue(
        IReadOnlyList<ModelCatalogueProvider> providers,
        IReadOnlyDictionary<string, IReadOnlyList<string>> instances,
        string? source)
    {
        Providers = providers;
        Source = source;
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _instances = instances;
    }

    /// <summary>The providers the catalogue lists, in its order.</summary>
    public IReadOnlyList<ModelCatalogueProvider> Providers { get; }

    /// <summary>The file the providers were read from, for the tooltip; null for a catalogue built from text.</summary>
    public string? Source { get; }

    /// <summary>
    /// The ids to suggest for <paramref name="provider"/>: the models of the custom-provider instance <paramref name="instance"/> when it names
    /// one that lists some (the TUI's rule), otherwise the catalogue's models for the provider. Empty when neither is known - a provider with
    /// no list (vllm, lm_studio) or none chosen yet.
    /// </summary>
    public IReadOnlyList<string> ModelsFor(string? provider, string? instance = null)
    {
        if (instance is { Length: > 0 } && _instances.TryGetValue(instance.Trim(), out var own) && own.Count > 0)
        {
            return own;
        }

        return provider is { Length: > 0 } && _providers.TryGetValue(provider.Trim(), out var entry) ? entry.Models : Array.Empty<string>();
    }

    /// <summary>The catalogue's label for a provider ("AWS Bedrock"), or null when it has none.</summary>
    public string? LabelFor(string? provider) =>
        provider is { Length: > 0 } && _providers.TryGetValue(provider.Trim(), out var entry) && entry.Label.Length > 0 ? entry.Label : null;

    /// <summary>
    /// Where the catalogue file is for the CLI at <paramref name="cliPath"/>, or null when it is not where the runtimes this app knows keep it.
    /// Only looks; reads nothing.
    /// </summary>
    public static string? Locate(string? cliPath)
    {
        if (string.IsNullOrWhiteSpace(cliPath))
        {
            return null;
        }

        try
        {
            var bin = Path.GetDirectoryName(cliPath);
            var root = bin is null ? null : Path.GetDirectoryName(bin);
            if (root is null)
            {
                return null;
            }

            return CatalogueFolders
                .Select(folder => Path.Combine(root, folder, "model_catalog.json"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the catalogue for the CLI at <paramref name="cliPath"/> and the custom-provider instances in <paramref name="dataDirectory"/>.
    /// Null when there is no catalogue to read (see the type remarks): the caller then offers a plain text box. A missing or broken instances
    /// file only means no instance has models of its own. Does file IO, so not for the UI thread.
    /// </summary>
    public static ModelCatalogue? Load(string? cliPath, string? dataDirectory)
    {
        if (Locate(cliPath) is not { } file || ReadBounded(file) is not { } text)
        {
            return null;
        }

        var instances = dataDirectory is { Length: > 0 } && ReadBounded(Path.Combine(dataDirectory, "custom-providers.json")) is { } overlay
            ? ParseInstances(overlay)
            : null;

        return Parse(text, instances, file);
    }

    /// <summary>The catalogue in <paramref name="json"/>, with the instances of <paramref name="instances"/>; null when it is not a catalogue.</summary>
    internal static ModelCatalogue? Parse(string json, IReadOnlyDictionary<string, IReadOnlyList<string>>? instances = null, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("providers", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var providers = new List<ModelCatalogueProvider>();
            foreach (var entry in list.EnumerateArray())
            {
                if (providers.Count >= MaxProviders)
                {
                    break;
                }

                if (entry.ValueKind != JsonValueKind.Object || Text(entry, "name") is not { Length: > 0 } name)
                {
                    continue;
                }

                providers.Add(new ModelCatalogueProvider(name, Text(entry, "label") ?? string.Empty, Strings(entry, "models")));
            }

            return providers.Count == 0
                ? null
                : new ModelCatalogue(providers, instances ?? new Dictionary<string, IReadOnlyList<string>>(), source);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Each custom-provider instance's <c>available_models</c>, by name; empty for text that is not an overlay.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseInstances(string json)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("providers", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var entry in list.EnumerateArray())
            {
                if (result.Count >= MaxProviders)
                {
                    break;
                }

                if (entry.ValueKind == JsonValueKind.Object && Text(entry, "name") is { Length: > 0 } name)
                {
                    result[name] = Strings(entry, "available_models");
                }
            }
        }
        catch (JsonException)
        {
            // Not an overlay: no instance has models of its own.
        }

        return result;
    }

    private static string? ReadBounded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    private static IReadOnlyList<string> Strings(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (result.Count >= MaxModelsPerProvider)
            {
                break;
            }

            // A model id is one line of printable text: anything else would be an id nobody could type, and could not be shown on one row.
            if (item.ValueKind == JsonValueKind.String &&
                item.GetString()?.Trim() is { Length: > 0 } id &&
                id.Length <= MaxModelLength &&
                !id.Any(char.IsControl) &&
                !result.Contains(id, StringComparer.Ordinal))
            {
                result.Add(id);
            }
        }

        return result;
    }
}

/// <summary>One row of the model picker: a catalogue model, or the text typed so far offered as it stands.</summary>
/// <param name="Value">What choosing the row puts in the box.</param>
/// <param name="IsTyped">True for the "use as typed" row, which is not in the catalogue.</param>
public sealed record ModelPickerRow(string Value, bool IsTyped)
{
    /// <summary>What the row says: the id, or that the typed text is used as it is.</summary>
    public string Label => IsTyped ? $"Use “{Value}” as typed" : Value;

    /// <summary>The name a screen reader gives the row.</summary>
    public string AutomationName => IsTyped ? $"Use {Value} as typed, it is not in the catalogue" : Value;
}

/// <summary>
/// The model picker's two rules, as the TUI has them (<c>filter_models</c> and <c>picker_rows</c> in <c>tui/screens/model_picker.py</c>): a
/// case-insensitive match of the typed text against the ids, best first, and a first row that is the typed text itself when no id is exactly it,
/// so Enter always has something to use and an id the catalogue has not shipped yet can still be entered.
/// </summary>
public static class ModelPicker
{
    /// <summary>
    /// The models that match <paramref name="query"/>: an exact match first, then ids that start with it, then ids that contain it (earlier in
    /// the id first), each group in the catalogue's order. An empty query is every model in its order.
    /// </summary>
    public static IReadOnlyList<string> Filter(string? query, IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        var q = (query ?? string.Empty).Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return models.ToArray();
        }

        var scored = new List<(int Score, int Index, string Model)>();
        for (var i = 0; i < models.Count; i++)
        {
            var lowered = models[i].ToLowerInvariant();
            int score;
            if (string.Equals(lowered, q, StringComparison.Ordinal))
            {
                score = 0;
            }
            else if (lowered.StartsWith(q, StringComparison.Ordinal))
            {
                score = 1;
            }
            else
            {
                var at = lowered.IndexOf(q, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                score = 2 + at;
            }

            scored.Add((score, i, models[i]));
        }

        return scored.OrderBy(s => s.Score).ThenBy(s => s.Index).Select(s => s.Model).ToArray();
    }

    /// <summary>
    /// The rows to show for <paramref name="query"/>: the matching models, behind a row for the typed text itself unless it already is one of the
    /// models exactly (compared as typed, with its case).
    /// </summary>
    public static IReadOnlyList<ModelPickerRow> Rows(string? query, IReadOnlyList<string> models)
    {
        var rows = Filter(query, models).Select(m => new ModelPickerRow(m, false)).ToList();
        var typed = (query ?? string.Empty).Trim();
        if (typed.Length > 0 && !models.Contains(typed, StringComparer.Ordinal))
        {
            rows.Insert(0, new ModelPickerRow(typed, true));
        }

        return rows;
    }
}
