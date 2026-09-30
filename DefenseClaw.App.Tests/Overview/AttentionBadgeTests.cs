using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// "What needs attention" says how severe a row is in a word (D4-9). The row's severity was the 4 px tone bar and, in Linear and
/// TUI, a glyph: under Default only a colour, which is nothing to a colour-blind reader or a screenshot in greyscale.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AttentionBadgeTests
{
    [Fact]
    public void Every_attention_row_carries_its_severity_as_a_word_whatever_the_style_draws_for_a_glyph()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, 1400, 900);
                var panel = shell.Show<OverviewPanel>();
                var vm = (OverviewPanelViewModel)shell.ViewModel;

                vm.Apply(new GatewaySnapshot
                {
                    State = AppGatewayState.Running,
                    Detail = "detail",
                    CriticalAlertCount = 2,
                    RecentAlerts = Array.Empty<GatewayAlert>(),
                    PolledAt = DateTimeOffset.UtcNow,
                });
                shell.Host.Relayout();

                var rows = vm.Attention.ToList();
                Assert.NotEmpty(rows);

                // One badge per row, reading the row's own severity word.
                var badges = VisualTree.Descendants<Border>(panel)
                    .Where(b => b.DataContext is AttentionRow && b.Child is TextBlock)
                    .Select(b => ((AttentionRow)b.DataContext, ((TextBlock)b.Child).Text))
                    .ToList();

                foreach (var row in rows)
                {
                    Assert.Contains(badges, badge => ReferenceEquals(badge.Item1, row) && badge.Text == row.SeverityKey);
                    Assert.False(string.IsNullOrWhiteSpace(row.SeverityKey));
                }
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }
}
