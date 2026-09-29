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
/// Builds the FORM tab's section tree from <c>defenseclaw config show --source --format yaml</c>
/// output, cross-referenced against the raw <c>config.yaml</c> so each field knows
/// whether <see cref="YamlSectionEditor"/> can actually locate it for editing.
/// <para>
/// <b>Why <c>--source</c>.</b> On DefenseClaw 0.8.10 (config v8) <c>config show --effective</c> returns
/// only the <c>observability</c> section, so FORM was empty. <c>--source</c> returns every section that
/// is in config.yaml (verified: <c>ai_discovery</c>, <c>cisco_ai_defense</c>, <c>claw</c>,
/// <c>config_version</c>, <c>gateway</c>, <c>guardrail</c>, <c>llm</c>, <c>observability</c>) with the
/// file's own values — but <b>masked</b>: secrets and header values are <c>[REDACTED]</c>, and URLs with
/// userinfo, a path or a query come back partly redacted. Every field whose key is secret-shaped, whose
/// value is a mask placeholder, or which sits under a headers map is therefore built read-only (see
/// <see cref="SensitiveKeyClassifier"/>): FORM can show it, never write it.
/// </para>
/// <para>
/// Top-level keys become <see cref="FormSection"/> cards. Within a section, scalars
/// become typed fields, sequences of scalars become simple list editors, and nested
/// mappings become sub-groups, recursively. Anything this scheme cannot represent safely
/// — a sequence containing mappings, or nesting past <see cref="MaxDepth"/> — renders as
/// a read-only, pretty-printed <see cref="RawBlockNode"/> instead of failing the whole
/// section. This is exactly the "unknown/complex nodes render read-only" rule from the
/// spec, and it is why a section like <c>observability</c> — mostly bucket/route tables once
/// it is configured — still renders (as a handful of real fields plus raw blocks) instead of
/// not rendering at all.
/// </para>
/// </summary>
public static class ConfigFormBuilder
{
    private const int MaxDepth = 8;

    /// <summary>Top-level scalars that are shown but never editable from FORM, with the reason.</summary>
    private static readonly Dictionary<string, string> ManagedKeys = new(StringComparer.Ordinal)
    {
        ["config_version"] = "The config schema version is managed by DefenseClaw — editing it by hand can make the CLI reject or migrate the file. Use the RAW tab if you really need to.",
    };

    private const string SecretReason =
        "Secret — shown masked, never written from this form. Edit it in the RAW tab, or keep the value out of config.yaml: store it with `defenseclaw keys set <ENV_NAME>` and point the matching *_env setting at that name.";

    private const string MaskedReason =
        "Part of this value is masked by `defenseclaw config show --source`, so FORM cannot write it back without destroying the real value. Edit it in the RAW tab.";

    private const string HeaderMapReason =
        "Header values are masked by `defenseclaw config show --source`. Edit them in the RAW tab.";

    // Only for rendering RawBlockNode.Yaml — re-serializes a subtree rather than slicing the
    // original text by YamlNode.Start/End marks, which turned out to span only a node's
    // first character for block sequences/mappings in this version of YamlDotNet's
    // representation model (verified against the live effective config: every raw block
    // came back as a single "-"). Re-serializing loses exact source formatting but is
    // guaranteed correct, and this text is read-only display, not a round-trip target.
    private static readonly ISerializer YamlBlockSerializer = new SerializerBuilder().Build();

    public sealed record BuildResult(IReadOnlyList<FormSection> Sections, IReadOnlyList<string> Warnings);

