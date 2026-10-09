using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Discovery;

/// <summary>
/// The state of each product on the AI Discovery Products view (CUST-329, over the signal state CUST-277 computes): the pill names the strongest
/// state of the product's signals (new, changed, active, seen or gone), and a product whose signals are not all in one state also says in words how
/// they split, so a "new" pill over twenty-eight seen signals is not read as twenty-nine new ones.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AiDiscoveryProductStateTests
{
    private static DiscoveryComponentCard Card(string product, params string[] states) => new()
    {
        Vendor = "Example Vendor",
        Product = product,
        Signals = states
            .Select((state, i) => new DiscoverySignalRecord(
                "Example Vendor", product, "agent", "process", "synthetic", 0.9, state, null, null, Array.Empty<DiscoveryEvidenceItem>())
            {
                SignalId = $"{product}-{i}",
                Name = $"{product} signal {i}",
            })
            .ToList(),
    };

    [Fact]
    public void A_product_whose_signals_agree_has_its_state_in_the_pill_and_nothing_to_add()
    {
        var card = Card("Example Agent", "new", "new");

        Assert.Equal("new", card.State);
        Assert.False(card.HasMixedStates);
        Assert.Equal("2 new", card.StateTally);
    }

    [Fact]
    public void A_product_with_mixed_signals_names_the_strongest_in_the_pill_and_the_split_in_words()
    {
        var card = Card("Example SDK", "seen", "new", "seen", "gone");

        Assert.Equal("new", card.State);
        Assert.True(card.HasMixedStates);
        Assert.Equal("1 new, 2 seen, 1 gone", card.StateTally);
        Assert.Contains("(1 new, 2 seen, 1 gone)", card.HeaderDisplay, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("changed", "active", "changed")]
    [InlineData("active", "gone", "active")]
    [InlineData("gone", "gone", "gone")]
    public void The_pill_is_the_strongest_of_new_changed_active_and_gone_and_a_wholly_gone_product_is_gone(string a, string b, string expected)
    {
        var card = Card("Example CLI", a, b);

        Assert.Equal(expected, card.State);
        Assert.Equal(DiscoveryStates.Tone(expected), card.StateKey);
        Assert.Equal(a != b, card.HasMixedStates);
    }

    [Fact]
    public void A_product_whose_signals_carry_no_state_shows_no_pill_and_no_split()
    {
        var card = Card("Example Legacy", string.Empty, string.Empty);

        Assert.False(card.HasState);
        Assert.False(card.HasMixedStates);
    }

    [Fact]
    public void The_loaded_products_sort_by_their_strongest_state_and_carry_the_split_from_the_file()
    {
        using var scene = DiscoveryScene.Open("ai-discovery-state.0.8.10.json", DiscoveryScene.DiscoveryOn);

        scene.OnUi(() =>
        {
            var cards = scene.ViewModel.CardsView.Cast<DiscoveryComponentCard>().ToList();

            Assert.Equal(cards.OrderBy(c => c.StateWeight).Select(c => c.Product), cards.Select(c => c.Product));
            Assert.All(cards, c => Assert.Equal(c.Signals.Select(s => DiscoveryStates.Normalize(s.State)).Where(s => s.Length > 0).Distinct().Count() > 1, c.HasMixedStates));
            Assert.Contains(cards, c => c.State == DiscoveryStates.New);
        });
    }
}
