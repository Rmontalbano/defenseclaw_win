using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>
/// Lays the form out the way the runtime's own catalog does (CUST-391; the Mac's <c>ConfigEditorView</c> over <c>DynamicConfigCatalog</c>,
/// <c>Features/SetupView.swift:615-625</c> and <c>:928-975</c> at v1.1.34): one card per catalog section, in the catalog's order, each row where the catalog
/// puts it, then <see cref="RuntimeConfigCatalog.OtherSectionName"/> for what config.yaml sets and the catalog does not describe.
/// <para>
/// <b>The fields are the form's own.</b> Every row config.yaml sets is the <see cref="FormField"/>, <see cref="FormListField"/> or <see cref="RawBlockNode"/>
/// that <see cref="ConfigFormBuilder"/> built from <c>config show --source</c>, moved here unchanged: the same masking, the same editability probe against
/// the RAW text, the same commit into RAW, the same validation. A <c>.. Paths ..</c> separator starts a group card; a summary row (<c>Config Version</c>,
/// <c>Webhooks</c>) is an <see cref="FormFieldKind.Info"/> line; a setting the file does not set shows the runtime's value, read-only, with how to set it.
/// </para>
/// <para>
/// <b>What "described" means.</b> A key config.yaml sets is the catalog's when a catalog row names exactly that dotted key (the Mac's rule). Everything
/// else, a key under a read-only summary row included (<c>claude_code.enabled</c> under Agent Hooks), is listed under Other, grouped by its top-level
/// key. Unlike the Mac's read-only Other rows, these keep the form's own rules: this editor writes config.yaml's text itself, and 1.0.4's own writer
/// keeps a key it does not model as it is (<c>Config.save</c>, <c>config.py:4448-4450</c>), so an edit here is not lost by the next runtime save.
/// </para>
/// </summary>
public static class RuntimeCatalogLayout
{
    /// <summary>The note under a setting the runtime's catalog lists and config.yaml does not set.</summary>
    internal const string NotInFileReason = "Not set in config.yaml: the runtime uses the value shown. To change it, add the key in the RAW tab.";

    /// <summary>The same for a secret: there is nothing to show, and the way to set one keeps it out of the file.</summary>
    internal const string NotInFileSecretReason =
        "Not set in config.yaml. Keep a secret out of the file: store it with `defenseclaw keys set <ENV_NAME>` and point the matching *_env setting at that name.";

    internal const string OtherSummary = "Keys config.yaml sets that the runtime's catalog does not describe yet.";

    internal const string OtherHelp =
        "They keep this form's rules (secrets, masked values and structured entries stay read-only). Upgrade the runtime for a typed editor, or edit them in the RAW tab.";

