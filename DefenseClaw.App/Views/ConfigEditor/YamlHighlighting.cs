using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace DefenseClaw.App.Views.ConfigEditor;

/// <summary>
/// YAML syntax highlighting for the RAW tab's AvalonEdit editor.
/// <para>
/// AvalonEdit 6.3.1 ships XSHD definitions for C#, JSON, XML, PowerShell, etc. as embedded
/// resources, but none for YAML — see the shipped resource list under
/// <c>ICSharpCode.AvalonEdit.Highlighting.Resources.*.xshd</c>. Rather than add a loose
/// <c>.xshd</c> file (which would need a second, non-package csproj edit to wire up as an
/// embedded resource — out of scope for this change), the definition is an inline XML
/// string parsed at first use. Colors are chosen to read reasonably in both the app's dark
/// and light Fluent themes: mid-brightness hues rather than pure white/black.
/// </para>
/// </summary>
public static class YamlHighlighting
{
    private const string Name = "DefenseClaw-YAML";

    private static IHighlightingDefinition? _definition;

    /// <summary>Lazily builds and registers the definition, returning the cached instance after the first call.</summary>
    public static IHighlightingDefinition Instance
    {
        get
        {
            if (_definition is not null)
            {
                return _definition;
            }

            using var reader = new XmlTextReader(new StringReader(Xshd));
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);

            // Registering makes the definition discoverable by name (e.g. from a
            // FoldingStrategy or another editor instance) and idempotent to call twice.
            if (HighlightingManager.Instance.GetDefinition(Name) is null)
            {
                HighlightingManager.Instance.RegisterHighlighting(Name, new[] { ".yaml", ".yml" }, definition);
            }

            _definition = definition;
            return definition;
        }
    }

    // Rule order matters: spans (comments/strings) are matched before the looser
    // punctuation/key rules get a chance at the same text.
    private const string Xshd = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="DefenseClaw-YAML" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#6A9955" />
          <Color name="Key" foreground="#4FC1FF" fontWeight="bold" />
          <Color name="String" foreground="#CE9178" />
          <Color name="Number" foreground="#B5CEA8" />
          <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
          <Color name="Anchor" foreground="#D7BA7D" />
          <Color name="Punctuation" foreground="#D4D4D4" />
          <Color name="DocMarker" foreground="#C586C0" fontWeight="bold" />

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
