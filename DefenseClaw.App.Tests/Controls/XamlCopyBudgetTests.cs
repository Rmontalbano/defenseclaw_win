using System.Net;
using System.Text.RegularExpressions;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Controls;

/// <summary>
/// The copy rules for text the app's XAML spells out itself (CUST-460): a literal <c>Text</c>, <c>Content</c>, <c>Header</c> or <c>ToolTip</c>
/// attribute stays at most <see cref="MaxChars"/> characters, and no literal uses a "(s)" plural. Bound values (<c>{Binding …}</c>) are not checked
/// here; their strings live in view-models. This reads every <c>*.xaml</c> of the app.
/// </summary>
public sealed partial class XamlCopyBudgetTests
{
    /// <summary>The longest literal allowed: the tooltip budget (200) plus a small margin for an exact command or path.</summary>
    private const int MaxChars = 220;

    /// <summary>
    /// Files allowed past the length check, each with its reason. Keep this empty unless a literal cannot be shorter without losing a fact the
    /// user needs at that moment.
    /// </summary>
    private static readonly Dictionary<string, string> LengthAllowList = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void No_literal_text_content_header_or_tooltip_is_longer_than_the_budget()
    {
        var found = new List<string>();
        foreach (var (file, line, name, value) in Literals())
        {
            if (value.Length > MaxChars && !LengthAllowList.ContainsKey(Path.GetFileName(file)))
            {
                found.Add($"{Path.GetFileName(file)}:{line} {name} ({value.Length} chars): {value}");
            }
        }

        Assert.True(found.Count == 0, $"Literal XAML text over {MaxChars} characters:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void No_literal_text_content_header_or_tooltip_uses_a_parenthesised_plural()
    {
        var found = Literals()
            .Where(static l => l.Value.Contains("(s)", StringComparison.Ordinal))
            .Select(static l => $"{Path.GetFileName(l.File)}:{l.Line} {l.Name}: {l.Value}")
            .ToList();

        Assert.True(found.Count == 0, "Use a real plural, not \"(s)\":" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void The_scan_sees_the_app_s_literals()
    {
        // A guard that finds nothing would pass for the wrong reason: the app has hundreds of literal labels.
        Assert.True(Literals().Count() > 100);
    }

    private static IEnumerable<(string File, int Line, string Name, string Value)> Literals()
    {
        foreach (var file in AppSource.Xaml())
        {
            var text = File.ReadAllText(file);
            foreach (Match attribute in CopyAttribute().Matches(text))
            {
                var raw = attribute.Groups[2].Value;
                if (raw.StartsWith('{'))
                {
                    continue;
                }

                var line = text[..attribute.Index].Count(static c => c == '\n') + 1;
                yield return (file, line, attribute.Groups[1].Value, WebUtility.HtmlDecode(raw));
            }
        }
    }

    // The attribute itself, not one whose name ends the same way (AutomationProperties.HelpText, SearchHelpText, PlaceholderText, ToolTipService.*).
    [GeneratedRegex(@"(?<![\w.:])(Text|Content|Header|ToolTip)=""([^""]*)""")]
    private static partial Regex CopyAttribute();
}
