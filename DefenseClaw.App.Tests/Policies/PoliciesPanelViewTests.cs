using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// What the operator sees on the Policies panel: the table, the inspector, the output box, the buttons that go off with their reason, and
/// the acknowledgement checkbox in the review. Synthetic data; a PNG is written when <c>DC_RENDER_DIR</c> names a folder.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class PoliciesPanelViewTests
{
    private static CliInvocation Result(int exit, IReadOnlyList<string> argv, params string[] lines)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        foreach (var line in lines)
        {
            InvocationFactory.Append(invocation, line);
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    private static Task<CliInvocation> Script(IReadOnlyList<string> argv, bool validateFails)
    {
        var text = string.Join(' ', argv);
        return Task.FromResult(text switch
        {
            "policy list" => Result(0, argv, PolicyFixtures.ListReport),
            "policy validate" when validateFails => Result(1, argv, "  OK data.json: OK", "  X FAIL: 'opa' binary not found - install OPA to validate Rego bundles."),
            "policy validate" => Result(0, argv, "  OK data.json: OK", "  OK All validations passed."),
            _ when text.StartsWith("policy show ", StringComparison.Ordinal) =>
                Result(0, argv, text.EndsWith("permissive", StringComparison.Ordinal) || text.EndsWith("team-baseline", StringComparison.Ordinal)
                    ? PolicyFixtures.Show(argv[2], install: "none", runtime: "enable", block: 4, alert: 3, trust: "none", firewallDefault: "allow")
                    : PolicyFixtures.Show(argv[2], extraOverride: "\nScanner Overrides:\n  mcp:\n    MEDIUM      install=block  file=quarantine  runtime=block\n")),
            _ => throw new InvalidOperationException(text),
        });
    }

    private static (PoliciesPanel View, PoliciesPanelViewModel Vm, OffscreenHost Host) Open(AppServices services, bool validateFails = false, double width = 1100, double height = 700)
    {
        var vm = new PoliciesPanelViewModel(services) { RunCli = (argv, _) => Script(argv, validateFails) };
        var view = new PoliciesPanel { DataContext = vm };
        var host = new OffscreenHost(view, width, height);
        var init = vm.InitializeAsync();
        UiThread.WaitFor(() => init.IsCompleted, "policies read");
        host.Relayout();
        return (view, vm, host);
    }

    [Fact]
    public void The_table_the_inspector_and_the_output_render_and_the_toolbar_follows_the_conventions()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        UiThread.Run(() =>
        {
            var (view, vm, host) = Open(services, validateFails: true);
            using (host)
            {
                var toolbar = Assert.Single(VisualTree.Descendants<DcPageToolbar>(view));
                Assert.Equal("Policies", toolbar.Title);
                Assert.Equal("4 policies - active: default", toolbar.Caption);

                var grid = Assert.Single(VisualTree.Descendants<Wpf.Ui.Controls.DataGrid>(view));
                Assert.Equal(4, grid.Items.Count);
                Assert.Equal(new[] { "Active", "Name", "Kind", "Description" }, grid.Columns.Select(c => c.Header?.ToString()).ToArray());

                vm.SelectedRow = vm.Rows.Single(r => r.Name == "strict");
                UiThread.WaitFor(() => vm.Detail is not null, "detail read");
                _ = vm.ValidateCommand.ExecuteAsync(null);
                UiThread.WaitFor(() => vm.HasOutput, "validate output");
                host.Relayout();

                var inspector = Assert.Single(VisualTree.Descendants<DcInspector>(view));
                Assert.True(inspector.IsOpen);
                var outputs = VisualTree.Descendants<DcCommandOutput>(view);
                Assert.Contains(outputs, o => o.Text.Contains("opa", StringComparison.Ordinal));

                // The pane fades in over 120 ms; a picture taken now would catch it half drawn.
                inspector.BeginAnimation(UIElement.OpacityProperty, null);
                inspector.Opacity = 1;
                RenderTo.Png(host, "policies-panel");
            }
        });
    }

    [Fact]
    public void Activate_edit_and_delete_go_off_with_the_reason_as_their_tooltip_when_the_list_is_not_trusted()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        UiThread.Run(() =>
        {
            var (view, vm, host) = Open(services);
            using (host)
            {
                vm.SelectedRow = vm.Rows.Single(r => r.Name == "team-baseline");
                UiThread.WaitFor(() => vm.Detail is not null, "detail read");
                host.Relayout();

                var buttons = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view);
                Wpf.Ui.Controls.Button Named(string name) => buttons.First(b => AutomationProperties.GetName(b) == name);

                Assert.True(Named("Activate policy").IsEnabled);
                Assert.True(Named("Delete policy").IsEnabled);

                vm.Trust.MarkFailed("fixture: read failed");
                vm.RefreshWarning = "fixture";
                typeof(PoliciesPanelViewModel).GetMethod("NotifyTrust", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                host.Relayout();

                foreach (var name in new[] { "Activate policy", "Edit policy", "Delete policy", "Create policy" })
                {
                    var button = Named(name);
                    Assert.False(button.IsEnabled, name);
                    Assert.Contains("read failed", button.ToolTip?.ToString(), StringComparison.Ordinal);
                }

                // Copy name and Validate are reads and stay on.
                Assert.True(Named("Copy policy name").IsEnabled);
                Assert.True(Named("Validate").IsEnabled);
            }
        });
    }

    [Fact]
    public void A_review_that_reduces_protection_shows_the_checkbox_and_keeps_the_confirm_button_off_until_it_is_ticked()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);
        UiThread.Run(() =>
        {
            var (view, vm, host) = Open(services);
            using (host)
            {
                vm.SelectedRow = vm.Rows.Single(r => r.Name == "team-baseline");
                UiThread.WaitFor(() => vm.Detail is not null, "detail read");

                var prepared = vm.ActivateCommand.ExecuteAsync(null);
                UiThread.WaitFor(() => prepared.IsCompleted, "activate prepared");
                host.Relayout();

                Assert.True(vm.Review.IsOpen);
                var box = VisualTree.Descendants<CheckBox>(view).Single(c => AutomationProperties.GetName(c) == "Acknowledge before running");
                Assert.True(box.IsVisible);
                var confirm = VisualTree.Descendants<Wpf.Ui.Controls.Button>(view).Single(b => b.Content as string == "Activate policy");
                Assert.False(confirm.IsEnabled);

                RenderTo.Png(host, "policies-review-weaker");

                box.IsChecked = true;
                host.Relayout();
                Assert.True(vm.Review.IsAcknowledged);
                Assert.True(confirm.IsEnabled);
            }
        });
    }
}
