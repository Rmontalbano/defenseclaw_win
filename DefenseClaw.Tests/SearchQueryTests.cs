using DefenseClaw.Core.Audit;

namespace DefenseClaw.Tests;

/// <summary>
/// The one search-token parser (CUST-261) that the Alerts, Audit and Logs panels share: which words are <c>field:value</c> tokens, how quoting keeps a
/// value together, that anything it does not recognise stays free text exactly as typed, and how a parsed search matches a row.
/// </summary>
public sealed class SearchQueryTests
{
    /// <summary>A parsed search as one line: <c>connector=codex|target=my skill ; free text</c>.</summary>
    private static string Render(SearchQuery query) =>
        string.Join('|', query.Tokens.Select(t => SearchQuery.NameOf(t.Field) + "=" + t.Value)) + " ; " + query.FreeText;

    // ------------------------------------------------------------------ the parser table

    public static TheoryData<string, string> Table() => new()
    {
        // The tokens of the issue, and the TUI's own.
        { "connector:claudecode", "connector=claudecode ; " },
        { "severity:high", "severity=high ; " },
        { "run:933f8ff9", "run=933f8ff9 ; " },
        { "id:evt-1", "id=evt-1 ; " },
        { "actor:audit_logger", "actor=audit_logger ; " },
        { "type:skill", "type=skill ; " },
        { "target:/skills/a", "target=/skills/a ; " },
        { "action:block", "action=block ; " },
        { "details:would_block", "details=would_block ; " },

        // The ids a finding carries; each has its long spelling too.
        { "trace:4bf92f3577b34da6a3ce929d0e0e4736", "trace=4bf92f3577b34da6a3ce929d0e0e4736 ; " },
        { "request:9c1d", "request=9c1d ; " },
        { "session:s-1", "session=s-1 ; " },
        { "run_id:r1 trace_id:t1 request_id:q1 session_id:s1", "run=r1|trace=t1|request=q1|session=s1 ; " },

        // Case: the name is folded, the value is kept as typed (matching ignores it).
        { "Connector:Codex", "connector=Codex ; " },
        { "SEVERITY:HIGH", "severity=HIGH ; " },
        { "Trace_Id:AbC", "trace=AbC ; " },

        // Tokens and free text together, in any order; the free words are one phrase.
        { "block skill connector:codex", "connector=codex ; block skill" },
        { "connector:codex block skill", "connector=codex ; block skill" },
        { "block connector:codex skill", "connector=codex ; block skill" },
        { "  connector:codex   severity:low  ", "connector=codex|severity=low ; " },

        // Quoting keeps a value, or a phrase, together.
        { "target:\"my skill\"", "target=my skill ; " },
        { "target:\"my skill\" extra", "target=my skill ; extra" },
        { "\"rm -rf\" tmp", " ; rm -rf tmp" },
        { "actor:\"two words\" \"a phrase\" word", "actor=two words ; a phrase word" },
        { "target:a\"b c\"d", "target=ab cd ; " },
        { "target:\"still being typed", "target=still being typed ; " },

        // A token with no value narrows nothing and is not text either.
        { "connector:", " ; " },
        { "connector:\"\"", " ; " },
        { "connector: codex", " ; codex" },
        { "severity:high connector:", "severity=high ; " },

        // Unknown tokens are free text, verbatim: no field of that name, a path, a URL, a time.
        { "foo:bar baz", " ; foo:bar baz" },
        { "foo:\"a b\"", " ; foo:\"a b\"" },
        { "a:b:c", " ; a:b:c" },
        { ":value", " ; :value" },
        { "http://example.test/x", " ; http://example.test/x" },
        { "12:30:45", " ; 12:30:45" },
        { "Foo:bar connector:codex", "connector=codex ; Foo:bar" },

        // The value is everything after the first colon.
        { "target:C:\\Dev\\x", "target=C:\\Dev\\x ; " },
        { "target:a:b:c", "target=a:b:c ; " },

        // The same field twice keeps both, in order.
        { "type:skill type:mcp", "type=skill|type=mcp ; " },
        { "connector:a connector:b", "connector=a|connector=b ; " },

        // Nothing recognised and nothing quoted: the text, trimmed - exactly, interior spacing and all.
        { "plain words", " ; plain words" },
        { "  plain   spaced   words ", " ; plain   spaced   words" },
        { "synthetic event 7", " ; synthetic event 7" },
        { "", " ; " },
        { "   ", " ; " },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void The_parser_table(string typed, string expected) => Assert.Equal(expected, Render(SearchQuery.Parse(typed)));

    [Fact]
    public void Null_is_an_empty_search()
    {
        var query = SearchQuery.Parse(null);

        Assert.True(query.IsEmpty);
        Assert.Empty(query.Tokens);
        Assert.Equal(string.Empty, query.FreeText);
        Assert.Same(SearchQuery.Empty, SearchQuery.Parse("   "));
    }

    [Fact]
    public void A_panel_names_the_fields_it_understands_and_the_rest_stay_text()
    {
        // The Logs panel knows connector: only (the TUI's Logs and Alerts panels know nothing else): severity: there is just a word to look for.
        var logs = SearchQuery.Parse("severity:high connector:codex run:7 hello", SearchField.Connector);

        Assert.Equal("connector=codex ; severity:high run:7 hello", Render(logs));

        // Several recognised fields.
        var two = SearchQuery.Parse("severity:high connector:codex target:x", SearchField.Severity, SearchField.Target);
        Assert.Equal("severity=high|target=x ; connector:codex", Render(two));
    }

    [Fact]
    public void The_last_value_of_a_field_wins_where_one_is_wanted_and_every_value_is_there_for_the_rest()
    {
        var query = SearchQuery.Parse("connector:a type:skill connector:b type:mcp");

        Assert.Equal("b", query.ValueOf(SearchField.Connector));
        Assert.Equal(new[] { "skill", "mcp" }, query.ValuesOf(SearchField.Type));
        Assert.Null(query.ValueOf(SearchField.Actor));
        Assert.Empty(query.ValuesOf(SearchField.Actor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData("\"\"\"")]
    [InlineData(":")]
    [InlineData("::")]
    [InlineData("connector")]
    [InlineData("connector:\"")]
    [InlineData("connector:\"\" \"")]
    [InlineData("\tconnector:\tcodex\r\n")]
    [InlineData("target:\"a\" \"b\" c:\"d")]
    [InlineData("\u0000:\u0001")]
    [InlineData("тип:значение connector:ｃｏｄｅｘ")]
    public void Whatever_is_typed_it_parses_and_prints(string typed)
    {
        var query = SearchQuery.Parse(typed);

        Assert.NotNull(query.FreeText);
        Assert.All(query.Tokens, t => Assert.NotEmpty(t.Value));
        _ = query.ToString();
    }

    // ------------------------------------------------------------------ typing a search for a thing that reads like a token

    [Theory]
    [InlineData("skill-x", "skill-x")]
    [InlineData("my skill", "my skill")]
    [InlineData("claudecode:PreToolUse", "claudecode:PreToolUse")]
    [InlineData("type:abc", "\"type:abc\"")]
    [InlineData("connector:", "\"connector:\"")]
    [InlineData("  trace:1  ", "\"  trace:1  \"")]
    public void A_target_that_reads_like_a_token_is_put_in_quotes_to_be_searched_for_as_text(string target, string typed)
    {
        Assert.Equal(typed, SearchQuery.AsFreeText(target));

        // Whatever form it is typed in, it is searched for as the text it is.
        var parsed = SearchQuery.Parse(SearchQuery.AsFreeText(target));
        Assert.Empty(parsed.Tokens);
        Assert.Equal(target.Trim(), parsed.FreeText);
    }

    [Fact]
    public void Text_that_holds_a_quote_is_left_as_it_is_there_being_no_way_to_quote_it()
    {
        Assert.Equal("say \"hi\"", SearchQuery.AsFreeText("say \"hi\""));
    }

    // ------------------------------------------------------------------ matching a row

    private static readonly Dictionary<SearchField, string?[]> Row = new()
    {
        [SearchField.Connector] = new string?[] { "claudecode" },
        [SearchField.Severity] = new string?[] { "HIGH", "High" },
        [SearchField.Target] = new string?[] { "skills/My Skill", null },
        [SearchField.Trace] = new string?[] { "4bf92f3577b34da6a3ce929d0e0e4736" },
    };

    private static readonly string?[] Haystack = { "block-skill", "skills/My Skill", null, "synthetic details", "claudecode" };

    private static bool Matches(string typed) =>
        SearchQuery.Parse(typed).Matches(field => Row.TryGetValue(field, out var values) ? values : Array.Empty<string?>(), Haystack);

    [Theory]
    [InlineData("connector:claudecode", true)]
    [InlineData("connector:CLAUDECODE", true)]
    [InlineData("connector:claude", false)]
    [InlineData("connector:codex", false)]
    [InlineData("severity:high", true)]
    [InlineData("severity:HI", true)]
    [InlineData("severity:low", false)]
    [InlineData("target:\"my skill\"", true)]
    [InlineData("target:my zzz", false)]
    [InlineData("trace:4bf92f35", true)]
    [InlineData("trace:ffff", false)]
    [InlineData("actor:cli", false)]
    [InlineData("block", true)]
    [InlineData("BLOCK-SKILL", true)]
    [InlineData("synthetic details", true)]
    [InlineData("synthetic  details", false)]
    [InlineData("nothing like this", false)]
    [InlineData("connector:claudecode severity:high block", true)]
    [InlineData("connector:claudecode severity:low block", false)]
    [InlineData("connector:claudecode severity:high missing", false)]
    [InlineData("foo:bar", false)]
    [InlineData("", true)]
    [InlineData("connector:", true)]
    public void A_row_passes_when_every_token_and_the_free_text_match(string typed, bool expected) => Assert.Equal(expected, Matches(typed));

    [Fact]
    public void A_connector_is_matched_whole_and_a_row_with_none_is_nobodys()
    {
        Assert.True(SearchQuery.SameConnector(" Codex ", "codex"));
        Assert.False(SearchQuery.SameConnector("codex-cli", "codex"));
        Assert.False(SearchQuery.SameConnector(null, "codex"));
        Assert.False(SearchQuery.SameConnector("  ", "codex"));
        Assert.False(SearchQuery.SameConnector(null, ""));
    }
}
