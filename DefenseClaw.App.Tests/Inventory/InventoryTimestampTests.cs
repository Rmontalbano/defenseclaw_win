using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>
/// The table browser formats timestamp-looking cells. Only strings that start like a date qualify:
/// DateTimeOffset.TryParse alone read version-like cells ("1-0", "2.1") as dates.
/// </summary>
public class InventoryTimestampTests
{
    [Theory]
    [InlineData("2026-07-28 21:33:39.2587908 +0000 UTC")]
    [InlineData("2026-07-28T21:33:39Z")]
    [InlineData("2026-07-28")]
    [InlineData("  2026-07-28 21:33:39 +0000 UTC")]
    public void Date_shaped_strings_parse(string raw) =>
        Assert.True(InventoryPanelViewModel.TryParseFlexibleTimestamp(raw, out _));

    [Theory]
    [InlineData("1-0")]
    [InlineData("2.1")]
    [InlineData("1.2.3")]
    [InlineData("10/11")]
    [InlineData("Jan 5")]
    [InlineData("26-07-28")]
    [InlineData("")]
    [InlineData(null)]
    public void Version_like_and_partial_strings_are_not_timestamps(string? raw) =>
        Assert.False(InventoryPanelViewModel.TryParseFlexibleTimestamp(raw, out _));
}
