using DefenseClaw.App.ViewModels.SetupResources;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// CUST-270: the three Setup list editors read their list with the exact read-only command (off the UI thread, bounded, every address cut to its
/// host), show a state per row, and tell an empty list from a read that failed. The CLI is the fake (<see cref="FakeSetupCli"/>) over fixtures written from
/// the 0.8.10 source; every credential is a made-up value that starts with <c>synth</c>.
/// </summary>
public sealed class SetupResourceListTests : IDisposable
{
    private readonly SetupEditorHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly string[] Secrets = { "synthuser", "synthpass", "synthkey", "synthtoken", "synthfrag", "synthpath-secret" };

    // ---- the read ----

    [Theory]
    [InlineData(SetupResource.Observability, "setup observability list --json")]
    [InlineData(SetupResource.Webhooks, "setup webhook list --json")]
    [InlineData(SetupResource.TrustedPaths, "setup trusted-paths list --json")]
    public async Task Opening_an_editor_reads_its_list_with_the_one_read_only_command_and_nothing_else(SetupResource resource, string expected)
    {
        var (vm, cli) = await _harness.OpenAsync(resource);

        Assert.Equal(expected.Split(' '), Assert.Single(cli.Ran));
        Assert.Empty(cli.Applied);
        Assert.Equal(SetupListState.Loaded, vm.State);
        Assert.True(vm.ShowList);
        Assert.False(vm.ShowEmpty);
        Assert.False(vm.ShowFailed);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_read_is_bounded_keeps_the_whole_document_and_cuts_every_address_in_what_it_stores()
    {
        var (_, cli) = await _harness.OpenAsync(SetupResource.Webhooks);

        var options = Assert.Single(cli.RanOptions);
        Assert.True(options.RetainFullOutput);
        Assert.NotNull(options.Timeout);
        Assert.True(options.Timeout <= TimeSpan.FromSeconds(60));
        var filter = Assert.IsType<Func<string, string>>(options.OutputLineFilter);
        Assert.Equal("  → https://hooks.example.test", filter("  → https://synthuser:synthpass@hooks.example.test/services/synthpath-secret?x=synthkey"));
    }

    [Fact]
    public async Task The_door_for_reads_refuses_everything_that_is_not_a_read()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks);

        foreach (var argv in new[]
        {
            new[] { "setup", "webhook", "remove", "--yes", "--", "example-slack" },
            new[] { "setup", "webhook", "test", "--", "example-slack" },
            new[] { "setup", "webhook", "list", "--json", "--connector", "claudecode" },
            new[] { "setup", "webhook", "list" },
            new[] { "setup", "observability", "show", "--json", "--", "x" },
        })
        {
            // The door throws before it returns a task: nothing was started.
            _ = Assert.Throws<InvalidOperationException>(() => { _ = vm.RunReadAsync(argv); });
        }

