using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Wizards;

public class SetupHelpParserTests
{
    private static readonly ParsedHelp ClaudeCode = SetupHelpParser.Parse(WizardSamples.ClaudeCodeHelp);

    [Fact]
    public void The_usage_and_the_first_sentence_of_the_description_are_read()
    {
        Assert.StartsWith("Usage: defenseclaw setup claude-code", ClaudeCode.Usage, StringComparison.Ordinal);
        Assert.Equal("Configure DefenseClaw hooks for Claude Code.", ClaudeCode.Summary);
        Assert.Contains("Run with --yes for unattended setup", ClaudeCode.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_option_but_help_is_read_in_order()
    {
        Assert.Equal(
            new[]
            {
                "--mode", "--fail-mode", "--human-approval", "--hilt-min-severity", "--block-message", "--rule-pack",
                "--rule-pack-dir", "--enable-judge", "--judge-hook-connectors", "--replace", "--workspace",
                "--with-local-stack", "--restart", "--yes",
            },
            ClaudeCode.Options.Select(o => o.Flag).ToArray());
    }

    [Fact]
    public void A_choice_metavar_becomes_its_values_and_a_default_marker_is_captured()
    {
        var mode = ClaudeCode.Option("--mode")!;

        Assert.Equal(new[] { "observe", "action" }, mode.Choices);
        Assert.Equal("observe", mode.Default);
        Assert.True(mode.TakesValue);
        Assert.Equal(new[] { "LOW", "MEDIUM", "HIGH", "CRITICAL" }, ClaudeCode.Option("--hilt-min-severity")!.Choices);
    }

    [Fact]
    public void A_paired_flag_carries_its_negative_form_and_a_wrapped_default_is_rejoined()
    {
        var restart = ClaudeCode.Option("--restart")!;
        Assert.Equal("--no-restart", restart.NegativeFlag);
        Assert.Equal("restart", restart.Default);

        var stack = ClaudeCode.Option("--with-local-stack")!;
        Assert.Equal("--no-local-stack", stack.NegativeFlag);
        Assert.Equal("no-local-stack", stack.Default);

        Assert.Equal("--no-human-approval", ClaudeCode.Option("--human-approval")!.NegativeFlag);
    }

    [Fact]
    public void A_short_and_long_spelling_are_one_option_and_a_bare_switch_takes_no_value()
    {
        var yes = ClaudeCode.Option("-y")!;

        Assert.Same(yes, ClaudeCode.Option("--yes"));
        Assert.Equal("--yes", yes.Flag);
        Assert.False(yes.TakesValue);
    }

    [Fact]
    public void A_wrapped_description_is_joined_into_one_line()
    {
        var message = ClaudeCode.Option("--block-message")!;

        Assert.Equal(
            "Message shown to the agent when a call is blocked (pass \"\" to clear).",
            message.Description);
    }

    [Fact]
    public void A_connector_without_a_platform_status_line_is_certified_and_a_non_connector_is_not_applicable()
    {
        Assert.Equal(PlatformStatus.Certified, ClaudeCode.PlatformStatus);

        var llm = SetupHelpParser.Parse(WizardSamples.LlmHelp);
        Assert.Equal(PlatformStatus.NotApplicable, llm.PlatformStatus);
    }

    private const string EmDash = "\u2014";

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void An_explicit_platform_status_line_is_honoured_with_its_note(string eol)
    {
        // The sample is LF whatever the source was checked out with, so the swap below always finds its target;
        // the screen is then re-ended, which is how the CLI's piped output reaches the parser on Windows.
        const string original = "  Configure DefenseClaw hooks for Claude Code.\n";
        Assert.Contains(original, WizardSamples.ClaudeCodeHelp, StringComparison.Ordinal);

        var help = WizardSamples.ClaudeCodeHelp.Replace(
            original,
            $"  Configure DefenseClaw hooks for Cursor.\n\n  Platform status on windows: not_certified {EmDash} hooks fire but the connector has not completed certification.\n",
            StringComparison.Ordinal);

        var parsed = SetupHelpParser.Parse(LineEndings.With(help, eol));

        Assert.Equal(PlatformStatus.NotCertified, parsed.PlatformStatus);
        Assert.Equal("hooks fire but the connector has not completed certification.", parsed.PlatformNote);
    }

    public static TheoryData<string, PlatformStatus, string> StatusLines { get; } = BuildStatusLines();

    private static TheoryData<string, PlatformStatus, string> BuildStatusLines()
    {
        var lines = new (string Line, PlatformStatus Expected)[]
        {
            ($"Platform status on windows: unsupported {EmDash} the connector is Linux only.", PlatformStatus.Unsupported),
            ("Platform status on windows: not_certified", PlatformStatus.NotCertified),
            ("Platform status on windows: certified", PlatformStatus.Certified),
            ("Platform status on windows: something_new", PlatformStatus.Unknown),
        };

        var data = new TheoryData<string, PlatformStatus, string>();
        foreach (var (line, expected) in lines)
        {
            data.Add(line, expected, LineEndings.Lf);
            data.Add(line, expected, LineEndings.Crlf);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StatusLines))]
    public void Each_status_word_maps_to_its_status(string line, PlatformStatus expected, string eol)
    {
        // Alone, and as one line of a screen: the CLI ends every line it prints, so the status word is always
        // followed by a line break in real text.
        Assert.Equal(expected, SetupHelpParser.ExtractPlatformStatus(line).Status);
        Assert.Equal(expected, SetupHelpParser.ExtractPlatformStatus(line + eol + eol + "Options:" + eol).Status);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_status_notes_text_never_carries_a_carriage_return(string eol)
    {
        var (status, note) = SetupHelpParser.ExtractPlatformStatus(
            $"Platform status on windows: not_certified {EmDash} The DefenseClaw Cursor{eol}  integration has not completed certification.{eol}");

        Assert.Equal(PlatformStatus.NotCertified, status);
        Assert.DoesNotContain('\r', note);
        Assert.StartsWith("The DefenseClaw Cursor", note, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_status_line_without_a_note_does_not_borrow_a_dash_from_the_next_line(string eol)
    {
        var (status, note) = SetupHelpParser.ExtractPlatformStatus(
            "Platform status on windows: not_certified" + eol + "- a bullet that is not this status's note" + eol);

        Assert.Equal(PlatformStatus.NotCertified, status);
        Assert.Equal(string.Empty, note);
    }

    [Theory]
    [InlineData("Cursor: not_certified on windows.", PlatformStatus.NotCertified)]
    [InlineData("OpenClaw: unsupported on windows.", PlatformStatus.Unsupported)]
    [InlineData("Configure something.", PlatformStatus.Unknown)]
    [InlineData("", PlatformStatus.Unknown)]
    public void The_top_level_summary_hint_is_read_before_the_per_target_help_lands(string summary, PlatformStatus expected)
    {
        Assert.Equal(expected, SetupHelpParser.StatusFromSummary(summary));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_commands_block_gives_each_command_and_its_wrapped_summary(string eol)
    {
        var text = LineEndings.With("""
            Usage: defenseclaw setup [OPTIONS] COMMAND [ARGS]...

              Configure DefenseClaw.

            Options:
              --help  Show this message and exit.

            Commands:
              claude-code  Configure Claude Code hooks.
              cursor       Cursor: not_certified on windows. Configure Cursor
                           hooks.
              llm          Configure LLM providers.
            """, eol);

        var help = SetupHelpParser.Parse(text);

        Assert.True(help.HasSubcommands);
        Assert.Equal(new[] { "claude-code", "cursor", "llm" }, help.Commands.Select(c => c.Name).ToArray());
        Assert.Equal("Cursor: not_certified on windows. Configure Cursor hooks.", help.Commands[1].Summary);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void Positionals_are_lifted_out_of_the_usage_line_after_the_command_nouns(string eol)
    {
        var add = SetupHelpParser.Parse(LineEndings.With(WizardSamples.ObservabilityAddHelp, eol), commandDepth: 2);

        var preset = Assert.Single(add.Positionals);
        Assert.Equal("preset", preset.Name);
        Assert.True(preset.IsRequired);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_choice_group_in_the_usage_becomes_a_positional_with_its_values(string eol)
    {
        var help = SetupHelpParser.Parse(LineEndings.With("Usage: defenseclaw setup notifications-set [OPTIONS] {slot_a|slot_b} [on|off]\n\n  Set one.\n\nOptions:\n  --help  Show this message and exit.\n", eol));

        Assert.Equal(2, help.Positionals.Count);
        Assert.True(help.Positionals[0].IsRequired);
        Assert.Equal(new[] { "slot_a", "slot_b" }, help.Positionals[0].Choices);
        Assert.False(help.Positionals[1].IsRequired);
        Assert.Equal(new[] { "on", "off" }, help.Positionals[1].Choices);
    }

    // ------------------------------------------------------------------ Windows line endings
    //
    // The CLI's stdout is CRLF on Windows, and SetupHelpProbe hands that text to the parser as it came. A status line
    // misread here is the costly kind of wrong: a connector that is not certified would show a green "Certified on
    // Windows" badge. These screens are held as LF and re-ended per test, so they mean the same on any checkout.

    /// <summary>Shaped like a real connector screen that carries a status paragraph, wrapped across two lines, with an em dash.</summary>
    private static readonly string CursorHelp = LineEndings.Normalize($"""
        Usage: defenseclaw setup cursor [OPTIONS]

          Configure DefenseClaw for Cursor through its agent lifecycle hooks.

          Registers this connector so hook-driven scanners can read the agent's
          documented local surfaces. The default mode is observe; pass --mode
          action to let the agent enforce verdicts. No proxy is involved.

          Platform status on windows: not_certified {EmDash} The DefenseClaw Cursor
          integration has not completed native Windows x64 certification.

        Options:
          --mode [observe|action]   Hook policy mode.  [default: observe]
          --fail-mode [open|closed]
                                    What the hook does when the gateway is
                                    unreachable.
          --replace / --no-replace  Replace hooks already installed by other tools.
                                    [default: no-replace]
          --restart / --no-restart  Restart the gateway after applying changes.
                                    [default: restart]
          -y, --yes                 Skip the confirmation prompt (non-
                                    interactive).
          --help                    Show this message and exit.
        """);

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_screen_with_an_em_dash_status_line_reads_as_not_certified_with_its_wrapped_note(string eol)
    {
        var parsed = SetupHelpParser.Parse(LineEndings.With(CursorHelp, eol));

        Assert.Equal(PlatformStatus.NotCertified, parsed.PlatformStatus);
        Assert.Equal(
            "The DefenseClaw Cursor integration has not completed native Windows x64 certification.",
            parsed.PlatformNote);
        Assert.Equal("Configure DefenseClaw for Cursor through its agent lifecycle hooks.", parsed.Summary);
        Assert.StartsWith("Usage: defenseclaw setup cursor", parsed.Usage, StringComparison.Ordinal);
        Assert.Equal(
            new[] { "--mode", "--fail-mode", "--replace", "--restart", "--yes" },
            parsed.Options.Select(o => o.Flag).ToArray());
        Assert.Equal("observe", parsed.Option("--mode")!.Default);
        Assert.Equal("restart", parsed.Option("--restart")!.Default);
        Assert.Equal("What the hook does when the gateway is unreachable.", parsed.Option("--fail-mode")!.Description);
        Assert.True(SetupHelpParser.IsConnectorShaped(parsed.Options));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_status_paragraph_alone_separates_a_certified_connector_from_a_not_certified_one(string eol)
    {
        const string paragraph =
            "  Platform status on windows: not_certified " + EmDash + " The DefenseClaw Cursor\n" +
            "  integration has not completed native Windows x64 certification.\n\n";
        Assert.Contains(paragraph, CursorHelp, StringComparison.Ordinal);
        var without = CursorHelp.Replace(paragraph, string.Empty, StringComparison.Ordinal);

        Assert.Equal(PlatformStatus.NotCertified, SetupHelpParser.Parse(LineEndings.With(CursorHelp, eol)).PlatformStatus);

        // A connector-shaped screen with no status line is Click's "certified here" convention: reading the line is
        // the only thing standing between a not-certified connector and a green badge.
        var certified = SetupHelpParser.Parse(LineEndings.With(without, eol));
        Assert.Equal(PlatformStatus.Certified, certified.PlatformStatus);
        Assert.Equal(string.Empty, certified.PlatformNote);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void A_status_line_printed_after_the_options_is_still_found(string eol)
    {
        // No description block before Options:, so the parser falls back to the whole screen (raw, not the joined description).
        const string screen =
            "Usage: defenseclaw setup thing [OPTIONS]\n\nOptions:\n  --help  Show this message and exit.\n\n" +
            "Platform status on windows: unsupported " + EmDash + " Linux only.\n";

        var parsed = SetupHelpParser.Parse(LineEndings.With(screen, eol));

        Assert.Equal(PlatformStatus.Unsupported, parsed.PlatformStatus);
        Assert.Equal("Linux only.", parsed.PlatformNote);
    }

    public static TheoryData<string, string> ScreensAndEndings { get; } = BuildScreensAndEndings();

    private static TheoryData<string, string> BuildScreensAndEndings()
    {
        // "\r\r\n" is what a CRLF-writing child produces when a second layer translates again; a lone "\r" is old-Mac
        // text. Neither should ever be seen, and neither may change what a screen means.
        var data = new TheoryData<string, string>();
        foreach (var name in new[] { "claude-code", "llm", "observability", "observability-add", "cursor" })
        {
            foreach (var eol in new[] { LineEndings.Lf, LineEndings.Crlf, "\r\r\n", "\r" })
            {
                data.Add(name, eol);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScreensAndEndings))]
    public void A_screen_parses_to_the_same_result_whatever_its_line_endings(string screen, string eol)
    {
        var lf = screen switch
        {
            "claude-code" => WizardSamples.ClaudeCodeHelp,
            "llm" => WizardSamples.LlmHelp,
            "observability" => WizardSamples.ObservabilityHelp,
            "observability-add" => WizardSamples.ObservabilityAddHelp,
            _ => CursorHelp,
        };
        var depth = screen == "observability-add" ? 2 : 1;

        var expected = Describe(SetupHelpParser.Parse(lf, depth));
        var actual = Describe(SetupHelpParser.Parse(LineEndings.With(lf, eol), depth));

        Assert.Equal(expected, actual);
        Assert.DoesNotContain('\r', actual);
    }

    /// <summary>Every field a parse produces, as one string, so two parses can be compared whole.</summary>
    private static string Describe(ParsedHelp help) => string.Join(
        "␞",
        new[] { help.Usage, help.Description, help.Summary, help.PlatformStatus.ToString(), help.PlatformNote, help.SubcommandOptional.ToString() }
            .Concat(help.Options.Select(o =>
                $"{string.Join(",", o.Names)};{o.Flag};{o.NegativeFlag};{o.Metavar};{string.Join(",", o.Choices)};{o.Default};{o.Description}"))
            .Concat(help.Commands.Select(c => $"{c.Name};{c.Summary}"))
            .Concat(help.Positionals.Select(p => $"{p.Name};{p.IsRequired};{string.Join(",", p.Choices)}")));

    [Fact]
    public void Text_that_is_not_help_parses_to_an_empty_result_rather_than_throwing()
    {
        var help = SetupHelpParser.Parse("Error: no such command 'nope'.\n");

        Assert.Empty(help.Options);
        Assert.Empty(help.Commands);
        Assert.Equal(PlatformStatus.NotApplicable, help.PlatformStatus);
    }
}

public class WizardFieldBuilderTests
{
    private static ParsedOption Option(
        string flag,
        string metavar = "",
        string description = "",
        string? negative = null,
        params string[] choices) => new()
    {
        Names = new[] { flag },
        Flag = flag,
        NegativeFlag = negative,
        Metavar = metavar,
        Description = description,
        Choices = choices,
    };

    [Theory]
    [InlineData("--token")]
    [InlineData("--access-token")]
    [InlineData("--hec-token")]
    [InlineData("--api-key")]
    [InlineData("--access-key")]
    [InlineData("--client-secret")]
    [InlineData("--password")]
    [InlineData("--passphrase")]
    public void A_flag_that_takes_the_secret_itself_is_a_secret_field(string flag)
    {
        Assert.Equal(WizardFieldKind.Secret, WizardFieldBuilder.KindFor(Option(flag, "TEXT")));
    }

    [Theory]
    [InlineData("--api-key-env")]
    [InlineData("--token-env")]
    [InlineData("--judge-api-key-env")]
    [InlineData("--cisco-api-key-env")]
    public void A_flag_that_names_a_variable_is_an_ordinary_field_even_though_it_mentions_a_key(string flag)
    {
        Assert.Equal(WizardFieldKind.EnvVarName, WizardFieldBuilder.KindFor(Option(flag, "TEXT")));
    }

    [Fact]
    public void The_control_follows_the_shape_of_the_option()
    {
        Assert.Equal(WizardFieldKind.Toggle, WizardFieldBuilder.KindFor(Option("--restart", negative: "--no-restart")));
        Assert.Equal(WizardFieldKind.Switch, WizardFieldBuilder.KindFor(Option("--yes")));
        Assert.Equal(WizardFieldKind.Choice, WizardFieldBuilder.KindFor(Option("--mode", "[a|b]", choices: new[] { "a", "b" })));
        Assert.Equal(WizardFieldKind.Integer, WizardFieldBuilder.KindFor(Option("--port", "INTEGER")));
        Assert.Equal(WizardFieldKind.Number, WizardFieldBuilder.KindFor(Option("--timeout", "FLOAT")));
        Assert.Equal(WizardFieldKind.Text, WizardFieldBuilder.KindFor(Option("--model", "TEXT")));
    }

    [Theory]
    [InlineData("--rule-pack-dir", "TEXT", WizardFieldKind.Path)]
    [InlineData("--policy-file", "TEXT", WizardFieldKind.Path)]
    [InlineData("--workspace-path", "TEXT", WizardFieldKind.Path)]
    [InlineData("--anything", "FILE", WizardFieldKind.Path)]
    [InlineData("--anything", "DIRECTORY", WizardFieldKind.Path)]
    [InlineData("--url-path", "TEXT", WizardFieldKind.Text)]
    public void Path_fields_are_recognised_but_the_path_part_of_a_url_is_text(string flag, string metavar, WizardFieldKind expected)
    {
        Assert.Equal(expected, WizardFieldBuilder.KindFor(Option(flag, metavar)));
    }

    [Fact]
    public void A_repeatable_option_becomes_a_multi_line_box_unless_it_names_a_variable()
    {
        Assert.Equal(
            WizardFieldKind.Lines,
            WizardFieldBuilder.KindFor(Option("--judge-hook-connectors", "TEXT", "Connectors that call the judge (repeatable).")));
        Assert.Equal(
            WizardFieldKind.EnvVarName,
            WizardFieldBuilder.KindFor(Option("--extra-env", "TEXT", "Variable names (repeatable).")));
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("--non-interactive")]
    [InlineData("--accept-defaults")]
    public void The_flags_that_suppress_prompts_are_recognised(string flag)
    {
        Assert.True(WizardFieldBuilder.IsNonInteractiveFlag(flag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--restart")]
    public void Other_flags_do_not_suppress_prompts(string? flag)
    {
        Assert.False(WizardFieldBuilder.IsNonInteractiveFlag(flag));
    }

    [Theory]
    [InlineData("--hilt-min-severity", "HILT min severity")]
    [InlineData("--api-key-env", "API key env")]
    [InlineData("--llm-model", "LLM model")]
    [InlineData("--s3-bucket", "S3 bucket")]
    [InlineData("--rule-pack-dir", "Rule pack dir")]
    [InlineData("--mode", "Mode")]
    public void A_flag_is_humanized_with_the_acronyms_operators_recognise(string flag, string expected)
    {
        Assert.Equal(expected, WizardFieldBuilder.Humanize(flag));
    }

    [Fact]
    public void A_field_starts_from_the_documented_default_and_a_prompt_flag_starts_on_while_its_baseline_is_off()
    {
        var mode = WizardFieldBuilder.From(new ParsedOption
        {
            Names = new[] { "--mode" },
            Flag = "--mode",
            Metavar = "[observe|action]",
            Choices = new[] { "observe", "action" },
            Default = "observe",
        })!;
        Assert.Equal("observe", mode.DefaultValue);
        Assert.Equal("observe", mode.BaselineValue);
        Assert.Equal("mode", mode.Id);
        Assert.Equal(new[] { string.Empty, "observe", "action" }, mode.Choices.Select(c => c.Value).ToArray());

        var yes = WizardFieldBuilder.From(new ParsedOption { Names = new[] { "-y", "--yes" }, Flag = "--yes" })!;
        Assert.Equal(ToggleValues.On, yes.DefaultValue);
        Assert.Equal(ToggleValues.Off, yes.BaselineValue);
    }

    [Fact]
    public void A_secret_field_starts_empty_and_says_where_the_value_lives_instead()
    {
        var token = WizardFieldBuilder.From(Option("--token", "TEXT"))!;

        Assert.Equal(string.Empty, token.DefaultValue);
        Assert.Equal(string.Empty, token.BaselineValue);
        Assert.Contains("keys set", token.Placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void A_paired_flag_default_is_read_from_either_spelling()
    {
        ParsedOption Paired(string defaultText) => new()
        {
            Names = new[] { "--restart" },
            Flag = "--restart",
            NegativeFlag = "--no-restart",
            Default = defaultText,
        };

        Assert.Equal(ToggleValues.On, WizardFieldBuilder.From(Paired("restart"))!.DefaultValue);
        Assert.Equal(ToggleValues.Off, WizardFieldBuilder.From(Paired("no-restart"))!.DefaultValue);
        Assert.Equal(ToggleValues.Unset, WizardFieldBuilder.From(Paired("whatever"))!.DefaultValue);
        Assert.Equal(ToggleValues.Unset, WizardFieldBuilder.From(new ParsedOption { Names = new[] { "--restart" }, Flag = "--restart", NegativeFlag = "--no-restart" })!.DefaultValue);
    }
}
