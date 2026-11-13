using System.Text;
using System.Text.Json;

namespace DefenseClaw.Core.Policy.Model;

/// <summary>
/// The posture (<c>strict</c>, <c>default</c> or <c>permissive</c>) of a rule-pack folder, read the way the gateway reads it (DefenseClaw 1.0.9).
/// <para>
/// The gateway takes the posture from the folder's <c>defenseclaw-pack.json</c> (<c>{"posture":"default"}</c> in the three built-in packs of 1.0.9,
/// <c>guardrail.ReadPackPosture</c>, internal/guardrail/compose.go@1.0.9:349-369): a missing file, a file over 1 MiB (<c>maxRulePackFileBytes</c>,
/// rulepack.go:308), bad JSON or any value other than those three counts as no posture, and then the folder's name decides
/// (<c>guardrailProfileForDir</c>, internal/gateway/decision.go@1.0.9:346-357: <c>strict</c> or <c>permissive</c> keep theirs, everything else is
/// <c>default</c>; thresholds.go@1.0.9:72-77 <c>postureFromManifest</c>). A runtime before 1.0.9 ships no such file in its built-in packs, so there the name
/// decides, as it always did. Only the one file in the pack folder is read; no path inside it is followed and nothing else in it is trusted.
/// </para>
/// <para>
/// <b>What this is not.</b> A broken <em>selected</em> <c>custom_packs</c> entry (folder missing or unreadable, the loader rejects it, a digest that does not match)
/// makes the <em>hook outage tier</em> strict (internal/config/hook_outage_fail_mode.go@1.0.9:151-162). That verdict needs the loader and the pinned digest, which
/// this app cannot reproduce, so it is never claimed here: a folder that cannot be read has the posture of its name.
/// </para>
/// </summary>
public static class RulePackPosture
{
    /// <summary>The manifest's file name in a pack folder (<c>PackManifestFile</c>).</summary>
    public const string ManifestFile = "defenseclaw-pack.json";

    /// <summary>The most bytes of the manifest the gateway reads (<c>maxRulePackFileBytes</c>, 1 MiB).</summary>
    public const int MaxManifestBytes = 1 << 20;

    /// <summary>The posture of <paramref name="folder"/>: its manifest's, else its name's.</summary>
    /// <param name="folder">The pack folder; empty reads as <c>default</c>.</param>
    /// <param name="readText">Reads a file's text, or null when it is missing or unreadable; null reads the file system. A test seam.</param>
    public static string For(string? folder, Func<string, string?>? readText = null) =>
        FromManifest(folder, readText) ?? FromFolderName(folder);

    /// <summary>The posture the folder's <c>defenseclaw-pack.json</c> names, or null when there is no usable one (missing, too large, not JSON, no or another value).</summary>
    public static string? FromManifest(string? folder, Func<string, string?>? readText = null)
    {
        var dir = (folder ?? string.Empty).Trim();
        if (dir.Length == 0)
        {
            return null;
        }

        string? text;
        try
        {
            text = (readText ?? ReadFromDisk)(Path.Combine(dir, ManifestFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > MaxManifestBytes)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("posture", out var posture)
                || posture.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return posture.GetString()?.Trim().ToLowerInvariant() switch
            {
                "strict" => "strict",
                "default" => "default",
                "permissive" => "permissive",
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The posture the folder's name gives (<c>guardrailProfileForDir</c>): the last path segment, lowercased; <c>strict</c> and <c>permissive</c> keep theirs, everything else
    /// is <c>default</c>.
    /// </summary>
    public static string FromFolderName(string? folder)
    {
        var raw = (folder ?? string.Empty).Trim().TrimEnd('/', '\\');
        if (raw.Length == 0)
        {
            return "default";
        }

        var cut = raw.LastIndexOfAny(new[] { '/', '\\' });
        var name = (cut >= 0 ? raw[(cut + 1)..] : raw).ToLowerInvariant();
        return name is "strict" or "permissive" ? name : "default";
    }

    /// <summary>The manifest's text, at most <see cref="MaxManifestBytes"/> bytes; null when the file is missing, larger or unreadable. A byte-order mark is kept (Go's JSON refuses one).</summary>
    private static string? ReadFromDisk(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxManifestBytes)
            {
                return null;
            }

            return Encoding.UTF8.GetString(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
