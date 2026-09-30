using System.IO;
using System.Xml;
using DefenseClaw.App.Services.Appearance;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// YAML syntax highlighting for the RAW tab's AvalonEdit editor, in a palette per appearance style and mode.
/// <para>
/// AvalonEdit 6.3.1 ships XSHD definitions for C#, JSON, XML, PowerShell, etc. as embedded
/// resources, but none for YAML — see the shipped resource list under
/// <c>ICSharpCode.AvalonEdit.Highlighting.Resources.*.xshd</c>. Rather than add a loose
/// <c>.xshd</c> file (which would need a second, non-package csproj edit to wire up as an
/// embedded resource — out of scope for this change), the definition is an inline XML
/// string parsed at first use.
/// </para>
/// <para>
/// <b>Six palettes, one rule set.</b> Default dark is the original one (VS Code "Dark+" hues); on the light theme those pale
/// colours vanish (the punctuation grey is 1.5:1 on a near-white card), so Default light is the Visual Studio "Light+"
/// hues, each at 4.5:1 or better on white. Linear and TUI have their own pair each, drawn from that style's palette
/// (Linear's indigo, blue, green and orange tones; TUI's cyan / green / orange / violet from the CLI's own theme) and held
/// to the same 4.5:1 against the style's window and card colours by a test. The editor asks <see cref="ForCurrent"/> when
/// it opens and again whenever the appearance changes.
/// </para>
/// </summary>
public static class YamlHighlighting
{
    private const string Name = "DefenseClaw-YAML";

    private static readonly Dictionary<(AppearanceStyle, bool), IHighlightingDefinition> Cache = new();

    /// <summary>The Default dark definition (kept as the default entry point).</summary>
    public static IHighlightingDefinition Instance => ForTheme(dark: true);

    /// <summary>The definition for the look on screen now: the running style and mode, or Default in the mode WPF-UI is in when there is no service.</summary>
    public static IHighlightingDefinition ForCurrent()
    {
        if (AppearanceService.Current is { } appearance)
        {
            return For(appearance.EffectiveStyle, appearance.IsDark);
        }

        return ForTheme(Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() != Wpf.Ui.Appearance.ApplicationTheme.Light);
    }

    /// <summary>Lazily builds the Default definition for the given theme, returning the cached instance after the first call.</summary>
    public static IHighlightingDefinition ForTheme(bool dark) => For(AppearanceStyle.Default, dark);

    internal static IHighlightingDefinition For(AppearanceStyle style, bool dark)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((style, dark), out var cached))
            {
                return cached;
            }

            var isOriginal = style == AppearanceStyle.Default && dark;
            var definition = Load(isOriginal ? Name : $"{Name}-{style}-{(dark ? "Dark" : "Light")}", PaletteFor(style, dark));

            // Registering makes the definition discoverable by name (e.g. from a
            // FoldingStrategy or another editor instance) and idempotent to call twice.
            if (isOriginal && HighlightingManager.Instance.GetDefinition(Name) is null)
            {
                HighlightingManager.Instance.RegisterHighlighting(Name, new[] { ".yaml", ".yml" }, definition);
            }

            Cache[(style, dark)] = definition;
            return definition;
        }
    }

    /// <summary>The eight token colours for a look; internal so the contrast test reads exactly what the editor is given.</summary>
    internal static IReadOnlyDictionary<string, string> PaletteFor(AppearanceStyle style, bool dark) => (style, dark) switch
    {
        (AppearanceStyle.Linear, true) => LinearDarkPalette,
        (AppearanceStyle.Linear, false) => LinearLightPalette,
        (AppearanceStyle.Tui, true) => TuiDarkPalette,
        (AppearanceStyle.Tui, false) => TuiLightPalette,
        (_, true) => DarkPalette,
        _ => LightPalette,
    };

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

    // Linear: the style's own tones. Comments are the secondary grey nudged to 4.5:1, keys the accent-hover step (the
    // one that clears contrast in each mode), strings green, numbers orange, keywords blue.
    private static readonly Dictionary<string, string> LinearDarkPalette = new()
    {
        ["@COMMENT@"] = "#7A8088",
        ["@KEY@"] = "#9AA5F0",
        ["@STRING@"] = "#4CB782",
        ["@NUMBER@"] = "#F2994A",
        ["@KEYWORD@"] = "#4EA7FC",
        ["@ANCHOR@"] = "#E6C07B",
        ["@PUNCT@"] = "#8A8F98",
        ["@DOC@"] = "#C084FC",
    };

    private static readonly Dictionary<string, string> LinearLightPalette = new()
    {
        ["@COMMENT@"] = "#62666D",
        ["@KEY@"] = "#4C57C4",
        ["@STRING@"] = "#1F7A4F",
        ["@NUMBER@"] = "#B45309",
        ["@KEYWORD@"] = "#1D63B8",
        ["@ANCHOR@"] = "#8A5A00",
        ["@PUNCT@"] = "#4B4F55",
        ["@DOC@"] = "#7C3AED",
    };

    // TUI dark: the accent hues of the CLI's own theme (defenseclaw/tui/theme.py): cyan keys, green strings, orange
    // numbers, violet keywords, amber anchors, pink document markers; punctuation is its secondary text. The comment
    // colour is its muted text lightened a step, because the muted value itself is 4.2:1 on its base.
    private static readonly Dictionary<string, string> TuiDarkPalette = new()
    {
        ["@COMMENT@"] = "#7A8BA5",
        ["@KEY@"] = "#22D3EE",
        ["@STRING@"] = "#34D399",
        ["@NUMBER@"] = "#FB923C",
        ["@KEYWORD@"] = "#A78BFA",
        ["@ANCHOR@"] = "#FBBF24",
        ["@PUNCT@"] = "#9FB2CC",
        ["@DOC@"] = "#F472B6",
    };

    // TUI light ("paper terminal"): the same hues at their 700 steps.
    private static readonly Dictionary<string, string> TuiLightPalette = new()
    {
        ["@COMMENT@"] = "#5B6B82",
        ["@KEY@"] = "#0E7490",
        ["@STRING@"] = "#047857",
        ["@NUMBER@"] = "#B23A06",
        ["@KEYWORD@"] = "#6D28D9",
        ["@ANCHOR@"] = "#92400E",
        ["@PUNCT@"] = "#3B4A63",
        ["@DOC@"] = "#BE185D",
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
