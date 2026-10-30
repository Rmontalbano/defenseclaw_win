using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// CUST-401: the Effective policy card shows the 1.0.5 sandbox refusal as one honest row (Windows has no sandboxes), puts the request paths into the error
/// sentence of a refused account, and draws nothing new for an answer without the fields. Synthetic answers; no socket.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class EffectivePolicyRefusalCardTests : IDisposable
{
    private const string Reason = "DefenseClaw refused this request: group membership is unconfirmed";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static EffectivePolicyRead Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return EffectivePolicyReader.Parse(document.RootElement.Clone());
    }

    [Fact]
    public void A_sandbox_refusal_is_a_row_that_says_sandboxes_are_not_supported_here()
    {
        using var services = TestServices.Create(_temp);
        var vm = new OverviewPanelViewModel(services);

        vm.ApplyEffectivePolicy(
            Parse("{\"profiles_configured\":true,\"profile\":\"strict\",\"match\":\"user\",\"refused_paths\":[\"sandbox\"],\"sandbox_refused_reason\":\"" + Reason + "\"," +
                  "\"warnings\":[\"a sandbox run of this account for codex is refused (" + Reason + "); its hook and inspect requests are not\"]," +
                  "\"effective\":{\"mode\":\"action\",\"block_at\":\"HIGH\",\"alert_at\":\"MEDIUM\"}}"),
            "codex");

        Assert.Equal(EffectivePolicyCardState.Shown, vm.EffectivePolicyState);
        var rows = vm.EffectivePolicyRows.ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("strict", rows["Profile"]);
        var sandbox = rows["Sandbox runs"];
        Assert.StartsWith("Refused (" + Reason + ").", sandbox, StringComparison.Ordinal);
        Assert.Contains("does not support sandboxes", sandbox, StringComparison.Ordinal);

        // The gateway's own sentence about it is the row now, not a second copy in the caption.
        Assert.Equal(string.Empty, vm.EffectivePolicyNote);
    }

    [Fact]
    public void An_answer_without_a_sandbox_refusal_has_no_such_row()
    {
        using var services = TestServices.Create(_temp);
        var vm = new OverviewPanelViewModel(services);

        vm.ApplyEffectivePolicy(Parse("{\"profiles_configured\":false,\"effective\":{\"mode\":\"observe\",\"block_at\":\"CRITICAL\"}}"), "codex");

        Assert.DoesNotContain(vm.EffectivePolicyRows, r => r.Label == "Sandbox runs");
    }

    [Fact]
    public void A_refusal_on_the_hook_path_is_the_cards_error_and_names_the_path()
    {
        using var services = TestServices.Create(_temp);
        var vm = new OverviewPanelViewModel(services);

        vm.ApplyEffectivePolicy(
            Parse("{\"profiles_configured\":true,\"profile\":\"strict\",\"refused\":true,\"refused_reason\":\"" + Reason + "\",\"refused_paths\":[\"hook\"],\"effective\":{\"mode\":\"action\"}}"),
            "codex");

        Assert.Equal(EffectivePolicyCardState.Error, vm.EffectivePolicyState);
        Assert.Empty(vm.EffectivePolicyRows);
        Assert.Contains("for hook requests", vm.EffectivePolicyNote, StringComparison.Ordinal);
        Assert.Contains("group membership is unconfirmed", vm.EffectivePolicyNote, StringComparison.Ordinal);
    }
}
