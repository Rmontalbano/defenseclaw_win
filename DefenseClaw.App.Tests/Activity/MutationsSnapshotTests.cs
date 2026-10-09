using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Activity;

/// <summary>
/// CUST-284 on the Mutations tab. A reload over a database that did not change decodes no row and leaves the list, the selection and the
/// connector list alone; a reload that finds the same changes after the database moved for another reason does the same; and a change with
/// a before / after / diff / record too large to load is listed with the reason, counted, and never mistaken for "recorded nothing" or for
/// an empty history. Synthetic rows from the real DDL; no WPF controls are built.
/// </summary>
public sealed class MutationsSnapshotTests : IDisposable
{
    /// <summary>Comfortably over the reader's 256 KiB limit.</summary>
    private const int OverLimit = 270_000;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private MutationDatabase Database() => new(_temp.File("audit.db"));

    private static string Big(string open = "{\"pad\":\"") => open + new string('x', OverLimit) + "\"}";

    // ------------------------------------------------------------------ a reload that finds nothing new

    [Fact]
    public async Task A_second_load_of_an_unchanged_database_decodes_no_row_and_leaves_the_list_alone()
    {
        var database = Database().Activity("a1", before: "{\"a\":1}", after: "{\"a\":2}").Change("c1", connector: "claudecode").Change("c2");
        using var probe = new AuditChangeProbe(database.Path);
        var reader = new MutationReader(database.Path, probe);
        var model = new ActivityMutationsViewModel(reader, TestTimeouts.Ceiling);

        await model.LoadAsync();
        var rows = model.Rows.ToArray();
        model.SelectedRow = rows[1];
        var decoded = reader.RowsDecoded;
        Assert.Equal(3, decoded);

        await model.LoadAsync();
        await model.LoadAsync();

        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.Equal(2, reader.UnchangedReads);
        Assert.True(rows.SequenceEqual(model.Rows));
        Assert.Same(rows[1], model.SelectedRow);
        Assert.Equal(new[] { ActivityMutationsViewModel.AllConnectors, "claudecode" }, model.Connectors);
        Assert.False(model.IsLoading);
        Assert.True(model.HasLoaded);
        Assert.Equal("3 changes", model.Summary);
    }

    [Fact]
    public async Task A_database_change_that_adds_no_change_reads_again_and_still_rebuilds_nothing()
    {
        var database = Database().Activity("a1").Change("c1");
        using var probe = new AuditChangeProbe(database.Path);
        var reader = new MutationReader(database.Path, probe);
        var model = new ActivityMutationsViewModel(reader, TestTimeouts.Ceiling);
        await model.LoadAsync();
        var rows = model.Rows.ToArray();
        var decoded = reader.RowsDecoded;

        // Telemetry, which the history does not list: the database moved, the answer did not.
        _ = database.Change("noise", "span", "telemetry.ingest");
        await model.LoadAsync();

        Assert.True(
            reader.RowsDecoded > decoded,
            $"the history had to be read again (note: '{model.StatusNote}'; reads {reader.ReadCount}, answered from memory {reader.UnchangedReads}; decoded {decoded} -> {reader.RowsDecoded})");
        Assert.True(rows.SequenceEqual(model.Rows));
    }

    [Fact]
    public async Task A_change_that_arrives_is_listed_and_the_next_reload_is_quiet_again()
    {
        var database = Database().Activity("a1");
        using var probe = new AuditChangeProbe(database.Path);
        var reader = new MutationReader(database.Path, probe);
        var model = new ActivityMutationsViewModel(reader, TestTimeouts.Ceiling);
        await model.LoadAsync();
        Assert.Single(model.Rows);

        _ = database.Activity("a2");
        await model.LoadAsync();
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("2 changes", model.Summary);

        var decoded = reader.RowsDecoded;
        var rows = model.Rows.ToArray();
        await model.LoadAsync();
        Assert.Equal(decoded, reader.RowsDecoded);
        Assert.True(rows.SequenceEqual(model.Rows));
    }

    [Fact]
    public async Task A_failed_read_is_a_note_and_the_next_good_one_clears_it_without_rebuilding()
    {
        var database = Database().Activity("a1");
        using var probe = new AuditChangeProbe(database.Path);
        var reader = new MutationReader(database.Path, probe);
        var model = new ActivityMutationsViewModel(reader, TestTimeouts.Ceiling);
        await model.LoadAsync();
        var rows = model.Rows.ToArray();

        model.StatusNote = "The audit database could not be read: simulated";
        await model.LoadAsync();

        Assert.Equal(string.Empty, model.StatusNote);
        Assert.True(rows.SequenceEqual(model.Rows));
    }

