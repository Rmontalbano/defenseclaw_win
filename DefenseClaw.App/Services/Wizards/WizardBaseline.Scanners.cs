using System.Globalization;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The scanner wizards open on the configured values (CUST-390, the Mac's <c>skillScannerLiveDefaults</c> / <c>mcpScannerLiveDefaults</c>, v1.1.32).
/// <para>
/// Every <c>setup skill-scanner</c> / <c>setup mcp-scanner</c> option defaults to None and only a flag that is given is applied (<c>cmd_setup.py</c> at
/// 1.0.4), so each value read here is the field's baseline: an untouched Run sends nothing, and a change sends exactly that flag, including the
/// <c>--no-</c> form that turns an analyzer off. A key that is not in config.yaml starts on the value the runtime's loader gives it
/// (<c>config.py</c> <c>load</c>, <c>SkillScannerConfig</c>, <c>MCPScannerConfig</c>; <c>scanner/settings.py</c> <c>effective_*</c>), which is also
/// what the TUI form opens on (<c>tui/panels/setup.py</c> <c>wizard_form_defs</c>). A value a control cannot show (a <c>custom</c> policy, an unknown
/// severity) is left alone by <see cref="Coerce"/>.
/// </para>
/// <para>
/// On 0.8.x the analyzer flags are bare switches (<c>is_flag</c>, no <c>--no-</c> form), which take no stored value, so those stay as they were; the
/// pages only show the flags the installed CLI lists in any case.
/// </para>
/// </summary>
public static partial class WizardBaseline
{
    private const string SkillScannerSource = "config.yaml → scanners.skill_scanner";

    private const string McpScannerSource = "config.yaml → scanners.mcp_scanner";

    private const string ScannerLlmSource = "config.yaml → llm";

    /// <summary>The config schema that keeps the v8 defaults: <c>use_llm</c> off and the <c>permissive</c> policy (<c>FIRST_CURRENT_CONFIG_VERSION</c>).</summary>
    private const int ConfigVersion8 = 8;

    private static readonly string[] SkillScanner = { "scanners", "skill_scanner" };

    private static readonly string[] McpScanner = { "scanners", "mcp_scanner" };

    // ---------------------------------------------------------------- skill-scanner

    private static WizardDefinition ApplySkillScanner(WizardDefinition definition, ConfigYaml yaml)
    {
        const string note = "Pre-filled from config.yaml → scanners.skill_scanner (and the shared llm: block for the LLM analyzer); a key that is not set " +
                            "shows the runtime's default. Only fields you change are sent.";

        var scanner = Replace(definition, SkillScannerFacts(yaml), SkillScannerSource, note);
        return Replace(scanner, ScannerLlmFacts(yaml), ScannerLlmSource, note);
    }

    /// <summary>
    /// What <c>scanners.skill_scanner</c> holds for each <c>setup skill-scanner</c> flag. <c>use_llm</c> and <c>policy</c> are the two keys whose
    /// default depends on the file: on and <c>quiet</c>, except in a <c>config_version: 8</c> file (0.8.x, or 1.0.x before <c>defenseclaw migrate</c>),
    /// where they are off and <c>permissive</c>. VirusTotal and AI Defense read <c>analyzers.*.enabled</c> and fall back to the v8
    /// <c>use_virustotal</c> / <c>use_aidefense</c> (<c>_merge_skill_scanner_analyzers</c>).
    /// </summary>
    private static Dictionary<string, Fact> SkillScannerFacts(ConfigYaml yaml)
    {
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        var v8 = ConfigVersion(yaml) == ConfigVersion8;

        string? Key(params string[] path) => yaml.Get(SkillScanner.Concat(path).ToArray());

        AddToggle(facts, "--use-llm", Key("use_llm"), absent: !v8);
        AddToggle(facts, "--use-behavioral", Key("use_behavioral"), absent: false);
        AddToggle(facts, "--enable-meta", Key("enable_meta"), absent: false);
        AddToggle(facts, "--use-trigger", Key("use_trigger"), absent: false);
        AddToggle(facts, "--use-virustotal", Key("analyzers", "virustotal", "enabled") ?? Key("use_virustotal"), absent: false);
        AddToggle(facts, "--use-aidefense", Key("analyzers", "aidefense", "enabled") ?? Key("use_aidefense"), absent: false);
        AddToggle(facts, "--use-osv", Key("analyzers", "osv", "enabled"), absent: false);
        AddToggle(facts, "--lenient", Key("lenient"), absent: true);

        facts["--llm-consensus-runs"] = new Fact(Key("llm_consensus_runs")?.Trim() is { Length: > 0 } runs ? runs : "0", IsBaseline: true);

        // An empty policy is the recommended quiet preset (scanner/settings.py effective_policy); a missing one is what the loader gives the file.
        var policy = Key("policy");
        facts["--policy"] = new Fact(
            policy is null ? (v8 ? "permissive" : "quiet") : policy.Trim() is { Length: > 0 } named ? named : "quiet",
            IsBaseline: true);

        facts["--fail-on-severity"] = new Fact(Severity(Key("fail_on_severity"), "HIGH"), IsBaseline: true);
        facts["--review-queue-min"] = new Fact(Severity(Key("review_queue_min"), "MEDIUM"), IsBaseline: true);
        return facts;
    }

    // ---------------------------------------------------------------- mcp-scanner

    private static WizardDefinition ApplyMcpScanner(WizardDefinition definition, ConfigYaml yaml)
    {
        const string note = "Pre-filled from config.yaml → scanners.mcp_scanner (and the shared llm: block for the LLM analyzer); a key that is not set " +
                            "shows the runtime's default. Only fields you change are sent, and clearing Analyzers sends auto.";

        var scanner = Replace(definition, McpScannerFacts(yaml), McpScannerSource, note);
        var applied = Replace(scanner, ScannerLlmFacts(yaml), ScannerLlmSource, note);
        if (ReferenceEquals(applied, definition))
        {
            return definition;
        }

        var inner = applied.FinalArgvBuilder;
        return applied.WithFinalArgvBuilder((def, values) => WithAutoAnalyzers(def, values, inner(def, values)));
    }

    /// <summary>
    /// What <c>scanners.mcp_scanner</c> holds: <c>analyzers</c> as the v8 comma-separated text or a v9 list joined with commas, <c>auto</c> when it is
    /// not set (<c>_merge_mcp_scanner</c>), and the three scope switches, off when not set.
    /// </summary>
    private static Dictionary<string, Fact> McpScannerFacts(ConfigYaml yaml)
    {
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);

        var path = McpScanner.Append("analyzers").ToArray();
        var analyzers = yaml.GetList(path) is { } list ? string.Join(',', list.Select(static a => a.Trim()).Where(static a => a.Length > 0)) : yaml.Get(path) ?? "auto";
        Add(facts, "--analyzers", analyzers);

        AddToggle(facts, "--scan-prompts", yaml.Get(McpScanner.Append("scan_prompts").ToArray()), absent: false);
        AddToggle(facts, "--scan-resources", yaml.Get(McpScanner.Append("scan_resources").ToArray()), absent: false);
        AddToggle(facts, "--scan-instructions", yaml.Get(McpScanner.Append("scan_instructions").ToArray()), absent: false);
        return facts;
    }

    /// <summary>
    /// A cleared Analyzers box means auto, as its hint says (the Mac's GAP-2572 rule): <c>--analyzers auto</c> is sent when the box is blank and the
    /// configuration had a list, and nothing when it already was auto or empty. <c>auto</c> is the value the runtime itself stores for an empty list
    /// (<c>setup mcp-scanner</c>: <c>",".join(names) or "auto"</c>; 0.8.10 stores it as given, and it is that runtime's default too).
    /// </summary>
    private static IReadOnlyList<string> WithAutoAnalyzers(WizardDefinition definition, WizardValues values, IReadOnlyList<string> argv)
    {
        var field = definition.VisibleFields(values).FirstOrDefault(static f => f.Flag == "--analyzers" && !f.IsPositional);
        if (field is null || values[field.Id].Trim().Length > 0 || argv.Contains("--analyzers", StringComparer.Ordinal))
        {
            return argv;
        }

        var opened = field.BaselineValue.Trim();
        if (opened.Length == 0 || string.Equals(opened, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return argv;
        }

        // In the field's place: straight after "setup mcp-scanner", where the first field of the first page goes.
        var at = Math.Min(argv.Count, 1 + CommandWords(definition.Target).Count);
        var result = argv.ToList();
        result.InsertRange(at, new[] { "--analyzers", "auto" });
        return result;
    }

    // ---------------------------------------------------------------- shared

    /// <summary>
    /// The scanner wizards' <c>--llm-provider</c>, <c>--llm-model</c> and <c>--llm-base-url</c> write the top-level <c>llm:</c> block
    /// (<c>_apply_scanner_llm_flags</c>), so that block is what leaving them off keeps.
    /// </summary>
    private static Dictionary<string, Fact> ScannerLlmFacts(ConfigYaml yaml)
    {
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        Add(facts, "--llm-provider", yaml.Get("llm", "provider"));
        Add(facts, "--llm-model", yaml.Get("llm", "model"));
        Add(facts, "--llm-base-url", yaml.Get("llm", "base_url"));
        return facts;
    }

    /// <summary>A yes/no the form opens on: the stored value, or <paramref name="absent"/> when the key is not there. A value that is not a yes/no is left alone.</summary>
    private static void AddToggle(Dictionary<string, Fact> facts, string flag, string? value, bool absent)
    {
        var on = value is null ? absent : TryBool(value);
        if (on is { } known)
        {
            facts[flag] = new Fact(known ? ToggleValues.On : ToggleValues.Off, IsBaseline: true);
        }
    }

    private static string Severity(string? value, string fallback) =>
        value?.Trim().ToUpperInvariant() is { Length: > 0 } severity ? severity : fallback;

    /// <summary><c>config_version</c> as the runtime reads it (<c>_exact_config_version</c>): a number, or 0.</summary>
    private static int ConfigVersion(ConfigYaml yaml) =>
        yaml.Get("config_version")?.Trim() is { Length: > 0 } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : 0;

    private static IReadOnlyList<string> CommandWords(string target) =>
        target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
