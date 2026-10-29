using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Security;
using DefenseClaw.Core.Text;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>One row of the pre-save review: a changed or context line (already masked), or a fold standing for unchanged lines.</summary>
/// <param name="Kind">"Added", "Removed", "Context" or "Fold" (the tint key the view styles on).</param>
/// <param name="OldNumber">1-based line in the on-disk text, or null.</param>
/// <param name="NewNumber">1-based line in the edited text, or null.</param>
/// <param name="Marker">"+", "-", or a blank for context and folds.</param>
/// <param name="Text">The line as it may be shown: secret values replaced; for a fold, "N unchanged lines".</param>
public sealed record ConfigDiffLine(string Kind, int? OldNumber, int? NewNumber, string Marker, string Text)
{
    public string OldNumberText => OldNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    public string NewNumberText => NewNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

/// <summary>The pre-save review of a config edit: what to draw and the counts to say.</summary>
public sealed record ConfigDiffReview(IReadOnlyList<ConfigDiffLine> Lines, int Added, int Removed, bool TextsDiffer)
{
    /// <summary>True when at least one line was added or removed.</summary>
    public bool HasLineChanges => Added + Removed > 0;

    public string Summary =>
        HasLineChanges
            ? $"{Added:N0} line{(Added == 1 ? string.Empty : "s")} added, {Removed:N0} removed"
            : TextsDiffer
                ? "No line changes: only line endings or the final line break differ."
                : "No changes.";
}

/// <summary>
/// Builds the review shown before a save: the line diff of the text on disk against the edited RAW, with every secret
/// masked on both sides. The diff is computed on the real lines (so a changed secret still shows as a changed line) and
/// only the <i>rendering</i> is masked: the real text never reaches a <see cref="ConfigDiffLine"/>.
/// </summary>
public static partial class ConfigDiffReviewBuilder
{
    /// <summary>What replaces a hidden value.</summary>
    public const string Hidden = "[hidden]";

    /// <summary>Unchanged lines kept around each change.</summary>
    public const int ContextLines = 3;

    public static ConfigDiffReview Build(string onDiskText, string editedText)
    {
        var oldLines = LineDiff.SplitLines(onDiskText);
        var newLines = LineDiff.SplitLines(editedText);
        var oldMasked = MaskLines(oldLines);
        var newMasked = MaskLines(newLines);

        var entries = LineDiff.Compute(oldLines, newLines);
        var blocks = LineDiff.Collapse(entries, ContextLines);

        var lines = new List<ConfigDiffLine>();
        int added = 0, removed = 0;
        foreach (var block in blocks)
        {
            if (block.SkippedUnchanged > 0)
            {
                lines.Add(new ConfigDiffLine(
                    "Fold",
                    null,
                    null,
                    string.Empty,
                    $"{block.SkippedUnchanged:N0} unchanged line{(block.SkippedUnchanged == 1 ? string.Empty : "s")}"));
                continue;
            }

            foreach (var entry in block.Entries)
            {
                switch (entry.Change)
                {
                    case LineChange.Removed:
                        removed++;
                        lines.Add(new ConfigDiffLine("Removed", entry.OldIndex + 1, null, "-", oldMasked[entry.OldIndex!.Value]));
                        break;
                    case LineChange.Added:
                        added++;
                        lines.Add(new ConfigDiffLine("Added", null, entry.NewIndex + 1, "+", newMasked[entry.NewIndex!.Value]));
                        break;
                    default:
                        lines.Add(new ConfigDiffLine("Context", entry.OldIndex + 1, entry.NewIndex + 1, " ", newMasked[entry.NewIndex!.Value]));
                        break;
                }
            }
        }

        return new ConfigDiffReview(lines, added, removed, !string.Equals(onDiskText, editedText, StringComparison.Ordinal));
    }

