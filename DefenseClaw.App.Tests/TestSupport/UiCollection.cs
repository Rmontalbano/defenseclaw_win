namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Puts every test that builds real views in one xunit collection, so they run one after another. They all share
/// <see cref="UiThread"/> (one dispatcher, one <c>Application</c>); serializing them keeps a slow layout in one test
/// from ever being interleaved with another test's dispatcher work.
/// </summary>
[CollectionDefinition(Name)]
public sealed class UiCollection
{
    public const string Name = "WPF views";
}
