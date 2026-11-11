using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.ClaudeCode;

namespace DefenseClaw.App.Tests.Setup;

/// <summary>
/// The Setup hub's Cowork card line follows the Cowork store (CUST-444): a read published while the hub is open redraws it, with no CLI call of
/// its own. Below 1.0.8 the store has nothing and the card has no line, as before. Synthetic state; nothing runs.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SetupCoworkCardTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public void Dispose()
    {
        _services?.Dispose();
        _temp.Dispose();
    }

    private static readonly WizardDefinition[] Roster =
    {
        new() { Target = "cowork", Title = "Cowork", Group = WizardGroups.Connectors, IsDetailLoaded = true },
        new() { Target = "llm", Title = "LLM", Group = WizardGroups.GuardrailAndPolicy, IsDetailLoaded = true },
    };

    private static CoworkProtection State(bool isProtected, string code) => new(isProtected, "the CLI's sentence", string.Empty, code, null, null, null);

    private async Task<AppServices> ServicesAsync(string version)
    {
        _services = TestServices.Create(_temp, "guardrail:\n  enabled: true\n", FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync();
        return _services;
    }

    private static WizardCardViewModel Card(SetupPanelViewModel vm) => vm.Groups.SelectMany(g => g.Cards).Single(c => c.Target == "cowork");

    [Fact]
    public async Task On_1_0_8_a_published_read_redraws_the_cowork_card_line_while_the_hub_is_open()
    {
        var services = await ServicesAsync("1.0.8");
        var store = CoworkProtectionStore.For(services);

        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(Roster);
            Assert.False(Card(vm).HasStateLine);

            store.Publish(State(false, CoworkReasonCodes.NoEvents));
            vm.FollowCoworkState();
            Assert.Equal("Built, not uploaded yet", Card(vm).StateLine);

            store.Publish(State(true, string.Empty));
            vm.FollowCoworkState();
            Assert.Equal("Protected", Card(vm).StateLine);
        });
    }

    [Fact]
    public async Task On_1_0_7_the_store_has_nothing_and_the_card_keeps_no_line()
    {
        var services = await ServicesAsync("1.0.7");
        var store = CoworkProtectionStore.For(services);

        UiThread.Run(() =>
        {
            var vm = new SetupPanelViewModel(services);
            vm.ShowCards(Roster);

            store.Publish(State(true, string.Empty));
            vm.FollowCoworkState();
            Assert.False(Card(vm).HasStateLine);
        });
    }
}