        Assert.Single(cli.Ran);
    }

    // ---- the rows ----

    [Fact]
    public async Task The_destinations_have_a_state_each_and_the_columns_the_tui_shows()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability);

        Assert.Equal(new[] { "Name", "Kind", "State", "Signals", "Redaction", "Endpoint" }, vm.Columns.Select(c => c.Header));
        Assert.Equal(new[] { "local-sqlite", "example-otlp", "example-hec", "example-jsonl", "example-local-stack" }, vm.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "enabled", "enabled", "disabled", "enabled", "unsupported" }, vm.Rows.Select(r => r.StateText));
        Assert.Equal(new[] { "Ok", "Ok", "Neutral", "Ok", "Warn" }, vm.Rows.Select(r => r.StateTone));

        var otlp = vm.Rows.Single(r => r.Key == "example-otlp");
        Assert.Equal("otlp", otlp.Cells["Kind"]);
        Assert.Equal("logs, traces", otlp.Cells["Signals"]);
        Assert.Equal("redacted: strict", otlp.Cells["Redaction"]);
        Assert.Equal("collector.example.test:4318", otlp.Cells["Endpoint"]);
        Assert.Equal("5 destinations", vm.Caption);
    }

    [Fact]
    public async Task The_webhooks_have_a_state_each_and_the_columns_the_tui_shows()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Webhooks);

        Assert.Equal(new[] { "Name", "Type", "State", "Min severity", "Events", "Endpoint" }, vm.Columns.Select(c => c.Header));
        Assert.Equal(new[] { "enabled", "disabled", "enabled", "enabled" }, vm.Rows.Select(r => r.StateText));
        var pager = vm.Rows.Single(r => r.Key == "example-pagerduty");
        Assert.Equal("pagerduty", pager.Cells["Type"]);
        Assert.Equal("CRITICAL", pager.Cells["Severity"]);
        Assert.Equal("block, scan", pager.Cells["Events"]);
        Assert.Equal("events.example.test", pager.Cells["Endpoint"]);
        Assert.Equal("4 webhooks", vm.Caption);
    }

    [Fact]
    public async Task The_trusted_folders_have_the_tuis_columns_source_status_owned_and_path()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.TrustedPaths);

        Assert.Equal(new[] { "Source", "Status", "Owned", "Path" }, vm.Columns.Select(c => c.Header));
        Assert.Equal(new[] { "default", "default", "config", "legacy .env", "config", "env" }, vm.Rows.Select(r => r.Cells["Source"]));
        Assert.Equal(new[] { "ok", "ok", "ok", "missing", "unsafe-permissions", "ok" }, vm.Rows.Select(r => r.StateText));
        Assert.Equal(new[] { "-", "-", "yes", "yes", "yes", "-" }, vm.Rows.Select(r => r.Cells["Owned"]));
        Assert.Equal(@"C:\Users\synthetic\.synthetic-tools\bin", vm.Rows[2].Cells["Path"]);
        Assert.Equal(new[] { "Ok", "Ok", "Ok", "Warn", "Bad", "Ok" }, vm.Rows.Select(r => r.StateTone));
        Assert.Equal("6 trusted folders", vm.Caption);
    }

    [Fact]
    public async Task A_row_selected_before_a_refresh_is_still_selected_after_it()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "example-otlp");

        await vm.ReadAsync();

        Assert.Equal("example-otlp", vm.SelectedRow?.Key);
        Assert.True(vm.HasSelection);
        Assert.Contains(vm.DetailFacts, f => f.Label == "Redaction" && f.Value == "redacted: strict");
    }

    // ---- an empty list and a failed read look different ----

    [Theory]
    [InlineData(SetupResource.Observability)]
    [InlineData(SetupResource.Webhooks)]
    [InlineData(SetupResource.TrustedPaths)]
    public async Task A_list_that_printed_an_empty_array_is_empty_and_says_so(SetupResource resource)
    {
        var (vm, _) = await _harness.OpenAsync(resource, c => { c.Observability = "[]"; c.Webhooks = "[]"; c.TrustedPaths = "[]"; });

        Assert.Equal(SetupListState.Empty, vm.State);
        Assert.True(vm.ShowEmpty);
        Assert.False(vm.ShowFailed);
        Assert.False(vm.ShowList);
        Assert.Empty(vm.Rows);
        Assert.Equal(string.Empty, vm.ErrorTitle);
        Assert.False(string.IsNullOrWhiteSpace(vm.EmptyTitle));
        Assert.False(string.IsNullOrWhiteSpace(vm.EmptyDetail));
    }

    [Theory]
    [InlineData(SetupResource.Observability)]
    [InlineData(SetupResource.Webhooks)]
    [InlineData(SetupResource.TrustedPaths)]
    public async Task A_read_that_exited_non_zero_is_failed_and_is_never_shown_as_empty(SetupResource resource)
    {
        var (vm, _) = await _harness.OpenAsync(resource, c => c.ReadExitCode = 1);

        Assert.Equal(SetupListState.Failed, vm.State);
        Assert.True(vm.ShowFailed);
        Assert.False(vm.ShowEmpty);
        Assert.False(vm.ShowList);
        Assert.Empty(vm.Rows);
        Assert.Equal(vm.FailedTitle, vm.ErrorTitle);
        Assert.Contains("exited 1", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("could not load config.yaml", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("empty", vm.FailedDetail, StringComparison.Ordinal);
        Assert.NotEqual(vm.EmptyTitle, vm.ErrorTitle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not json")]
    [InlineData("{\"rows\": []}")]
    public async Task Output_that_is_not_the_list_is_a_failed_read_not_an_empty_one(string printed)
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Webhooks, c => c.Webhooks = printed);

        Assert.Equal(SetupListState.Failed, vm.State);
        Assert.False(vm.ShowEmpty);
        Assert.StartsWith("Unexpected output from defenseclaw setup webhook list --json", vm.ErrorTitle, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public async Task A_missing_cli_is_its_own_state()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability, c => c.CliMissing = true);

        Assert.Equal(SetupListState.CliUnavailable, vm.State);
        Assert.True(vm.ShowFailed);
        Assert.Equal("The defenseclaw CLI was not found", vm.ErrorTitle);
    }

    [Fact]
    public async Task The_trusted_folder_editor_names_the_allow_list_when_it_could_not_read_it()
    {
        var (failed, _) = await _harness.OpenAsync(SetupResource.TrustedPaths, c => c.ReadExitCode = 1);
        var (empty, _) = await _harness.OpenAsync(SetupResource.TrustedPaths, c => c.TrustedPaths = "[]");

        Assert.Equal("Could not read the trusted-path allow-list", failed.ErrorTitle);
        Assert.Contains("unknown, not empty", failed.FailedDetail, StringComparison.Ordinal);
        Assert.Equal("The allow-list is empty", empty.EmptyTitle);
        Assert.NotEqual(failed.ErrorTitle, empty.EmptyTitle);
        Assert.False(failed.ShowEmpty);
        Assert.False(empty.ShowFailed);
    }

    [Fact]
    public async Task A_refresh_that_fails_keeps_the_rows_labels_them_and_turns_every_change_off()
    {
        var (vm, cli) = await _harness.OpenAsync(SetupResource.Observability);
        SetupEditorHarness.Select(vm, "example-hec");
        Assert.True(vm.CanEnable);

        cli.ReadExitCode = 1;
        await vm.ReadAsync();

        Assert.Equal(SetupListState.Loaded, vm.State); // the rows stay
        Assert.Equal(5, vm.Rows.Count);
        Assert.True(vm.HasRefreshWarning);
        Assert.Contains("Showing the last good read", vm.RefreshWarning, StringComparison.Ordinal);
        Assert.False(vm.CanEnable);
        Assert.False(vm.CanRemove);
        Assert.Contains("last read failed", vm.EnableTip, StringComparison.Ordinal);

        cli.ReadExitCode = 0;
        await vm.ReadAsync();

        Assert.False(vm.HasRefreshWarning);
        Assert.True(vm.CanEnable);
    }

    // ---- no endpoint secret anywhere ----

    private static string EverythingShown(SetupResourceViewModel vm) => string.Join(
        "\n",
        vm.Rows.SelectMany(r => r.Cells.Values.Append(r.Title).Append(r.AutomationName).Append(r.StateText).Concat(r.Facts.SelectMany(f => new[] { f.Label, f.Value })))
            .Concat(vm.DetailFacts.SelectMany(f => new[] { f.Label, f.Value }))
            .Append(vm.Caption).Append(vm.ErrorTitle).Append(vm.ErrorMessage).Append(vm.RefreshWarning).Append(vm.DetailRaw).Append(vm.DetailCommand)
            .Append(vm.NoticeMessage));

    [Fact]
    public async Task A_destination_address_with_credentials_shows_its_host_only()
    {
        const string list = """
            [
              {"name": "example-otlp", "kind": "otlp", "enabled": true, "generated": false, "signals": ["logs"], "capabilities": ["logs"],
               "policy": "concise", "bucket_count": 1, "redaction": "redacted: strict",
               "target": "https://synthuser:synthpass@collector.example.test:4318/v1/synthpath-secret?api_key=synthkey#synthfrag",
               "platform_status": "supported"}
            ]
            """;

        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability, c => c.Observability = list);
        SetupEditorHarness.Select(vm, "example-otlp");

        var shown = EverythingShown(vm);
        Assert.Contains("collector.example.test:4318", shown, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.Ordinal));
        Assert.DoesNotContain("api_key", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_webhook_address_a_cli_failed_to_cut_shows_its_host_only_in_the_row_the_details_and_the_show_output()
    {
        const string leaky = """
            {"name": "example-slack", "type": "slack", "enabled": true,
             "url": "https://synthuser:synthpass@hooks.example.test/services/synthpath-secret?token=synthtoken#synthfrag",
             "secret_env": "", "room_id": "", "min_severity": "HIGH", "events": [], "timeout_seconds": 10, "cooldown_seconds": null}
            """;

        var (vm, cli) = await _harness.OpenAsync(SetupResource.Webhooks, c =>
        {
            c.Webhooks = "[" + leaky + "]";
            c.WebhookShow = leaky;
        });
        SetupEditorHarness.Select(vm, "example-slack");
        await vm.ShowCommand.ExecuteAsync(null);

        Assert.Equal("setup webhook show --json -- example-slack".Split(' '), cli.Ran.Last());
        Assert.Equal("defenseclaw setup webhook show --json -- example-slack", vm.DetailCommand);
        var shown = EverythingShown(vm);
        Assert.Contains("hooks.example.test", shown, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, shown, StringComparison.Ordinal));
        Assert.False(vm.HasDetailError, vm.DetailError);
    }

    [Fact]
    public async Task The_address_a_failed_read_quotes_is_cut_too()
    {
        var (vm, _) = await _harness.OpenAsync(SetupResource.Observability, c =>
        {
            c.ReadExitCode = 1;
            c.ReadError = "Error: dial https://synthuser:synthpass@collector.example.test:4318/v1/synthpath-secret?api_key=synthkey failed";
        });

        Assert.Equal(SetupListState.Failed, vm.State);
        Assert.Contains("https://collector.example.test:4318", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.All(Secrets, secret => Assert.DoesNotContain(secret, EverythingShown(vm), StringComparison.Ordinal));
    }
}
