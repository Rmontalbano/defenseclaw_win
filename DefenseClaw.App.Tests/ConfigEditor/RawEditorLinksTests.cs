using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// The RAW config editor is a text editor, not a browser (D4-8): AvalonEdit turns URLs and e-mail addresses into links by
/// default, and a Ctrl+click on an address in the file (a password's user@host, a webhook URL) would launch the mail client or
/// the browser. The window switched URLs off and had missed the e-mail half.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RawEditorLinksTests
{
    [Fact]
    public void The_raw_editor_draws_no_links_for_URLs_or_for_mail_addresses()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, ConfigSamples.Raw);

        UiThread.Run(() =>
        {
            var window = new ConfigEditorWindow(services);
            try
            {
                var options = window.RawEditor.Options;
                Assert.False(options.EnableHyperlinks);
                Assert.False(options.EnableEmailHyperlinks);

                // What actually draws a link is an element generator on the text view; neither kind may be there.
                Assert.DoesNotContain(
                    window.RawEditor.TextArea.TextView.ElementGenerators,
                    generator => generator.GetType().Name.Contains("Link", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
