using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Inventory;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Inventory;

/// <summary>
/// Generate AI BOM, from the confirm to the browser: what the review asks and runs (<c>aibom scan --json</c> behind the existing confirm, with the scope
/// chips and the connector on the command line in the TUI's order, the whole output kept), and what the page makes of each way the run can end - a
/// scan, one that is too large to keep or to read, one with connectors it could not use, output that is not JSON, a run that did not finish, and
/// the same review on a read-only installation (off, with the installation's reason; never run). Output comes through
/// <see cref="DiscoverActionReview.RunStep"/>, so no process starts and <c>aibom scan</c> is never run; every name is synthetic.
/// </summary>
public sealed class InventoryBomFlowTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();
    private readonly List<(IReadOnlyList<string> Argv, CliRunOptions? Options)> _ran = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private InventoryPanelViewModel Open(params string[] roster) => OpenOn(null, roster);

    /// <summary>The page over a composition on <paramref name="installation"/> (null: the usual writable, user-owned one).</summary>
    private InventoryPanelViewModel OpenOn(InstallationContext? installation, params string[] roster)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _services.Add(services);
        if (roster.Length > 0)
        {
            services.ConnectorScope.UpdateRoster(roster);
        }

        return new InventoryPanelViewModel(services);
    }

    private static CliInvocation Done(IReadOnlyList<string> argv, string stdout, int exitCode, bool retainFullOutput = true)
    {
        var invocation = InvocationFactory.Create(retainFullOutput, argv.ToArray());
        foreach (var line in stdout.Split('\n'))
        {
            InvocationFactory.Append(invocation, line.TrimEnd('\r'));
        }

        InvocationFactory.Finish(invocation, exitCode);
        return invocation;
    }

    /// <summary>Opens the review and confirms it; the command's output is <paramref name="invocation"/>.</summary>
    private async Task RunAsync(InventoryPanelViewModel vm, Func<IReadOnlyList<string>, CliInvocation> invocation)
    {
        vm.Review.RunStep = (_, argv, options) =>
        {
            _ran.Add((argv, options));
            return Task.FromResult(invocation(argv));
        };
        vm.GenerateAiBomCommand.Execute(null);
        await vm.Review.ConfirmCommand.ExecuteAsync(null);
    }

    private Task RunAsync(InventoryPanelViewModel vm, string stdout, int exitCode = 0) =>
        RunAsync(vm, argv => Done(argv, stdout, exitCode));

    // ------------------------------------------------------------------ behind the confirm

    [Fact]
    public void Generate_only_opens_the_review_and_nothing_runs_until_it_is_confirmed()
    {
        var vm = Open();
        vm.Review.RunStep = (_, argv, options) =>
        {
            _ran.Add((argv, options));
            return Task.FromResult(Done(argv, "[]", 0));
        };

        vm.GenerateAiBomCommand.Execute(null);

        Assert.True(vm.Review.IsConfirming);
        Assert.Empty(_ran);
        Assert.Equal("Generate an AI BOM?", vm.Review.CommandReview!.Title);
        var step = Assert.Single(vm.Review.CommandReview.Steps);
        Assert.Equal(new[] { "aibom", "scan", "--json" }, step.Argv);
        // A scan records an audit event and posts to the gateway: it is a change, never a read.
        Assert.Equal(CommandTier.StateChanging, step.Tier);
        Assert.Equal("Generate", vm.Review.CommandReview.ConfirmLabel);

        // Dismissed, it never runs and the page is as it was.
        vm.Review.DismissCommand.Execute(null);
        Assert.Empty(_ran);
        Assert.False(vm.HasBom);
        Assert.Equal(InventoryPanelViewModel.ViewComponents, vm.ActiveView);
    }

    [Fact]
    public async Task A_confirmed_run_keeps_the_whole_output_and_lands_on_the_ai_bom_view_with_the_scan_loaded()
    {
        var vm = Open();
        Assert.True(vm.IsComponentsView);

        await RunAsync(vm, PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json"));

        var (argv, options) = Assert.Single(_ran);
        Assert.Equal(new[] { "aibom", "scan", "--json" }, argv);
        // A BOM is far longer than an ordinary command's 2,000 lines; the run keeps all of it, up to the runner's own ceiling.
        Assert.True(options!.RetainFullOutput);
        Assert.Equal(CliRunner.ExtendedTimeout, options.Timeout);

        Assert.Equal(InventoryPanelViewModel.ViewBom, vm.ActiveView);
        Assert.True(vm.IsBomView);
        Assert.False(vm.IsComponentsView);
        Assert.True(vm.BomBrowser.HasSnapshot);
        Assert.False(vm.BomFailed);
        Assert.False(vm.HasBomWarning);
        Assert.True(vm.CanSaveBom);
        Assert.StartsWith("AI BOM generated ", vm.BomStatus, StringComparison.Ordinal);
        Assert.Contains("for every active connector", vm.BomStatus, StringComparison.Ordinal);

        // After Generate, Agents, Models and Memory have their rows with the TUI's detail fields.
        var browser = vm.BomBrowser;
        browser.ActiveTab = "agents";
        Assert.Equal(3, browser.Rows.Count);
        browser.SelectedRow = browser.Rows[0];
        Assert.Equal(new[] { "Model", "Workspace", "Default", "Source", "Max Concurrent" }, browser.DetailFields.Select(f => f.Key).ToArray());
        browser.ActiveTab = "models";
        Assert.Equal(5, browser.Rows.Count);
        browser.ActiveTab = "memory";
        Assert.Single(browser.Rows);

        // The per-connector count line of the earlier page is kept.
        Assert.Equal("openclaw", Assert.Single(vm.BomRows).Connector);
    }

    [Fact]
    public async Task The_scope_chips_and_the_chosen_connector_go_on_the_command_line_in_the_tuis_order()
    {
        var vm = Open("claudecode", "codex");
        vm.BomBrowser.ShowFastScopeCommand.Execute(null);
        vm.SelectedBomConnector = "codex";

        vm.Review.RunStep = (_, argv, options) =>
        {
            _ran.Add((argv, options));
            return Task.FromResult(Done(argv, "[]", 0));
        };
        vm.GenerateAiBomCommand.Execute(null);

        var step = Assert.Single(vm.Review.CommandReview!.Steps);
        Assert.Equal(new[] { "aibom", "scan", "--json", "--only", "skills,plugins,mcp", "--connector", "codex" }, step.Argv);
        Assert.Contains("only the skills, plugins, MCP servers of the codex connector", vm.Review.CommandReview.Summary, StringComparison.Ordinal);
        Assert.Contains("fails if the gateway is down", vm.Review.CommandReview.Summary, StringComparison.Ordinal);

        await vm.Review.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(step.Argv, Assert.Single(_ran).Argv);
    }

    [Fact]
    public void Without_chips_or_a_connector_the_command_is_the_bare_scan_of_every_active_connector()
    {
        var vm = Open("claudecode", "codex");
        vm.BomBrowser.ScopeChips[3].IsActive = false;
        vm.BomBrowser.ShowAllScopeCommand.Execute(null);

        vm.GenerateAiBomCommand.Execute(null);

        var step = Assert.Single(vm.Review.CommandReview!.Steps);
        Assert.Equal(new[] { "aibom", "scan", "--json" }, step.Argv);
        Assert.Contains("the skills, plugins, MCP servers, agents, tools, models and memory of every active connector", vm.Review.CommandReview.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chips_are_kept_for_the_next_scan_and_the_scan_remembers_what_it_was_limited_to()
    {
        var vm = Open();
        vm.BomBrowser.SetCategories(new[] { InventoryBomKind.Skills, InventoryBomKind.Plugins });

        await RunAsync(vm, PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json"));

        Assert.Equal(new[] { "aibom", "scan", "--json", "--only", "skills,plugins" }, Assert.Single(_ran).Argv);
        Assert.Equal("skills,plugins", vm.BomBrowser.OnlyArgument);
        // 0.8.10 prints zeros for what --only left out; the page knows it did not ask.
        vm.BomBrowser.ActiveTab = "agents";
        Assert.Equal("Agents were not collected", vm.BomBrowser.EmptyTitle);
    }

    // ------------------------------------------------------------------ a read-only installation (CUST-308)

    [Fact]
    public async Task On_a_managed_installation_generate_is_off_with_the_installations_reason_and_a_review_started_anyway_cannot_be_confirmed()
    {
        var vm = OpenOn(TestInstallations.ManagedAt(_temp.Path));
        vm.Review.RunStep = (_, argv, options) =>
        {
            _ran.Add((argv, options));
            throw new InvalidOperationException("a blocked review ran a step");
        };

        // The control is off, and the one sentence it gives is the installation's own.
        Assert.False(vm.CanChangeInstallation);
        Assert.Equal(TestInstallations.ManagedReason, vm.InstallationBlockedReason);
        Assert.False(vm.GenerateAiBomCommand.CanExecute(null));

        // Reaching the command another way (a shortcut, the palette) still gets the confirm - with the read-only bar, and no way to run it.
        vm.GenerateAiBomCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);
        var review = vm.Review.CommandReview!;
        Assert.True(review.IsBlocked);
        Assert.Equal(TestInstallations.ManagedReason, review.BlockedReason);
        var bar = Assert.Single(review.Warnings, w => w.Title == CommandReviewWarning.ReadOnlyInstallationTitle);
        Assert.Equal(TestInstallations.ManagedReason, bar.Message);
        Assert.False(vm.Review.ConfirmCommand.CanExecute(null));

        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(_ran);
        Assert.False(vm.Review.IsRunning);
        Assert.False(vm.Review.IsFinished);
        Assert.False(vm.BomBrowser.HasSnapshot);
        Assert.False(vm.BomFailed);
        Assert.Empty(_services[^1].Cli.Activity);
    }

    [Fact]
    public void A_scan_limited_to_categories_or_a_connector_is_refused_by_the_installation_like_the_plain_one()
    {
        var vm = OpenOn(TestInstallations.ManagedAt(_temp.Path), "claudecode", "codex");
        vm.BomBrowser.ShowFastScopeCommand.Execute(null);
        vm.SelectedBomConnector = "codex";

        vm.GenerateAiBomCommand.Execute(null);

        var step = Assert.Single(vm.Review.CommandReview!.Steps);
        Assert.Equal(new[] { "aibom", "scan", "--json", "--only", "skills,plugins,mcp", "--connector", "codex" }, step.Argv);
        // The gate the runner enforces says the same about every shape of the command this page builds: none of them is a read.
        Assert.False(InstallationGate.IsReadOnly(step.Executable, step.Argv));
        Assert.False(InstallationGate.IsReadOnly(step.Executable, new[] { "aibom", "scan", "--json" }));
        Assert.True(vm.Review.CommandReview.IsBlocked);
        Assert.False(vm.Review.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_review_opened_on_a_writable_installation_is_refused_at_confirm_when_it_has_turned_read_only_and_the_page_is_left_as_it_was()
    {
        var vm = Open();
        var services = _services[^1];
        vm.Review.RunStep = (_, argv, options) =>
        {
            _ran.Add((argv, options));
            throw new InvalidOperationException("a refused review ran a step");
        };

        vm.GenerateAiBomCommand.Execute(null);
        Assert.False(vm.Review.CommandReview!.IsBlocked);
        Assert.True(vm.Review.ConfirmCommand.CanExecute(null));

        // config.yaml is edited to managed while the question is on screen: the live answer is asked again at Confirm.
        services.Installation.Replace(TestInstallations.ManagedAt(_temp.Path));
        await vm.Review.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(_ran);
        Assert.True(vm.Review.IsFinished);
        Assert.Equal("Bad", vm.Review.ResultKey);
        Assert.Equal("Not run. " + TestInstallations.ManagedReason + " Nothing was changed; the refusal is recorded in the Activity panel.", vm.Review.ResultText);
        var recorded = Assert.Single(services.Cli.Activity);
        Assert.Equal(new[] { "aibom", "scan", "--json" }, recorded.Argv);
        Assert.Contains(TestInstallations.ManagedReason, recorded.FailureReason, StringComparison.Ordinal);

        // Nothing was scanned, so the AI BOM view is not told that a scan failed, and the page stays where it was.
        Assert.False(vm.BomFailed);
        Assert.False(vm.BomBrowser.HasSnapshot);
        Assert.Equal(InventoryPanelViewModel.ViewComponents, vm.ActiveView);
    }

    // ------------------------------------------------------------------ output over the cap

    [Fact]
    public async Task Output_over_the_parsers_cap_shows_the_too_large_message_and_nothing_is_kept_or_crashes()
    {
        var vm = Open();
        var padding = new string('x', InventoryBomSnapshot.MaxOutputBytes + 1_300_000);
        // One line, as a pretty-printer would never print it, so the runner retains it whole (its newest line is never dropped).
        var big = "{\"connector\": \"claudecode\", \"skills\": [{\"id\": \"a\", \"description\": \"" + padding + "\"}]}";

        await RunAsync(vm, big);

        Assert.True(vm.BomFailed);
        Assert.False(vm.BomBrowser.HasSnapshot);
        Assert.False(vm.CanSaveBom);
        Assert.Equal(InventoryPanelViewModel.ViewBom, vm.ActiveView);
        Assert.StartsWith("Too large to display: aibom scan output is 5.2 MB, over the 4 MB limit (", vm.BomStatus, StringComparison.Ordinal);
        Assert.Contains("Narrow the scan", vm.BomStatus, StringComparison.Ordinal);
        Assert.Contains("run 'defenseclaw aibom scan --json' in a terminal", vm.BomStatus, StringComparison.Ordinal);
        Assert.Empty(vm.BomRows);
        Assert.True(vm.BomBrowser.ShowGenerate);
    }

    [Fact]
    public async Task Output_the_runner_had_to_cut_is_too_large_and_is_never_parsed_as_a_smaller_inventory()
    {
        var vm = Open();
        // Longer than the ordinary 2,000-line transcript: the runner keeps the end and drops the start, which is the middle of the document.
        var json = PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json");
        var padded = string.Join('\n', Enumerable.Repeat(json, 20));

        await RunAsync(vm, argv => Done(argv, padded, 0, retainFullOutput: false));

        Assert.True(vm.BomFailed);
        Assert.False(vm.BomBrowser.HasSnapshot);
        Assert.False(vm.CanSaveBom);
        Assert.StartsWith(
            $"Too large to display: the AI BOM output ran past the {CliInvocation.MaxRetainedOutputLines.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} lines this app keeps of one command's output (",
            vm.BomStatus,
            StringComparison.Ordinal);
        Assert.Contains(" were dropped), so none of it is shown here", vm.BomStatus, StringComparison.Ordinal);
        Assert.Contains("Narrow the scan - one connector, or fewer categories under Scope", vm.BomStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_past_the_runners_full_output_ceiling_is_the_same_message_with_that_ceiling()
    {
        var vm = Open();

        await RunAsync(vm, argv =>
        {
            var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
            InvocationFactory.AppendNumbered(invocation, CliInvocation.MaxFullOutputLines + 100);
            InvocationFactory.Finish(invocation, 0);
            return invocation;
        });

        Assert.True(vm.BomFailed);
        Assert.StartsWith(
            $"Too large to display: the AI BOM output ran past the {CliInvocation.MaxFullOutputLines.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} lines this app keeps",
            vm.BomStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_run_forgets_what_the_last_one_made_of_the_page()
    {
        var vm = Open();
        await RunAsync(vm, PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json"));
        Assert.True(vm.BomBrowser.HasSnapshot);
        _ran.Clear();

        await RunAsync(vm, "Traceback (most recent call last):");

        Assert.False(vm.BomBrowser.HasSnapshot);
        Assert.True(vm.BomFailed);
        Assert.False(vm.CanSaveBom);
        Assert.Empty(vm.BomRows);
        Assert.Empty(vm.BomBrowser.Rows);
    }

    // ------------------------------------------------------------------ connectors that did not come through

    [Fact]
    public async Task A_connector_that_could_not_be_read_is_skipped_and_named_and_one_whose_commands_failed_is_kept_and_named()
    {
        var vm = Open();

        await RunAsync(vm, PayloadFixtures.Read("aibom-scan.partial.synthetic.json"));

        Assert.True(vm.BomBrowser.HasSnapshot);
        Assert.False(vm.BomFailed);
        Assert.Equal(new[] { "claudecode", "codex" }, vm.BomBrowser.Snapshot!.Connectors.Select(c => c.Name).ToArray());
        Assert.True(vm.HasBomWarning);
        var warning = vm.BomWarning!;
        Assert.Contains("Skipped 3 connectors the output named but this page could not read: ", warning, StringComparison.Ordinal);
        Assert.Contains("entry 2 (it is text, not an inventory object)", warning, StringComparison.Ordinal);
        Assert.Contains("entry 3 (it is not an inventory (no connector, summary or category list))", warning, StringComparison.Ordinal);
        Assert.Contains("entry 4 (it is null, not an inventory object)", warning, StringComparison.Ordinal);
        Assert.Contains("codex: 2 inventory commands failed (codex:mcp, codex:plugins); what it did list is shown.", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("claudecode:", warning, StringComparison.Ordinal);

        // The ones that were read are on the page.
        vm.BomBrowser.ActiveTab = "skills";
        Assert.Equal(new[] { "claudecode", "codex" }, vm.BomBrowser.Rows.Select(r => r.Cells[0]).ToArray());
    }

    [Fact]
    public async Task When_no_connector_in_the_output_can_be_read_the_status_names_them_all_and_there_is_nothing_to_browse()
    {
        var vm = Open();

        await RunAsync(vm, "[7, \"x\"]");

        Assert.True(vm.BomFailed);
        Assert.False(vm.BomBrowser.HasSnapshot);
        Assert.Contains("no connector in its output could be read: entry 1 (it is a number, not an inventory object); entry 2 (it is text, not an inventory object)", vm.BomStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_scan_has_no_warning()
    {
        var snapshot = InventoryBomSnapshot.Parse(PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json")).Snapshot!;

        Assert.Null(InventoryPanelViewModel.WarningFor(snapshot));
    }

    // ------------------------------------------------------------------ the other ways a run ends

    [Fact]
    public async Task A_scan_of_no_connector_says_there_is_nothing_to_summarize()
    {
        var vm = Open();

        await RunAsync(vm, PayloadFixtures.Read("aibom-scan.no-connectors.json"));

        Assert.True(vm.BomFailed);
        Assert.Contains("listed no connector, so there is nothing to summarize", vm.BomStatus, StringComparison.Ordinal);
        Assert.False(vm.BomBrowser.HasSnapshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("{ not json")]
    public async Task Output_that_is_not_the_json_is_reported_and_the_page_survives(string stdout)
    {
        var vm = Open();

        await RunAsync(vm, stdout);

        Assert.True(vm.BomFailed);
        Assert.Contains("its output was not the JSON this page expected (", vm.BomStatus, StringComparison.Ordinal);
        Assert.Contains("See the Activity panel", vm.BomStatus, StringComparison.Ordinal);
        Assert.False(vm.CanSaveBom);
    }

    [Fact]
    public async Task A_run_that_failed_says_it_did_not_finish_and_keeps_nothing()
    {
        var vm = Open();

        await RunAsync(vm, "gateway unreachable", exitCode: 1);

        Assert.True(vm.BomFailed);
        Assert.StartsWith("The AI BOM run did not finish (", vm.BomStatus, StringComparison.Ordinal);
        Assert.Equal(InventoryPanelViewModel.ViewBom, vm.ActiveView);
        Assert.False(vm.BomBrowser.HasSnapshot);
    }

    [Fact]
    public void The_search_box_and_the_toolbar_follow_the_view()
    {
        var vm = Open();

        Assert.Equal("Search name, vendor, framework…", vm.SearchPlaceholder);
        Assert.Equal("Search components", vm.SearchAutomationName);

        vm.ActiveView = InventoryPanelViewModel.ViewBom;
        Assert.Equal("Search the AI BOM…", vm.SearchPlaceholder);
        Assert.Equal("Search the AI BOM", vm.SearchAutomationName);
        Assert.Equal("No AI BOM yet", vm.ToolbarCaption);

        vm.SearchText = "x";
        Assert.Equal("x", vm.BomBrowser.SearchText);
    }

    [Fact]
    public async Task The_toolbar_caption_says_which_scan_is_on_screen()
    {
        var vm = Open();
        vm.StatusMessage = "60 components across 8 vendors. · as of 10:00";
        Assert.Equal("60 components across 8 vendors. · as of 10:00", vm.ToolbarCaption);

        await RunAsync(vm, PayloadFixtures.Read("aibom-scan.openclaw.synthetic.json"));

        Assert.StartsWith("openclaw · 16 items", vm.ToolbarCaption, StringComparison.Ordinal);
        vm.ActiveView = InventoryPanelViewModel.ViewComponents;
        Assert.Equal("60 components across 8 vendors. · as of 10:00", vm.ToolbarCaption);
    }
}
