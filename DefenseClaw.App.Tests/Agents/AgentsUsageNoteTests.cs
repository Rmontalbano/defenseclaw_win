using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Correlation;

namespace DefenseClaw.App.Tests.Agents;

/// <summary>
/// The note under the Agents panel's Tokens and Cost tiles (CUST-457): the ledger's own sentence on every runtime, plus, on a runtime with tokenomics
/// (1.0.10), one sentence saying where model usage is kept. The tiles still say "Not reported".
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AgentsUsageNoteTests
{
    private const string CodexIdentity = "agt-0000000000000001";

    private static async Task<string[]> NotesAsync(string? runtimeVersion)
    {
        using var scene = new AgentsScene(runtimeVersion);
        _ = await scene.OpenAsync();
        scene.Select(CodexIdentity);
        var tiles = scene.Ui(() => scene.Vm.Kpis.Where(k => k.Label is "Tokens" or "Cost").ToArray());
        Assert.All(tiles, t => Assert.Equal("Not reported", t.Value));
        return tiles.Select(t => t.Note).ToArray();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.0.9")]
    public async Task Before_1_0_10_the_note_is_the_ledgers_sentence_unchanged(string? version)
    {
        var notes = await NotesAsync(version);

        Assert.Equal(2, notes.Length);
        Assert.All(notes, n => Assert.Equal(AgentKpis.UsageNote, n));
        Assert.Equal(
            "Not reported. The correlation ledger holds identifiers and event names only, so nothing is estimated.",
            AgentKpis.UsageNote);
    }

    [Fact]
    public async Task On_1_0_10_the_note_adds_one_sentence_about_the_tokenomics_store()
    {
        var notes = await NotesAsync("1.0.10");

        Assert.All(notes, n => Assert.Equal(AgentKpis.UsageNote + " " + AgentKpis.UsageSourceNote, n));
        Assert.Contains("GET /usage", AgentKpis.UsageSourceNote, StringComparison.Ordinal);
        Assert.Contains("no per-agent breakdown", AgentKpis.UsageSourceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_note_function_adds_the_sentence_only_with_tokenomics()
    {
        Assert.Equal(AgentKpis.UsageNote, AgentKpis.UsageNoteFor(tokenomics: false));
        Assert.StartsWith(AgentKpis.UsageNote, AgentKpis.UsageNoteFor(tokenomics: true), StringComparison.Ordinal);
        Assert.EndsWith(AgentKpis.UsageSourceNote, AgentKpis.UsageNoteFor(tokenomics: true), StringComparison.Ordinal);
    }
}
