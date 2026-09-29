using DefenseClaw.App.Services.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// <c>defenseclaw guardrail status</c> has no JSON form, so the Setup hub reads a fixed-width table. These use
/// tables laid out the way the CLI lays them out: columns as wide as their widest cell, a dashed rule under the
/// header, the bullet glyph in front of the summary lines.
/// </summary>
public class GuardrailStatusParserTests
{
    private static readonly string[] Headers = { "Connector", "Key", "State", "Mode", "Fail", "Rule pack", "HILT", "Scan", "Judge" };

    private static string Table(string[][] rows, string indent = "      ")
    {
        var widths = Headers.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        string Line(IEnumerable<string> cells) => indent + string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i])));

        var lines = new List<string>
        {
            Line(Headers),
            indent + string.Join("  ", widths.Select(w => new string('-', w))),
        };
        lines.AddRange(rows.Select(r => Line(r)));
        return string.Join('\n', lines);
    }

    private static readonly string[][] TwoConnectors =
    {
        new[] { "Claude Code", "claudecode", "enabled", "observe", "closed", "default", "off", "regex_only", "off" },
        new[] { "Codex", "codex", "enabled", "action", "open", "strict", "on", "regex_only", "on" },
    };

    private static string Status(string table, string bullet = "•") =>
        $"  {bullet} enabled:    yes\n{table}\n  ! runtime fail-mode drift: settings.json says open, the gateway says closed\n  {bullet} port:       4000\n";

    [Fact]
    public void A_realistic_status_is_split_into_the_summary_the_roster_and_the_warnings()
    {
        var status = GuardrailStatusParser.Parse(Status(Table(TwoConnectors)));

        Assert.True(status.Enabled);
        Assert.Equal("4000", status.Port);
        Assert.True(status.HasRoster);
        Assert.Equal(2, status.Connectors.Count);
        Assert.Equal("runtime fail-mode drift: settings.json says open, the gateway says closed", Assert.Single(status.Warnings));
    }

    [Fact]
    public void A_connector_name_with_a_space_stays_in_one_column()
    {
        var row = GuardrailStatusParser.Parse(Status(Table(TwoConnectors))).Connectors[0];

        Assert.Equal("Claude Code", row.Name);
        Assert.Equal("claudecode", row.Key);
        Assert.Equal("enabled", row.State);
        Assert.Equal("observe", row.Mode);
        Assert.Equal("closed", row.Fail);
        Assert.Equal("default", row.Column("Rule pack"));
        Assert.Equal("off", row.Column("HILT"));
        Assert.Equal("regex_only", row.Column("Scan"));
        Assert.Equal("off", row.Column("Judge"));
    }

    [Fact]
    public void Every_row_of_the_roster_is_read_and_a_wide_name_moves_the_columns_with_it()
    {
        var rows = new[]
        {
            new[] { "GitHub Copilot CLI", "copilot", "enabled", "observe", "open", "default", "off", "regex_only", "off" },
            new[] { "Gemini CLI", "geminicli", "off", "observe", "open", "default", "off", "regex_only", "off" },
            new[] { "Claude Code", "claudecode", "enabled", "action", "closed", "default", "on", "regex_only", "on" },
        };

        var status = GuardrailStatusParser.Parse(Status(Table(rows)));

        Assert.Equal(new[] { "GitHub Copilot CLI", "Gemini CLI", "Claude Code" }, status.Connectors.Select(c => c.Name).ToArray());
        Assert.Equal(new[] { "copilot", "geminicli", "claudecode" }, status.Connectors.Select(c => c.Key).ToArray());
        Assert.Equal(new[] { "enabled", "off", "enabled" }, status.Connectors.Select(c => c.State).ToArray());
        Assert.Equal("closed", status.Connectors[2].Fail);
        Assert.Equal("on", status.Connectors[2].Column("Judge"));
    }

    [Fact]
    public void The_column_lookup_ignores_case_and_answers_empty_for_a_column_that_is_not_there()
    {
        var row = GuardrailStatusParser.Parse(Status(Table(TwoConnectors))).Connectors[1];

        Assert.Equal("strict", row.Column("RULE PACK"));
        Assert.Equal("on", row.Column("hilt"));
        Assert.Equal(string.Empty, row.Column("No such column"));
    }

    [Fact]
    public void A_row_is_summarised_in_a_sentence_for_screen_readers()
    {
        var rows = GuardrailStatusParser.Parse(Status(Table(TwoConnectors))).Connectors;

        Assert.Equal("Claude Code: enabled, mode observe, fail-closed", rows[0].ToString());
        Assert.Equal("Codex: enabled, mode action, fail-open", rows[1].ToString());
    }

    [Fact]
    public void A_value_in_the_last_column_that_is_wider_than_its_header_is_not_cut_off()
    {
        const string text =
            "  • enabled:    yes\n" +
            "      Connector    Key         State    Mode     Fail    Scan        Judge\n" +
            "      -----------  ----------  -------  -------  ------  ----------  -----\n" +
            "      Claude Code  claudecode  enabled  observe  closed  regex_only  on (gpt-4o-mini)\n";

        var row = Assert.Single(GuardrailStatusParser.Parse(text).Connectors);

        Assert.Equal("Claude Code", row.Name);
        Assert.Equal("regex_only", row.Column("Scan"));
        Assert.Equal("on (gpt-4o-mini)", row.Column("Judge"));
    }

    [Theory]
    [InlineData("•")]
    [InlineData("â€¢")]
    [InlineData("*")]
    [InlineData("")]
    public void The_bullet_glyph_is_ignored_so_a_runner_that_decodes_it_wrongly_still_parses(string bullet)
    {
        var status = GuardrailStatusParser.Parse(Status(Table(TwoConnectors), bullet));

        Assert.True(status.Enabled);
        Assert.Equal("4000", status.Port);
        Assert.Equal(2, status.Connectors.Count);
        Assert.Equal("Claude Code", status.Connectors[0].Name);
    }

    [Fact]
    public void Windows_line_endings_parse_the_same()
    {
        var status = GuardrailStatusParser.Parse(Status(Table(TwoConnectors)).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(2, status.Connectors.Count);
        Assert.Equal("Claude Code", status.Connectors[0].Name);
        Assert.Equal("4000", status.Port);
        Assert.DoesNotContain("\r", status.Warnings[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("enabled: yes", true)]
    [InlineData("enabled:    true", true)]
    [InlineData("Enabled: ON", true)]
    [InlineData("enabled: no", false)]
    [InlineData("enabled: false", false)]
    [InlineData("enabled: off", false)]
    public void The_enabled_line_is_read_in_every_spelling(string line, bool expected)
    {
        Assert.Equal(expected, GuardrailStatusParser.Parse("  • " + line + "\n").Enabled);
    }

    [Fact]
    public void Without_a_table_the_status_has_no_roster_and_keeps_its_text_verbatim()
    {
        const string text = "  • enabled:    no\n  • port:       4000\n\nGuardrail is disabled.\n";

        var status = GuardrailStatusParser.Parse(text);

        Assert.False(status.Enabled);
        Assert.False(status.HasRoster);
        Assert.Empty(status.Connectors);
        Assert.Equal(text.Trim(), status.Raw);
    }

    [Fact]
    public void A_missing_enabled_line_is_unknown_not_false()
    {
        var status = GuardrailStatusParser.Parse("something else entirely\n");

        Assert.Null(status.Enabled);
        Assert.Equal(string.Empty, status.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("      -----------  ----------\n")]
    [InlineData("header\n      --  --\n")]
    [InlineData("      \n      -----------  ----------  -------\n")]
    [InlineData("!\n")]
    [InlineData("port:\nenabled:\n")]
    public void Malformed_or_partial_output_never_throws(string text)
    {
        var status = GuardrailStatusParser.Parse(text);

        Assert.NotNull(status);
        Assert.False(status.HasRoster);
    }

    [Fact]
    public void The_table_ends_at_the_first_blank_line_or_unindented_text()
    {
        var afterBlankLine = "  • enabled:    yes\n" + Table(TwoConnectors) + "\n\n      Not a row  because  a blank line came first\n";
        var afterUnindented = "  • enabled:    yes\n" + Table(TwoConnectors) + "\nBack at column zero  with  two  spaces\n";

        Assert.Equal(2, GuardrailStatusParser.Parse(afterBlankLine).Connectors.Count);
        Assert.Equal(2, GuardrailStatusParser.Parse(afterUnindented).Connectors.Count);
    }

    [Fact]
    public void Null_text_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => GuardrailStatusParser.Parse(null!));
    }
}