    /// <param name="catalog">The runtime's catalog.</param>
    /// <param name="fileSections">The source-driven form <see cref="ConfigFormBuilder.Build"/> made (with <see cref="RuntimeConfigCatalog.Describe"/> for its labels and hints).</param>
    /// <param name="onFieldCommitted">The commit callback for the rows made here; they are read-only and never call it.</param>
    public static IReadOnlyList<FormSection> Arrange(RuntimeConfigCatalog catalog, IReadOnlyList<FormSection> fileSections, Action<FormField> onFieldCommitted)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileSections);
        ArgumentNullException.ThrowIfNull(onFieldCommitted);

        var byPath = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var section in fileSections)
        {
            Index(section, byPath);
        }

        var placed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        var sections = new List<FormSection>();

        foreach (var source in catalog.Sections)
        {
            var section = new FormSection(source.Name, source.Name, source.Name, isKnownToCore: true, existsInRawConfig: true, source.Summary, source.Help);
            FormGroup current = section;
            foreach (var row in source.Fields)
            {
                if (row.IsSeparator)
                {
                    if (row.GroupName.Length > 0)
                    {
                        current = new FormGroup(row.GroupName, row.GroupName, source.Name + " / " + row.GroupName);
                        section.SubGroups.Add(current);
                    }

                    continue;
                }

                // A key the catalog names twice is shown where it comes first.
                if (row.Key.Length > 0 && !usedKeys.Add(row.Key))
                {
                    continue;
                }

                if (row.Key.Length > 0 && byPath.TryGetValue(row.Key, out var item) && placed.Add(item))
                {
                    Place(current, item);
                }
                else
                {
                    current.Fields.Add(FromCatalog(row, onFieldCommitted));
                }
            }

            sections.Add(section);
        }

        if (Other(fileSections, placed) is { } other)
        {
            sections.Add(other);
        }

        return sections;
    }

    /// <summary>A row the file does not set (or a summary row): what the runtime's editor shows for it, as a field that is never editable.</summary>
    internal static FormField FromCatalog(RuntimeCatalogField row, Action<FormField> onFieldCommitted)
    {
        var leaf = row.Key.Length > 0 ? row.Key[(row.Key.LastIndexOf('.') + 1)..] : row.Label;
        var hint = row.Hint.Length > 0 ? row.Hint : null;

        if (row.Kind == RuntimeFieldKind.Header)
        {
            var shown = row.Value.Length > 0 || row.Key.Length == 0 ? row.Value : "(unset)";
            return new FormField(leaf, row.Label, row.Key, FormFieldKind.Info, shown, isEditable: false, disabledReason: null, onFieldCommitted, hint: hint);
        }

        (FormFieldKind Kind, object? Value) shape = row.Kind switch
        {
            RuntimeFieldKind.Password => (FormFieldKind.Secret, string.Empty),
            RuntimeFieldKind.Bool => (FormFieldKind.Bool, string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase)),
            RuntimeFieldKind.Int when int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => (FormFieldKind.Int, number),
            RuntimeFieldKind.Choice => (FormFieldKind.Choice, row.Value),
            _ when SensitiveKeyClassifier.IsEnvNameKey(leaf) => (FormFieldKind.EnvName, row.Value),
            _ when SensitiveKeyClassifier.IsSecretKey(leaf) => (FormFieldKind.Secret, string.Empty),
            _ => (FormFieldKind.String, row.Value),
        };

        var (kind, value) = shape;
        var reason = kind == FormFieldKind.Secret ? NotInFileSecretReason : NotInFileReason;
        return new FormField(
            leaf,
            row.Label,
            row.Key,
            kind,
            value,
            isEditable: false,
            reason,
            onFieldCommitted,
            options: kind == FormFieldKind.Choice ? row.Options : null,
            hint: hint);
    }

    private static void Index(FormGroup group, Dictionary<string, object> byPath)
    {
        foreach (var field in group.Fields)
        {
            _ = byPath.TryAdd(field.Path, field);
        }

        foreach (var list in group.Lists)
        {
            _ = byPath.TryAdd(list.Path, list);
        }

        foreach (var block in group.RawBlocks)
        {
            _ = byPath.TryAdd(block.Path, block);
        }

        foreach (var sub in group.SubGroups)
        {
            Index(sub, byPath);
        }
    }

    private static void Place(FormGroup group, object item)
    {
        switch (item)
        {
            case FormField field:
                group.Fields.Add(field);
                break;
            case FormListField list:
                group.Lists.Add(list);
                break;
            case RawBlockNode block:
                group.RawBlocks.Add(block);
                break;
        }
    }

    /// <summary>
    /// What is left of the file's own tree once the catalog has taken its rows: a top-level scalar or list as a row of its own, every other top-level key
    /// as a group card with its remaining rows in their nesting. Null when nothing is left.
    /// </summary>
    private static FormSection? Other(IReadOnlyList<FormSection> fileSections, HashSet<object> placed)
    {
        var other = new FormSection("other", RuntimeConfigCatalog.OtherSectionName, RuntimeConfigCatalog.OtherSectionName, isKnownToCore: false, existsInRawConfig: true, OtherSummary, OtherHelp);
        foreach (var section in fileSections)
        {
            var own = Remaining(section.Fields, placed).Where(f => f.Path == section.Key).ToList();
            var ownLists = Remaining(section.Lists, placed).Where(l => l.Path == section.Key).ToList();
            var ownBlocks = Remaining(section.RawBlocks, placed).Where(b => b.Path == section.Key).ToList();
            own.ForEach(other.Fields.Add);
            ownLists.ForEach(other.Lists.Add);
            ownBlocks.ForEach(other.RawBlocks.Add);

            var exclude = new HashSet<object>(placed, ReferenceEqualityComparer.Instance);
            exclude.UnionWith(own);
            exclude.UnionWith(ownLists);
            exclude.UnionWith(ownBlocks);
            if (Prune(section, exclude) is { } rest)
            {
                other.SubGroups.Add(rest);
            }
        }

        return other.HasContent ? other : null;
    }

    private static FormGroup? Prune(FormGroup source, HashSet<object> exclude)
    {
        var group = new FormGroup(source.Key, source.DisplayName, source.Path);
        Remaining(source.Fields, exclude).ToList().ForEach(group.Fields.Add);
        Remaining(source.Lists, exclude).ToList().ForEach(group.Lists.Add);
        Remaining(source.RawBlocks, exclude).ToList().ForEach(group.RawBlocks.Add);
        foreach (var sub in source.SubGroups)
        {
            if (Prune(sub, exclude) is { } kept)
            {
                group.SubGroups.Add(kept);
            }
        }

        return group.HasContent ? group : null;
    }

    private static IEnumerable<T> Remaining<T>(IEnumerable<T> items, HashSet<object> exclude)
        where T : class =>
        items.Where(item => !exclude.Contains(item));
}