    // ------------------------------------------------------------------ too large to load

    [Fact]
    public async Task A_change_with_a_before_image_too_large_to_show_is_listed_with_the_reason_and_counted()
    {
        var model = new ActivityMutationsViewModel(new MutationReader(Database().Activity("big", reason: "tighten", before: Big(), after: "{\"mode\":\"action\"}").Path), TestTimeouts.Ceiling);

        await model.LoadAsync();

        var row = Assert.Single(model.Rows);
        Assert.False(model.IsEmpty);
        Assert.Equal("1 change too large to display.", model.StatusNote);
        Assert.True(row.IsOversized);
        Assert.Equal(MutationDiffMode.BeforeAfter, row.Mode);
        Assert.True(row.ShowBeforeAfter);
        Assert.False(row.ShowNothing);

        // The before / after wells are narrow and scroll sideways rather than wrap: the placeholder is broken into lines that fit them.
        Assert.Equal("(not shown:\nbefore_json is\n270,010 bytes,\nover the 256 KB\nlimit)", row.BeforeText);
        Assert.Contains("\"mode\": \"action\"", row.AfterText, StringComparison.Ordinal);
        Assert.Contains(row.Fields, field => field.Key == "Unavailable" && field.Value.Contains("before_json is 270,010 bytes", StringComparison.Ordinal));
        Assert.Contains(row.Fields, field => field.Key == "Reason" && field.Value == "tighten");
    }

    [Fact]
    public async Task A_history_of_only_changes_too_large_to_load_is_not_the_empty_state()
    {
        var database = Database().Activity("a", after: Big()).Activity("b", diff: Big("[\"")).Activity("c", before: Big());
        var model = new ActivityMutationsViewModel(new MutationReader(database.Path), TestTimeouts.Ceiling);

        await model.LoadAsync();

        Assert.Equal(3, model.Rows.Count);
        Assert.False(model.IsEmpty);
        Assert.All(model.Rows, row => Assert.True(row.IsOversized));
        Assert.All(model.Rows, row => Assert.False(row.ShowNothing));
        Assert.Equal("3 changes too large to display.", model.StatusNote);
        Assert.Equal("3 changes", model.Summary);
    }

    [Fact]
    public async Task A_diff_too_large_to_load_says_so_in_place_of_the_diff()
    {
        var model = new ActivityMutationsViewModel(new MutationReader(Database().Activity("d", diff: Big("[\"")).Path), TestTimeouts.Ceiling);
        await model.LoadAsync();

        var row = Assert.Single(model.Rows);

        Assert.Equal(MutationDiffMode.Unified, row.Mode);

        // One diff line per line of the placeholder, so the box (which scrolls sideways) shows all of it.
        Assert.Equal(new[] { "(not shown: diff_json is 270,004 bytes,", "over the 256 KB limit)" }, row.UnifiedLines.Select(line => line.Text));
        Assert.All(row.UnifiedLines, line => Assert.Equal(DiffLineKind.Context, line.Kind));
    }

    [Fact]
    public async Task An_audit_rows_record_too_large_to_load_takes_the_recorded_section()
    {
        var model = new ActivityMutationsViewModel(new MutationReader(Database().Change("c", structuredJson: Big()).Path), TestTimeouts.Ceiling);
        await model.LoadAsync();

        var row = Assert.Single(model.Rows);

        Assert.Equal(MutationDiffMode.Recorded, row.Mode);
        Assert.True(row.ShowRecorded);
        Assert.Equal("(not shown: structured_json is\n270,010 bytes, over the 256 KB limit)", row.RecordedText);
        Assert.Equal("1 change too large to display.", model.StatusNote);
    }

    [Fact]
    public async Task The_note_joins_the_older_changes_note_and_a_change_that_fits_has_no_reason()
    {
        // The oversized change is the newest (each row added is a minute older than the one before), then more than a window's worth.
        var database = Database().Activity("big", before: Big());
        for (var i = 0; i < MutationReader.DefaultLimit + 1; i++)
        {
            _ = database.Change("c" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
        }

        var model = new ActivityMutationsViewModel(new MutationReader(database.Path), TestTimeouts.Ceiling);

        await model.LoadAsync();

        Assert.Equal(MutationReader.DefaultLimit, model.Rows.Count);
        Assert.StartsWith("Showing the newest 500 changes; older ones are in the Audit panel.", model.StatusNote, StringComparison.Ordinal);
        Assert.EndsWith("1 change too large to display.", model.StatusNote, StringComparison.Ordinal);
        Assert.All(model.Rows.Where(r => r.Id != "big"), row => Assert.Equal(string.Empty, row.OversizedNotice));
    }
}
