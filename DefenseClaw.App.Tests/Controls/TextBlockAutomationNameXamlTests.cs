using System.Text.RegularExpressions;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Controls;

/// <summary>
/// A <c>TextBlock</c>'s automation name is its text. A fixed <c>AutomationProperties.Name</c> on one whose text is bound replaces that text for a
/// screen reader (CUST-444: "Runtime monitoring degraded" was read, the reasons never were). A label is fine when it carries the bound value too
/// (<c>{Binding X, StringFormat='Label: {0}'}</c>). This reads every <c>*.xaml</c> of the app.
/// </summary>
public sealed partial class TextBlockAutomationNameXamlTests
{
    [Fact]
    public void No_text_block_with_bound_text_has_a_fixed_automation_name()
    {
        var found = new List<string>();
        foreach (var file in AppSource.Xaml())
        {
            var text = File.ReadAllText(file);
            foreach (Match element in TextBlockElement().Matches(text))
            {
                var tag = element.Value;
                if (FixedName().Match(tag) is { Success: true } name && BoundText().IsMatch(tag))
                {
                    var line = text[..element.Index].Count(static c => c == '\n') + 1;
                    found.Add($"{Path.GetFileName(file)}:{line} AutomationProperties.Name=\"{name.Groups[1].Value}\"");
                }
            }
        }

        Assert.True(found.Count == 0, "A fixed name hides the bound text:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    [GeneratedRegex(@"<(?:\w+:)?TextBlock\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex TextBlockElement();

    [GeneratedRegex(@"AutomationProperties\.Name=""([^""{][^""]*)""")]
    private static partial Regex FixedName();

    [GeneratedRegex(@"\bText=""\{")]
    private static partial Regex BoundText();
}
