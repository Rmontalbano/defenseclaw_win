using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The severity breakdown of a scan in the details of a skill, an MCP server and a plugin (CUST-276): the TUI's <c>_parse_severity_counts</c> and
/// <c>_format_severity_breakdown</c> (E4i), shown as the <c>Findings</c> line the Skills details have always had. 0.8.10 prints the counts for skills only
/// (<c>cmd_skill.py</c> <c>_scan_payload_from_latest</c>); an MCP server there has a bare <c>severity</c> and a plugin a scan without counts, so for them the
/// line appears when a CLI sends the counts and is simply absent when it does not. The payloads are synthetic.
/// </summary>
public sealed class SeverityBreakdownTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DefenseClaw.App.Services.AppServices _services;

    public SeverityBreakdownTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static string? Breakdown(string scanJson)
    {
        using var document = JsonDocument.Parse(scanJson);
        return GovernJson.SeverityBreakdown(document.RootElement);
    }

    private static IReadOnlyList<(string Bucket, int Count)> Counts(string scanJson)
    {
        using var document = JsonDocument.Parse(scanJson);
        return GovernJson.SeverityCounts(document.RootElement);
    }

    // ------------------------------------------------------------------ the reader

    [Fact]
    public void The_breakdown_lists_the_buckets_with_findings_worst_first()
    {
        Assert.Equal(
            "critical 1 · high 2 · low 3",
            Breakdown("""{"severity_counts": {"info": 0, "low": 3, "medium": 0, "high": 2, "critical": 1}}"""));
        Assert.Equal("high 2 · medium 1", Breakdown("""{"severity_counts": {"critical": 0, "high": 2, "medium": 1, "low": 0, "info": 0}}"""));
        Assert.Equal("info 4", Breakdown("""{"severity_counts": {"info": 4}}"""));
    }

    [Fact]
    public void The_reader_folds_case_adds_spellings_cuts_fractions_and_takes_numeric_text_like_the_tui()
    {
        Assert.Equal(
            new[] { ("critical", 1), ("high", 3), ("medium", 2), ("low", 7) },
            Counts("""{"severity_counts": {"CRITICAL": 1, "High": 2, "high": 1, "medium": 2.9, "low": "7", " info ": 0}}""").ToArray());
    }

    [Theory]
    [InlineData("""{"severity_counts": {}}""")]
    [InlineData("""{"severity_counts": {"critical": 0, "high": 0, "medium": 0, "low": 0, "info": 0}}""")]
    [InlineData("""{"severity_counts": {"critical": -2, "high": null, "medium": "many", "low": true, "info": [1]}}""")]
    [InlineData("""{"severity_counts": {"unknown": 2, "warning": 5, "": 1}}""")]
    [InlineData("""{"severity_counts": [1, 2, 3]}""")]
    [InlineData("""{"severity_counts": "high 2"}""")]
    [InlineData("""{"severity_counts": null}""")]
    [InlineData("""{"max_severity": "HIGH", "total_findings": 3}""")]
    [InlineData("{}")]
    public void No_buckets_with_findings_is_no_breakdown(string scan)
    {
        Assert.Null(Breakdown(scan));
        Assert.Empty(Counts(scan));
    }

    [Fact]
    public void A_scan_that_is_not_an_object_has_no_breakdown_and_does_not_throw()
    {
        using var array = JsonDocument.Parse("[1, 2]");
        using var text = JsonDocument.Parse("\"HIGH\"");

        Assert.Null(GovernJson.SeverityBreakdown(array.RootElement));
        Assert.Null(GovernJson.SeverityBreakdown(text.RootElement));
    }

    [Fact]
    public void A_count_beyond_the_range_of_a_number_is_held_at_the_largest_one()
    {
        Assert.Equal(new[] { ("high", int.MaxValue) }, Counts("""{"severity_counts": {"high": 99999999999}}""").ToArray());
        Assert.Equal(new[] { ("high", int.MaxValue) }, Counts("""{"severity_counts": {"high": 1e30}}""").ToArray());
    }

    // ------------------------------------------------------------------ the details of each panel

    [Fact]
    public void A_skills_details_keep_the_breakdown_line_they_always_had()
    {
        var rows = new SkillsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json"));

        var pdf = Assert.Single(rows, r => r.Name == "pdf-tools");
        Assert.Contains(pdf.Fields, f => f is { Label: "Findings", Value: "high 2 · medium 1" });

        // A clean scan (every bucket zero) has no line.
        Assert.DoesNotContain(Assert.Single(rows, r => r.Name == "notes-helper").Fields, f => f.Label == "Findings");
    }

    [Fact]
    public void An_mcp_servers_details_show_the_breakdown_target_and_findings_when_the_cli_sends_a_scan()
    {
        var rows = new McpsPanelViewModel(_services).ParseRows("""
            [
              {
                "name": "docs-search", "transport": "stdio", "command": "npx",
                "scan": {
                  "target": "C:\\Users\\example\\mcp\\docs-search", "clean": false, "max_severity": "HIGH", "total_findings": 4,
                  "severity_counts": {"critical": 0, "high": 3, "medium": 1, "low": 0, "info": 0}
                }
              },
              {"name": "remote-tools", "transport": "sse", "url": "https://mcp.example.test/v1", "severity": "MEDIUM"}
            ]
            """);

        var docs = Assert.Single(rows, r => r.Name == "docs-search");
        Assert.Equal("HIGH · 4 findings", docs.ScanLabel);
        Assert.Contains(docs.Fields, f => f is { Label: "Scan", Value: "HIGH · 4 findings" });
        Assert.Contains(docs.Fields, f => f is { Label: "Scan target", Value: @"C:\Users\example\mcp\docs-search" });
        Assert.Contains(docs.Fields, f => f is { Label: "Findings", Value: "high 3 · medium 1" });

        // 0.8.10's shape: a bare severity. The Scan line says it; there is no breakdown to show, and none is made up.
        var remote = Assert.Single(rows, r => r.Name == "remote-tools");
        Assert.Equal("MEDIUM", remote.ScanLabel);
        Assert.Contains(remote.Fields, f => f is { Label: "Scan", Value: "MEDIUM" });
        Assert.DoesNotContain(remote.Fields, f => f.Label is "Findings" or "Scan target");
    }

    [Fact]
    public void A_plugins_details_show_the_breakdown_when_the_scan_carries_one_and_say_nothing_extra_when_it_does_not()
    {
        var rows = new PluginsPanelViewModel(_services).ParseRows("""
            [
              {
                "id": "code-review", "name": "Code Review", "connector": "claudecode", "enabled": true,
                "scan": {
                  "target": "C:\\Users\\example\\plugins\\code-review", "clean": false, "max_severity": "CRITICAL", "total_findings": 6,
                  "severity_counts": {"CRITICAL": 1, "HIGH": 2, "LOW": 3}
                }
              },
              {
                "id": "release-notes", "name": "Release Notes", "connector": "claudecode", "enabled": true,
                "scan": {"target": "C:\\Users\\example\\plugins\\release-notes", "clean": false, "max_severity": "MEDIUM", "total_findings": 2}
              }
            ]
            """);

        var review = Assert.Single(rows, r => r.Name == "code-review");
        Assert.Equal("CRITICAL · 6 findings", review.ScanLabel);
        Assert.Contains(review.Fields, f => f is { Label: "Findings", Value: "critical 1 · high 2 · low 3" });

        // 0.8.10's plugin scan has the worst severity and the total only.
        var notes = Assert.Single(rows, r => r.Name == "release-notes");
        Assert.Equal("MEDIUM · 2 findings", notes.ScanLabel);
        Assert.Contains(notes.Fields, f => f is { Label: "Scan", Value: "MEDIUM · 2 findings" });
        Assert.DoesNotContain(notes.Fields, f => f.Label == "Findings");
    }

    [Fact]
    public void The_existing_fixtures_of_the_three_panels_read_as_they_did()
    {
        // No fixture of the CLI's 0.8.10 mcp or plugin lists carries counts, so no row gets a Findings line from them.
        var mcps = new McpsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("mcp-list.single-connector.json"));
        var plugins = new PluginsPanelViewModel(_services).ParseRows(PayloadFixtures.Read("plugin-list.claudecode.json"));

        Assert.NotEmpty(mcps);
        Assert.NotEmpty(plugins);
        Assert.All(mcps.Concat(plugins), row => Assert.DoesNotContain(row.Fields, f => f.Label == "Findings"));
    }
}
