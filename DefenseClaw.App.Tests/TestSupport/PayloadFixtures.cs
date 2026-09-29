namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// The synthetic payloads under <c>Fixtures/CliPayloads</c>. Each is shaped like what DefenseClaw 0.8.10's CLI prints
/// (or, for the AI runtime reply, what the upstream gateway serves) and is built from made-up names, paths under
/// <c>C:\Users\example</c> and <c>*.example.test</c> hosts: no real machine, user or customer appears in them.
/// The tests cite the emitting source (file and line in the 0.8.10 wheel, or upstream) for the shape each one follows.
/// <para>
/// How they were checked: the skill, mcp, plugin, tool, registry list, registry index.json and AI BOM shapes were
/// compared, key by key and type by type, with what the wheel's own emitter functions print when run over a synthetic
/// sandbox HOME (a temp folder holding made-up ~/.claude skills, settings.json, plugins and a registry cache;
/// USERPROFILE, HOME and DEFENSECLAW_HOME pointed into it, no store, no gateway). Every key the real output has is in the
/// matching fixture with the same JSON type; the keys only a fixture has belong to branches the sandbox did not exercise
/// (for example homepage, the policy_* fields that need an audit store, a null updated_at). The gateway's AI runtime
/// reply exists only upstream (see AiRuntimePayloadTests), so its fixtures follow the Go struct and could not be
/// compared with a live reply. Non-ASCII text is written as \uXXXX escapes, as Python's json.dumps prints it.
/// </para>
/// </summary>
internal static class PayloadFixtures
{
    public static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CliPayloads", fileName));
}