    /// <param name="sourceYaml">Stdout of <c>defenseclaw config show --source --format yaml</c> (masked).</param>
    /// <param name="document">The raw config.yaml, for editability probing and current-on-disk field lookups.</param>
    /// <param name="onFieldCommitted">Invoked whenever a generated field or list changes.</param>
    public static BuildResult Build(
        string sourceYaml,
        ConfigDocument document,
        Action<FormField> onFieldCommitted,
        Action<FormListField> onListCommitted)
    {
        ArgumentNullException.ThrowIfNull(sourceYaml);
        ArgumentNullException.ThrowIfNull(document);

        var warnings = new List<string>();
        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(sourceYaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            warnings.Add($"Could not parse the configuration the CLI returned: {ex.Message}");
            return new BuildResult(Array.Empty<FormSection>(), warnings);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            warnings.Add("The configuration the CLI returned did not contain a YAML mapping at its root.");
            return new BuildResult(Array.Empty<FormSection>(), warnings);
        }

        var sections = new List<FormSection>();

        // The CLI sorts keys alphabetically; the schema version reads better last than first.
        var ordered = Entries(root)
            .OrderBy(entry => ManagedKeys.ContainsKey(ScalarText(entry.Key)) ? 1 : 0)
            .ToList();

        foreach (var (keyNode, valueNode) in ordered)
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
        var text = scalar.Value ?? string.Empty;

        // Masking comes first and overrides everything the raw-document probe would say: the text this
        // field holds came from `config show --source`, and if it is a placeholder (or the field is a
        // secret, or sits under a headers map) FORM must never write it back. There is no path from
        // here to a config.yaml edit for such a field — it is built non-editable, it never commits, and
        // the view-model refuses it a second time.
        bool isEditable;
        string? reason;
        var isMasked = false;

        if (kind == FormFieldKind.Secret)
        {
            (isEditable, reason, isMasked) = (false, SecretReason, true);
        }
        else if (SensitiveKeyClassifier.IsMaskedValue(text))
        {
            (isEditable, reason, isMasked) = (false, MaskedReason, true);
        }
        else if (HasHeaderMapAncestor(path))
        {
            (isEditable, reason, isMasked) = (false, HeaderMapReason, true);
        }
        else if (path.Count == 1 && ManagedKeys.TryGetValue(path[0], out var managedReason))
        {
            (isEditable, reason) = (false, managedReason);
        }
        else
        {
            (isEditable, reason) = ProbeScalar(document, path);

            // A plain (unquoted) scalar that is not a bool or an int but still reads as a
            // non-string — a float, "null"/"yes", a date — would be written back through the
            // string path, which quotes it ('0.85'), silently changing its YAML type. Refuse the
            // edit instead; RAW keeps the type intact.
            if (isEditable &&
                kind == FormFieldKind.String &&
                scalar.Style == ScalarStyle.Plain &&
                YamlSectionEditor.LooksLikeNonStringPlainScalar(text))
            {
                (isEditable, reason) = (false, "This value is a number, date or null/yes/no word rather than a string — edit it in the RAW tab so its type is preserved.");
            }
        }

        var dotted = string.Join('.', path);

        group.Fields.Add(new FormField(key, Humanize(key), dotted, kind, value, isEditable, reason, onFieldCommitted, isMasked));
    }

    /// <summary>True when any ancestor key of the value at <paramref name="path"/> is a headers map (the CLI masks every string beneath one).</summary>
    private static bool HasHeaderMapAncestor(IReadOnlyList<string> path)
    {
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (SensitiveKeyClassifier.IsHeaderMapKey(path[i]))
            {
                return true;
            }
        }

        return false;
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
            var items = seq.Children.Cast<YamlScalarNode>().Select(s => s.Value ?? string.Empty).ToList();

            // A list is rewritten whole when it changes, so one masked item (or a secret-shaped or
            // headers key) would be written back as its placeholder: lock the whole list instead.
            bool isEditable;
            string? reason;
            if (SensitiveKeyClassifier.IsSecretKey(key) && items.Count > 0)
            {
                (isEditable, reason) = (false, SecretReason);
            }
            else if (items.Any(SensitiveKeyClassifier.IsMaskedValue))
            {
                (isEditable, reason) = (false, MaskedReason);
            }
            else if (SensitiveKeyClassifier.IsHeaderMapKey(key) || HasHeaderMapAncestor(path))
            {
                (isEditable, reason) = (false, HeaderMapReason);
            }
            else
            {
                (isEditable, reason) = ProbeList(document, path);
            }

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
            return (false, "This key is not on a line of its own in config.yaml (it may be written inline, or the RAW tab changed since the form was built) — edit it in the RAW tab.");
        }

        if (lookup.Ambiguous)
        {
            return (false, "This key appears more than once in config.yaml — edit it in the RAW tab.");
        }

        if (lookup.Unsupported)
        {
            return (false, "This value has a trailing comment, is a block scalar/anchor/alias, or continues on the next line — edit it in the RAW tab.");
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
            return (false, "This list is not written as a block of '- item' lines in config.yaml (it may be inline, or the RAW tab changed since the form was built) — edit it in the RAW tab.");
        }

        if (lookup.Ambiguous)
        {
            return (false, "This key appears more than once in config.yaml — edit it in the RAW tab.");
        }

        if (lookup.Unsupported)
        {
            return (false, "This list has comments, nested or non-scalar items, or other content a rewrite could corrupt — edit it in the RAW tab.");
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

            // A plain number under a secret-shaped key (`max_tokens: 4096`) is not a secret: `config show`
            // masks a secret's text, so an unmasked number here is an ordinary setting.
            if (raw.Length > 0 &&
                (raw[0] == '-' || char.IsDigit(raw[0])) &&
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                number is >= int.MinValue and <= int.MaxValue)
            {
                return (FormFieldKind.Int, (int)number);
            }
        }

        if (SensitiveKeyClassifier.IsSecretKey(key))
        {
            return (FormFieldKind.Secret, raw);
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
