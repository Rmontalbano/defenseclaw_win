using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Logs;

namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>What a row of the runtime's catalog is (<c>ConfigFieldKind</c>, <c>tui/services/setup_state.py:34</c> at 1.0.4).</summary>
public enum RuntimeFieldKind
{
    String,
    Bool,
    Int,
    Choice,

    /// <summary>A secret the runtime's editor writes and never shows. The dump carries no value for it, and neither does this app.</summary>
    Password,

    /// <summary>A read-only row: a <c>.. Group ..</c> separator, a summary, or a key the runtime's editor does not let its operator edit.</summary>
    Header,
}

/// <summary>One row of a runtime catalog section (<c>ConfigField</c>: label, dotted key, kind, value, options, hint).</summary>
/// <param name="Label">What the runtime's editor calls the row.</param>
/// <param name="Key">The dotted config.yaml key; empty for a separator or a keyless summary. A summary row may carry a key that names no setting (<c>webhooks.hint</c>).</param>
/// <param name="Kind">The row's kind; an unknown one is <see cref="RuntimeFieldKind.String"/>, as on the Mac.</param>
/// <param name="Value">The value the runtime resolved, made safe to show (<see cref="RuntimeConfigCatalog.DisplayValue"/>); always empty for a password.</param>
/// <param name="Options">A choice's values, in the runtime's order.</param>
/// <param name="Hint">The runtime's one-line hint.</param>
public sealed record RuntimeCatalogField(string Label, string Key, RuntimeFieldKind Kind, string Value, IReadOnlyList<string> Options, string Hint)
{
    /// <summary>A <c>.. Paths ..</c> line: a header with no key and no value, which starts a group of the rows after it.</summary>
    public bool IsSeparator => Kind == RuntimeFieldKind.Header && Key.Length == 0 && Value.Length == 0;

    /// <summary>The separator's group name: <c>.. Paths ..</c> is <c>Paths</c>.</summary>
    public string GroupName => Label.Trim().Trim('.').Trim();
}

/// <summary>One section of the runtime's catalog (<c>ConfigSection</c>: name, fields, summary, help), in the runtime's order.</summary>
public sealed record RuntimeCatalogSection(string Name, string Summary, string Help, IReadOnlyList<RuntimeCatalogField> Fields);

/// <summary>
/// The installed runtime's own Setup config catalog (CUST-391), the Mac's <c>DynamicConfigCatalog</c> (<c>DataLayer/DynamicConfigCatalog.swift</c>, v1.1.34): the runtime's
/// Python imports <c>defenseclaw.tui.panels.setup.build_setup_sections(cfg)</c> and prints its sections and fields as JSON, so the editor gains a section or
/// a field the moment the runtime is upgraded, with no app update. 1.0.4 answers with 24 sections (General … Trusted Paths, <c>setup.py:1786-2021</c>); the
/// function is in 0.8.10 too (<c>setup.py:1391</c>).
/// <para>
/// <b>What this app takes from it, and what it does not.</b> The sections, their order, the rows' labels, hints, kinds and choices: the presentation. The
/// values the editor edits still come from config.yaml through <c>defenseclaw config show --source</c> (masked) and are still written into the RAW text by
/// <see cref="YamlSectionEditor"/>, so validation, masking, the diff review, rebase and the restart rules are what they were. A value from the dump is only
/// ever displayed: for a key the file does not set (the runtime's default, read-only) and for a summary row.
/// </para>
/// <para>
/// <b>Secrets.</b> The dump script blanks every password and every secret-shaped key before it prints anything (the Mac blanks passwords only), and
/// <see cref="Parse"/> does it again: a password never has a value here, a secret-shaped key shows at most "(hidden)", and every other value has a URL's
/// path, query and user info masked (as <c>config show --source</c> does) and known token shapes removed (<see cref="DisplayRedaction"/>).
/// </para>
/// </summary>
public sealed partial class RuntimeConfigCatalog
{
    /// <summary>The prefix of the one stdout line that carries the JSON, so warnings Python prints cannot corrupt the parse (the Mac's sentinel).</summary>
    public const string Sentinel = "DCCATALOG:";

    /// <summary>What the observability section's status row says: the dump does not read the gateway's plan, so its default text ("loading …") would never change.</summary>
    internal const string ObservabilityNote = "not read by this editor; manage destinations with defenseclaw setup observability";

