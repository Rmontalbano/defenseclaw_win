using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Scanners card's two added rows (CUST-389, the Mac's <c>ScannerProbe.statuses</c>): <c>aibom</c>, a built-in, and <c>keys</c>, the required
/// credentials the last doctor run found missing. The keys row reads the doctor cache the Doctor card already holds, so it adds no read and no
/// command to Overview load; these tests feed it cache text only. Key names are the synthetic ones the doctor cache tests use.
/// </summary>
public sealed class OverviewScannersCardTests
{
    private static string Cache(params (string Status, string Label)[] checks) =>
        "{\"passed\": 3, \"failed\": 1, \"warned\": 0, \"skipped\": 0, \"captured_at\": \"" +
        DateTimeOffset.UtcNow.AddMinutes(-2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) +
        "\", \"checks\": [" + string.Join(",", checks.Select(c => $"{{\"status\": \"{c.Status}\", \"label\": \"{c.Label}\", \"detail\": \"d\"}}")) + "]}";

    private static ScannerRow Keys(string json) => OverviewPanelViewModel.KeysRow(DoctorCacheReader.Parse(json));

    [Fact]
    public void With_no_doctor_cache_the_keys_row_says_not_checked_in_amber()
    {
        var row = OverviewPanelViewModel.KeysRow(null);

        Assert.Equal("keys", row.Name);
        Assert.Equal("not checked", row.StateText);
        Assert.Equal("Warn", row.StateKey);
        Assert.Contains("defenseclaw doctor", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_with_no_results_is_not_a_check_either()
    {
        var row = Keys("{\"passed\": 0, \"failed\": 0, \"warned\": 0, \"skipped\": 0, \"checks\": []}");

        Assert.Equal("not checked", row.StateText);
    }

    [Fact]
    public void A_cache_with_no_failing_credential_check_says_all_required_set()
    {
        var row = Keys(Cache(("pass", "credential ANTHROPIC_API_KEY"), ("fail", "sidecar api"), ("warn", "credential OPENAI_API_KEY")));

        Assert.Equal("all required set", row.StateText);
        Assert.Equal("Ok", row.StateKey);
    }

    [Fact]
    public void One_or_two_missing_keys_are_named()
    {
        var one = Keys(Cache(("fail", "credential ANTHROPIC_API_KEY")));
        var two = Keys(Cache(("fail", "credential ANTHROPIC_API_KEY"), ("fail", "credential OPENAI_API_KEY"), ("fail", "credential ANTHROPIC_API_KEY")));

        Assert.Equal("1 missing", one.StateText);
        Assert.Equal("Warn", one.StateKey);
        Assert.StartsWith("ANTHROPIC_API_KEY:", one.Detail, StringComparison.Ordinal);

        // A key doctor listed twice is one key.
        Assert.Equal("2 missing", two.StateText);
        Assert.StartsWith("ANTHROPIC_API_KEY, OPENAI_API_KEY:", two.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("more", two.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_two_missing_keys_name_two_and_count_the_rest()
    {
        var row = Keys(Cache(
            ("fail", "credential ANTHROPIC_API_KEY"),
            ("fail", "credential OPENAI_API_KEY"),
            ("fail", "credential EXAMPLE_ONE_KEY"),
            ("fail", "credential EXAMPLE_TWO_KEY")));

        Assert.Equal("4 missing", row.StateText);
        Assert.StartsWith("ANTHROPIC_API_KEY, OPENAI_API_KEY (+2 more):", row.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMPLE_ONE_KEY", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_row_never_carries_a_value_only_names()
    {
        // A credential check's detail can say anything the runtime wrote; the row shows the label's name and nothing from the detail.
        var json = "{\"passed\": 0, \"failed\": 1, \"warned\": 0, \"skipped\": 0, \"checks\": [{\"status\": \"fail\", \"label\": \"credential ANTHROPIC_API_KEY\", \"detail\": \"sk-synthetic-detail-must-not-appear\"}]}";

        var row = Keys(json);

        Assert.DoesNotContain("sk-synthetic", row.ToString(), StringComparison.Ordinal);
        Assert.Equal("keys. 1 missing. ANTHROPIC_API_KEY: doctor found them unset. Fill missing keys sets them.", row.ToString());
    }
}

/// <summary>The card in the view-model and as a real view: the Mac's row order, and the keys row following the doctor cache. A PNG of each state is written when <c>DC_RENDER_DIR</c> names a folder.</summary>
[Collection(UiCollection.Name)]
public sealed class OverviewScannersCardViewTests
{
    private const string ThreeMissing =
        "{\"passed\": 20, \"failed\": 3, \"warned\": 0, \"skipped\": 0, \"checks\": [" +
        "{\"status\": \"fail\", \"label\": \"credential ANTHROPIC_API_KEY\", \"detail\": \"not set\"}," +
        "{\"status\": \"fail\", \"label\": \"credential OPENAI_API_KEY\", \"detail\": \"not set\"}," +
        "{\"status\": \"fail\", \"label\": \"credential EXAMPLE_GATEWAY_TOKEN\", \"detail\": \"not set\"}]}";

    [Fact]
    public async Task The_card_lists_the_macs_six_in_the_macs_order_and_the_keys_row_follows_the_cache()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        var vm = UiThread.Run(() => new OverviewPanelViewModel(services));

        // No cache yet: the card says it did not check.
        await UiThread.Run(() => vm.ReloadDoctorCacheAsync(CancellationToken.None));
        UiThread.Run(() =>
        {
            var names = vm.ScannerRows.Select(r => r.Name).ToList();
            Assert.Equal(new[] { "skill-scanner", "mcp-scanner", "aibom", "codeguard", "keys" }, names.Take(5).ToArray());
            Assert.Equal("built-in", vm.ScannerRows.Single(r => r.Name == "aibom").StateText);
            Assert.Equal("not checked", vm.ScannerRows.Single(r => r.Name == "keys").StateText);
        });

        // Doctor wrote a cache with three required keys unset: the row follows the next read.
        _ = temp.WriteFile("doctor_cache.json", ThreeMissing);
        await UiThread.Run(() => vm.ReloadDoctorCacheAsync(CancellationToken.None));
        UiThread.Run(() =>
        {
            var keys = vm.ScannerRows.Single(r => r.Name == "keys");
            Assert.Equal("3 missing", keys.StateText);
            Assert.Equal("Warn", keys.StateKey);
            Assert.StartsWith("ANTHROPIC_API_KEY, OPENAI_API_KEY (+1 more):", keys.Detail, StringComparison.Ordinal);
        });

        // Scoped to a connector the policy row still leads, and the rows after it are the same.
        UiThread.Run(() =>
        {
            services.ConnectorScope.UpdateRoster(new[] { "claudecode", "hermes" });
            _ = services.ConnectorScope.Set("hermes");
            vm.Apply(services.Monitor.Current);
            Assert.Equal(new[] { "policy", "skill-scanner", "mcp-scanner", "aibom", "codeguard", "keys" }, vm.ScannerRows.Take(6).Select(r => r.Name).ToArray());
        });
    }

    [Theory]
    [InlineData(980, 692, "Light")]
    [InlineData(980, 692, "Dark")]
    [InlineData(1468, 875, "Light")]
    [InlineData(1468, 875, "Dark")]
    public async Task The_scanners_card_shows_aibom_and_keys_without_clipping(int width, int height, string mode)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));
        using var data = OverviewScene.Create(seedAudit: true);
        _ = data.Temp.WriteFile("doctor_cache.json", ThreeMissing);

        PanelShell? shell = null;
        try
        {
            var panel = UiThread.Run(() =>
            {
                data.Publish(OverviewScene.Snapshot());
                shell = new PanelShell(data.Services, width, height);
                var view = shell.Show<OverviewPanel>();
                var vm = (OverviewPanelViewModel)shell.ViewModel;
                vm.Apply(OverviewScene.Snapshot());
                shell.Host.Relayout();
                return view;
            });
            var model = UiThread.Run(() => (OverviewPanelViewModel)shell!.ViewModel);
            await UiThread.Run(() => model.ReloadDoctorCacheAsync(CancellationToken.None));

            UiThread.Run(() =>
            {
                shell!.Host.Relayout();
                var header = VisualTree.Descendants<DcCardHeader>(panel).First(h => Equals(h.Content, "Scanners"));
                var card = Ancestor<Border>(header);
                card.BringIntoView();
                shell.Host.Relayout();

                // Every row of the card is drawn, in order, with its state word, and nothing is cut off at the card's right edge.
                var names = VisualTree.Descendants<TextBlock>(card).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                Assert.Contains("aibom", names);
                Assert.Contains("keys", names);
                Assert.Contains("built-in", names);
                Assert.Contains("3 missing", names);
                Assert.True(names.IndexOf("aibom") < names.IndexOf("codeguard") && names.IndexOf("codeguard") < names.IndexOf("keys"));

                var cardBounds = new Rect(card.TranslatePoint(new Point(0, 0), shell.Host.Content), new Size(card.ActualWidth, card.ActualHeight));
                foreach (var text in VisualTree.Descendants<TextBlock>(card).Where(t => t.IsVisible && t.ActualWidth > 0))
                {
                    var right = text.TranslatePoint(new Point(text.ActualWidth, 0), shell.Host.Content).X;
                    Assert.True(right <= cardBounds.Right + 0.5, $"'{text.Text}' runs past the card ({right} > {cardBounds.Right})");
                }

                RenderTo.Png(shell.Host, $"overview-scanners-{width}x{height}-{mode.ToLowerInvariant()}");
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    private static T Ancestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(start); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        throw new InvalidOperationException($"no {typeof(T).Name} above the element");
    }
}
