using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Payloads;

/// <summary>
/// The Registries panel reads two things: <c>defenseclaw registry list --json</c> (one CLI run) and, for the selected
/// source, the cache file <c>registries\&lt;id&gt;\index.json</c>. Synthetic payloads in <c>Fixtures/CliPayloads</c> follow
/// the code that writes them; file:line refer to the <c>defenseclaw</c> 0.8.10 wheel. <c>registry list --json</c> printing
/// <c>[]</c> was captured live (this machine has no registry source); a populated list and any index.json follow the
/// source alone.
/// </summary>
public sealed class RegistryPayloadTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DefenseClaw.App.Services.AppServices _services;

    public RegistryPayloadTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private (RegistriesPanelViewModel Vm, List<RegistrySourceRow> Rows) ReadSources(string json)
    {
        var vm = new RegistriesPanelViewModel(_services);
        var rows = new List<RegistrySourceRow>();
        vm.ParseSources(json, rows);
        return (vm, rows);
    }

    private void WriteIndex(string sourceId, string json)
    {
        var folder = Path.Combine(_temp.Path, "registries", sourceId);
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.json"), json);
    }

    private static RegistrySourceRow Source(string id) => new() { Id = id, Fields = Array.Empty<RegistryFieldRow>() };

    // ------------------------------------------------------------------ registry list --json

    // Emitter: commands/cmd_registry.py:504-535 (list_cmd: a JSON array, sorted keys via _emit_json at 206-207) and 210-222
    // (_source_to_dict: id, kind, url, content, auth_env, enabled, auto_sync, sync_interval_hours, last_sync, last_status),
    // plus an "entries" object {total, clean, warning, blocked, error} read from the cached index (cache.py:93-118).
    // Kinds are config.py:1479-1487, content types 1496, last_status is "ok" or "error: <reason>" (config.py:1536).
    [Fact]
    public void A_registry_list_reads_every_field_the_cli_prints()
    {
        var (_, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));

        Assert.Equal(new[] { "corp-skills", "team.catalog", "clawhub-main" }, rows.Select(r => r.Id).ToArray());

        var corp = rows[0];
        Assert.Equal("http_yaml", corp.Kind);
        Assert.Equal("both", corp.Content);
        Assert.Equal("https://registry.example.test/defenseclaw-registry.yaml", corp.Location);
        Assert.Equal("EXAMPLE_REGISTRY_TOKEN", corp.AuthEnv);
        Assert.Equal("Enabled", corp.EnabledDisplay);
        Assert.Equal("Ok", corp.EnabledKey);
        Assert.Equal("5 (2 clean, 1 warning, 1 blocked)", corp.EntriesDisplay);
        Assert.Equal("ok", corp.StatusDisplay);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", corp.LastSyncDisplay);
        Assert.Equal(11, corp.Fields.Count);
        Assert.Contains(corp.Fields, f => f is { Key: "sync_interval_hours", Value: "24" });
        Assert.Contains(corp.Fields, f => f is { Key: "auto_sync", Value: "false" });
    }

    [Fact]
    public void A_source_that_was_never_synced_reads_not_synced_even_though_the_cli_prints_a_zeroed_entries_object()
    {
        var (_, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));

        var never = rows[1];
        Assert.Equal("team.catalog", never.Id);
        Assert.Equal("not synced", never.EntriesDisplay);
        Assert.Equal("never", never.LastSyncDisplay);
        Assert.Equal("Disabled", never.EnabledDisplay);
        Assert.Equal("Neutral", never.EnabledKey);
        Assert.Equal("—", never.StatusDisplay);
        Assert.Null(never.AuthEnv);
    }

    [Fact]
    public void A_synced_source_whose_entries_all_failed_and_whose_last_status_is_an_error_says_so()
    {
        var (_, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));

        var hub = rows[2];
        Assert.Equal("clawhub", hub.Kind);
        Assert.Equal("3 (3 error)", hub.EntriesDisplay);
        Assert.Equal("error: fetch failed: HTTP 503", hub.StatusDisplay);
        Assert.Equal("Enabled", hub.EnabledDisplay);
    }

    [Fact]
    public void An_empty_registry_list_is_no_sources_and_no_error()
    {
        // Live capture: `defenseclaw registry list --json` printed [] on the dev machine: the normal standalone state.
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.empty.json"));

        Assert.Empty(rows);
        Assert.Null(vm.CliErrorMessage);
    }

    [Fact]
    public void A_banner_line_before_the_json_is_skipped()
    {
        var (vm, rows) = ReadSources("some banner the CLI printed first\n" + PayloadFixtures.Read("registry-list.sources.json"));

        Assert.Equal(3, rows.Count);
        Assert.Null(vm.CliErrorMessage);
    }

    [Theory]
    [InlineData("", "printed nothing")]
    [InlineData("   \n", "printed nothing")]
    [InlineData("{\"error\": \"not a list\"}", "JSON array")]
    [InlineData("42", "JSON array")]
    [InlineData("Traceback (most recent call last):", "valid JSON")]
    public void Output_that_is_not_a_list_of_sources_is_an_error_and_not_no_sources(string stdout, string expected)
    {
        var (vm, rows) = ReadSources(stdout);

        Assert.Empty(rows);
        Assert.NotNull(vm.CliErrorMessage);
        Assert.Contains(expected, vm.CliErrorMessage, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ index.json

    // Emitter: registries/cache.py:120-123 (SourceIndex.to_dict), 70-89 (EntryVerdict.to_dict: name, type, status,
    // approved, rejected always; severity, scan_id, target, error, last_scanned_at, source_url, transport, command, url,
    // connector, sha256, findings and args only when non-empty) and 205-210 (save_index sorts the keys). Types are
    // manifest.py:88 ("skill", "mcp"); status is pending, clean, warning, blocked or error (cache.py:48).
    [Fact]
    public async Task A_synced_index_lists_its_entries_with_review_state_severity_and_location()
    {
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));
        WriteIndex("corp-skills", PayloadFixtures.Read("registry-index.synced.json"));

        await vm.LoadEntriesAsync(rows[0]);

        Assert.Null(vm.EntriesMessage);
        Assert.Equal(new[] { "pdf-tools", "docs-mcp", "remote-mcp", "broken-skill", "new-entry" }, vm.Entries.Select(e => e.Name).ToArray());

        var pdf = vm.Entries[0];
        Assert.Equal("skill", pdf.TypeDisplay);
        Assert.Equal("clean", pdf.StatusDisplay);
        Assert.Equal("Ok", pdf.StatusKey);
        Assert.Equal("Approved", pdf.ReviewDisplay);
        Assert.Equal("—", pdf.SeverityDisplay);
        Assert.Equal("https://registry.example.test/skills/pdf-tools.tar.gz", pdf.Location);
        Assert.True(pdf.CanReview);

        var docs = vm.Entries[1];
        Assert.Equal("mcp", docs.TypeDisplay);
        Assert.Equal("warning", docs.StatusDisplay);
        Assert.Equal("Warn", docs.StatusKey);
        Assert.Equal("MEDIUM", docs.SeverityDisplay);
        Assert.Null(docs.Location);

        var remote = vm.Entries[2];
        Assert.Equal("Bad", remote.StatusKey);
        Assert.Equal("Rejected", remote.ReviewDisplay);
        Assert.Equal("HIGH", remote.SeverityDisplay);
        Assert.Equal("https://mcp.example.test/sse", remote.Location);

        Assert.Equal("Bad", vm.Entries[3].StatusKey);
        Assert.Equal("Neutral", vm.Entries[4].StatusKey);
        Assert.Equal("pending", vm.Entries[4].StatusDisplay);
        Assert.Equal("—", vm.Entries[4].ReviewDisplay);
    }

    [Fact]
    public async Task A_source_id_with_a_dot_is_a_valid_cli_id_and_its_cache_is_read()
    {
        // cmd_registry.py:82 accepts [a-z0-9._-]; the panel used to refuse the dot ("not a plain name").
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));
        WriteIndex("team.catalog", PayloadFixtures.Read("registry-index.bare-array.json"));

        await vm.LoadEntriesAsync(rows[1]);

        Assert.Null(vm.EntriesMessage);
        Assert.Equal("pdf-tools", Assert.Single(vm.Entries).Name);
    }

    [Fact]
    public async Task An_index_with_no_verdicts_says_the_last_sync_cached_nothing()
    {
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));
        WriteIndex("corp-skills", PayloadFixtures.Read("registry-index.empty.json"));

        await vm.LoadEntriesAsync(rows[0]);

        Assert.Empty(vm.Entries);
        Assert.Equal("The last sync cached no entries.", vm.EntriesMessage);
    }

    [Fact]
    public async Task A_source_with_no_index_file_says_nothing_is_cached_yet()
    {
        // The CLI never creates index.json until a sync has run (load_index returns a fresh index for a missing file).
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));

        await vm.LoadEntriesAsync(rows[0]);

        Assert.Empty(vm.Entries);
        Assert.Contains("Nothing cached yet", vm.EntriesMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public async Task A_corrupt_index_is_reported_and_not_shown_as_empty(string content)
    {
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));
        WriteIndex("corp-skills", content);

        await vm.LoadEntriesAsync(rows[0]);

        Assert.Empty(vm.Entries);
        Assert.StartsWith("Could not read the cached index", vm.EntriesMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_index_object_with_no_verdicts_member_asks_for_a_sync()
    {
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));
        WriteIndex("corp-skills", "{ \"source_id\": \"corp-skills\", \"schema_version\": 1 }");

        await vm.LoadEntriesAsync(rows[0]);

        Assert.Empty(vm.Entries);
        Assert.Contains("no entry list yet", vm.EntriesMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_with_nulls_unknown_members_unicode_names_and_junk_entries_is_read_without_failing()
    {
        // Not something 0.8.10 writes: tolerance for a later schema_version and for a hand-edited file.
        var rows = new List<RegistryEntryRow>();

        var message = RegistriesPanelViewModel.ParseIndex(PayloadFixtures.Read("registry-index.tolerant.json"), rows);

        Assert.Null(message);
        Assert.Equal(new[] { "nulls-and-unknowns", "naïve-скилл-助手", "type-a-registry-may-add" }, rows.Select(r => r.Name).ToArray());

        var nulls = rows[0];
        Assert.Equal("—", nulls.TypeDisplay);
        Assert.Equal("—", nulls.StatusDisplay);
        Assert.Equal("—", nulls.ReviewDisplay);
        Assert.False(nulls.CanReview);
        Assert.Equal("https://mcp.example.test/fallback", nulls.Location);

        Assert.True(rows[1].CanReview);
        Assert.Equal("Approved", rows[1].ReviewDisplay);

        // approve / reject take --type {skill,mcp} only, so any other type is listed but not reviewable.
        Assert.False(rows[2].CanReview);
        Assert.Equal(@"C:\Users\example\.defenseclaw\registries\corp-skills\agent", rows[2].Location);
    }

    [Fact]
    public void An_index_that_is_a_bare_array_of_entries_is_read_too()
    {
        var rows = new List<RegistryEntryRow>();

        Assert.Null(RegistriesPanelViewModel.ParseIndex(PayloadFixtures.Read("registry-index.bare-array.json"), rows));

        Assert.Equal("pdf-tools", Assert.Single(rows).Name);
    }

    // ------------------------------------------------------------------ what may be read

    [Theory]
    [InlineData("corp-skills")]
    [InlineData("team.catalog")]
    [InlineData("a1")]
    [InlineData("x_y-z.9")]
    [InlineData("Corp.Skills-2")]
    public void The_ids_the_cli_accepts_are_plain_names(string id)
    {
        Assert.True(RegistriesPanelViewModel.IsPlainSourceId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("a.")]
    [InlineData(".hidden")]
    [InlineData("-dash")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..\\evil")]
    [InlineData("a:b")]
    [InlineData("a b")]
    [InlineData("aux.")]
    public void An_id_that_could_leave_the_registries_folder_or_is_not_a_cli_id_is_not_a_plain_name(string id)
    {
        Assert.False(RegistriesPanelViewModel.IsPlainSourceId(id));
    }

    [Fact]
    public void An_id_over_sixty_four_characters_is_not_a_plain_name()
    {
        Assert.True(RegistriesPanelViewModel.IsPlainSourceId(new string('a', 64)));
        Assert.False(RegistriesPanelViewModel.IsPlainSourceId(new string('a', 65)));
    }

    [Fact]
    public async Task A_traversing_id_from_the_cli_json_never_reads_a_file_outside_the_registries_folder()
    {
        // <temp>\registries\..\evil\index.json would be <temp>\evil\index.json.
        var outside = Path.Combine(_temp.Path, "evil");
        _ = Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "index.json"), PayloadFixtures.Read("registry-index.bare-array.json"));
        var vm = new RegistriesPanelViewModel(_services);

        await vm.LoadEntriesAsync(Source(@"..\evil"));

        Assert.Empty(vm.Entries);
        Assert.Contains("not a plain name", vm.EntriesMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_selected_source_clears_the_entries_and_the_message()
    {
        var (vm, rows) = ReadSources(PayloadFixtures.Read("registry-list.sources.json"));
        WriteIndex("corp-skills", PayloadFixtures.Read("registry-index.synced.json"));
        await vm.LoadEntriesAsync(rows[0]);
        Assert.NotEmpty(vm.Entries);

        await vm.LoadEntriesAsync(null);

        Assert.Empty(vm.Entries);
        Assert.Null(vm.EntriesMessage);
        Assert.False(vm.IsEntriesLoading);
    }
}
