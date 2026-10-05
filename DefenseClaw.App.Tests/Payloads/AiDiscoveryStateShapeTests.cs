using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Payloads;

/// <summary>
/// <c>ai_discovery_state.json</c> is written by the gateway's scanner from what it finds on the machine, and is the one source of the AI
/// Discovery cards. Valid JSON of the wrong shape (a signal that is a string, a number, a list) must cost that signal, not the whole panel:
/// reading properties off a non-object throws <see cref="InvalidOperationException"/>, which the loader does not catch, so the panel
/// used to be left empty with no word of why.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryStateShapeTests : IDisposable
{
    private const string GoodSignal =
        "{\"vendor\":\"Anthropic\",\"product\":\"Claude Code\",\"category\":\"agent\",\"detector\":\"process\",\"confidence\":0.9," +
        "\"state\":\"active\",\"evidence\":[{\"type\":\"process\",\"basename\":\"claude.exe\",\"quality\":1}]}";

    private readonly TempDirectory _temp = new();
    private readonly DefenseClaw.App.Services.AppServices _services;

    public AiDiscoveryStateShapeTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private async Task<(IReadOnlyList<DiscoverySignalRecord> Signals, DiscoverySourceInfo Source)> LoadAsync(string stateJson)
    {
        _ = _temp.WriteFile("ai_discovery_state.json", stateJson);
        var vm = new AiDiscoveryPanelViewModel(_services);
        return await vm.LoadSignalsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_well_formed_state_file_yields_its_signal()
    {
        var (signals, source) = await LoadAsync("{\"signals\":{\"a\":" + GoodSignal + "}}");

        Assert.Equal("Claude Code", Assert.Single(signals).Product);
        Assert.True(source.Available);
    }

    [Theory]
    [InlineData("\"not-an-object\"")]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[1,2,3]")]
    [InlineData("[]")]
    public async Task A_signal_that_is_not_an_object_is_skipped_and_the_others_still_load(string badValue)
    {
        var (signals, source) = await LoadAsync("{\"signals\":{\"bad\":" + badValue + ",\"good\":" + GoodSignal + "}}");

        Assert.Equal("Claude Code", Assert.Single(signals).Product);
        Assert.True(source.Available);
    }

    [Fact]
    public async Task A_state_file_whose_every_signal_is_the_wrong_shape_loads_as_empty_instead_of_throwing()
    {
        var (signals, _) = await LoadAsync("{\"signals\":{\"a\":1,\"b\":\"x\",\"c\":null}}");

        Assert.Empty(signals);
    }
}
