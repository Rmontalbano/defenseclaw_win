using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// What the Policies model panel tests (CUST-293) share: the synthetic fixtures of the pinned runtime (source commit 95159fd, shared with the
/// Core suite), a runtime probe that answers from the help-screen fixtures, a scripted CLI that answers the catalog's read-only commands
/// from them, and a stand-in for the runtime's data files. Nothing here starts a process or touches the machine's DefenseClaw install.
/// </summary>
internal static class PolicyModelTestSupport
{
    public const string Fresh = "fresh";
    public const string Connectors = "connectors";

    /// <summary>The runtime fixture set of the pinned runtime and of the installed 0.8.10.</summary>
    public const string Pinned = "95159fd";

    public const string Installed = "0.8.10";

    /// <summary>A pack folder of the fresh install's capture, as list-packs prints it.</summary>
    public const string DefaultPackFolder = @"C:\Users\operator\.defenseclaw\policies\guardrail\default";

    public static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", relative.Replace('/', Path.DirectorySeparatorChar)))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    // ---- the runtime probe -------------------------------------------------------------------------------------------------

    /// <summary>Answers the runtime probes (<c>--version-json</c> and the help screens) from one fixture set; anything else is "exit 2".</summary>
    public static RuntimeProbeRunner ProbeRunner(Func<string> set) => (arguments, _) =>
    {
        var file = string.Join(' ', arguments) switch
        {
            "--version-json" => "version.json",
            "--help" => "root.txt",
            "setup --help" => "setup.txt",
            "guardrail --help" => "guardrail.txt",
            "config --help" => "config.txt",
            "sandbox --help" => "sandbox.txt",
            "acp --help" => "acp.txt",
            "setup redaction --help" => "setup-redaction.txt",
            _ => null,
        };

        var path = file is null ? null : Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-" + set(), file);
        return Task.FromResult(path is not null && File.Exists(path)
            ? RuntimeProbeOutput.Ok(File.ReadAllText(path))
            : RuntimeProbeOutput.Fail("exit 2"));
    };

    public static RuntimeProbeRunner ProbeRunner(string set) => ProbeRunner(() => set);

    // ---- an invocation without a process -------------------------------------------------------------------------------------

    public static CliInvocation Result(int? exit, IReadOnlyList<string> argv, string stdout = "", string stderr = "", string? failure = null)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        foreach (var line in stdout.Split('\n'))
        {
            if (line.Length > 0)
            {
                InvocationFactory.Append(invocation, line.TrimEnd('\r'));
            }
        }

        foreach (var line in stderr.Split('\n'))
        {
            if (line.Length > 0)
            {
                InvocationFactory.Append(invocation, line.TrimEnd('\r'), CliStream.StandardError);
            }
        }

        if (failure is not null)
        {
            InvocationFactory.Fail(invocation, failure);
        }
        else
        {
            InvocationFactory.Finish(invocation, exit ?? 0);
        }

