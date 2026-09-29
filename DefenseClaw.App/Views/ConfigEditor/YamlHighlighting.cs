using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// YAML syntax highlighting for the RAW tab's AvalonEdit editor, in a dark and a light palette.
/// <para>
/// AvalonEdit 6.3.1 ships XSHD definitions for C#, JSON, XML, PowerShell, etc. as embedded
/// resources, but none for YAML — see the shipped resource list under
/// <c>ICSharpCode.AvalonEdit.Highlighting.Resources.*.xshd</c>. Rather than add a loose
/// <c>.xshd</c> file (which would need a second, non-package csproj edit to wire up as an
/// embedded resource — out of scope for this change), the definition is an inline XML
/// string parsed at first use.
/// </para>
/// <para>
/// <b>Two palettes, one rule set.</b> The dark palette is the original one (VS Code "Dark+" hues);
/// on the light theme those pale colours vanish (the punctuation grey is 1.5:1 on a near-white
/// card), so <see cref="ForTheme"/> hands back a second definition built from the same rules with
/// the Visual Studio "Light+" hues, each at 4.5:1 or better on white. The editor picks one at
/// start-up and again whenever the app theme changes.
/// </para>
/// </summary>
public static class YamlHighlighting
{
    private const string Name = "DefenseClaw-YAML";

    private static IHighlightingDefinition? _dark;
    private static IHighlightingDefinition? _light;

    /// <summary>The dark-theme definition (kept as the default entry point).</summary>
    public static IHighlightingDefinition Instance => ForTheme(dark: true);

    /// <summary>Lazily builds the definition for the given theme, returning the cached instance after the first call.</summary>
    public static IHighlightingDefinition ForTheme(bool dark)
    {
        if (dark)
        {
            if (_dark is not null)
            {
                return _dark;
            }

            var definition = Load(Name, DarkPalette);

            // Registering makes the definition discoverable by name (e.g. from a
            // FoldingStrategy or another editor instance) and idempotent to call twice.
            if (HighlightingManager.Instance.GetDefinition(Name) is null)
            {
                HighlightingManager.Instance.RegisterHighlighting(Name, new[] { ".yaml", ".yml" }, definition);
            }

            _dark = definition;
            return definition;
        }

        return _light ??= Load(Name + "-Light", LightPalette);
    }

    private static IHighlightingDefinition Load(string name, IReadOnlyDictionary<string, string> palette)
    {
        var xshd = Xshd.Replace("@NAME@", name, StringComparison.Ordinal);
        foreach (var (token, color) in palette)
        {
            xshd = xshd.Replace(token, color, StringComparison.Ordinal);
        }

        using var reader = new XmlTextReader(new StringReader(xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static readonly Dictionary<string, string> DarkPalette = new()
    {
        ["@COMMENT@"] = "#6A9955",
        ["@KEY@"] = "#4FC1FF",
        ["@STRING@"] = "#CE9178",
        ["@NUMBER@"] = "#B5CEA8",
        ["@KEYWORD@"] = "#569CD6",
        ["@ANCHOR@"] = "#D7BA7D",
        ["@PUNCT@"] = "#D4D4D4",
        ["@DOC@"] = "#C586C0",
    };

    // Visual Studio "Light+" hues; every one is >= 4.5:1 on white.
    private static readonly Dictionary<string, string> LightPalette = new()
    {
        ["@COMMENT@"] = "#008000",
        ["@KEY@"] = "#0451A5",
        ["@STRING@"] = "#A31515",
        ["@NUMBER@"] = "#0B6E4F",
        ["@KEYWORD@"] = "#0000FF",
        ["@ANCHOR@"] = "#795E26",
        ["@PUNCT@"] = "#444444",
        ["@DOC@"] = "#AF00DB",
    };

    // Rule order matters: spans (comments/strings) are matched before the looser
    // punctuation/key rules get a chance at the same text. Colours are @TOKENS@ filled from a palette.
    private const string Xshd = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="@NAME@" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="@COMMENT@" />
          <Color name="Key" foreground="@KEY@" fontWeight="bold" />
          <Color name="String" foreground="@STRING@" />
          <Color name="Number" foreground="@NUMBER@" />
          <Color name="Keyword" foreground="@KEYWORD@" fontWeight="bold" />
          <Color name="Anchor" foreground="@ANCHOR@" />
          <Color name="Punctuation" foreground="@PUNCT@" />
          <Color name="DocMarker" foreground="@DOC@" fontWeight="bold" />

          <RuleSet>
            <Span color="Comment" begin="#" />
            <Span color="String" begin="&quot;" end="&quot;" />
            <Span color="String" begin="'" end="'" />

            <Rule color="DocMarker">^(---|\.\.\.)\s*$</Rule>

            <Rule color="Key">^[ \t]*(- )?[A-Za-z0-9_][A-Za-z0-9_.\-]*(?=[ \t]*:([ \t]|$))</Rule>

            <Rule color="Keyword">\b(true|false|True|False|TRUE|FALSE|null|Null|NULL|yes|no|Yes|No)\b|(?&lt;=[ \t:\[,])~(?=[ \t,\]\r\n]|$)</Rule>

            <Rule color="Number">\b-?[0-9]+(\.[0-9]+)?\b</Rule>

            <Rule color="Anchor">[&amp;*][A-Za-z0-9_\-]+</Rule>

            <Rule color="Punctuation">[:\[\]{}]|(^[ \t]*-(?=[ \t]|$))</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
