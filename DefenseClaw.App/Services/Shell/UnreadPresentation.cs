using System.Globalization;

namespace DefenseClaw.App.Services;

/// <summary>What one sidebar entry's "new since last visit" capsule shows: its text, and the sentence a screen reader appends to the entry's name.</summary>
/// <param name="Text">The capsule's text ("7", "99+"); empty when there is nothing new.</param>
/// <param name="Description">"7 new since last visit"; empty with no capsule.</param>
internal readonly record struct UnreadBadgeState(string Text, string Description)
{
    /// <summary>Nothing new: no capsule, and the entry keeps its plain name.</summary>
    public static UnreadBadgeState None { get; } = new(string.Empty, string.Empty);

    /// <summary>True when the capsule shows.</summary>
    public bool IsShown => Description.Length > 0;
}

/// <summary>
/// How the "new since last visit" count (CUST-265) is worded on the sidebar, so the capsule, the entry's accessible name and its tooltip say the
/// same thing: "7", "99+", "7 new since last visit", "Audit, 7 new since last visit". The count is capped at <see cref="UnreadCountsService.Cap"/>
/// (99, as the TUI's tab strip is): more than that reads "99+", never a number the app did not count to and never a plain "99" that would claim there
/// are exactly that many.
/// </summary>
internal static class UnreadPresentation
{
    /// <summary>The capsule's text: the count, "99+" past the cap, and nothing at all for zero (a count that has not been read is zero).</summary>
    public static string Badge(int count) =>
        count <= 0
            ? string.Empty
            : count > UnreadCountsService.Cap
                ? UnreadCountsService.Cap.ToString(CultureInfo.InvariantCulture) + "+"
                : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>"7 new since last visit", "99+ new since last visit"; empty for nothing new, which is when there is no capsule.</summary>
    public static string Sentence(int count) =>
        count <= 0 ? string.Empty : Badge(count) + " new since last visit";

    /// <summary>The entry's accessible name: "Audit, 7 new since last visit", or just "Audit" when nothing is new.</summary>
    public static string NavigationName(string title, int count)
    {
        ArgumentNullException.ThrowIfNull(title);
        return count <= 0 ? title : title + ", " + Sentence(count);
    }

    /// <summary>The state a count gives its entry's capsule.</summary>
    public static UnreadBadgeState StateOf(int count) =>
        count <= 0 ? UnreadBadgeState.None : new UnreadBadgeState(Badge(count), Sentence(count));
}
