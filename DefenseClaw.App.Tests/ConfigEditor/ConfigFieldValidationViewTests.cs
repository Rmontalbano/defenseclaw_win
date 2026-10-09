using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.App.Views.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-268 on screen: the FORM tab draws a Choice as a combo box (a value the list does not have is its selected item), draws what a field says
/// about its value under its label, and the header's Save is off, with the reason, while a value with an error is held. The window is built and its
/// content hosted offscreen (never shown); the view-model is the window's own, reached by reflection, loaded from a temp copy of config.yaml.
/// Set <c>DC_RENDER_DIR</c> to also write the PNG.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConfigFieldValidationViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private const string Config = """
        claw:
          mode: openclaw
          home_dir: ''
        gateway:
          host: 127.0.0.1
          api_port: 18970
          tls_skip_verify: false
          token_env: DEFENSECLAW_GATEWAY_TOKEN
        guardrail:
          mode: observe
          hook_fail_mode: open
        config_version: 8

        """;

    private static IEnumerable<string> VisibleTexts(OffscreenHost host) =>
        VisualTree.Descendants<TextBlock>(host.Content)
            .Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text))
            .Select(t => t.Text);

    private static ComboBox ComboFor(OffscreenHost host, string path) =>
        VisualTree.Descendants<ComboBox>(host.Content).Single(c => c.DataContext is FormField f && f.Path == path);

    [Fact]
    public async Task The_form_draws_choices_as_combos_a_held_value_as_an_error_and_turns_the_save_button_off()
    {
        var raw = LineEndings.Normalize(Config);
        using var services = TestServices.Create(_temp, raw);
        services.ConfigWatcher.Dispose();

        await UiThread.Run(async () =>
        {
            var window = new ConfigEditorWindow(services);
            var vm = (ConfigEditorWindowViewModel)typeof(ConfigEditorWindow)
                .GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            vm.FormSourceOverride = _ => Task.FromResult(raw);
            await vm.LoadAsync();

            var content = (FrameworkElement)window.Content;
            window.Content = null;
            content.DataContext = vm;
            using var host = new OffscreenHost(content, 1100, 900);
            try
            {
                var tabs = VisualTree.Find<TabControl>(host.Content, t => t.Name == "RootTabs")!;
                tabs.SelectedIndex = 1;
                host.Relayout();

                var save = VisualTree.Find<Wpf.Ui.Controls.Button>(host.Content, b => b.IsVisible && (b.Content as string) == "Save")!;
                Assert.True(save.IsEnabled);
                Assert.DoesNotContain(VisibleTexts(host), t => t.StartsWith("Save is off", StringComparison.Ordinal));

                // claw.mode: a combo whose list is the Windows one, with the value it holds (openclaw) selected as one more item.
                var mode = ComboFor(host, "claw.mode");
                Assert.Equal("openclaw", mode.SelectedValue);
                Assert.Equal(new[] { "codex", "claudecode", "openclaw" }, mode.Items.Cast<ChoiceOption>().Select(o => o.Value));
                Assert.Equal("Label", mode.DisplayMemberPath);
                Assert.True(mode.IsEnabled);

                // guardrail.mode: an ordinary choice.
                var guardrail = ComboFor(host, "guardrail.mode");
                Assert.Equal("observe", guardrail.SelectedValue);
                Assert.Equal(new[] { "observe", "action" }, guardrail.Items.Cast<ChoiceOption>().Select(o => o.Value));

                // The hook fail mode is a read-only text row with its reason, not a combo.
                Assert.DoesNotContain(VisualTree.Descendants<ComboBox>(host.Content), c => c.DataContext is FormField { Path: "guardrail.hook_fail_mode" });

                var texts = VisibleTexts(host).ToList();
                Assert.Contains("observe=log only; action=block.", texts);
                Assert.Contains("Active agent framework.", texts);
                Assert.Contains(texts, t => t.StartsWith("Warning: already in config.yaml, kept as is: choose one of: codex, claudecode", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("Setup panel", StringComparison.Ordinal));

                // 70000 in a port: the error under the label, the banner, and a Save that is off with the reason.
                var port = vm.Sections.SelectMany(s => s.Fields).Single(f => f.Path == "gateway.api_port");
                port.NumberValue = 70000;
                host.Relayout();
                RenderTo.Png(host, "config-field-errors");

                texts = VisibleTexts(host).ToList();
                Assert.Contains("Error: port must be between 1 and 65535", texts);
                Assert.Contains("Save is off — fix the field errors in the FORM tab", texts);
                Assert.Contains("gateway.api_port: port must be between 1 and 65535", texts);
                Assert.DoesNotContain(texts, t => t.Contains("70000", StringComparison.Ordinal));
                Assert.False(save.IsEnabled);
                Assert.Contains("gateway.api_port: port must be between 1 and 65535", (string)save.ToolTip, StringComparison.Ordinal);

                // Fixed: the line, the banner and the reason go, and Save is on again.
                port.NumberValue = 8080;
                host.Relayout();

                texts = VisibleTexts(host).ToList();
                Assert.DoesNotContain(texts, t => t.StartsWith("Error: ", StringComparison.Ordinal));
                Assert.DoesNotContain(texts, t => t.StartsWith("Save is off", StringComparison.Ordinal));
                Assert.True(save.IsEnabled);
                Assert.Equal("Review and save (Ctrl+S)", save.ToolTip);

                // Picking from the combo is a commit like any other.
                mode.SelectedValue = "codex";
                host.Relayout();
                Assert.Equal(
                    raw.Replace("mode: openclaw", "mode: codex", StringComparison.Ordinal).Replace("api_port: 18970", "api_port: 8080", StringComparison.Ordinal),
                    vm.RawText);
                Assert.DoesNotContain(VisibleTexts(host), t => t.StartsWith("Warning: already in config.yaml", StringComparison.Ordinal));
            }
            finally
            {
                // A dirty editor would ask save / discard / cancel in a modal dialog when it closes; this one is being thrown away.
                typeof(ConfigEditorWindow).GetField("_closeApproved", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                window.Close();
            }
        });
    }
}
