using System.Diagnostics;
using System.Text;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The Audit panel's row model and paging: <c>structured_json</c> is pretty-printed on demand, and the
/// row list stops growing at <see cref="AuditPanelViewModel.MaxRows"/> with a notice instead of
/// accumulating for as long as the operator clicks "Load more".
/// </summary>
public sealed class AuditPanelTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TempDirectory _temp = new();
    private AppServices? _services;

    public AuditPanelTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        _services?.Dispose();
        SqlitePools.Release(_temp.Path);
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ AuditRow: lazy structured_json

    private static AuditEvent Event(int n, string? structuredJson) => new()
    {
        Id = "evt-" + n,
        Timestamp = DateTimeOffset.UtcNow.AddSeconds(-n),
        RawTimestamp = DateTimeOffset.UtcNow.AddSeconds(-n).ToString("O"),
        Action = "hook_decision",
        Severity = "INFO",
        Connector = "claudecode",
        StructuredJsonRaw = structuredJson,
    };

    /// <summary>A payload of roughly <paramref name="kilobytes"/> KB: the "often several kilobytes" the column holds.</summary>
    private static string Payload(int kilobytes)
    {
        var text = new StringBuilder("{\"decision\":\"allow\",\"detail\":[");
        for (var i = 0; text.Length < kilobytes * 1024; i++)
        {
            _ = text.Append(i == 0 ? "" : ",").Append("{\"rule\":\"r").Append(i).Append("\",\"score\":").Append(i % 7).Append(",\"note\":\"synthetic finding text\"}");
        }

        return text.Append("]}").ToString();
    }

    [Fact]
    public void Building_a_page_of_rows_pretty_prints_nothing_until_a_row_is_opened()
    {
        var payload = Payload(20);
        var events = Enumerable.Range(0, 100).Select(n => Event(n, payload)).ToArray();

        var built = Stopwatch.StartNew();
        var rows = events.Select(e => new AuditRow(e)).ToArray();
        built.Stop();

        Assert.All(rows, row => Assert.False(row.IsStructuredJsonMaterialized));

        var opened = rows[42].StructuredJson;

        Assert.True(rows[42].IsStructuredJsonMaterialized);
        Assert.Single(rows, row => row.IsStructuredJsonMaterialized);
        Assert.Contains("\"decision\": \"allow\"", opened, StringComparison.Ordinal);
        Assert.Contains("\n", opened, StringComparison.Ordinal);

        var everything = Stopwatch.StartNew();
        foreach (var row in rows)
        {
            _ = row.StructuredJson;
        }

        everything.Stop();

        _output.WriteLine(
            $"100 rows x 20 KB structured_json: constructing the page {built.Elapsed.TotalMilliseconds:N1} ms " +
            $"(0 payloads parsed); pretty-printing all 100 on demand {everything.Elapsed.TotalMilliseconds:N1} ms " +
            "(the work the constructor used to do for every row, opened or not).");
    }

    [Fact]
    public void The_pretty_print_is_computed_once_per_row()
    {
        var row = new AuditRow(Event(1, "{\"a\":1}"));

        var first = row.StructuredJson;
        var second = row.StructuredJson;

        Assert.Same(first, second);
        Assert.Equal("{\r\n  \"a\": 1\r\n}".ReplaceLineEndings(), first.ReplaceLineEndings());
    }

    [Fact]
    public void A_row_with_no_payload_says_so_and_a_malformed_one_shows_the_raw_text()
    {
        Assert.Equal("(no structured payload on this row)", new AuditRow(Event(1, null)).StructuredJson);
        Assert.Equal("(no structured payload on this row)", new AuditRow(Event(2, "   ")).StructuredJson);
        Assert.Equal("{not json", new AuditRow(Event(3, "{not json")).StructuredJson);
    }

    [Fact]
    public void Everything_but_structured_json_is_still_available_without_reading_it()
    {
        var row = new AuditRow(Event(1, Payload(4)));

        Assert.Equal("hook_decision", row.Action);
        Assert.Equal("claudecode", row.Connector);
        Assert.Contains(row.Fields, field => field.Name == "action");
        Assert.False(row.IsStructuredJsonMaterialized);
    }

    // ------------------------------------------------------------------ AuditPanelViewModel: the row cap

    private AuditPanelViewModel PanelOver(int rows, Func<int, string?>? connectorFor = null)
    {
        var dataDirectory = _temp.Path;
        AuditTestDatabase.Create(Path.Combine(dataDirectory, "audit.db"), rows, connectorFor);
        _services = TestServices.Create(_temp);
        return new AuditPanelViewModel(_services) { ActionableOnly = false };
    }

    private static async Task LoadPagesAsync(AuditPanelViewModel panel, int pages)
    {
        for (var i = 0; i < pages; i++)
        {
            await panel.LoadMoreCommand.ExecuteAsync(null);
        }
    }

    [Fact]
    public void The_cap_is_twenty_pages()
    {
        Assert.Equal(2000, AuditPanelViewModel.MaxRows);
        Assert.Equal(0, AuditPanelViewModel.MaxRows % AuditPanelViewModel.PageSize);
    }

    [Fact]
    public async Task Paging_stops_at_the_cap_with_a_notice_and_no_load_more()
    {
        var panel = PanelOver(2250);

        await panel.InitializeAsync();
        Assert.Equal(AuditPanelViewModel.PageSize, panel.Rows.Count);
        Assert.True(panel.HasMore);
        Assert.False(panel.IsRowCapReached);

        await LoadPagesAsync(panel, 19);

        Assert.Equal(AuditPanelViewModel.MaxRows, panel.Rows.Count);
        Assert.True(panel.IsRowCapReached);
        Assert.False(panel.HasMore);
        Assert.Contains("newest 2,000", panel.RowCapNotice, StringComparison.Ordinal);

        // The newest rows are the ones kept: row 0 is the newest event, and the last one is the 2,000th.
        Assert.Equal("evt-000000", panel.Rows[0].Id);
        Assert.Equal("evt-001999", panel.Rows[^1].Id);
        Assert.Equal(panel.Rows.Count, panel.Rows.Select(r => r.Id).Distinct().Count());

        // Asking for more anyway (the button is hidden, but the command is not disabled) does nothing.
        await LoadPagesAsync(panel, 3);
        Assert.Equal(AuditPanelViewModel.MaxRows, panel.Rows.Count);
        Assert.False(panel.HasMore);
        Assert.True(panel.IsRowCapReached);

        _output.WriteLine($"2,250 matching events: list holds {panel.Rows.Count:N0} (was unbounded), summary '{panel.ResultSummary}'.");
    }

    [Fact]
    public async Task A_result_of_exactly_the_cap_is_complete_so_there_is_no_notice()
    {
        var panel = PanelOver(AuditPanelViewModel.MaxRows);

        await panel.InitializeAsync();
        await LoadPagesAsync(panel, 19);

        Assert.Equal(AuditPanelViewModel.MaxRows, panel.Rows.Count);
        Assert.False(panel.HasMore);
        Assert.False(panel.IsRowCapReached);
    }

    [Fact]
    public async Task A_result_under_the_cap_pages_to_its_end_with_no_notice()
    {
        var panel = PanelOver(250);

        await panel.InitializeAsync();
        await LoadPagesAsync(panel, 2);

        Assert.Equal(250, panel.Rows.Count);
        Assert.False(panel.HasMore);
        Assert.False(panel.IsRowCapReached);
    }

    /// <summary>
    /// What a failed assertion about the paging should say. A load never throws: a read it cannot do (audit.db locked or unreadable for a
    /// moment) is written to <c>StatusNote</c> and the list stays as it was, so a load that quietly did nothing looks like wrong numbers
    /// unless the note is in the message.
    /// </summary>
    private static string State(AuditPanelViewModel panel) =>
        $"rows={panel.Rows.Count}, capReached={panel.IsRowCapReached}, hasMore={panel.HasMore}, loading={panel.IsLoading}, note='{panel.StatusNote}'";

    [Fact]
    public async Task A_fresh_load_after_hitting_the_cap_clears_the_notice_and_restores_load_more()
    {
        var panel = PanelOver(2250);
        await panel.InitializeAsync();
        await LoadPagesAsync(panel, 19);
        Assert.True(panel.IsRowCapReached, "the twentieth page should have reached the cap: " + State(panel));

        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.True(panel.Rows.Count == AuditPanelViewModel.PageSize, "a fresh load should hold one page: " + State(panel));
        Assert.False(panel.IsRowCapReached, "a fresh load should clear the cap notice: " + State(panel));
        Assert.True(panel.HasMore, "a fresh load should offer Load more again: " + State(panel));
    }

    [Fact]
    public async Task The_cap_also_holds_when_a_refinement_thins_every_page_so_it_lands_mid_page()
    {
        // Platform-only rows are a refinement over the keyset stream: 100 raw rows per page, but only
        // the ones with no connector are kept - 60 per page here, so 2,000 falls part-way through page 34.
        var panel = PanelOver(5000, connectorFor: i => i % 5 < 3 ? null : "claudecode");
        panel.SelectedConnector = ConnectorOption.PlatformOnlyOption;

        await panel.InitializeAsync();
        await LoadPagesAsync(panel, 40);

        Assert.Equal(AuditPanelViewModel.MaxRows, panel.Rows.Count);
        Assert.All(panel.Rows, row => Assert.True(row.IsPlatform));
        Assert.True(panel.IsRowCapReached);
        Assert.False(panel.HasMore);
    }
}
