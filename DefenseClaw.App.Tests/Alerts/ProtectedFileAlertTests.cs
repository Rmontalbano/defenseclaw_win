using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;
using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Gateway;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Install;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Alerts;

/// <summary>
/// The protected-file alert of DefenseClaw 1.0.6 in the app (CUST-422), from synthetic rows in the shape <c>emitProtectedFileChange</c> writes (no 1.0.6 has been run):
/// how the row reaches the queue, that it is one row when both sources hold it, that the badge, the page and the tray say one number, the row's first-class
/// title and inspector, the Kind filter, the Overview row and its deep link, and that nothing opens the watched file. Renders when <c>DC_RENDER_DIR</c> is set.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ProtectedFileAlertTests : IDisposable
{
    private const string File1 = @"C:\Users\operator\.ssh\authorized_keys";
    private const string File2 = @"C:\Users\operator\.ssh\authorized_keys2";

    private const string Details =
        "rule=persistence.protected_file_changed class=authorized_keys before=sha256:0123456789ab after=sha256:ba9876543210 " +
        "connector=codex session=ses-1 tool_invocation=call-7";

    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow.AddHours(-1);

    private readonly TempDirectory _temp = new();
    private AppServices? _services;
    private AlertQueueDatabase? _database;

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    private async Task<AppServices> CreateAsync(string version)
    {
        _services = TestServices.Create(_temp, runtimeProbeRunner: FirstRun.FirstRunFailModeTests.Versioned(version));
        _ = await _services.Runtime.RefreshAsync();
        _database = new AlertQueueDatabase(_services.Paths.AuditDatabasePath);
        return _services;
    }

    /// <summary>The row as 1.0.6's gateway files it: <c>security.finding</c> under its <c>legacy.audit.*</c> name, not <c>finding.observed</c>.</summary>
    private static void AddChange(string database, string id, int minutes, string target = File1, string? details = Details, string severity = "HIGH", string? connector = "codex", string? payload = null)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, timestamp, action, target, actor, details, severity, bucket, connector, event_name, session_id, payload_json)
            VALUES ($id, $ts, 'protected-file-changed', $target, 'defenseclaw', $details, $severity, 'security.finding', $connector, 'legacy.audit.protected.file.changed', 'ses-1', $payload)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$ts", AlertQueueDatabase.Format(Base.AddMinutes(minutes)));
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.Parameters.AddWithValue("$severity", severity);
        command.Parameters.AddWithValue("$connector", (object?)connector ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", (object?)payload ?? DBNull.Value);
        _ = command.ExecuteNonQuery();
    }

    private static RuntimeAlertRow RuntimeRow(string id, int minutes, string target = File1) =>
        new(id, Base.AddMinutes(minutes), "HIGH", AuditSeverity.High, ProtectedFileAlert.Action, target, "defenseclaw", "codex", Details,
            new[] { new RuntimeAlertFact("session_id", "ses-1"), new RuntimeAlertFact("agent_identity_id", "agt-00000000000000aa") });

    // ------------------------------------------------------------------ the queue

    private AlertQueueReader Reader(bool on) =>
        new(_database!.Path, excludeDetectionOnly: () => true, includeProtectedFileChanges: () => on);

    [Fact]
    public async Task The_row_is_in_neither_ordinary_arm_and_the_capability_adds_it()
    {
        _ = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1);
        _database.AddFinding("f1", Base, "MEDIUM");

        var off = await Reader(on: false).ReadAsync();
        Assert.Equal(["f1"], off.Counts.Newest.Select(i => i.Id));
        Assert.Empty(off.Counts.ProtectedFileChanges);

        var on = await Reader(on: true).ReadAsync();
        Assert.Equal(["p1", "f1"], on.Counts.Newest.Select(i => i.Id));
        Assert.Equal(1, on.Counts.High);
        Assert.Equal(1, on.Counts.Medium);
        Assert.Equal(File1, Assert.Single(on.Counts.ProtectedFileChanges).Target);
    }

    [Fact]
    public async Task An_acknowledged_change_leaves_the_queue_and_a_row_below_the_alert_severities_never_enters()
    {
        _ = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1);
        AddChange(_database.Path, "p2", 2);
        AddChange(_database.Path, "info", 3, severity: "INFO");
        _database.Acknowledge("p2");

        var result = await Reader(on: true).ReadAsync();

        Assert.Equal(["p1"], result.Counts.ProtectedFileChanges.Select(i => i.Id));
        Assert.Equal(1, result.Counts.Total);
    }

    [Fact]
    public async Task It_is_not_dropped_as_detection_only_but_the_shared_filter_still_applies_to_its_arm()
    {
        _ = await CreateAsync("1.0.6");

        // protectedFileChangeDetails writes no finding tags. A payload that does say detection-only follows the same rule as every other arm.
        AddChange(_database!.Path, "plain", 1, payload: "{\"defenseclaw.finding.rule_id\":\"persistence.protected_file_changed\"}");
        AddChange(_database.Path, "tagged", 2, payload: "{\"defenseclaw.finding.tags\":[\"detection-only\"]}");

        var result = await Reader(on: true).ReadAsync();

        Assert.Equal(["plain"], result.Counts.ProtectedFileChanges.Select(i => i.Id));
    }

    [Fact]
    public async Task The_arm_is_searched_by_its_action_index_and_the_ordinary_plan_is_unchanged_without_the_capability()
    {
        _ = await CreateAsync("1.0.6");

        var off = await Reader(on: false).ExplainAsync();
        Assert.DoesNotContain(off, line => line.Contains("idx_audit_action", StringComparison.Ordinal));

        var on = await Reader(on: true).ExplainAsync();
        Assert.Contains(on, line => line.Contains("idx_audit_action", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_both_sources_hold_is_one_row_and_one_count()
    {
        _ = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1);
        var queue = (await Reader(on: true).ReadAsync(AlertQueueReader.DefaultWindowLimit)).Counts.Newest;

        // In both; and the runtime's list alone (a capability that has not answered yet, or a queue read made first).
        var both = RuntimeAlertMerge.Merge(queue, new[] { RuntimeRow("p1", 1) });
        Assert.Same(queue, both);
        Assert.Equal(1, new AlertCounts(both, hasMore: false).High);

        var runtimeOnly = RuntimeAlertMerge.Merge(Array.Empty<AlertQueueItem>(), new[] { RuntimeRow("p9", 1) });
        var counted = new AlertCounts(runtimeOnly, hasMore: false);
        Assert.Equal(1, counted.High);
        Assert.Single(counted.ProtectedFileChanges);
    }

    // ------------------------------------------------------------------ one number everywhere

    [Fact]
    public async Task The_badge_the_page_tiles_and_the_tray_say_the_same_number_and_acknowledging_drops_them_all()
    {
        var services = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1);
        AddChange(_database.Path, "p2", 2, target: File2);
        _database.AddFinding("f1", Base, "HIGH");

        await services.AlertCounts.RefreshAsync();
        var counts = services.AlertCounts.Current;
        Assert.Equal(3, counts.Total);
        Assert.Equal("3", AlertCountPresentation.Badge(counts));

        var (page, tray) = UiThread.Run(() =>
        {
            var vm = new AlertsPanelViewModel(services);
            vm.SetActive(true);
            var flyout = new TrayFlyoutViewModel(services, () => { }, () => { });
            flyout.SetVisible(true);
            return (vm, flyout);
        });
        try
        {
            UiThread.WaitFor(() => page.Alerts.Count == 3, "the page's rows");
            UiThread.Run(() =>
            {
                Assert.Equal(3, page.Tiles.Single(t => t.Severity == "HIGH").Count);
                Assert.Equal(2, page.Alerts.Count(a => a.IsProtectedFileChange));
            });
            UiThread.WaitFor(() => tray.Metrics[2].Value == "3", "the tray's number");

            _database.Acknowledge("p1");
            await services.AlertCounts.RefreshAsync();
            Assert.Equal("2", AlertCountPresentation.Badge(services.AlertCounts.Current));
            Assert.Equal(["p2"], services.AlertCounts.Current.ProtectedFileChanges.Select(i => i.Id));
            UiThread.WaitFor(() => page.Alerts.Count == 2, "the page to follow");
            UiThread.WaitFor(() => tray.Metrics[2].Value == "2", "the tray to follow");
        }
        finally
        {
            UiThread.Run(() =>
            {
                page.SetActive(false);
                tray.Dispose();
            });
        }
    }

    // ------------------------------------------------------------------ the row

    private static AuditEvent Audited(string id = "p1", string? details = Details) => new()
    {
        Id = id,
        Timestamp = Base,
        RawTimestamp = AlertQueueDatabase.Format(Base),
        Action = ProtectedFileAlert.Action,
        Target = File1,
        Actor = "defenseclaw",
        Details = details,
        Severity = "HIGH",
        Connector = "codex",
        SessionId = "ses-1",
    };

    private static Dictionary<string, string> Facts(AlertItem item) =>
        item.ProtectedFileFacts.ToDictionary(f => f.Name, f => f.Value);

    [Fact]
    public void From_the_audit_row_it_has_a_readable_title_the_rule_and_every_fact()
    {
        var item = AlertItem.FromAudit(Audited());

        Assert.True(item.IsProtectedFileChange);
        Assert.Equal(ProtectedFileAlert.RuleId, item.RuleId);
        Assert.StartsWith("Protected file changed: ", item.Headline, StringComparison.Ordinal);
        Assert.Contains("authorized_keys", item.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("rule=", item.Headline, StringComparison.Ordinal);
        Assert.Equal("High", item.SeverityKey);
        Assert.Equal(AlertKinds.ProtectedFiles, item.Kind);
        Assert.False(item.IsBlock);

        var facts = Facts(item);
        Assert.Equal(File1, facts["File"]);
        Assert.Equal("codex", facts["Agent"]);
        Assert.Equal("ses-1", facts["Session"]);
        Assert.Equal("call-7", facts["Tool call"]);
        Assert.Equal("0123456789ab", facts["SHA-256 before"]);
        Assert.Equal("ba9876543210", facts["SHA-256 after"]);
        Assert.False(facts.ContainsKey("Restore"));
        Assert.Contains("Attribution is by time", item.ProtectedFileNote, StringComparison.Ordinal);
        Assert.Contains("blocks nothing", item.ProtectedFileNote, StringComparison.Ordinal);
    }

    [Fact]
    public void From_the_runtime_list_alone_it_reads_the_same_and_its_facts_fill_the_gaps()
    {
        var item = AlertItem.FromRuntime(RuntimeRow("p1", 0));

        Assert.True(item.IsProtectedFileChange);
        Assert.Equal(ProtectedFileAlert.RuleId, item.RuleId);
        Assert.StartsWith("Protected file changed: ", item.Headline, StringComparison.Ordinal);
        Assert.Equal(AlertKinds.ProtectedFiles, item.Kind);
        Assert.Equal("agt-00000000000000aa", Facts(item)["Agent identity"]);
    }

    [Fact]
    public void With_the_runtime_facts_added_later_and_as_a_group_copy_it_stays_first_class()
    {
        var item = AlertItem.FromAudit(Audited()).WithRuntime(RuntimeRow("p1", 0));

        Assert.StartsWith("Protected file changed: ", item.Headline, StringComparison.Ordinal);
        Assert.Equal("agt-00000000000000aa", Facts(item)["Agent identity"]);
        Assert.Contains(item.Fields, f => f.Name == "runtime.session_id");
        Assert.True(item.CloneForGroup().IsProtectedFileChange);
    }

    [Fact]
    public void A_row_of_only_what_the_queue_knows_invents_nothing()
    {
        var item = AlertItem.FromQueue(new AlertQueueItem("p1", AuditSeverity.High, ProtectedFileAlert.Action, File1, "codex", Base));

        var facts = Facts(item);
        Assert.Equal(File1, facts["File"]);
        Assert.Equal("not recorded", facts["SHA-256 before"]);
        Assert.False(facts.ContainsKey("Session"));
        Assert.False(facts.ContainsKey("Tool call"));
    }

    [Theory]
    [InlineData("restored=true", "The last accepted content was put back.")]
    [InlineData("restored=false restore_error=\"no saved copy\"", "Failed: no saved copy")]
    public void What_restore_did_is_a_fact_of_the_row(string more, string expected)
    {
        var item = AlertItem.FromAudit(Audited(details: Details + " " + more));

        Assert.Equal(expected, Facts(item)["Restore"]);
    }

    [Fact]
    public void Two_files_do_not_fold_into_one_row_and_two_changes_of_one_file_do()
    {
        var one = AlertItem.FromQueue(new AlertQueueItem("a", AuditSeverity.High, ProtectedFileAlert.Action, File1, "codex", Base));
        var same = AlertItem.FromQueue(new AlertQueueItem("b", AuditSeverity.High, ProtectedFileAlert.Action, File1, "codex", Base));
        var other = AlertItem.FromQueue(new AlertQueueItem("c", AuditSeverity.High, ProtectedFileAlert.Action, File2, "codex", Base));

        Assert.Equal(one.GroupKey, same.GroupKey);
        Assert.NotEqual(one.GroupKey, other.GroupKey);
    }

    [Fact]
    public void Another_rows_title_and_kind_are_untouched()
    {
        var item = AlertItem.FromQueue(new AlertQueueItem("a", AuditSeverity.High, "connector-hook", "x", "codex", Base));

        Assert.False(item.IsProtectedFileChange);
        Assert.Empty(item.ProtectedFileFacts);
        Assert.Empty(item.ProtectedFileNote);
        Assert.Equal(AlertKinds.Audit, item.Kind);
        Assert.Equal("connector-hook", item.Headline);
    }

    // ------------------------------------------------------------------ the kind

    [Fact]
    public void The_kind_spellings_are_known_and_the_older_list_is_the_macs_five()
    {
        Assert.Equal(AlertKinds.ProtectedFiles, AlertKinds.Normalize("Protected-Files"));
        Assert.Equal(AlertKinds.ProtectedFiles, AlertKinds.Normalize("protected files"));
        Assert.Equal(5, AlertKinds.Choices.Count);
        Assert.Equal(6, AlertKinds.ChoicesWithProtectedFiles.Count);
    }

    [Theory]
    [InlineData("1.0.5", false)]
    [InlineData("1.0.6", true)]
    public async Task The_popup_offers_the_kind_only_on_a_runtime_with_the_watch(string version, bool expected)
    {
        var services = await CreateAsync(version);

        var choices = UiThread.Run(() => new AlertsPanelViewModel(services).KindChoices);

        Assert.Equal(expected, choices.Any(c => c.Value == AlertKinds.ProtectedFiles));
    }

    [Fact]
    public async Task The_deep_link_filters_to_the_kind_and_every_severity()
    {
        var services = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1);
        _database.AddFinding("f1", Base, "HIGH");

        var vm = UiThread.Run(() =>
        {
            var panel = new AlertsPanelViewModel(services);
            panel.SetActive(true);
            panel.Accept(new AlertsFilter(Kind: AlertKinds.ProtectedFiles));
            return panel;
        });
        try
        {
            UiThread.WaitFor(() => vm.Alerts.Count == 1, "the filtered list");
            UiThread.Run(() =>
            {
                Assert.Equal(AlertKinds.ProtectedFiles, vm.KindFilter);
                Assert.Equal("p1", vm.Alerts.Single().Key);
            });
        }
        finally
        {
            UiThread.Run(() => vm.SetActive(false));
        }
    }

    // ------------------------------------------------------------------ the watched file itself

    [Fact]
    public async Task Showing_the_alert_never_opens_the_watched_file()
    {
        var services = await CreateAsync("1.0.6");

        // A file nobody else can open: a read of it anywhere below would throw or show.
        var watched = _temp.File("authorized_keys");
        const string Secret = "ssh-ed25519 AAAAsyntheticcontent operator@example.test";
        File.WriteAllText(watched, Secret);
        using var locked = new FileStream(watched, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AddChange(_database!.Path, "p1", 1, target: watched);

        await services.AlertCounts.RefreshAsync();
        var vm = UiThread.Run(() =>
        {
            var panel = new AlertsPanelViewModel(services);
            panel.SetActive(true);
            return panel;
        });
        try
        {
            UiThread.WaitFor(() => vm.Alerts.Count == 1, "the row");
            UiThread.Run(() =>
            {
                var item = vm.Alerts.Single();
                vm.SelectedAlert = item;
                var everything = string.Join(' ', item.ProtectedFileFacts.Select(f => f.Value)) + item.Headline + item.Details + item.ProtectedFileNote + item.CopyLine;
                Assert.DoesNotContain("AAAAsyntheticcontent", everything, StringComparison.Ordinal);
                Assert.Equal(watched, Facts(item)["File"]);
            });
        }
        finally
        {
            UiThread.Run(() => vm.SetActive(false));
        }
    }

    [Fact]
    public void Nothing_in_the_app_or_core_names_a_watched_file_except_to_say_what_is_checked()
    {
        var core = Path.Combine(Path.GetDirectoryName(AppSource.ProjectDirectory)!, "DefenseClaw.Core");
        var sources = Directory.EnumerateFiles(AppSource.ProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

        var naming = sources.Where(f => File.ReadAllText(f).Contains("authorized_keys", StringComparison.Ordinal)).Select(Path.GetFileName).Order().ToList();

        // Text and a list of names only: the first documents a class word of the row, the second is the list the setting shows.
        Assert.Equal(["ProtectedFileAlert.cs", "ProtectedFilesSettings.cs"], naming);
        foreach (var name in naming)
        {
            var text = File.ReadAllText(sources.First(f => Path.GetFileName(f) == name));
            Assert.DoesNotContain("File.Read", text, StringComparison.Ordinal);
            Assert.DoesNotContain("File.Open", text, StringComparison.Ordinal);
            Assert.DoesNotContain("FileStream", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SharedFile", text, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ the Overview row

    private static GatewaySnapshot Snapshot() => new()
    {
        State = AppGatewayState.Running,
        Install = InstallState.Running,
        Detail = "ok",
        ActiveConnectors = new[] { "codex" },
        PolledAt = DateTimeOffset.UtcNow,
    };

    private const string RowStart = "A protected file changed";

    private static OverviewPanelViewModel Panel(AppServices services) => UiThread.Run(() =>
    {
        var vm = new OverviewPanelViewModel(services);
        vm.Apply(Snapshot());
        return vm;
    });

    [Fact]
    public async Task A_change_waiting_raises_one_High_row_naming_the_newest_file_with_a_link_to_Alerts()
    {
        var services = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1, target: File1);
        AddChange(_database.Path, "p2", 2, target: File2);
        await services.AlertCounts.RefreshAsync();
        var seen = new List<NavigationRequest>();
        services.Navigation.Requested += (_, e) => seen.Add(e.Request);

        var vm = Panel(services);

        var row = UiThread.Run(() => vm.Attention.Single(r => r.Title.Contains("protected-file changes", StringComparison.Ordinal)));
        Assert.Equal("High", row.SeverityKey);
        Assert.Contains("authorized_keys2", row.Detail, StringComparison.Ordinal);
        Assert.Contains("One more change", row.Detail, StringComparison.Ordinal);
        Assert.Contains("Attribution is by time", row.Detail, StringComparison.Ordinal);
        var action = Assert.Single(row.Actions.Items);
        Assert.Equal(OverviewPanelViewModel.OpenProtectedFileAlertsActionId, action.Id);

        await UiThread.Run(() => vm.RunAttentionActionCommand.ExecuteAsync(action.Id));

        var request = Assert.Single(seen);
        Assert.Equal("alerts", request.PanelId);
        Assert.Equal(new AlertsFilter(Kind: AlertKinds.ProtectedFiles), request.Payload);
    }

    [Fact]
    public async Task The_row_goes_when_the_change_is_acknowledged()
    {
        var services = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1);
        await services.AlertCounts.RefreshAsync();
        var vm = Panel(services);
        Assert.Contains(UiThread.Run(() => vm.Attention.ToList()), r => r.Title.StartsWith(RowStart, StringComparison.Ordinal));

        _database.Acknowledge("p1");
        await services.AlertCounts.RefreshAsync();
        UiThread.Run(() => vm.Apply(Snapshot()));

        Assert.DoesNotContain(UiThread.Run(() => vm.Attention.ToList()), r => r.Title.StartsWith(RowStart, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_change_no_row_and_an_older_runtime_never_has_one()
    {
        var services = await CreateAsync("1.0.6");
        Assert.DoesNotContain(UiThread.Run(() => Panel(services).Attention.ToList()), r => r.Title.StartsWith(RowStart, StringComparison.Ordinal));
        services.Dispose();

        using var temp = new TempDirectory();
        using var older = TestServices.Create(temp, runtimeProbeRunner: FirstRun.FirstRunFailModeTests.Versioned("1.0.5"));
        _ = await older.Runtime.RefreshAsync();
        var database = new AlertQueueDatabase(older.Paths.AuditDatabasePath);
        AddChange(database.Path, "p1", 1);
        await older.AlertCounts.RefreshAsync();

        // The queue's arm is off there too, so the row is not counted by a runtime whose own list would not hold it.
        Assert.Empty(older.AlertCounts.Current.ProtectedFileChanges);
        Assert.DoesNotContain(UiThread.Run(() => Panel(older).Attention.ToList()), r => r.Title.StartsWith(RowStart, StringComparison.Ordinal));
        SqlitePools.Release(temp.Path);
    }

    // ------------------------------------------------------------------ pictures

    public static IEnumerable<object[]> Renders() =>
        from size in new[] { (980, 692), (1468, 875) }
        from mode in new[] { "Light", "Dark" }
        select new object[] { size.Item1, size.Item2, mode };

    [Theory]
    [MemberData(nameof(Renders))]
    public async Task Rendered_row_and_inspector(int width, int height, string mode)
    {
        var services = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1, details: Details + " agent_identity=agt-00000000000000aa");
        AddChange(_database.Path, "p2", 2, target: File2, details: "rule=persistence.protected_file_changed class=authorized_keys before=absent after=sha256:ba9876543210 connector=codex session=ses-1 tool_invocation=call-9 restored=true");
        _database.AddFinding("f1", Base, "MEDIUM");
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));

        PanelShell? shell = null;
        try
        {
            var (vm, panel) = UiThread.Run(() =>
            {
                shell = new PanelShell(services, width, height);
                var view = shell.Show<AlertsPanel>();
                return ((AlertsPanelViewModel)shell.ViewModel, view);
            });
            UiThread.WaitFor(() => vm.Alerts.Count == 3, "the rows");

            UiThread.Run(() =>
            {
                vm.SelectedAlert = vm.Alerts.First(a => a.Key == "p1");
                shell!.Host.Relayout();

                var inspector = (DefenseClaw.App.Views.Controls.DcInspector)panel.FindName("Inspector");
                var boxes = VisualTree.Descendants<TextBox>(inspector).Where(b => b.IsVisible).ToDictionary(b => AutomationProperties.GetName(b), b => b.Text);
                Assert.Equal(File1, boxes["File"]);
                Assert.Equal("0123456789ab", boxes["SHA-256 before"]);
                Assert.Equal("call-7", boxes["Tool call"]);
            });

            WaitForFade();
            UiThread.Run(() => RenderTo.Png(shell!.Host, string.Create(CultureInfo.InvariantCulture, $"cust422-alerts-inspector-{mode.ToLowerInvariant()}-{width}x{height}")));
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    [Theory]
    [MemberData(nameof(Renders))]
    public async Task Rendered_overview_row(int width, int height, string mode)
    {
        var services = await CreateAsync("1.0.6");
        AddChange(_database!.Path, "p1", 1);
        await services.AlertCounts.RefreshAsync();
        var snapshot = Snapshot();
        _ = typeof(GatewayMonitor).GetMethod("Publish", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(services.Monitor, new object[] { snapshot });
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, Enum.Parse<AppearanceMode>(mode)));

        PanelShell? shell = null;
        try
        {
            UiThread.Run(() =>
            {
                shell = new PanelShell(services, width, height);
                var panel = shell.Show<OverviewPanel>();
                var vm = (OverviewPanelViewModel)shell.ViewModel;
                vm.Apply(snapshot);
                shell.Host.Relayout();

                var list = VisualTree.Find<ItemsControl>(panel, l => AutomationProperties.GetName(l) == "What needs attention")!;
                Assert.Contains(VisualTree.Descendants<TextBlock>(list), t => t.Text == "A protected file changed during an agent call");
                Assert.Contains(VisualTree.Descendants<Wpf.Ui.Controls.Button>(list), b => Equals(b.Content, "Review in Alerts"));

                RenderTo.Png(shell.Host, string.Create(CultureInfo.InvariantCulture, $"cust422-overview-row-{mode.ToLowerInvariant()}-{width}x{height}"));
            });
        }
        finally
        {
            UiThread.Run(() => shell?.Dispose());
        }
    }

    private static void WaitForFade()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RenderTo.EnvironmentVariable)))
        {
            return;
        }

        var until = Environment.TickCount64 + 400;
        while (Environment.TickCount64 < until)
        {
            Thread.Sleep(20);
            UiThread.Run(UiThread.Settle);
        }
    }
}