    /// <summary>
    /// The dump, run as <c>python -I -B -c &lt;this&gt;</c> (<see cref="RuntimeCatalogLoader"/>). It reads config.yaml through the runtime's own loader and writes
    /// nothing: <c>config.load()</c> only reads (<c>config.py:7411</c>), <c>build_setup_sections</c> builds rows, and <c>-B</c> keeps Python from writing
    /// bytecode into the install. The JSON shape is the Mac's (<c>DynamicConfigCatalog.dumpScript</c>); this one also blanks secret-shaped keys and tells the
    /// observability rows why there is no plan (an older runtime without that argument is asked without it).
    /// </summary>
    public static readonly string DumpScript = string.Join(
        "\n",
        "import json, re",
        "from defenseclaw import config as dc_config",
        "from defenseclaw.tui.panels.setup import build_setup_sections",
        "cfg = dc_config.load()",
        "try:",
        "    sections = build_setup_sections(cfg, observability_status_error=" + PythonString(ObservabilityNote) + ")",
        "except TypeError:",
        "    sections = build_setup_sections(cfg)",
        "secret = re.compile(r\"(token|key|secret|passw(or)?d|credential|authorization|bearer|_pem$)\", re.I)",
        "out = []",
        "for s in sections:",
        "    fields = []",
        "    for f in s.fields:",
        "        kind = str(getattr(f, 'kind', 'string'))",
        "        key = str(getattr(f, 'key', '') or '')",
        "        leaf = key.rsplit('.', 1)[-1]",
        "        hidden = kind == 'password' or (bool(secret.search(leaf)) and not leaf.lower().endswith('_env'))",
        "        fields.append({'label': str(f.label), 'key': key, 'kind': kind,",
        "                       'value': '' if hidden else str(getattr(f, 'value', '')),",
        "                       'options': [str(o) for o in getattr(f, 'options', ())],",
        "                       'hint': str(getattr(f, 'hint', '') or '')})",
        "    out.append({'name': str(s.name), 'summary': str(getattr(s, 'summary', '') or ''),",
        "                'help': str(getattr(s, 'help', '') or ''), 'fields': fields})",
        "print(" + PythonString(Sentinel) + " + json.dumps(out))");

    /// <summary>The name of the section that lists config.yaml keys the catalog does not describe (the Mac's).</summary>
    public const string OtherSectionName = "Other (uncatalogued)";

    private const int MaxSections = 200;
    private const int MaxFieldsPerSection = 2000;
    private const int MaxTextLength = 2000;
    private const int MaxOptions = 200;

    private readonly Dictionary<string, RuntimeCatalogField> _byKey;

    private RuntimeConfigCatalog(IReadOnlyList<RuntimeCatalogSection> sections)
    {
        Sections = sections;
        _byKey = new Dictionary<string, RuntimeCatalogField>(StringComparer.Ordinal);
        foreach (var field in sections.SelectMany(static s => s.Fields))
        {
            if (field.Key.Length > 0)
            {
                _ = _byKey.TryAdd(field.Key, field);
            }
        }
    }

    /// <summary>The sections, in the runtime's order.</summary>
    public IReadOnlyList<RuntimeCatalogSection> Sections { get; }

    /// <summary>Every dotted key a row of the catalog names (settings and the keys of summary rows alike).</summary>
    public IReadOnlyCollection<string> Keys => _byKey.Keys;

    /// <summary>The first row that names <paramref name="key"/>, or null.</summary>
    public RuntimeCatalogField? Field(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>
    /// What the catalog says about a dotted key, for <see cref="ConfigFormBuilder"/>: the label, hint and choices, whether it is a password, and for a
    /// header row with a key (a setting the runtime's own editor shows read-only, <c>setup.py</c> <c>_lock_unmodeled_fields</c>) why. Null for a key the
    /// catalog does not name.
    /// </summary>
    public ConfigFieldInfo? Describe(string key)
    {
        if (Field(key) is not { } field)
        {
            return null;
        }

        return new ConfigFieldInfo(
            field.Hint,
            field.Kind == RuntimeFieldKind.Choice ? field.Options : null,
            field.Kind == RuntimeFieldKind.Header ? ReadOnlyReason(field) : null,
            field.Label,
            field.Kind == RuntimeFieldKind.Password);
    }

    /// <summary>Why a header row with a key is read-only: the runtime's own hint, or a sentence that says whose rule it is.</summary>
    internal static string ReadOnlyReason(RuntimeCatalogField field) =>
        field.Hint.Length > 0 ? field.Hint : "Read-only in DefenseClaw's own editor. Edit it in the RAW tab.";

    /// <summary>
    /// The catalog in the dump's stdout, or null: no sentinel line, JSON that does not parse, or no section with a name (a renamed symbol in a future
    /// runtime is the same "unavailable" as a missing interpreter, and the editor falls back to the built-in catalog). Rows that are not objects with a
    /// label are skipped, as on the Mac. Text with LF or CRLF line ends reads the same.
    /// </summary>
    public static RuntimeConfigCatalog? Parse(string? standardOutput)
    {
        if (string.IsNullOrEmpty(standardOutput))
        {
            return null;
        }

        var line = standardOutput
            .Split('\n')
            .Select(static l => l.TrimEnd('\r'))
            .FirstOrDefault(static l => l.StartsWith(Sentinel, StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line[Sentinel.Length..]);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var sections = new List<RuntimeCatalogSection>();
            foreach (var item in document.RootElement.EnumerateArray().Take(MaxSections))
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { Length: > 0 } name)
                {
                    continue;
                }

                var fields = new List<RuntimeCatalogField>();
                if (item.TryGetProperty("fields", out var rows) && rows.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in rows.EnumerateArray().Take(MaxFieldsPerSection))
                    {
                        if (ParseField(row) is { } field)
                        {
                            fields.Add(field);
                        }
                    }
                }

                sections.Add(new RuntimeCatalogSection(name, Text(item, "summary") ?? string.Empty, Text(item, "help") ?? string.Empty, fields));
            }

