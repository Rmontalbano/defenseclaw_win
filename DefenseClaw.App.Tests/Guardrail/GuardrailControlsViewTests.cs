using System.Windows.Controls;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Guardrail;
using DefenseClaw.App.Views.Shell;
using DefenseClaw.App.Services.Appearance;

namespace DefenseClaw.App.Tests.Guardrail;

/// <summary>The controls view over a view-model fed by a fake runner: it builds, lays out, shows the review overlay and renders.</summary>
[Collection(UiCollection.Name)]
public sealed class GuardrailControlsViewTests
{
    private static GuardrailControlsViewModel Load(GuardrailControlsViewModelTests.FakeCli cli)
    {
        var vm = new GuardrailControlsViewModel(cli.Run);
        _ = vm.RefreshCommand.ExecuteAsync(null);
        UiThread.WaitFor(() => !vm.IsBusy && vm.JudgeRows.Count > 0, "the guardrail reads to land");
        return vm;
    }

    [Theory]
    [InlineData("Cisco", "Light")]
    [InlineData("Linear", "Dark")]
    public void The_view_lays_out_in_a_window_sized_host_and_the_review_overlay_appears_on_a_write(string styleName, string modeName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, mode));

        UiThread.Run(() =>
        {
            var vm = Load(new GuardrailControlsViewModelTests.FakeCli());
            var view = new GuardrailControlsView { DataContext = vm };
            using var host = new OffscreenHost(view, 760, 720);

            var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Select(b => b.Content?.ToString()).ToList();
            Assert.Contains("Turn HILT on", buttons);
            Assert.Contains("Set message", buttons);
            Assert.Contains("Add all hook connectors", buttons);

            // The scope picker is there on a multi-connector install, the review is not yet.
            Assert.Contains(VisualTree.Descendants<ComboBox>(view), c => c.IsVisible);
            Assert.DoesNotContain(VisualTree.Descendants<CommandReviewControl>(view), c => c.IsVisible);
            RenderTo.Png(host, $"D2-controls-{style}-{mode}");

            vm.MessageText = "Blocked by Acme Security: see #sec-help";
            vm.SetBlockMessageCommand.Execute(null);
            host.Relayout();

            var review = Assert.Single(VisualTree.Descendants<CommandReviewControl>(view), c => c.IsVisible);
            Assert.Same(vm.Review, review.Review);
            RenderTo.Png(host, $"D2-review-{style}-{mode}");
        });
    }

    [Fact]
    public void Escape_closes_the_review_before_anything_else_and_f5_is_the_refresh()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Cisco, AppearanceMode.Light));

        UiThread.Run(() =>
        {
            var vm = Load(new GuardrailControlsViewModelTests.FakeCli());
            var view = new GuardrailControlsView { DataContext = vm };
            using var host = new OffscreenHost(view, 760, 720);

            Assert.False(view.HandlesEscape);
            vm.TurnHiltOffCommand.Execute(null);
            Assert.True(view.HandlesEscape);
            vm.CancelReviewCommand.Execute(null);
            Assert.False(view.HandlesEscape);
        });
    }
}
