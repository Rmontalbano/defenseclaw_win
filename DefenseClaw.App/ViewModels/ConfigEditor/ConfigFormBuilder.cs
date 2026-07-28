using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DefenseClaw.Core.Config;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// Builds the FORM tab's section tree from <c>defenseclaw config show --effective</c>
/// output, cross-referenced against the raw <c>config.yaml</c> so each field knows
/// whether <see cref="YamlSectionEditor"/> can actually locate it for editing.
/// <para>
/// Top-level keys become <see cref="FormSection"/> cards. Within a section, scalars
/// become typed fields, sequences of scalars become simple list editors, and nested
/// mappings become sub-groups, recursively. Anything this scheme cannot represent safely
/// — a sequence containing mappings, or nesting past <see cref="MaxDepth"/> — renders as
/// a read-only, pretty-printed <see cref="RawBlockNode"/> instead of failing the whole
/// section. This is exactly the "unknown/complex nodes render read-only" rule from the
/// spec, and it is why a section like <c>observability</c> — whose effective view is
/// mostly compiler-generated bucket/route/provenance tables — still renders (as a handful
/// of real fields plus several raw blocks) instead of not rendering at all.
/// </para>
/// </summary>
public static class ConfigFormBuilder
{
    private const int MaxDepth = 8;

    // Only for rendering RawBlockNode.Yaml — re-serializes a subtree rather than slicing the
    // original text by YamlNode.Start/End marks, which turned out to span only a node's
    // first character for block sequences/mappings in this version of YamlDotNet's
    // representation model (verified against the live effective config: every raw block
    // came back as a single "-"). Re-serializing loses exact source formatting but is
    // guaranteed correct, and this text is read-only display, not a round-trip target.
    private static readonly ISerializer YamlBlockSerializer = new SerializerBuilder().Build();

    public sealed record BuildResult(IReadOnlyList<FormSection> Sections, IReadOnlyList<string> Warnings);

    /// <param name="effectiveYaml">Stdout of <c>defenseclaw config show --effective --format yaml</c>.</param>
    /// <param name="document">The raw config.yaml, for editability probing and current-on-disk field lookups.</param>
    /// <param name="onFieldCommitted">Invoked whenever a generated field or list changes.</param>
    public static BuildResult Build(
        string effectiveYaml,
        ConfigDocument document,
        Action<FormField> onFieldCommitted,
        Action<FormListField> onListCommitted)
    {
        ArgumentNullException.ThrowIfNull(effectiveYaml);
        ArgumentNullException.ThrowIfNull(document);

        var warnings = new List<string>();
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(effectiveYaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            warnings.Add($"Could not parse the effective configuration: {ex.Message}");
            return new BuildResult(Array.Empty<FormSection>(), warnings);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings.Add("The effective configuration did not contain a YAML mapping at its root.");
            return new BuildResult(Array.Empty<FormSection>(), warnings);
        }

        var sections = new List<FormSection>();
        foreach (var (keyNode, valueNode) in Entries(root))
        {
            var key = ScalarText(keyNode);
            if (key.Length == 0)
            {
                continue;
            }

            var existsInRaw = document.SectionText(key) is not null;
            var section = new FormSection(
                key,
                Humanize(key),
                key,
                DefenseClawConfig.KnownSections.Contains(key),
                existsInRaw);

            var path = new List<string> { key };

            switch (valueNode)
            {
                case YamlMappingNode map:
                    Populate(section, map, path, document, onFieldCommitted, onListCommitted, depth: 1);
                    break;
                case YamlScalarNode scalar:
                    AddScalarField(section, key, path, scalar, document, onFieldCommitted);
                    break;
                case YamlSequenceNode seq:
                    AddSequenceEntry(section, key, path, seq, document, onFieldCommitted, onListCommitted);
                    break;
            }

            sections.Add(section);
        }

        return new BuildResult(sections, warnings);
    }