            return sections.Count == 0 ? null : new RuntimeConfigCatalog(sections);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RuntimeCatalogField? ParseField(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || Text(row, "label") is not { } label)
        {
            return null;
        }

        var key = Text(row, "key") ?? string.Empty;
        var kind = KindOf(Text(row, "kind"));
        var options = row.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(static o => o.ValueKind == JsonValueKind.String).Take(MaxOptions).Select(static o => Clip(o.GetString()!)).ToArray()
            : Array.Empty<string>();

        return new RuntimeCatalogField(
            label,
            key,
            kind,
            DisplayValue(key, kind, Text(row, "value") ?? string.Empty),
            options,
            Text(row, "hint") ?? string.Empty);
    }

    /// <summary>Unknown runtime kinds degrade to free text, so a future field type stays visible rather than vanishing (the Mac's rule).</summary>
    private static RuntimeFieldKind KindOf(string? raw) => raw switch
    {
        "bool" => RuntimeFieldKind.Bool,
        "int" => RuntimeFieldKind.Int,
        "choice" => RuntimeFieldKind.Choice,
        "password" => RuntimeFieldKind.Password,
        "header" => RuntimeFieldKind.Header,
        _ => RuntimeFieldKind.String,
    };

    /// <summary>
    /// A dumped value as this app may show it. A password has none. A secret-shaped key (<see cref="SensitiveKeyClassifier.IsSecretKey"/> on its last
    /// segment) shows "(hidden)" when it holds anything. Everything else keeps only the scheme and host of a URL (a webhook's secret is its path) and loses
    /// any token shape <see cref="DisplayRedaction"/> knows, then is cut to a sane length.
    /// </summary>
    internal static string DisplayValue(string key, RuntimeFieldKind kind, string raw)
    {
        if (kind == RuntimeFieldKind.Password || raw.Length == 0)
        {
            return string.Empty;
        }

        var leaf = key[(key.LastIndexOf('.') + 1)..];
        if (leaf.Length > 0 && SensitiveKeyClassifier.IsSecretKey(leaf))
        {
            return "(hidden)";
        }

        try
        {
            // The TUI escapes a bracket for its markup renderer (\[enabled], setup.py _webhook_summary_fields); here it is just a bracket.
            var text = raw.Replace(@"\[", "[", StringComparison.Ordinal);
            return Clip(DisplayRedaction.Prose(MaskUrls(text), MaxTextLength));
        }
        catch (RegexMatchTimeoutException)
        {
            // Text slow enough to time a mask out is not shown at all.
            return "(hidden)";
        }
    }

    /// <summary>Every <c>scheme://…</c> in <paramref name="text"/> down to its scheme and host: user info goes, and a path, query or fragment becomes <c>/[REDACTED]</c>.</summary>
    internal static string MaskUrls(string text) => UrlPattern().Replace(text, static match =>
    {
        var scheme = match.Groups["scheme"].Value;
        var rest = match.Groups["rest"].Value;
        var end = rest.IndexOfAny(new[] { '/', '?', '#' });
        var authority = end < 0 ? rest : rest[..end];
        var tail = end < 0 ? string.Empty : rest[end..];
        var at = authority.LastIndexOf('@');
        var host = at < 0 ? authority : authority[(at + 1)..];
        var masked = tail.Trim('/').Length > 0 || at >= 0;
        return scheme + "://" + host + (masked ? "/[REDACTED]" : tail);
    });

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Clip(value.GetString()!) : null;

    private static string Clip(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength] + "…";

    /// <summary>A Python string literal for an ASCII sentence with no quotes or backslashes of its own.</summary>
    private static string PythonString(string text) => "'" + text + "'";

    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*)://(?<rest>[^\s'""<>]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UrlPattern();
}
