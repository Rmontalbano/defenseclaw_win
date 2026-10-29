using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.App.Views.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-391 in the editor: the form laid out in the runtime's own catalog (a synthetic 1.0.4 dump; no interpreter starts), the label beside Reload, the
/// "Other (uncatalogued)" card, the fields the file sets still being the form's own (they edit RAW, validate and mask as before), a setting the file
/// does not set shown read-only with the runtime's value, no password value anywhere, the quiet fallback to the built-in layout, and one session, one
/// draft and one read for every view. The renders are fixture renders (DC_RENDER_DIR).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConfigEditorRuntimeCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Task<ConfigEditorHarness> WithCatalog(RuntimePythonRunner? runner = null) =>
        ConfigEditorHarness.LoadAsync(
            runtimeProbeRunner: RuntimeCatalogFixtures.Version104,
            catalogRunner: runner ?? RuntimeCatalogFixtures.Answering(RuntimeCatalogFixtures.Dump104()).Runner);

    private static FormSection Section(ConfigEditorWindowViewModel vm, string name) => vm.Sections.Single(s => s.DisplayName == name);

    private static IEnumerable<FormField> AllFields(IEnumerable<FormGroup> groups)
    {
        foreach (var group in groups)
        {
            foreach (var field in group.Fields)
            {
                yield return field;
            }

            foreach (var field in AllFields(group.SubGroups))
            {
                yield return field;
            }
        }
    }

    [Fact]
    public async Task The_form_is_the_runtimes_sections_in_its_order_then_other_and_the_label_says_so()
    {
        using var harness = await WithCatalog();
        var vm = harness.ViewModel;

        Assert.True(vm.UsesRuntimeCatalog);
        Assert.Equal(RuntimeCatalogFixtures.SectionNames.Append(RuntimeConfigCatalog.OtherSectionName), vm.Sections.Select(s => s.DisplayName));
        Assert.Equal("runtime catalog 1.0.4 · 24 sections", vm.CatalogLabel);
        Assert.Contains("runtime's own Setup catalog", vm.CatalogToolTip, StringComparison.Ordinal);

        // The catalog's summary and help line go with its card; separators are group cards in the catalog's order.
        var general = Section(vm, "General");
        Assert.Equal("Global paths, environment label, and the shared LLM key fallback.", general.Summary);
        Assert.True(general.HasHelp);
        Assert.Equal(["Paths", "Unified LLM (for scanners + guardrail)"], general.SubGroups.Select(g => g.DisplayName));
        Assert.Equal(["Provider", "Model", "API Key Env", "API Key (redacted)", "Base URL", "Timeout (s)"], general.SubGroups[1].Fields.Select(f => f.DisplayName));

        // A row config.yaml sets is the form's own field, under the runtime's label; one it does not set shows the runtime's value, read-only.
        var gateway = Section(vm, "Gateway").Fields;
        Assert.Equal(["Host", "Port", "API Port", "TLS", "Token Env", "Token (redacted)"], gateway.Select(f => f.DisplayName));
        Assert.Same(harness.Field("gateway.host"), gateway[0]);
        Assert.True(gateway[0].IsEditable);
        Assert.False(gateway[1].IsEditable);
        Assert.Equal(18789, gateway[1].NumberValue);
        Assert.Equal(RuntimeCatalogLayout.NotInFileReason, gateway[1].DisabledReason);
        Assert.Equal(FormFieldKind.Bool, gateway[3].Kind);
        Assert.Equal(FormFieldKind.EnvName, gateway[4].Kind);

        // Summary rows are information lines; the runtime's read-only rows stay read-only.
        var webhook = Section(vm, "Webhooks").Fields[0];
        Assert.Equal(FormFieldKind.Info, webhook.Kind);
        Assert.Equal("[enabled] https://hooks.example.test/[REDACTED]", webhook.TextValue);
        Assert.False(webhook.IsEditable);
        Assert.Equal("Set by defenseclaw setup claude-code", Section(vm, "Agent Hooks").Fields.Single().Hint);
        Assert.False(harness.Field("config_version").IsEditable);

        // The 1.0.x scanner rows come from the runtime with its choices.
        var blockAt = harness.Field("scanners.skill_scanner.fail_on_severity");
        Assert.Equal("Block At", blockAt.DisplayName);
        Assert.Equal(["", "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO"], blockAt.ChoiceValues);
    }

    [Fact]
    public async Task Keys_the_catalog_does_not_describe_are_under_other_grouped_by_their_top_level_key_and_keep_the_forms_rules()
    {
        using var harness = await WithCatalog();
        var other = Section(harness.ViewModel, RuntimeConfigCatalog.OtherSectionName);

        Assert.Equal(RuntimeCatalogLayout.OtherSummary, other.Summary);
        Assert.Equal(["Guardrail", "Llm"], other.SubGroups.Select(g => g.DisplayName));
        var guardrail = other.SubGroups[0];
        Assert.Equal(["guardrail.allowed_hosts", "guardrail.api_keys", "guardrail.scan_roots"], guardrail.Lists.Select(l => l.Path));
        Assert.False(guardrail.Lists.Single(l => l.Path == "guardrail.api_keys").IsEditable);
        var llm = AllFields(new[] { other.SubGroups[1] }).Select(f => f.Path).ToList();
        Assert.Contains("llm.max_tokens", llm);
        Assert.Contains("llm.endpoint", llm);
        Assert.DoesNotContain("llm.model", llm);

        // Nothing is shown twice, and nothing the file sets is lost.
        var everyPath = AllFields(harness.ViewModel.Sections).Select(f => f.Path).Where(p => p.Length > 0).ToList();
        Assert.Equal(everyPath.Count, everyPath.Distinct(StringComparer.Ordinal).Count());
        foreach (var path in new[] { "gateway.host", "gateway.api_port", "gateway.token", "gateway.token_env", "llm.provider", "llm.model", "llm.api_key_env", "llm.temperature", "guardrail.mode", "guardrail.enabled", "config_version" })
        {
            Assert.Contains(path, everyPath);
        }

        // An uncatalogued key the form can write is still written: this editor writes config.yaml's text (1.0.4's own save keeps such a key).
        var maxTokens = harness.Field("llm.max_tokens");
        Assert.True(maxTokens.IsEditable);
        maxTokens.NumberValue = 2048;
        Assert.Contains("max_tokens: 2048", harness.ViewModel.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_catalog_driven_field_edits_raw_validates_and_a_not_set_one_never_commits()
    {
        using var harness = await WithCatalog();
        var vm = harness.ViewModel;

        harness.Field("gateway.host").TextValue = "127.0.0.2";
        Assert.Contains("host: 127.0.0.2", vm.RawText, StringComparison.Ordinal);
        Assert.True(vm.IsRawModified);

        var apiPort = harness.Field("gateway.api_port");
        apiPort.NumberValue = 70000;
        Assert.True(apiPort.Validation.IsError);
        Assert.True(vm.HasBlockingFieldErrors);
        Assert.False(vm.ReviewAndSaveCommand.CanExecute(null));
        apiPort.NumberValue = 18970;
        Assert.False(vm.HasBlockingFieldErrors);

        var before = vm.RawText;
        var port = harness.Field("gateway.port");
        port.NumberValue = 1234;
        Assert.Equal(before, vm.RawText);
        Assert.False(port.IsDirty);

        // The pre-save review is the usual one, over the same buffer.
        await vm.ReviewAndSaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsReviewing);
        Assert.Contains(vm.Review!.Lines, line => line.Text.Contains("127.0.0.2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_password_value_reaches_any_field_and_secrets_show_only_set_or_not_set()
    {
        using var harness = await WithCatalog();
        var fields = AllFields(harness.ViewModel.Sections).ToList();

        foreach (var field in fields)
        {
            foreach (var text in new[] { field.TextValue, field.OriginalValue?.ToString(), field.DisplayName, field.Hint, field.ToolTipText, field.MaskedText, field.ToString() })
            {
                Assert.DoesNotContain(RuntimeCatalogFixtures.LeakedSecret, text ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain("sample-not-a-real-token", text ?? string.Empty, StringComparison.Ordinal);
                Assert.DoesNotContain(RuntimeCatalogFixtures.WebhookSecretPath, text ?? string.Empty, StringComparison.Ordinal);
            }
        }

        var token = harness.Field("gateway.token");
        Assert.Equal(FormFieldKind.Secret, token.Kind);
        Assert.False(token.IsEditable);
        Assert.NotEqual("(not set)", token.MaskedText);

        var apiKey = harness.Field("llm.api_key");
        Assert.Equal(FormFieldKind.Secret, apiKey.Kind);
        Assert.Equal("(not set)", apiKey.MaskedText);
        Assert.Equal(RuntimeCatalogLayout.NotInFileSecretReason, apiKey.DisabledReason);
    }

    [Fact]
    public async Task A_dump_that_times_out_keeps_the_built_in_layout_with_one_quiet_line_and_no_banner()
    {
        using var builtIn = await ConfigEditorHarness.LoadAsync();
        using var harness = await WithCatalog(RuntimeCatalogFixtures.Ending(RuntimePythonOutput.Fail("timed out after 20 s")));
        var vm = harness.ViewModel;

        Assert.False(vm.UsesRuntimeCatalog);
        Assert.Equal(builtIn.ViewModel.Sections.Select(s => s.DisplayName), vm.Sections.Select(s => s.DisplayName));
        Assert.DoesNotContain(vm.Sections, s => s.DisplayName == RuntimeConfigCatalog.OtherSectionName);
        Assert.Equal("built-in catalog · " + vm.Sections.Count + " sections", vm.CatalogLabel);
        Assert.Contains("timed out after 20 s", vm.CatalogToolTip, StringComparison.Ordinal);
        Assert.Null(vm.FormUnavailableReason);
        Assert.Null(vm.LoadError);
        Assert.False(vm.ShowFormEmptyState);
        Assert.Null(vm.FieldErrorMessage);
        Assert.True(harness.Field("gateway.host").IsEditable);

        // A composition whose CLI is faked never starts an interpreter of its own either.
        Assert.False(builtIn.ViewModel.UsesRuntimeCatalog);
        Assert.Null(builtIn.ViewModel.CatalogLoader);
        Assert.StartsWith("built-in catalog · ", builtIn.ViewModel.CatalogLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_on_disk_reads_the_catalog_again_and_the_layout_stays_the_runtimes()
    {
        var (runner, launches) = RuntimeCatalogFixtures.Answering(RuntimeCatalogFixtures.Dump104());
        using var harness = await WithCatalog(runner);
        var vm = harness.ViewModel;
        Assert.Single(launches);

        harness.WriteConfigExternally(harness.Raw.Replace("host: 127.0.0.1", "host: 127.0.0.3", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(harness.ConfigPath, DateTime.UtcNow.AddMinutes(1));
        await vm.HandleDiskChangeAsync();

        Assert.Equal(2, launches.Count);
        Assert.True(vm.UsesRuntimeCatalog);
        Assert.Contains("host: 127.0.0.3", vm.RawText, StringComparison.Ordinal);
        Assert.False(vm.ShowExternalChangeNotice);
    }

    // ------------------------------------------------------------------ one session, one read

    private AppServices Services()
    {
        RuntimeCatalogFixtures.PlantCliAndPython(_temp.Path);
        var services = TestServices.Create(_temp, ConfigSamples.Raw, RuntimeCatalogFixtures.Version104);
        services.ConfigWatcher.Dispose();
        _ = services.Runtime.RefreshAsync().GetAwaiter().GetResult();
        return services;
    }

    private static ConfigEditorSession Loaded(AppServices services, RuntimePythonRunner runner)
    {
        var session = ConfigEditorSession.For(services);
        session.Editor.FormSourceOverride = _ => Task.FromResult(ConfigSamples.MaskedSource);
        session.Editor.CatalogRunnerOverride = runner;
        _ = UiThread.Run(() => session.EnsureLoadedAsync());
        UiThread.WaitFor(() => session.Editor.HasLoadStarted && !session.Editor.IsLoading, "the draft loaded");
        return session;
    }

    private static ConfigEditorWindowViewModel ViewModelOf(ConfigEditorWindow window) =>
        (ConfigEditorWindowViewModel)typeof(ConfigEditorWindow).GetField("_viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    [Fact]
    public void Every_view_is_the_one_session_and_the_catalog_is_read_once_until_reload_asks_again()
    {
        using var services = Services();
        var (runner, launches) = RuntimeCatalogFixtures.Answering(RuntimeCatalogFixtures.Dump104());
        var session = Loaded(services, runner);

        _ = UiThread.Run(() => session.EnsureLoadedAsync());
        var window = UiThread.Run(() => new ConfigEditorWindow(services));
        try
        {
            Assert.Same(session.Editor, UiThread.Run(() => ViewModelOf(window)));
            Assert.Same(session, ConfigEditorSession.For(services));
            Assert.Single(launches);
            Assert.True(session.Editor.UsesRuntimeCatalog);

            // Reload is the operator asking for a fresh look: the catalog is read again even though nothing it depends on changed.
            _ = UiThread.Run(() => session.Editor.ReloadCommand.ExecuteAsync(null));
            UiThread.WaitFor(() => !session.Editor.IsLoading && launches.Count == 2, "the reload read the catalog again");
            Assert.True(session.Editor.UsesRuntimeCatalog);
        }
        finally
        {
            UiThread.Run(window.Close);
        }
    }

    // ------------------------------------------------------------------ fixture renders

    public static IEnumerable<object[]> Renders() => new[]
    {
        new object[] { 980, 692, "Light" },
        new object[] { 1468, 875, "Light" },
        new object[] { 1468, 875, "Dark" },
        new object[] { 980, 692, "Dark" },
    };

    [Theory]
    [MemberData(nameof(Renders))]
    public void The_editor_shows_the_catalog_label_the_runtimes_sections_and_other(int width, int height, string mode)
    {
        using var services = Services();
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));
        var session = Loaded(services, RuntimeCatalogFixtures.Answering(RuntimeCatalogFixtures.Dump104()).Runner);

        var (window, view, host) = UiThread.Run(() =>
        {
            var w = new ConfigEditorWindow(services);
            var content = (FrameworkElement)w.Content;
            w.Content = null;
            content.DataContext = ViewModelOf(w);
            return (w, VisualTree.Find<ConfigEditorView>(content)!, new OffscreenHost(content, width, height));
        });
        try
        {
            UiThread.Run(() =>
            {
                host.Relayout();
                RenderTo.Png(host, $"cust391-editor-{width}x{height}-{mode}-top");

                var label = VisualTree.Find<TextBlock>(view, t => t.Text == session.Editor.CatalogLabel)!;
                Assert.True(label.IsVisible);
                Assert.Equal("runtime catalog 1.0.4 · 24 sections", label.Text);
                var list = VisualTree.Find<ListBox>(view, l => l.Name == "SectionList")!;
                Assert.Equal(25, list.Items.Count);

                // The summary line of the first card is on screen, and nothing is clipped off its right edge.
                var summary = VisualTree.Find<TextBlock>(view, t => t.IsVisible && t.Text == "Global paths, environment label, and the shared LLM key fallback.")!;
                Assert.True(summary.ActualWidth <= view.ActualWidth);

                // Other, picked in the list, comes to the top of the form.
                list.SelectedIndex = 24;
                UiThread.Settle();
                host.Relayout();
                RenderTo.Png(host, $"cust391-editor-{width}x{height}-{mode}-other");
                Assert.Contains(
                    VisualTree.Descendants<TextBlock>(view),
                    t => t.IsVisible && t.Text == RuntimeCatalogLayout.OtherSummary);

                // And the Webhooks summary row with its masked address.
                list.SelectedIndex = 14;
                UiThread.Settle();
                host.Relayout();
                RenderTo.Png(host, $"cust391-editor-{width}x{height}-{mode}-webhooks");
            });
        }
        finally
        {
            UiThread.Run(() =>
            {
                host.Dispose();
                window.Close();
            });
        }
    }
}