    /// <summary>
    /// The lines with every secret replaced by <see cref="Hidden"/>: the value of a secret-named key (not an <c>_env</c>
    /// name), everything beneath a secret-named or header key, a value <see cref="SecretHeuristics"/> recognises whatever
    /// its key, a value in an <c>_env</c> key that <see cref="ConfigFieldValidator.LooksLikeSecretValue"/> calls a secret (a key
    /// pasted where a variable name belongs), and the userinfo of a URL. Same length as the input, line for line.
    /// </summary>
    public static string[] MaskLines(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var result = new string[lines.Count];

        // While set, every line indented deeper than this belongs to a secret/header key and is hidden whole.
        int? hiddenBelow = null;
        var headerMap = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (trimmed.Length == 0)
            {
                result[i] = line;
                continue;
            }

            if (hiddenBelow is { } below)
            {
                // YAML lets a list sit at the same indent as its key (the CLI writes it that way), so a "- " line at
                // the key's own indent still belongs to it.
                if (indent > below || (indent == below && (trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal))))
                {
                    // Under a header map the header names are not secret and make the diff readable; their values are.
                    var headerLine = headerMap ? KeyValuePattern().Match(line) : null;
                    result[i] = trimmed.StartsWith('#')
                        ? line
                        : headerLine is { Success: true } hm && hm.Groups["value"].Value.Trim().Length > 0
                            ? line[..hm.Groups["value"].Index].TrimEnd() + " " + Hidden
                            : headerLine is { Success: true }
                                ? line
                                : new string(' ', indent) + Hidden;
                    continue;
                }

                hiddenBelow = null;
            }

            if (trimmed.StartsWith('#'))
            {
                result[i] = line;
                continue;
            }

            var match = KeyValuePattern().Match(line);
            if (!match.Success)
            {
                // A list item or anything else that is not "key: value".
                result[i] = MaskValueText(line, wholeLineIsValue: trimmed.StartsWith("- ", StringComparison.Ordinal), indent);
                continue;
            }

            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();
            var prefix = line[..match.Groups["value"].Index].TrimEnd();
            var secretKey = SensitiveKeyClassifier.IsSecretKey(key) || SensitiveKeyClassifier.IsHeaderMapKey(key);

            if (secretKey)
            {
                if (value.Length == 0 || value[0] is '|' or '>' || value.StartsWith('#'))
                {
                    // Nothing on the line to hide; whatever is nested beneath it is the value.
                    hiddenBelow = indent;
                    headerMap = SensitiveKeyClassifier.IsHeaderMapKey(key) && !SensitiveKeyClassifier.IsSecretKey(key);
                    result[i] = line;
                }
                else
                {
                    result[i] = prefix + " " + Hidden;
                }

                continue;
            }

            if (value.Length == 0 || value[0] is '|' or '>')
            {
                result[i] = line;
                if (value.Length > 0 && SecretHeuristics.IsSecretName(key))
                {
                    hiddenBelow = indent;
                    headerMap = false;
                }

                continue;
            }

            // A secret pasted where an env var NAME belongs (the editor warns about it and lets it through): the TUI hides it in its own
            // review for any value that looks like one, whatever its shape (setup_state.py:575-580, mask_config_value).
            if (SensitiveKeyClassifier.IsEnvNameKey(key) && ConfigFieldValidator.LooksLikeSecretValue(value.Trim('"', '\'')))
            {
                result[i] = prefix + " " + Hidden;
                continue;
            }

            result[i] = prefix + " " + MaskValueText(value, wholeLineIsValue: true, 0);
        }

        return result;
    }

    /// <summary>Masks the value part of a line that is not under a secret key: a recognisable secret, or the userinfo of a URL.</summary>
    private static string MaskValueText(string text, bool wholeLineIsValue, int indent)
    {
        var value = text;
        var lead = string.Empty;
        if (indent > 0 || text.TrimStart().StartsWith("- ", StringComparison.Ordinal))
        {
            var trimmed = text.TrimStart();
            lead = text[..(text.Length - trimmed.Length)];
            value = trimmed;
        }

        var dash = string.Empty;
        if (wholeLineIsValue && value.StartsWith("- ", StringComparison.Ordinal))
        {
            dash = "- ";
            value = value[2..].TrimStart();
        }

        var unquoted = value.Trim().Trim('"', '\'');
        if (SecretHeuristics.LooksSecret(unquoted))
        {
            return lead + dash + Hidden;
        }

        var masked = UrlUserInfoPattern().Replace(value, "$1" + Hidden + "@");
        return lead + dash + UrlSecretQueryPattern().Replace(masked, "$1" + Hidden);
    }

    [GeneratedRegex(@"^\s*(?:-\s+)?(?<key>[A-Za-z0-9_][A-Za-z0-9_.\-]*)\s*:(?<value>(?:\s.*)?)$")]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"(://)[^/@\s""']+@")]
    private static partial Regex UrlUserInfoPattern();

    [GeneratedRegex(@"([?&][^=&\s""']*(?:key|token|secret|passw|credential|auth)[^=&\s""']*=)[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlSecretQueryPattern();
}