        return invocation;
    }

    // ---- a scripted CLI ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The CLI as the Policies model panel sees it: the four read-only catalog commands answered from the fixtures of a scenario, the validation
    /// of a rule pack and of the policy bundle answered as a test says, and every command recorded in <see cref="Calls"/>. A command nothing
    /// scripts throws, so a panel that ran something unexpected fails the test at once.
    /// </summary>
    public sealed class ScriptedCli
    {
        private readonly object _gate = new();

        public ScriptedCli(string scenario = Fresh)
        {
            Scenario = scenario;
        }

        public string Scenario { get; set; }

        public List<string> Calls { get; } = new();

        /// <summary>The text of a command that replaces its fixture answer (and its exit code).</summary>
        public Dictionary<string, (int Exit, string Stdout, string Stderr)> Overrides { get; } = new(StringComparer.Ordinal);

        /// <summary>Commands whose answer is "did not complete" (a timeout, a missing executable).</summary>
        public HashSet<string> DidNotComplete { get; } = new(StringComparer.Ordinal);

        /// <summary>Exit code of <c>guardrail validate-pack</c>: 0 valid, 1 invalid, 2 the validator is unavailable.</summary>
        public int PackValidationExit { get; set; }

        /// <summary>Exit code of <c>policy validate</c>.</summary>
        public int PolicyValidateExit { get; set; }

        public Task<CliInvocation> Run(IReadOnlyList<string> argv, CliRunOptions _) => Task.FromResult(Handle(argv));

        public CliInvocation Handle(IReadOnlyList<string> argv)
        {
            var text = string.Join(' ', argv);

            // The catalog's reads run side by side, and their continuations may land on different threads.
            lock (_gate)
            {
                Calls.Add(text);
            }

            if (DidNotComplete.Contains(text))
            {
                return Result(null, argv, failure: "fixture: timed out");
            }

            if (Overrides.TryGetValue(text, out var answer))
            {
                return Result(answer.Exit, argv, answer.Stdout, answer.Stderr);
            }

            var suffix = string.Equals(Scenario, Connectors, StringComparison.Ordinal) ? ".connectors" : string.Empty;
            return text switch
            {
                "policy list --json" => Result(0, argv, Fixture("cli/policy-list.json")),
                "guardrail list-packs --json" => Result(0, argv, Fixture($"cli/guardrail-list-packs{suffix}.json")),
                "guardrail protection list --json" => Result(0, argv, Fixture($"cli/guardrail-protection-list{suffix}.json")),
                "config show --section guardrail --format json" => Result(0, argv, Fixture($"cli/config-show-guardrail{suffix}.json")),
                "policy validate" => PolicyValidateExit == 0
                    ? Result(0, argv, "  OK data.json: OK\n  OK All validations passed.")
                    : Result(PolicyValidateExit, argv, "  OK data.json: OK\n  X FAIL: 'opa' binary not found - install OPA to validate Rego bundles."),
                _ when argv.Count == 4 && argv[0] == "guardrail" && argv[1] == "validate-pack" && argv[3] == "--json" => PackValidation(argv),
                _ => throw new InvalidOperationException("a read ran that nothing scripts: " + text),
            };
        }

        private CliInvocation PackValidation(IReadOnlyList<string> argv) => PackValidationExit switch
        {
            0 => Result(0, argv, "{\"valid\": true, \"summary\": {\"rule_count\": 31, \"enabled_rule_count\": 29, \"rule_file_count\": 8, \"digest\": \"0123456789abcdef0123\"}}"),
            1 => Result(1, argv, "{\"valid\": false, \"error\": {\"reason\": \"rules.yaml declares an unknown severity\", \"path\": \"$.rules[3].severity\"}}"),
            _ => Result(2, argv, "{\"valid\": false, \"error\": {\"reason\": \"the gateway binary was not found\", \"path\": \"$\"}}"),
        };

        /// <summary>Everything the panel ran that is not one of the reads it is allowed to start on its own.</summary>
        public IEnumerable<string> Mutations => Calls.Where(c =>
            c is not ("policy list --json" or "guardrail list-packs --json" or "guardrail protection list --json" or "config show --section guardrail --format json" or "policy validate")
            && !c.StartsWith("guardrail validate-pack ", StringComparison.Ordinal));
    }

    // ---- the runtime's data files ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A stand-in for the runtime's data files: the chain catalog and the rule families of the capture (the Phase 2 document's
    /// <c>raw_catalog.families</c>, keyed by pack folder), and a composed pack's base. Reads nothing from disk but the fixtures.
    /// </summary>
    public sealed class FixtureData : IPolicyDataFiles
    {
        private readonly Dictionary<string, IReadOnlyList<RuleFamily>> _families = new(StringComparer.Ordinal);

        public FixtureData()
        {
            using var document = JsonDocument.Parse(Fixture("policy-model/phase2-model.json"));
            foreach (var entry in document.RootElement.GetProperty("raw_catalog").GetProperty("families").EnumerateObject())
            {
                _families[entry.Name] = entry.Value.EnumerateArray()
                    .Select(f => new RuleFamily(f.GetProperty("name").GetString()!, f.GetProperty("rules").GetInt32(), f.GetProperty("enabled").GetInt32(), f.GetProperty("description").GetString()!))
                    .ToArray();
            }
        }

        public IReadOnlyList<ToolChain> ReadToolChains(IReadOnlyList<NamedPolicy> policies) =>
            PolicyCatalogJson.ParseToolChains(Fixture("policy-model/tool-chains.json"));

        public IReadOnlyList<RuleFamily> ReadRuleFamilies(string packFolder, IReadOnlyList<NamedPolicy> policies) =>
            _families.TryGetValue(packFolder, out var found)
                ? found
                : _families.TryGetValue(DefaultPackFolder, out var defaults) ? defaults : Array.Empty<RuleFamily>();

        public string? ReadPackBase(string packFolder) =>
            packFolder.Contains("protected-", StringComparison.Ordinal) ? "strict" : null;
    }
}
