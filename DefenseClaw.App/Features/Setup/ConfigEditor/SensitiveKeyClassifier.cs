using System.Text.RegularExpressions;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// Decides whether a YAML key names an environment-variable reference (e.g.
/// <c>token_env</c>, <c>api_key_env</c>) or a literal secret (e.g. <c>token</c>), and whether a
/// value the CLI handed back is one of its masking placeholders.
/// <para>
/// The two key shapes look similar — both match <c>*token*</c>/<c>*key*</c>/<c>*secret*</c> — but an
/// <c>_env</c> key holds a variable *name*, never the value itself, so it is safe to show
/// in the clear (that is what the CLI itself does — "env-var names are always shown").
/// A literal secret key is checked first-and-only-if it is not an <c>_env</c> key.
/// </para>
/// <para>
/// <b>Masks.</b> The FORM tab is built from <c>defenseclaw config show --source</c>, which is a
/// *masked* projection of config.yaml: secret fields become <c>[REDACTED]</c>, header values
/// become <c>[REDACTED]</c>, and a URL that carries userinfo, a path, a query or a fragment comes
/// back as <c>[REDACTED_URL]</c> or with <c>/[REDACTED]</c> in place of its path. (Older CLIs mask
/// with <c>***</c>.) A masked text is a placeholder, not the value — writing it back into
/// config.yaml would destroy the real secret or URL. <see cref="IsMaskedValue"/> and
/// <see cref="CountMaskMarkers"/> are how the form builder and the save pipeline recognise that.
/// </para>
/// </summary>
public static partial class SensitiveKeyClassifier
{
    /// <summary>True for keys like <c>token_env</c> or <c>api_key_env</c> — a variable name, not a value.</summary>
    public static bool IsEnvNameKey(string key) =>
        key.EndsWith("_env", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for keys that carry a literal secret value: anything matching
    /// <c>*token*</c>/<c>*key*</c>/<c>*secret*</c>/<c>*password*</c>/<c>*credential*</c>/<c>*authorization*</c>/<c>*bearer*</c>/<c>*_pem</c>
    /// that is not itself an <see cref="IsEnvNameKey"/>.
    /// </summary>
    public static bool IsSecretKey(string key) =>
        !IsEnvNameKey(key) && SecretKeyPattern().IsMatch(key);

    /// <summary>
    /// True for a map key whose children are HTTP header values (<c>headers</c>, <c>extra_headers</c>):
    /// <c>config show --source</c> replaces every string beneath one with <c>[REDACTED]</c>, so nothing
    /// under it is safe to write back from FORM even when its own text does not look masked.
    /// </summary>
    public static bool IsHeaderMapKey(string key) =>
        key.Equals("headers", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("extra_headers", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="value"/> is (or contains) one of the CLI's masking placeholders:
    /// <c>[REDACTED]</c>, <c>[REDACTED_URL]</c>, <c>/[REDACTED]</c> inside a URL, <c>***REDACTED***</c>,
    /// or a run of asterisks. Such a value is display-only.
    /// </summary>
    public static bool IsMaskedValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Contains("[REDACTED", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("REDACTED]", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("***REDACTED***", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Legacy masks: "***", "****", "abcd***wxyz" (partial reveal).
        return value.Contains("***", StringComparison.Ordinal);
    }

    /// <summary>
    /// How many masking placeholders <paramref name="text"/> contains. The save-side invariant is
    /// "a FORM edit never raises this count": a patched document with more placeholders than the
    /// one it replaced has had a mask written into it.
    /// </summary>
    public static int CountMaskMarkers(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return MaskMarkerPattern().Count(text);
    }

    /// <summary>
    /// Scans a block of raw YAML text for lines that look like they set an <c>_env</c>-style
    /// or secret-style key, regardless of nesting depth. Used to decide whether the window
    /// title bar should carry the "contains secret references" hint. A best-effort textual
    /// scan is enough here — false positives only make the hint show up slightly more often,
    /// never leak anything, since it never inspects the value.
    /// </summary>
    public static bool ContainsSensitiveReferences(string rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return false;
        }

        foreach (var line in rawText.Split('\n'))
        {
            var match = KeyLinePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value;
            if (IsEnvNameKey(key) || IsSecretKey(key))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"(token|key|secret|passw(or)?d|credential|authorization|bearer|_pem$)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyPattern();

    [GeneratedRegex(@"\[REDACTED[A-Z_]*\]|\*\*\*", RegexOptions.IgnoreCase)]
    private static partial Regex MaskMarkerPattern();

    [GeneratedRegex(@"^\s*(?:-\s*)?(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-]*)\s*:")]
    private static partial Regex KeyLinePattern();
}
