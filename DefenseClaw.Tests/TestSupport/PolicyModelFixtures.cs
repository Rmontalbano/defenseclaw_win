using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Policy.Model;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// The synthetic fixtures of the Policies seven-view model (<c>Fixtures/runtime-95159fd/cli</c> and <c>.../policy-model</c>), derived from the
/// Phase 2 capture of the pinned runtime (source commit 95159fd; see <c>docs/RUNTIME-COMPAT-95159fd.md</c>), and a runner that answers the
/// catalog's read-only commands from them, so no test starts a process.
/// </summary>
public static class PolicyModelFixtures
{
    public const string Fresh = "fresh";
    public const string Connectors = "connectors";

    /// <summary>The Phase 2 model document: the runtime's catalog read (<c>raw_catalog</c>) and the rows it rendered from it (<c>views</c>).</summary>
    public static JsonDocument Phase2() => JsonDocument.Parse(RuntimeFixtures.Read("policy-model/phase2-model.json"));

    /// <summary>
    /// The catalog the Phase 2 capture holds. The capture also holds the runtime's seventh view, Sandbox packs; Windows leaves that view out
    /// (see <see cref="PolicyModel"/>), so nothing of it is read here.
    /// </summary>
    public static PolicyCatalog Phase2Catalog()
    {
        using var document = Phase2();
        var raw = document.RootElement.GetProperty("raw_catalog");

        var policies = raw.GetProperty("policies").EnumerateArray().Select(p => PolicyCatalogJson.Policy(p)!).ToArray();
        var postures = raw.GetProperty("postures").EnumerateArray().Select(Posture).ToArray();
        var families = new Dictionary<string, IReadOnlyList<RuleFamily>>(StringComparer.Ordinal);
        foreach (var entry in raw.GetProperty("families").EnumerateObject())
        {
            families[entry.Name] = entry.Value.EnumerateArray()
                .Select(f => new RuleFamily(f.GetProperty("name").GetString()!, f.GetProperty("rules").GetInt32(), f.GetProperty("enabled").GetInt32(), f.GetProperty("description").GetString()!))
                .ToArray();
        }

        var chains = PolicyCatalogJson.ParseToolChains(RuntimeFixtures.Read("policy-model/tool-chains.json"));

        return new PolicyCatalog
        {
            Policies = policies,
            GlobalPack = ScopePackOf(raw.GetProperty("global_pack")),
            ConnectorPacks = raw.GetProperty("connectors").EnumerateArray().Select(ScopePackOf).ToArray(),
            Packs = raw.GetProperty("packs").EnumerateArray()
                .Select(p => new RulePackEntry(p.GetProperty("name").GetString()!, p.GetProperty("path").GetString()!, p.GetProperty("kind").GetString()!, p.GetProperty("used_by").EnumerateArray().Select(u => u.GetString()!).ToArray()))
                .ToArray(),
            Postures = postures,
            Protection = raw.GetProperty("protection").EnumerateArray().Select(p => PolicyCatalogJson.ProtectionPackOf(p)!).ToArray(),
            Families = families,
            Chains = chains,
            MultiConnector = false,
            PolicyFolder = @"C:\Users\operator\.defenseclaw\policies",
        };
    }

    private static ScopePack ScopePackOf(JsonElement e) =>
        new(e.GetProperty("connector").GetString()!, e.GetProperty("pack").GetString()!, e.GetProperty("path").GetString()!, e.GetProperty("source").GetString()!);

    private static ScopePosture Posture(JsonElement e) =>
        new(
            e.GetProperty("scope").GetString()!,
            e.GetProperty("mode").GetString()!,
            e.GetProperty("mode_source").GetString()!,
            e.GetProperty("hilt").GetString()!,
            e.GetProperty("pack").GetString()!,
            e.GetProperty("pack_path").GetString()!,
            e.GetProperty("pack_source").GetString()!,
            e.GetProperty("protection").EnumerateArray().Select(p => p.GetString()!).ToArray(),
            e.GetProperty("block_at").GetString()!,
            e.GetProperty("alert_at").GetString()!,
            e.GetProperty("levels_source").GetString()!,
            e.GetProperty("own_block_at").GetString()!,
            e.GetProperty("own_alert_at").GetString()!);

    // ---- a CliInvocation without a process --------------------------------------------------------------------------------

    /// <summary>A finished invocation with the given output, as the runner would have recorded it.</summary>
    public static CliInvocation Invocation(int? exit, string stdout, string stderr = "", string? failure = null, params string[] argv)
    {
        var invocation = new CliInvocation("defenseclaw", argv.Length == 0 ? new[] { "policy", "list", "--json" } : argv, DateTimeOffset.UtcNow, retainFullOutput: true);
        foreach (var line in stdout.Split('\n', StringSplitOptions.None))
        {
            if (line.Length > 0)
            {
                invocation.Append(new CliOutputLine(DateTimeOffset.UtcNow, CliStream.StandardOutput, line.TrimEnd('\r')));
            }
        }

        foreach (var line in stderr.Split('\n', StringSplitOptions.None))
        {
            if (line.Length > 0)
            {
                invocation.Append(new CliOutputLine(DateTimeOffset.UtcNow, CliStream.StandardError, line.TrimEnd('\r')));
            }
        }

        invocation.FinishedAt = DateTimeOffset.UtcNow;
        invocation.ExitCode = exit;
        invocation.FailureReason = failure;
        return invocation;
    }

    /// <summary>
    /// A runner that answers each read-only catalog command from the fixtures of one scenario (<see cref="Fresh"/>: the Phase 2 capture's fresh
    /// install; <see cref="Connectors"/>: two active connectors, one with its own composed pack). <paramref name="overrides"/> replace an answer by
    /// the command's text; every command it is asked for is recorded in <paramref name="asked"/>.
    /// </summary>
    public static PolicyModelReader.Runner Runner(string scenario, List<string>? asked = null, IReadOnlyDictionary<string, Func<CliInvocation>>? overrides = null) =>
        (argv, _) =>
        {
            var text = string.Join(' ', argv);
            asked?.Add(text);
            if (overrides is not null && overrides.TryGetValue(text, out var answer))
            {
                return Task.FromResult(answer());
            }

            var suffix = string.Equals(scenario, Connectors, StringComparison.Ordinal) ? ".connectors" : string.Empty;
            var invocation = text switch
            {
                "policy list --json" => Invocation(0, RuntimeFixtures.Read("cli/policy-list.json"), argv: argv.ToArray()),
                "guardrail list-packs --json" => Invocation(0, RuntimeFixtures.Read($"cli/guardrail-list-packs{suffix}.json"), argv: argv.ToArray()),
                "guardrail protection list --json" => Invocation(0, RuntimeFixtures.Read($"cli/guardrail-protection-list{suffix}.json"), argv: argv.ToArray()),
                "config show --section guardrail --format json" => Invocation(0, RuntimeFixtures.Read($"cli/config-show-guardrail{suffix}.json"), argv: argv.ToArray()),
                _ => throw new InvalidOperationException($"No fixture answers '{text}'."),
            };
            return Task.FromResult(invocation);
        };
}
