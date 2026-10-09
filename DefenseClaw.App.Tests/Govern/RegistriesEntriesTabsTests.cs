using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The Registries panel's Entries and Approved tabs and the source detail (CUST-276): the cached <c>index.json</c> of every source in one table, the
/// approved ones of every source, the counts, publisher and fetch time of a source, and the "Open in Registries" landing. The TUI's
/// <c>RegistriesPanelModel</c> (<c>tui/panels/registries.py</c>) is the reference. Three synthetic sources: <c>corp-skills</c> (the five-entry
/// <c>registry-index.synced.json</c>), <c>team.catalog</c> (<c>registry-index.second-source.json</c>; it also lists a <c>pdf-tools</c> skill, which
/// corp-skills approved and it did not) and <c>clawhub-main</c> (no cache at all, unless a test writes one).
/// </summary>
public sealed class RegistriesEntriesTabsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public RegistriesEntriesTabsTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private void WriteIndex(string sourceId, string json)
    {
        var folder = Path.Combine(_temp.Path, "registries", sourceId);
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.json"), json);
    }

    private static RegistrySourceRow Source(string id) => new() { Id = id, Fields = Array.Empty<RegistryFieldRow>() };

    /// <summary>A panel over the three sources, their caches written, with a complete read stood in for (the actions are meant to work).</summary>
    private RegistriesPanelViewModel Panel(bool withSecondSource = true)
    {
        WriteIndex("corp-skills", PayloadFixtures.Read("registry-index.synced.json"));
        if (withSecondSource)
        {
            WriteIndex("team.catalog", PayloadFixtures.Read("registry-index.second-source.json"));
        }

        var vm = new RegistriesPanelViewModel(_services);
        vm.Trust.BeginRead();
        vm.Trust.MarkComplete();
        vm.NotifyTrust();

        // Listed in the order the CLI lists them (config order), which is not id order.
        vm.Sources.Add(Source("team.catalog"));
        vm.Sources.Add(Source("corp-skills"));
        vm.Sources.Add(Source("clawhub-main"));
        vm.HasSources = true;
        return vm;
    }

    private static string[] Names(RegistriesPanelViewModel vm) => vm.TabEntries.Select(e => e.SourceId + "/" + e.Name).ToArray();

    // ------------------------------------------------------------------ the Entries tab

    [Fact]
    public async Task The_entries_tab_lists_the_cached_entries_of_every_source_in_source_order()
    {
        var vm = Panel();

        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        // Sources by id (clawhub-main has no cache), each in its file's own order, as the TUI's _entry_rows has them.
        Assert.Equal(
            new[]
            {
                "corp-skills/pdf-tools", "corp-skills/docs-mcp", "corp-skills/remote-mcp", "corp-skills/broken-skill", "corp-skills/new-entry",
                "team.catalog/pdf-tools", "team.catalog/docs-mcp", "team.catalog/wiki-skill", "team.catalog/scratch-notes",
            },
            Names(vm));
        Assert.True(vm.IsEntriesTab);
        Assert.True(vm.ShowEntriesView);
        Assert.False(vm.ShowSourcesView);
        Assert.True(vm.HasTabEntries);
        Assert.False(vm.ShowTabEmpty);
        Assert.Equal(9, vm.EntriesCount);
        Assert.Equal(3, vm.ApprovedCount);
        Assert.Null(vm.EntriesNote);
    }

    [Fact]
    public async Task A_row_of_the_entries_tab_carries_its_source_and_says_it_to_a_screen_reader_but_the_selected_sources_own_list_does_not()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        var docs = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "docs-mcp" });
        Assert.Equal("team.catalog", docs.SourceDisplay);
        Assert.Equal("mcp entry docs-mcp from team.catalog, status clean, review Approved", docs.ToString());

        // The list under the selected source has always read without the source, and still does.
        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "corp-skills"));
        Assert.All(vm.Entries, row => Assert.Null(row.SourceId));
        Assert.Equal("skill entry pdf-tools, status clean, review Approved", vm.Entries[0].ToString());
    }

    [Fact]
    public async Task The_location_of_a_stdio_mcp_entry_is_what_it_runs_and_the_other_entries_keep_the_location_they_had()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        var docs = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "docs-mcp" });
        Assert.Null(docs.Location);
        Assert.Equal("npx -y @example/docs-mcp", docs.LaunchLine);
        Assert.Equal("npx -y @example/docs-mcp", docs.LocationDisplay);
        Assert.Equal("stdio", docs.Transport);

        var remote = vm.TabEntries.Single(e => e is { SourceId: "corp-skills", Name: "remote-mcp" });
        Assert.Equal("https://mcp.example.test/sse", remote.LocationDisplay);
        Assert.Null(remote.LaunchLine);

        var pending = vm.TabEntries.Single(e => e is { Name: "scratch-notes" });
        Assert.Equal(string.Empty, pending.LocationDisplay);
    }

    [Fact]
    public async Task The_sources_are_read_in_id_order_whatever_order_they_are_listed_in()
    {
        var vm = Panel();
        WriteIndex("clawhub-main", PayloadFixtures.Read("registry-index.bare-array.json"));

        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        Assert.Equal(
            new[] { "clawhub-main", "corp-skills", "team.catalog" },
            vm.TabEntries.Select(e => e.SourceId!).Distinct().ToArray());
    }

    [Fact]
    public async Task A_source_with_nothing_cached_has_no_rows_and_is_not_a_warning()
    {
        var vm = Panel(withSecondSource: false);

        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        Assert.Equal(5, vm.TabEntries.Count);
        Assert.All(vm.TabEntries, row => Assert.Equal("corp-skills", row.SourceId));
        Assert.Null(vm.EntriesNote);
    }

    [Fact]
    public async Task A_cache_that_cannot_be_read_is_named_above_the_table_and_the_other_sources_still_list()
    {
        var vm = Panel();
        WriteIndex("clawhub-main", "{ not json");

        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        Assert.Equal(9, vm.TabEntries.Count);
        Assert.True(vm.HasEntriesNote);
        Assert.StartsWith("clawhub-main: Could not read the cached index", vm.EntriesNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cache_that_is_too_large_and_one_cut_at_the_row_limit_are_warned_about()
    {
        var vm = Panel(withSecondSource: false);
        var folder = Path.Combine(_temp.Path, "registries", "clawhub-main");
        _ = Directory.CreateDirectory(folder);
        using (var big = File.Create(Path.Combine(folder, "index.json")))
        {
            big.SetLength((4L * 1024 * 1024) + 1);
        }

        vm.Sources.Add(Source("team.catalog"));
        var many = new System.Text.StringBuilder("{\"verdicts\": [");
        for (var i = 0; i < 5_001; i++)
        {
            _ = many.Append(i == 0 ? string.Empty : ",").Append($"{{\"name\": \"s{i}\", \"type\": \"skill\", \"status\": \"clean\"}}");
        }

        WriteIndex("team.catalog", many.Append("]}").ToString());

        await vm.LoadAllEntriesAsync();

        Assert.Contains("clawhub-main: The cached index is larger than 4 MiB", vm.EntriesNote, StringComparison.Ordinal);
        Assert.Contains($"team.catalog: Showing the first {5_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} entries.", vm.EntriesNote, StringComparison.Ordinal);
        Assert.Equal(5 + 5_000, vm.EntriesCount); // corp-skills' five and the 5,000 that were read
    }

    [Fact]
    public async Task A_source_id_that_is_not_a_plain_name_is_never_read_as_a_path()
    {
        var vm = Panel(withSecondSource: false);
        var outside = Path.Combine(_temp.Path, "evil");
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "index.json"), PayloadFixtures.Read("registry-index.bare-array.json"));
        vm.Sources.Add(Source(@"..\evil"));

        await vm.LoadAllEntriesAsync();

        Assert.DoesNotContain(vm.TabEntries, row => row.Name == "pdf-tools" && row.SourceId == @"..\evil");
        Assert.Contains("not a plain name", vm.EntriesNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_sources_is_an_empty_table_with_the_tuis_sentence()
    {
        var vm = new RegistriesPanelViewModel(_services);

        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        Assert.Empty(vm.TabEntries);
        Assert.Equal(0, vm.EntriesCount);
        Assert.Equal("Sync a source to populate this view.", vm.EmptyEntriesText);
        Assert.False(vm.ShowEntriesView); // with no sources the panel shows its "no sources yet" card on every tab
    }

    [Fact]
    public void Until_the_entries_have_been_read_the_tabs_have_no_count()
    {
        var vm = Panel();

        Assert.Null(vm.EntriesCount);
        Assert.Null(vm.ApprovedCount);
    }

    // ------------------------------------------------------------------ the Approved tab

    [Fact]
    public async Task The_approved_tab_lists_the_approved_entries_across_sources()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;

        Assert.True(vm.IsApprovedTab);
        Assert.Equal(new[] { "corp-skills/pdf-tools", "team.catalog/docs-mcp", "team.catalog/wiki-skill" }, Names(vm));
        Assert.All(vm.TabEntries, row => Assert.True(row.Approved));
        Assert.Equal(3, vm.ApprovedCount);

        // The entries a rejected or unreviewed state leaves out stay on the Entries tab.
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        Assert.Contains(vm.TabEntries, row => row is { Name: "remote-mcp", Rejected: true });
        Assert.Contains(vm.TabEntries, row => row is { SourceId: "team.catalog", Name: "pdf-tools", Approved: false });
    }

    [Fact]
    public async Task The_approved_tab_with_nothing_approved_says_so_with_the_tuis_sentence()
    {
        var vm = Panel(withSecondSource: false);
        WriteIndex("corp-skills", PayloadFixtures.Read("registry-index.bare-array.json")); // one entry, not approved
        await vm.LoadAllEntriesAsync();

        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;

        Assert.Empty(vm.TabEntries);
        Assert.True(vm.ShowTabEmpty);
        Assert.StartsWith("No entries approved yet.", vm.EmptyEntriesText, StringComparison.Ordinal);
        Assert.Equal(0, vm.ApprovedCount);
        Assert.Equal(1, vm.EntriesCount);
    }

    [Fact]
    public async Task An_approval_that_lands_on_disk_shows_on_the_approved_tab_after_the_next_read()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
        Assert.Equal(3, vm.TabEntries.Count);

        // `registry approve team.catalog scratch-notes` rewrote the cache.
        var index = System.Text.Json.Nodes.JsonNode.Parse(PayloadFixtures.Read("registry-index.second-source.json"))!;
        var scratch = index["verdicts"]!.AsArray().Single(v => (string?)v!["name"] == "scratch-notes")!;
        scratch["approved"] = true;
        WriteIndex("team.catalog", index.ToJsonString());
        await vm.LoadAllEntriesAsync();

        Assert.Equal(4, vm.TabEntries.Count);
        Assert.Contains(vm.TabEntries, row => row is { SourceId: "team.catalog", Name: "scratch-notes" });
        Assert.Equal(4, vm.ApprovedCount);
    }

    // ------------------------------------------------------------------ tabs, selection

    [Fact]
    public async Task Changing_tab_starts_the_new_one_at_the_top_with_nothing_selected()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        vm.SelectedTabEntry = vm.TabEntries[3];

        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;

        Assert.Null(vm.SelectedTabEntry);
        Assert.False(vm.HasSelectedTabEntry);

        vm.ActiveTab = RegistriesPanelViewModel.SourcesTab;
        Assert.True(vm.ShowSourcesView);
        Assert.False(vm.ShowEntriesView);
    }

    [Fact]
    public async Task A_name_that_is_not_a_tab_falls_back_to_the_sources_tab()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        vm.ActiveTab = "everything";

        Assert.Equal(RegistriesPanelViewModel.SourcesTab, vm.ActiveTab);
    }

    [Fact]
    public async Task A_selected_row_stays_selected_when_the_entries_are_read_again()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        var chosen = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "wiki-skill" });
        vm.SelectedTabEntry = chosen;

        await vm.LoadAllEntriesAsync();

        // A read makes new row objects; the selection follows the entry (source, type and name), not the object.
        Assert.NotSame(chosen, vm.SelectedTabEntry);
        Assert.Equal(("team.catalog", "wiki-skill"), (vm.SelectedTabEntry!.SourceId, vm.SelectedTabEntry.Name));
        Assert.Same(vm.SelectedTabEntry, vm.TabEntries.Single(e => e.Key == chosen.Key));
    }

    [Fact]
    public async Task A_selection_whose_entry_is_gone_is_cleared()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        vm.SelectedTabEntry = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "scratch-notes" });

        File.Delete(Path.Combine(_temp.Path, "registries", "team.catalog", "index.json"));
        await vm.LoadAllEntriesAsync();

        Assert.Null(vm.SelectedTabEntry);
        Assert.Equal(5, vm.TabEntries.Count);
    }

    [Fact]
    public async Task A_newer_read_supersedes_one_still_running()
    {
        var vm = Panel();

        var first = vm.LoadAllEntriesAsync();
        var second = vm.LoadAllEntriesAsync();
        await Task.WhenAll(first, second);
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;

        Assert.Equal(9, vm.TabEntries.Count); // not 18: the older read does not add its rows to the newer one's
        Assert.Equal(9, vm.EntriesCount);
    }

    // ------------------------------------------------------------------ the toolbar's source actions on these tabs

    [Fact]
    public async Task Sync_selected_on_the_entries_tab_syncs_the_source_of_the_selected_entry()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        Assert.False(vm.CanChangeSelectedSource); // nothing is selected

        vm.SelectedTabEntry = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "wiki-skill" });
        Assert.True(vm.CanChangeSelectedSource);

        vm.SyncSelectedCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        Assert.Equal(new[] { "registry", "sync", "team.catalog", "--json" }, vm.Review.CommandReview!.Steps[0].Argv);
        Assert.Equal("Sync “team.catalog”?", vm.Review.CommandReview.Title);
    }

    [Fact]
    public async Task Sync_selected_on_the_sources_tab_is_still_about_the_selected_source()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.SelectedSource = vm.Sources.Single(s => s.Id == "corp-skills");
        Assert.True(vm.CanChangeSelectedSource);

        vm.SyncSelectedCommand.Execute(null);

        Assert.Equal(new[] { "registry", "sync", "corp-skills", "--json" }, vm.Review.CommandReview!.Steps[0].Argv);
    }

    // ------------------------------------------------------------------ approve and reject from the tables

    [Fact]
    public async Task Approve_on_the_entries_tab_reviews_the_command_for_the_rows_own_source()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        vm.SelectedTabEntry = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "pdf-tools" });
        Assert.True(vm.CanReviewTabEntry);

        vm.ApproveListedEntryCommand.Execute(null);

        Assert.True(vm.Review.IsOpen);
        Assert.Equal(
            new[] { "registry", "approve", "team.catalog", "pdf-tools", "--type", "skill", "--json" },
            vm.Review.CommandReview!.Steps[0].Argv);
        Assert.Equal("Approve skill “pdf-tools”?", vm.Review.CommandReview.Title);
    }

    [Fact]
    public async Task Reject_from_the_approved_tab_reviews_the_reject_command_and_a_dash_name_goes_after_the_separator()
    {
        var vm = Panel();
        WriteIndex("team.catalog", """[{"name": "-odd", "type": "skill", "status": "clean", "approved": true, "rejected": false}]""");
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
        vm.SelectedTabEntry = vm.TabEntries.Single(e => e.Name == "-odd");

        vm.RejectListedEntryCommand.Execute(null);

        Assert.Equal(
            new[] { "registry", "reject", "--type", "skill", "--json", "--", "team.catalog", "-odd" },
            vm.Review.CommandReview!.Steps[0].Argv);
    }

    [Fact]
    public async Task Only_a_skill_or_an_mcp_entry_can_be_reviewed_from_the_tables()
    {
        var vm = Panel();
        WriteIndex("team.catalog", """[{"name": "odd-plugin", "type": "plugin", "status": "clean"}]""");
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        vm.SelectedTabEntry = vm.TabEntries.Single(e => e.Name == "odd-plugin");

        Assert.False(vm.CanReviewTabEntry);
        vm.ApproveListedEntryCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
    }

    [Fact]
    public async Task The_review_commands_of_the_tables_are_held_back_by_the_same_gates_as_the_rest_of_the_panel()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        vm.SelectedTabEntry = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "pdf-tools" });

        // A read that failed: the rows are information, not authority.
        vm.Trust.MarkFailed("could not read the sources");
        vm.NotifyTrust();
        Assert.False(vm.CanReviewTabEntry);
        Assert.False(vm.CanChangeSelectedSource);
        vm.ApproveListedEntryCommand.Execute(null);
        vm.RejectListedEntryCommand.Execute(null);
        vm.SyncSelectedCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasActionMessage);
        Assert.Contains("Changes are off", vm.ActionMessage, StringComparison.Ordinal);

        // An installation that may not be changed says its own sentence first.
        vm.Trust.BeginRead();
        vm.Trust.MarkComplete();
        _services.Installation.Replace(TestInstallations.Managed(_temp.Path));
        vm.NotifyTrust();
        Assert.False(vm.CanReviewTabEntry);
        vm.ApproveListedEntryCommand.Execute(null);
        Assert.False(vm.Review.IsOpen);
        Assert.Equal(TestInstallations.ManagedReason, vm.ActionMessage);
        Assert.Empty(_services.Cli.Activity);
    }

    // ------------------------------------------------------------------ show source

    [Fact]
    public async Task Show_source_selects_the_entrys_source_on_the_sources_tab()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        vm.SelectedTabEntry = vm.TabEntries.Single(e => e is { SourceId: "team.catalog", Name: "wiki-skill" });

        vm.ShowEntrySourceCommand.Execute(null);

        Assert.True(vm.IsSourcesTab);
        Assert.Equal("team.catalog", vm.SelectedSource!.Id);
    }

    // ------------------------------------------------------------------ the source detail

    [Fact]
    public async Task A_sources_details_carry_its_publisher_when_it_was_fetched_and_the_four_counts()
    {
        var vm = Panel();

        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "corp-skills"));

        var facts = vm.SourceFacts;
        Assert.True(facts.HasCounts);
        Assert.Equal("Example Corp", facts.Publisher);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", facts.FetchedAt);
        Assert.Equal("5 entries", facts.EntriesText);
        Assert.Equal("1 clean", facts.CleanText);
        Assert.Equal("1 warning", facts.WarningText);
        Assert.Equal("1 blocked", facts.BlockedText);
        Assert.Equal("1 error", facts.ErrorText);
        Assert.Equal("5 entries: 1 clean, 1 warning, 1 blocked, 1 error", facts.CountsText);
        Assert.Equal(new[] { "Ok", "Warn", "Bad", "Bad" }, new[] { facts.CleanTone, facts.WarningTone, facts.BlockedTone, facts.ErrorTone });
    }

    [Fact]
    public async Task A_count_of_zero_is_shown_as_zero_in_the_quiet_tone()
    {
        var vm = Panel();

        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "team.catalog"));

        var facts = vm.SourceFacts;
        Assert.Equal("Team Catalog", facts.Publisher);
        Assert.Equal("4 entries", facts.EntriesText);
        Assert.Equal("3 clean", facts.CleanText);
        Assert.Equal("0 warning", facts.WarningText);
        Assert.Equal("0 blocked", facts.BlockedText);
        Assert.Equal("0 error", facts.ErrorText);
        Assert.Equal(new[] { "Ok", "Neutral", "Neutral", "Neutral" }, new[] { facts.CleanTone, facts.WarningTone, facts.BlockedTone, facts.ErrorTone });
    }

    [Fact]
    public async Task An_index_without_counts_is_counted_from_its_entries_as_the_tui_counts_it()
    {
        var vm = Panel();
        WriteIndex("clawhub-main", """
            {"source_id": "clawhub-main", "publisher": "  Hub\nMain  ", "verdicts": [
              {"name": "a", "type": "skill", "status": "clean"},
              {"name": "b", "type": "skill", "status": "clean"},
              {"name": "c", "type": "mcp", "status": "warning"},
              {"name": "d", "type": "mcp", "status": "blocked"},
              {"name": "e", "type": "mcp", "status": "error"},
              {"name": "f", "type": "mcp", "status": "pending"}
            ]}
            """);

        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "clawhub-main"));

        var facts = vm.SourceFacts;
        Assert.Equal("6 entries", facts.EntriesText);
        Assert.Equal(
            new[] { "2 clean", "1 warning", "1 blocked", "1 error" },
            new[] { facts.CleanText, facts.WarningText, facts.BlockedText, facts.ErrorText });
        Assert.Equal("Hub Main", facts.Publisher); // one line
        Assert.Equal("—", facts.FetchedAt); // the index states no time
    }

    [Fact]
    public async Task A_count_the_file_states_is_the_one_shown()
    {
        var vm = Panel();
        WriteIndex("clawhub-main", """{"entry_count": 40, "clean_count": "30", "warning_count": 4.0, "blocked_count": 3, "error_count": null, "verdicts": [{"name": "a", "type": "skill", "status": "error"}]}""");

        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "clawhub-main"));

        var facts = vm.SourceFacts;
        Assert.Equal("40 entries", facts.EntriesText);
        Assert.Equal("30 clean", facts.CleanText);
        Assert.Equal("4 warning", facts.WarningText);
        Assert.Equal("3 blocked", facts.BlockedText);
        Assert.Equal("1 error", facts.ErrorText); // null: counted
    }

    [Fact]
    public async Task A_bare_list_index_has_counts_but_no_publisher_or_time()
    {
        var vm = Panel();
        WriteIndex("clawhub-main", PayloadFixtures.Read("registry-index.bare-array.json"));

        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "clawhub-main"));

        Assert.True(vm.SourceFacts.HasCounts);
        Assert.Equal("—", vm.SourceFacts.Publisher);
        Assert.Equal("—", vm.SourceFacts.FetchedAt);
        Assert.Equal("1 entry", vm.SourceFacts.EntriesText);
        Assert.Equal("1 clean", vm.SourceFacts.CleanText);
    }

    [Fact]
    public async Task A_source_with_no_readable_index_has_no_counts_to_show()
    {
        var vm = Panel();

        // Never synced.
        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "clawhub-main"));
        Assert.False(vm.SourceFacts.HasCounts);
        Assert.Equal("—", vm.SourceFacts.Publisher);
        Assert.Equal("—", vm.SourceFacts.FetchedAt);
        Assert.Equal("No counts available", vm.SourceFacts.CountsText);
        Assert.Contains("Nothing cached yet", vm.EntriesMessage, StringComparison.Ordinal);

        // Corrupt.
        WriteIndex("clawhub-main", "{ not json");
        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "clawhub-main"));
        Assert.False(vm.SourceFacts.HasCounts);
        Assert.StartsWith("Could not read the cached index", vm.EntriesMessage, StringComparison.Ordinal);

        // No selected source.
        await vm.LoadEntriesAsync(null);
        Assert.False(vm.SourceFacts.HasCounts);
    }

    [Fact]
    public async Task A_publisher_with_a_control_character_is_spelled_out()
    {
        var vm = Panel();
        WriteIndex("clawhub-main", "{\"publisher\": \"Corp\\u202Eevil\", \"verdicts\": []}");

        await vm.LoadEntriesAsync(vm.Sources.Single(s => s.Id == "clawhub-main"));

        Assert.Equal("Corp\\u202Eevil", vm.SourceFacts.Publisher);
    }

    // ------------------------------------------------------------------ Open in Registries: the landing

    [Fact]
    public async Task A_link_narrows_the_entries_tab_to_the_entry_in_every_source_and_selects_the_one_of_the_named_source()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        var requested = 0;
        vm.FocusEntryRequested += (_, _) => requested++;

        var found = vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        Assert.True(found);
        Assert.True(vm.IsEntriesTab);
        Assert.True(vm.HasEntryFocus);
        Assert.Equal(new[] { "corp-skills/pdf-tools", "team.catalog/pdf-tools" }, Names(vm));
        Assert.Equal("team.catalog", vm.SelectedTabEntry!.SourceId);
        Assert.Equal("pdf-tools", vm.SelectedTabEntry.Name);
        Assert.Equal("Showing skill “pdf-tools” in every source (2 matches)", vm.EntryFocusText);
        Assert.False(vm.HasFocusNote);
        Assert.Equal(1, requested);
    }

    [Fact]
    public async Task The_source_a_link_names_outranks_the_row_that_was_selected_before_it()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        vm.SelectedTabEntry = vm.TabEntries.Single(e => e is { SourceId: "corp-skills", Name: "pdf-tools" });

        _ = vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        Assert.Equal("team.catalog", vm.SelectedTabEntry!.SourceId);
    }

    [Fact]
    public async Task A_link_to_a_source_that_does_not_list_the_entry_selects_the_first_match()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        Assert.True(vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "clawhub-main")));
        Assert.Equal("corp-skills", vm.SelectedTabEntry!.SourceId);

        Assert.True(vm.FocusEntry(new RegistryFocus("skill", "pdf-tools")));
        Assert.Equal("corp-skills", vm.SelectedTabEntry!.SourceId);
    }

    [Fact]
    public async Task A_link_to_an_entry_in_one_source_narrows_to_that_one_row()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        Assert.True(vm.FocusEntry(new RegistryFocus("mcp", "remote-mcp", "corp-skills")));

        Assert.Equal(new[] { "corp-skills/remote-mcp" }, Names(vm));
        Assert.Equal("remote-mcp", vm.SelectedTabEntry!.Name);
        Assert.Equal("Showing mcp “remote-mcp” in every source (1 match)", vm.EntryFocusText);
    }

    [Fact]
    public async Task The_type_is_matched_without_regard_to_case_and_the_name_exactly()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        Assert.True(vm.FocusEntry(new RegistryFocus(" SKILL ", "pdf-tools")));
        Assert.False(vm.FocusEntry(new RegistryFocus("skill", "PDF-Tools")));
        Assert.False(vm.FocusEntry(new RegistryFocus("skill", "pdf-tools ")));
        Assert.False(vm.FocusEntry(new RegistryFocus("mcp", "pdf-tools"))); // the other kind
    }

    [Fact]
    public async Task A_link_to_an_entry_nobody_has_cached_leaves_the_tab_whole_and_says_so()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        var requested = 0;
        vm.FocusEntryRequested += (_, _) => requested++;

        var found = vm.FocusEntry(new RegistryFocus("skill", "gone-skill", "corp-skills"));

        Assert.False(found);
        Assert.True(vm.IsEntriesTab);
        Assert.False(vm.HasEntryFocus);
        Assert.Equal(9, vm.TabEntries.Count);
        Assert.True(vm.HasFocusNote);
        Assert.Contains("skill “gone-skill”", vm.FocusNote, StringComparison.Ordinal);
        Assert.Contains("registry:corp-skills", vm.FocusNote, StringComparison.Ordinal);
        Assert.Contains("Sync that source", vm.FocusNote, StringComparison.Ordinal);
        Assert.Equal(0, requested);

        // Without a source the note does not name one.
        _ = vm.FocusEntry(new RegistryFocus("skill", "gone-skill"));
        Assert.Equal("No registry source has a cached entry for skill “gone-skill”.", vm.FocusNote);
    }

    [Fact]
    public async Task A_link_from_a_source_that_is_no_longer_configured_says_the_source_is_gone_and_not_to_sync_it()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        var found = vm.FocusEntry(new RegistryFocus("skill", "gone-skill", "retired-registry"));

        Assert.False(found);
        Assert.Contains("registry:retired-registry", vm.FocusNote, StringComparison.Ordinal);
        Assert.Contains("no registry source called “retired-registry”", vm.FocusNote, StringComparison.Ordinal);
        Assert.Contains("removed from config.yaml by hand", vm.FocusNote, StringComparison.Ordinal);
        Assert.DoesNotContain("Sync that source", vm.FocusNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_from_the_approved_tab_goes_to_the_entries_tab_which_also_lists_what_is_not_approved()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
        Assert.DoesNotContain(vm.TabEntries, row => row is { SourceId: "team.catalog", Name: "pdf-tools" });

        _ = vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        Assert.True(vm.IsEntriesTab);
        Assert.Contains(vm.TabEntries, row => row is { SourceId: "team.catalog", Name: "pdf-tools", Approved: false });
    }

    [Fact]
    public async Task Show_all_entries_ends_the_narrowing_and_keeps_the_selected_row()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        _ = vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        vm.ClearEntryFocusCommand.Execute(null);

        Assert.False(vm.HasEntryFocus);
        Assert.Equal(string.Empty, vm.EntryFocusText);
        Assert.Equal(9, vm.TabEntries.Count);
        Assert.Equal(("team.catalog", "pdf-tools"), (vm.SelectedTabEntry!.SourceId, vm.SelectedTabEntry.Name));
    }

    [Fact]
    public async Task Changing_tab_ends_the_narrowing_as_the_tuis_set_tab_does()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        _ = vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
        Assert.False(vm.HasEntryFocus);

        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        Assert.False(vm.HasEntryFocus);
        Assert.Equal(9, vm.TabEntries.Count);
        Assert.Null(vm.SelectedTabEntry);
    }

    [Fact]
    public async Task Escape_ends_a_narrowing_after_the_dialogs_and_forms_have_had_it()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        _ = vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        vm.SyncAllCommand.Execute(null);
        Assert.True(vm.Review.IsOpen);
        Assert.True(vm.HandleEscape()); // the dialog first
        Assert.False(vm.Review.IsOpen);
        Assert.True(vm.HasEntryFocus);

        Assert.True(vm.HandleEscape()); // then the narrowing
        Assert.False(vm.HasEntryFocus);
        Assert.False(vm.HandleEscape()); // nothing left
    }

    [Fact]
    public async Task A_narrowing_survives_a_new_read_and_the_selected_row_with_it()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        _ = vm.FocusEntry(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        await vm.LoadAllEntriesAsync();

        Assert.True(vm.HasEntryFocus);
        Assert.Equal(2, vm.TabEntries.Count);
        Assert.Equal("team.catalog", vm.SelectedTabEntry!.SourceId);
    }

    // ------------------------------------------------------------------ the navigation payload

    [Fact]
    public async Task The_panel_takes_a_registry_focus_payload_and_lands_on_the_entry()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        vm.Accept(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        Assert.True(vm.IsEntriesTab);
        Assert.Equal("team.catalog", vm.SelectedTabEntry!.SourceId);
        Assert.Equal(2, vm.TabEntries.Count);
    }

    [Fact]
    public void A_payload_of_another_type_or_one_that_names_no_entry_is_ignored()
    {
        var vm = Panel();

        vm.Accept("alerts");
        vm.Accept(new AlertsFilter());
        vm.Accept(new RegistryFocus("skill", "  "));
        vm.Accept(new RegistryFocus(" ", "pdf-tools"));

        Assert.True(vm.IsSourcesTab);
        Assert.False(vm.HasEntryFocus);
    }

    [Fact]
    public async Task A_link_that_arrives_before_the_first_read_waits_for_it()
    {
        var vm = Panel();

        // The very first visit: the panel is shown and handed the payload while its entries are still being read.
        vm.Accept(new RegistryFocus("skill", "pdf-tools", "team.catalog"));
        Assert.True(vm.IsEntriesTab);
        Assert.Empty(vm.TabEntries);
        Assert.False(vm.HasEntryFocus);
        Assert.False(vm.HasFocusNote); // not "not found": nothing has been read to say so

        await vm.LoadAllEntriesAsync();

        Assert.True(vm.HasEntryFocus);
        Assert.Equal(2, vm.TabEntries.Count);
        Assert.Equal("team.catalog", vm.SelectedTabEntry!.SourceId);
    }

    [Fact]
    public async Task Choosing_another_tab_while_the_link_waits_withdraws_it()
    {
        var vm = Panel();
        vm.Accept(new RegistryFocus("skill", "pdf-tools", "team.catalog"));

        vm.ActiveTab = RegistriesPanelViewModel.SourcesTab;
        await vm.LoadAllEntriesAsync();

        Assert.True(vm.IsSourcesTab);
        Assert.False(vm.HasEntryFocus);
        Assert.Null(vm.SelectedTabEntry);
    }

    [Fact]
    public async Task A_link_for_an_entry_that_is_not_there_when_the_first_read_ends_says_so()
    {
        var vm = Panel();
        vm.Accept(new RegistryFocus("skill", "gone-skill", "corp-skills"));

        await vm.LoadAllEntriesAsync();

        Assert.True(vm.IsEntriesTab);
        Assert.True(vm.HasFocusNote);
        Assert.Equal(9, vm.TabEntries.Count);
    }

    [Fact]
    public async Task The_second_link_replaces_the_first()
    {
        var vm = Panel();
        await vm.LoadAllEntriesAsync();

        vm.Accept(new RegistryFocus("skill", "pdf-tools", "team.catalog"));
        vm.Accept(new RegistryFocus("mcp", "remote-mcp", "corp-skills"));

        Assert.Equal(new[] { "corp-skills/remote-mcp" }, Names(vm));
    }

    // ------------------------------------------------------------------ the whole read, with a scripted `registry list`

    private static CliInvocation ListResult(string json)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, "registry", "list", "--json");
        InvocationFactory.Append(invocation, json);
        InvocationFactory.Finish(invocation, 0);
        return invocation;
    }

    private RegistriesPanelViewModel Scripted(Func<Task<CliInvocation>> list)
    {
        WriteIndex("corp-skills", PayloadFixtures.Read("registry-index.synced.json"));
        return new RegistriesPanelViewModel(_services) { ReadSources = (_, _) => list() };
    }

    [Fact]
    public async Task A_read_of_the_sources_reads_the_entries_of_every_one_of_them_too()
    {
        WriteIndex("team.catalog", PayloadFixtures.Read("registry-index.second-source.json"));
        var vm = Scripted(() => Task.FromResult(ListResult(PayloadFixtures.Read("registry-list.sources.json"))));

        await vm.InitializeAsync();

        Assert.Equal(new[] { "corp-skills", "team.catalog", "clawhub-main" }, vm.Sources.Select(s => s.Id).ToArray());
        Assert.True(vm.HasSources);
        Assert.Equal(9, vm.EntriesCount);
        Assert.Equal(3, vm.ApprovedCount);

        vm.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
        Assert.Equal(new[] { "corp-skills/pdf-tools", "team.catalog/docs-mcp", "team.catalog/wiki-skill" }, Names(vm));
    }

    [Fact]
    public async Task A_link_that_arrives_while_the_first_read_is_still_open_is_answered_from_what_the_read_brings()
    {
        WriteIndex("team.catalog", PayloadFixtures.Read("registry-index.second-source.json"));
        var gate = new TaskCompletionSource();
        var vm = Scripted(async () =>
        {
            await gate.Task;
            return ListResult(PayloadFixtures.Read("registry-list.sources.json"));
        });

        // The very first visit: the catalog shows the panel, its read begins, and the link is handed over before the read has answered.
        var first = vm.InitializeAsync();
        vm.Accept(new RegistryFocus("skill", "pdf-tools", "team.catalog"));
        Assert.True(vm.IsEntriesTab);
        Assert.False(vm.HasEntryFocus);
        Assert.False(vm.HasFocusNote); // there is nothing yet to say it is not found in

        gate.SetResult();
        await first;

        Assert.True(vm.HasEntryFocus);
        Assert.Equal(2, vm.TabEntries.Count);
        Assert.Equal("team.catalog", vm.SelectedTabEntry!.SourceId);
        Assert.False(vm.HasFocusNote);
    }

    [Fact]
    public async Task A_link_that_arrives_while_a_refresh_is_open_is_answered_from_the_rows_the_refresh_brings()
    {
        // The panel was visited a while ago: its entries are old, and coming back starts a read at the same moment the link arrives.
        var calls = 0;
        var gate = new TaskCompletionSource();
        var vm = Scripted(async () =>
        {
            if (++calls == 2)
            {
                await gate.Task;
            }

            return ListResult(PayloadFixtures.Read("registry-list.sources.json"));
        });
        await vm.InitializeAsync();
        vm.ActiveTab = RegistriesPanelViewModel.EntriesTab;
        Assert.DoesNotContain(vm.TabEntries, row => row.Name == "wiki-skill");

        // team.catalog has been synced since (the cache gained an entry), and the second read is held open.
        WriteIndex("team.catalog", PayloadFixtures.Read("registry-index.second-source.json"));
        var refresh = vm.RefreshCommand.ExecuteAsync(null);
        vm.Accept(new RegistryFocus("skill", "wiki-skill", "team.catalog"));

        // Not answered from the old rows ("not found"): it waits for the new ones.
        Assert.False(vm.HasEntryFocus);
        Assert.False(vm.HasFocusNote);

        gate.SetResult();
        await refresh;

        Assert.True(vm.HasEntryFocus);
        Assert.Equal(("team.catalog", "wiki-skill"), (vm.SelectedTabEntry!.SourceId, vm.SelectedTabEntry.Name));
        Assert.False(vm.HasFocusNote);
    }

    [Fact]
    public async Task A_link_is_answered_when_the_read_of_the_sources_fails_too()
    {
        var vm = new RegistriesPanelViewModel(_services)
        {
            ReadSources = (_, _) => Task.FromResult(ListResultWithExit(2, "boom")),
        };
        var read = vm.InitializeAsync();
        vm.Accept(new RegistryFocus("skill", "pdf-tools", "corp-skills"));

        await read;

        // Nothing could be read: the link is answered, as "not found", and the panel says the read failed.
        Assert.True(vm.HasCliError);
        Assert.True(vm.HasFocusNote);
        Assert.False(vm.HasEntryFocus);
    }

    private static CliInvocation ListResultWithExit(int exit, string stderr)
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, "registry", "list", "--json");
        InvocationFactory.Append(invocation, stderr, CliStream.StandardError);
        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }

    [Fact]
    public async Task A_link_is_applied_to_the_panel_the_catalog_hands_the_pending_request()
    {
        // The whole path of the Skills row action: the request waits in the inbox, the panel takes it, and the view-model lands on the entry.
        var vm = Panel();
        await vm.LoadAllEntriesAsync();
        _services.Navigation.Request("registries", new RegistryFocus("skill", "pdf-tools", "corp-skills"));

        var request = _services.Navigation.TryTake("registries");
        Assert.NotNull(request);
        vm.AcceptNavigation(request!.Payload!);

        Assert.Equal("corp-skills", vm.SelectedTabEntry!.SourceId);
        Assert.Null(_services.Navigation.Pending);
    }
}
