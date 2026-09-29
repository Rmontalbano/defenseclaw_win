using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Payloads;

/// <summary>
/// <c>defenseclaw aibom scan --json</c>. It could not be run for this work (a live run writes an audit.db record and
/// posts to the gateway), so its shape comes from the code that prints it, in the <c>defenseclaw</c> 0.8.10 wheel:
/// commands/cmd_aibom.py:111-118 (a bare object for one connector, a list for several, <c>[]</c> when none is active),
/// inventory/claw_inventory.py:2091-2177 (<c>_build_aibom_from_filesystem</c>, the non-OpenClaw path: version,
/// generated_at, connector, claw_home, claw_mode, live, seven category arrays, errors, limitations, connector_* paths,
/// connector_config, summary), 724-747 (<c>_build_summary</c>: <c>total_items</c>, a <c>{count, …}</c> object per
/// category, <c>errors</c> and <c>limitations</c> as numbers), 221-327 (policy enrichment adds <c>policy_*</c> and
/// <c>scan_*</c> objects to the summary) and provenance.py:105-123 (a <c>provenance</c> object on the envelope and each item).
/// </summary>
public sealed class AiBomPayloadTests
{
    [Fact]
    public void One_connectors_bom_reads_each_categorys_count_from_the_summary()
    {
        var rows = InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.claudecode.json"), requestedConnector: null);

        var row = Assert.Single(rows);
        Assert.Equal("claudecode", row.Connector);
        Assert.Equal("skills 2 · plugins 1 · mcp 2 · agents 1 · tools 1 · model providers 1 · memory 1", row.Summary);
    }

    [Fact]
    public void The_summary_is_the_components_and_not_the_paths_or_the_policy_counters()
    {
        // The summary also holds total_items, policy_skills{…}, scan_skills{…}, and the envelope holds connector_skill_dirs,
        // connector_config_files, …: none of those is a component count.
        var summary = Assert.Single(InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.claudecode.json"), null)).Summary;

        Assert.DoesNotContain("total items", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("policy", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("dirs", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("config", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_connectors_give_one_row_each_and_failed_or_unsupported_categories_are_called_out()
    {
        var rows = InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.multi-connector.json"), null);

        Assert.Equal(2, rows.Count);
        Assert.Equal("claudecode", rows[0].Connector);
        Assert.Equal("skills 1 · plugins 0 · mcp 1 · agents 0 · tools 0 · model providers 0 · memory 0 · limitations 4", rows[0].Summary);
        Assert.Equal("codex", rows[1].Connector);
        Assert.Equal("skills 0 · plugins 0 · mcp 0 · agents 0 · tools 0 · model providers 0 · memory 0 · errors 1", rows[1].Summary);
    }

    [Fact]
    public void A_scan_of_no_connector_prints_an_empty_list_and_yields_no_rows()
    {
        // cmd_aibom.py:105-118: with no active connector `invs` is empty and json.dumps([]) is printed.
        Assert.Empty(InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.no-connectors.json"), null));
    }

    [Fact]
    public void The_bom_json_may_follow_a_banner_line()
    {
        var rows = InventoryPanelViewModel.ParseBom("Scanning live claudecode environment …\n" + PayloadFixtures.Read("aibom-scan.claudecode.json"), null);

        Assert.Single(rows);
    }

    [Fact]
    public void An_inventory_without_a_summary_counts_only_the_category_arrays()
    {
        // Not something 0.8.10 prints (it always adds a summary): the fallback, which must not count path lists such as
        // connector_skill_dirs or an unknown array.
        var rows = InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.tolerant.json"), requestedConnector: "claudecode");

        Assert.Equal(4, rows.Count);
        Assert.Equal(("no-summary", "skills 2 · plugins 0 · mcp 1"), (rows[0].Connector, rows[0].Summary));
    }

    [Fact]
    public void A_summary_of_plain_numbers_is_read_and_one_with_nothing_countable_says_so()
    {
        var rows = InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.tolerant.json"), requestedConnector: "claudecode");

        Assert.Equal(("flat-numeric-summary", "skills 4 · mcp 2 · agents 0 · errors 2"), (rows[1].Connector, rows[1].Summary));

        Assert.Equal("nothing-countable", rows[2].Connector);
        Assert.Contains("could not be read", rows[2].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_object_with_no_connector_name_takes_the_requested_one_and_a_non_object_entry_is_skipped()
    {
        var rows = InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.tolerant.json"), requestedConnector: "claudecode");
        Assert.Equal(("claudecode", "skills 1"), (rows[3].Connector, rows[3].Summary));

        var unscoped = InventoryPanelViewModel.ParseBom(PayloadFixtures.Read("aibom-scan.tolerant.json"), requestedConnector: null);
        Assert.Equal("active connector", unscoped[3].Connector);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("{ not json")]
    public void Output_that_is_not_json_is_an_error_the_panel_reports(string stdout)
    {
        Assert.ThrowsAny<JsonException>(() => InventoryPanelViewModel.ParseBom(stdout, null));
    }
}
