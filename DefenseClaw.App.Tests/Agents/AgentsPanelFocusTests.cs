using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Agents;

/// <summary>The Agents panel's deep link from the AI BOM's agent identities (CUST-381).</summary>
[Collection(UiCollection.Name)]
public sealed class AgentsPanelFocusTests : IDisposable
{
    private readonly AgentsScene _scene = new();

    public void Dispose() => _scene.Dispose();

    [Fact]
    public async Task An_agent_focus_selects_that_directory_row_and_opens_it()
    {
        var vm = await _scene.OpenAsync();

        UiThread.Run(() => vm.Accept(new AgentFocus("agt-0000000000000001")));
        UiThread.WaitFor(() => vm.HasLedger || vm.HasOpenMessage, "the agent to open");

        Assert.Equal("agt-0000000000000001", _scene.Ui(() => vm.SelectedAgent?.AgentId));
        Assert.NotEqual(AgentsPanelViewModel.AgentNotInViewMessage, _scene.Ui(() => vm.OpenMessage));
    }

    [Fact]
    public async Task An_agent_that_is_not_listed_selects_nothing_and_says_so_quietly()
    {
        var vm = await _scene.OpenAsync();

        UiThread.Run(() => vm.Accept(new AgentFocus("agt-unknown")));

        Assert.Null(_scene.Ui(() => vm.SelectedAgent));
        Assert.Equal(AgentsPanelViewModel.AgentNotInViewMessage, _scene.Ui(() => vm.OpenMessage));
        Assert.Equal(AgentsState.Ready, _scene.Ui(() => vm.State));
    }

    [Fact]
    public async Task A_payload_of_another_type_or_without_an_id_is_ignored()
    {
        var vm = await _scene.OpenAsync();

        UiThread.Run(() =>
        {
            vm.Accept(new AuditPreset("x"));
            vm.Accept(new AgentFocus("  "));
        });

        Assert.Null(_scene.Ui(() => vm.SelectedAgent));
        Assert.Equal(string.Empty, _scene.Ui(() => vm.OpenMessage));
    }

    [Fact]
    public async Task A_link_that_arrives_before_the_directory_is_read_lands_when_the_read_ends()
    {
        var vm = await _scene.OpenAsync(model => model.Accept(new AgentFocus("agt-0000000000000002")));

        Assert.Equal("agt-0000000000000002", _scene.Ui(() => vm.SelectedAgent?.AgentId));
    }
}