    private static void Populate(
        FormGroup group,
        YamlMappingNode node,
        List<string> path,
        ConfigDocument document,
        Action<FormField> onFieldCommitted,
        Action<FormListField> onListCommitted,
        int depth)
    {
        foreach (var (keyNode, valueNode) in Entries(node))
        {
            var key = ScalarText(keyNode);
            if (key.Length == 0)
            {
                continue;
            }

            path.Add(key);
            try
            {
                switch (valueNode)
                {
                    case YamlScalarNode scalar:
                        AddScalarField(group, key, path, scalar, document, onFieldCommitted);
                        break;

                    case YamlSequenceNode seq:
                        AddSequenceEntry(group, key, path, seq, document, onFieldCommitted, onListCommitted);
                        break;

                    case YamlMappingNode map:
                        if (depth >= MaxDepth)
                        {
                            group.RawBlocks.Add(RawBlockFor(key, path, map, "Nested too deeply to edit as fields."));
                            break;
                        }

                        var subGroup = new FormGroup(key, Humanize(key), string.Join('.', path));
                        Populate(subGroup, map, path, document, onFieldCommitted, onListCommitted, depth + 1);
                        if (subGroup.HasContent)
                        {
                            group.SubGroups.Add(subGroup);
                        }

                        break;
                }
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private static void AddScalarField(
        FormGroup group,
        string key,
        List<string> path,
        YamlScalarNode scalar,
        ConfigDocument document,
        Action<FormField> onFieldCommitted)
    {
        var (kind, value) = ClassifyScalar(key, scalar);
        var (isEditable, reason) = ProbeScalar(document, path);
        var dotted = string.Join('.', path);

        group.Fields.Add(new FormField(key, Humanize(key), dotted, kind, value, isEditable, reason, onFieldCommitted));
    }

    private static void AddSequenceEntry(
        FormGroup group,
        string key,
        List<string> path,
        YamlSequenceNode seq,
        ConfigDocument document,
        Action<FormField> onFieldCommitted,
        Action<FormListField> onListCommitted)
    {
        var dotted = string.Join('.', path);

        if (seq.Children.All(c => c is YamlScalarNode))
        {
            var items = seq.Children.Cast<YamlScalarNode>().Select(s => s.Value ?? string.Empty);
            var (isEditable, reason) = ProbeList(document, path);
            group.Lists.Add(new FormListField(key, Humanize(key), dotted, items, isEditable, reason, onListCommitted));
            return;
        }

        group.RawBlocks.Add(RawBlockFor(key, path, seq, "A list of structured entries — edit in RAW."));
    }

    private static RawBlockNode RawBlockFor(string key, List<string> path, YamlNode node, string reason)
    {
        var dotted = string.Join('.', path);
        var yaml = ExtractSource(node);
        return new RawBlockNode(key, Humanize(key), dotted, yaml, reason);
    }

    /// <summary>Re-serializes the subtree to YAML for display — see <see cref="YamlBlockSerializer"/> for why.</summary>
    private static string ExtractSource(YamlNode node)
    {
        try
        {
            return YamlBlockSerializer.Serialize(ToPlainObject(node)).TrimEnd();
        }
        catch (YamlException)
        {
            return string.Empty;
        }
    }

    private static object? ToPlainObject(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value,
        YamlSequenceNode seq => seq.Children.Select(ToPlainObject).ToList(),
        YamlMappingNode map => map.Children.ToDictionary(kv => ScalarText(kv.Key), kv => ToPlainObject(kv.Value)),
        _ => null,
    };

    private static (bool IsEditable, string? Reason) ProbeScalar(ConfigDocument document, IReadOnlyList<string> path)
    {
        var sectionText = document.SectionText(path[0]);
        if (sectionText is null)
        {
            return (false, "Not present in config.yaml. Add it in the RAW tab first.");
        }

        var lookup = YamlSectionEditor.FindScalar(sectionText, path);
        if (!lookup.Found)
        {
            return (false, "Not set explicitly in config.yaml (showing the effective default). Add it in RAW to edit here.");
        }

        if (lookup.Ambiguous)
        {
            return (false, "This key appears more than once in config.yaml — edit it in the RAW tab.");
        }

        return (true, null);
    }

    private static (bool IsEditable, string? Reason) ProbeList(ConfigDocument document, IReadOnlyList<string> path)
    {
        var sectionText = document.SectionText(path[0]);
        if (sectionText is null)
        {
            return (false, "Not present in config.yaml. Add it in the RAW tab first.");
        }

        var lookup = YamlSectionEditor.FindList(sectionText, path);
        if (!lookup.Found)
        {
            return (false, "Not set explicitly in config.yaml (showing the effective default). Add it in RAW to edit here.");
        }

        if (lookup.Ambiguous)
        {
            return (false, "This key appears more than once in config.yaml — edit it in the RAW tab.");
        }

        return (true, null);
    }

    private static (FormFieldKind Kind, object? Value) ClassifyScalar(string key, YamlScalarNode node)
    {
        var raw = node.Value ?? string.Empty;

        if (SensitiveKeyClassifier.IsEnvNameKey(key))
        {
            return (FormFieldKind.EnvName, raw);
        }

        if (SensitiveKeyClassifier.IsSecretKey(key))
        {
            return (FormFieldKind.Secret, raw);
        }

        // Only unquoted scalars are candidates for bool/int — the source file quotes
        // things like '~' precisely to keep them strings, and that choice must survive.
        if (node.Style == ScalarStyle.Plain)
        {
            if (string.Equals(raw, "true", StringComparison.Ordinal))
            {
                return (FormFieldKind.Bool, true);
            }

            if (string.Equals(raw, "false", StringComparison.Ordinal))
            {
                return (FormFieldKind.Bool, false);
            }

            if (raw.Length > 0 &&
                (raw[0] == '-' || char.IsDigit(raw[0])) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                number is >= int.MinValue and <= int.MaxValue)
            {
                return (FormFieldKind.Int, (int)number);
            }
        }

        return (FormFieldKind.String, raw);
    }

    private static IEnumerable<(YamlNode Key, YamlNode Value)> Entries(YamlMappingNode node) =>
        node.Children.Select(kv => (kv.Key, kv.Value));

    private static string ScalarText(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;

    private static string Humanize(string key)
    {
        var parts = key.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return key;
        }

        return string.Join(' ', parts.Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
