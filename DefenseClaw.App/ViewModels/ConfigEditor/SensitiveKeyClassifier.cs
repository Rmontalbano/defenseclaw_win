using System.Text.RegularExpressions;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// Decides whether a YAML key names an environment-variable reference (e.g.
/// <c>token_env</c>, <c>api_key_env</c>) or a literal secret (e.g. <c>token</c>).
/// <para>
/// The two look similar — both match <c>*token*</c>/<c>*key*</c>/<c>*secret*</c> — but an
/// <c>_env</c> key holds a variable *name*, never the value itself, so it is safe to show
/// in the clear (that is what the CLI itself does — "env-var names are always shown").
/// A literal secret key is checked first-and-only-if it is not an <c>_env</c> key.
/// </para>
/// </summary>
public static partial class SensitiveKeyClassifier
{
    /// <summary>True for keys like <c>token_env</c> or <c>api_key_env</c> — a variable name, not a value.</summary>
    public static bool IsEnvNameKey(string key) =>
        key.EndsWith("_env", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for keys that carry a literal secret value: anything matching
    /// <c>*token*</c>/<c>*key*</c>/<c>*secret*</c> that is not itself an <see cref="IsEnvNameKey"/>.
    /// </summary>
    public static bool IsSecretKey(string key) =>
        !IsEnvNameKey(key) && SecretKeyPattern().IsMatch(key);

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

    [GeneratedRegex(@"(token|key|secret)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyPattern();

    [GeneratedRegex(@"^\s*(?:-\s*)?(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-]*)\s*:")]
    private static partial Regex KeyLinePattern();
}
