using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Security;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The commands the Runtime panel runs (CUST-309): a fixed argv each, no secret on any of them, and never <c>--grant</c>. And the reader of
/// <c>permissions --json</c>, whose "how to grant it" sentences become copy-only command lines. The fixture is synthetic, written from
/// <c>_RUNTIME_GRANTS["windows"]</c> in <c>cli/defenseclaw/commands/cmd_agent.py</c>.
/// </summary>
public class AiRuntimeCommandsTests
{
    private static readonly string[] Forbidden = ["--grant", "--revert"];

    // ------------------------------------------------------------------ argv

    [Fact]
    public void Poll_now_is_the_scan_command_with_no_flags_at_all()
    {
        Assert.Equal(new[] { "agent", "discovery", "runtime", "scan" }, AiRuntimeCommands.PollNow.ToArray());
    }

    [Fact]
    public void The_prerequisites_read_is_exactly_permissions_json_and_nothing_else_passes_for_it()
    {
        Assert.Equal(new[] { "agent", "discovery", "runtime", "permissions", "--json" }, AiRuntimeCommands.ReadPermissions.ToArray());
        Assert.True(AiRuntimeCommands.IsPermissionsRead(AiRuntimeCommands.ReadPermissions));

        string[][] notTheRead =
        [
            ["agent", "discovery", "runtime", "permissions"],
            ["agent", "discovery", "runtime", "permissions", "--json", "--grant"],
            ["agent", "discovery", "runtime", "permissions", "--json", "--revert"],
            ["agent", "discovery", "runtime", "permissions", "--json", "--yes"],
            ["agent", "discovery", "runtime", "permissions", "--grant", "--json"],
            ["agent", "discovery", "runtime", "permissions", "--os", "linux", "--json"],
            ["agent", "discovery", "runtime", "scan"],
            [],
        ];
        Assert.All(notTheRead, argv => Assert.False(AiRuntimeCommands.IsPermissionsRead(argv), string.Join(' ', argv)));
    }

    [Fact]
    public void The_classifier_would_review_a_grant_even_if_one_were_ever_built()
    {
        // The panel never builds it; this pins that no verb in the path or flag could make it look like a read.
        var grant = AiRuntimeCommands.ReadPermissions.Append("--grant").ToArray();

        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(grant));
        Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(["agent", "discovery", "runtime", "permissions", "--grant", "--yes"]));
        Assert.False(CommandTiers.IsUnreviewedRead(AiRuntimeCommands.ReadPermissions));
    }

    [Fact]
    public void Poll_enable_and_disable_are_state_changing_so_each_is_reviewed()
    {
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(AiRuntimeCommands.PollNow));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(AiRuntimeCommands.Enable(new AiRuntimeEnableOptions())));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(AiRuntimeCommands.Disable()));
        Assert.Equal(CommandTier.StateChanging, CommandTiers.Classify(AiRuntimeCommands.Disable(restart: false)));
    }

    [Fact]
    public void Enable_with_nothing_chosen_changes_nothing_but_says_yes()
    {
        Assert.Equal(
            new[] { "agent", "discovery", "runtime", "enable", "--yes" },
            AiRuntimeCommands.Enable(new AiRuntimeEnableOptions()).ToArray());
    }

    [Fact]
    public void Enable_carries_exactly_the_flags_that_were_chosen_in_the_documented_order()
    {
        var all = new AiRuntimeEnableOptions(HostPlane: true, DnsCapture: false, PollIntervalSeconds: 30, MinRiskToReport: 45, Restart: false);

        Assert.Equal(
            new[]
            {
                "agent", "discovery", "runtime", "enable", "--yes",
                "--enable-host-plane", "--no-dns-capture", "--poll-interval-s", "30", "--min-risk-to-report", "45", "--no-restart",
            },
            AiRuntimeCommands.Enable(all).ToArray());

        Assert.Equal(
            new[] { "agent", "discovery", "runtime", "enable", "--yes", "--no-enable-host-plane", "--dns-capture" },
            AiRuntimeCommands.Enable(new AiRuntimeEnableOptions(HostPlane: false, DnsCapture: true)).ToArray());
    }

    [Fact]
    public void Disable_says_yes_and_adds_no_restart_only_when_asked()
    {
        Assert.Equal(new[] { "agent", "discovery", "runtime", "disable", "--yes" }, AiRuntimeCommands.Disable().ToArray());
        Assert.Equal(new[] { "agent", "discovery", "runtime", "disable", "--yes", "--no-restart" }, AiRuntimeCommands.Disable(false).ToArray());
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(3600, true)]
    [InlineData(4, false)]
    [InlineData(3601, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void The_poll_interval_is_held_to_the_range_the_cli_accepts(int seconds, bool accepted)
    {
        var options = new AiRuntimeEnableOptions(PollIntervalSeconds: seconds);

        Assert.Equal(accepted, AiRuntimeCommands.Validate(options) is null);
        if (accepted)
        {
            Assert.Contains(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), AiRuntimeCommands.Enable(options));
        }
        else
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => AiRuntimeCommands.Enable(options));
            Assert.Contains("between 5 and 3600 seconds", AiRuntimeCommands.Validate(options), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    public void The_reporting_floor_is_held_to_the_range_the_cli_accepts(int floor, bool accepted)
    {
        var options = new AiRuntimeEnableOptions(MinRiskToReport: floor);

        Assert.Equal(accepted, AiRuntimeCommands.Validate(options) is null);
        if (!accepted)
        {
            Assert.Contains("between 1 and 100", AiRuntimeCommands.Validate(options), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_combination_of_options_ever_puts_grant_or_revert_or_a_secret_on_the_command_line()
    {
        bool?[] tri = [null, true, false];
        int?[] intervals = [null, 5, 60, 3600];
        int?[] floors = [null, 1, 30, 100];
        bool[] restarts = [true, false];

        var count = 0;
        foreach (var host in tri)
        {
            foreach (var dns in tri)
            {
                foreach (var interval in intervals)
                {
                    foreach (var floor in floors)
                    {
                        foreach (var restart in restarts)
                        {
                            var enable = AiRuntimeCommands.Enable(new AiRuntimeEnableOptions(host, dns, interval, floor, restart));
                            AssertSafe(enable);
                            AssertSafe(AiRuntimeCommands.Disable(restart));
                            count++;
                        }
                    }
                }
            }
        }

        AssertSafe(AiRuntimeCommands.PollNow);
        AssertSafe(AiRuntimeCommands.ReadPermissions);
        Assert.Equal(3 * 3 * 4 * 4 * 2, count);
    }

    private static void AssertSafe(IReadOnlyList<string> argv)
    {
        Assert.Equal(new[] { "agent", "discovery", "runtime" }, argv.Take(3).ToArray());
        Assert.All(argv, token =>
        {
            Assert.DoesNotContain(token, Forbidden);
            Assert.False(SecretHeuristics.LooksSecret(token), token);
            Assert.DoesNotContain('=', token);          // no --name=value pairs: nothing carries a value that could be a credential
            Assert.False(string.IsNullOrWhiteSpace(token));
        });
        Assert.DoesNotContain(argv, token => token.StartsWith("--gateway", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ permissions --json

    private static string Permissions() => RuntimeFixtures.Read("cli/agent-discovery-runtime-permissions.windows.synthetic.json");

    [Fact]
    public void The_windows_guidance_reads_with_one_grant_per_need_and_each_state()
    {
        var read = AiRuntimePermissionsReader.Parse(Permissions());

        Assert.True(read.IsOk, read.Message);
        var permissions = read.Permissions!;
        Assert.Equal("windows", permissions.Os);
        Assert.True(permissions.IsWindows);
        Assert.True(permissions.CheckedThisHost);
        Assert.Equal(5, permissions.Grants.Count);
        Assert.Equal(
            new[] { "missing", "missing", "unknown", "missing", "unknown" },
            permissions.Grants.Select(g => g.StateText).ToArray());
        Assert.Equal(3, permissions.MissingCount);
        Assert.Equal(2, permissions.UnknownCount);
        Assert.Equal("shadow egress (B)", permissions.Grants[1].Plane);
        Assert.Equal("elevated token AND Advanced Audit Policy", permissions.Grants[2].Needs);
    }

    [Fact]
    public void The_auditpol_sentence_is_written_out_as_the_three_commands_it_means_and_the_reg_line_as_it_stands()
    {
        var permissions = AiRuntimePermissionsReader.Parse(Permissions()).Permissions!;

        Assert.Equal(
            new[]
            {
                "auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable",
                "auditpol /set /subcategory:\"User Account Management\" /success:enable /failure:enable",
                "auditpol /set /subcategory:\"Sensitive Privilege Use\" /success:enable /failure:enable",
            },
            permissions.Grants[2].CommandLines.ToArray());
        Assert.Equal(
            new[]
            {
                @"reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit /v ProcessCreationIncludeCmdLine_Enabled /t REG_DWORD /d 1 /f",
            },
            permissions.Grants[3].CommandLines.ToArray());

        // Guidance that is not a command stays a sentence: elevation, an audit ACE.
        Assert.Empty(permissions.Grants[0].CommandLines);
        Assert.Empty(permissions.Grants[1].CommandLines);
        Assert.Empty(permissions.Grants[4].CommandLines);
        Assert.Equal(4, permissions.CommandLines.Count);
        Assert.Equal(4, permissions.CommandLines.Distinct().Count());
    }

    [Fact]
    public void A_grant_that_is_in_place_or_not_asked_for_offers_no_command_to_copy()
    {
        var granted = new AiRuntimeGrant("p", "n", "w", "auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable", true, false);
        var off = new AiRuntimeGrant("p", "n", "w", "auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable", false, true);

        Assert.Equal(AiRuntimeGrantState.Granted, granted.State);
        Assert.Equal(AiRuntimeGrantState.Off, off.State);
        Assert.Empty(granted.CommandLines);
        Assert.Empty(off.CommandLines);
    }

    [Theory]
    [InlineData("auditpol /set /subcategory:\"Process Creation\" /success:disable /failure:disable")]
    [InlineData("auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable & calc")]
    [InlineData("auditpol /set /subcategory:\"Pro\";cess\" /success:enable /failure:enable")]
    [InlineData("auditpol /clear /y")]
    [InlineData("auditpol /set /subcategory:\"Process Creation\" /success:enable")]
    [InlineData("reg add HKCU\\Software\\X /v A /t REG_DWORD /d 1 /f")]
    [InlineData("reg add HKLM\\SOFTWARE\\X /v A /t REG_SZ /d 1 /f")]
    [InlineData("reg delete HKLM\\SOFTWARE\\X /f")]
    [InlineData("powershell -c iex(...)")]
    [InlineData("run the gateway elevated")]
    [InlineData("")]
    public void Anything_that_is_not_one_of_the_two_exact_shapes_produces_no_line(string how)
    {
        var lines = AiRuntimeGuidance.CommandLines(how);

        // Either nothing, or - for the one that merely has a tail after a good command - only the good command, never the tail.
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain("&", line, StringComparison.Ordinal);
            Assert.DoesNotContain(";", line, StringComparison.Ordinal);
            Assert.DoesNotContain("calc", line, StringComparison.Ordinal);
            Assert.DoesNotContain("disable", line, StringComparison.Ordinal);
            Assert.DoesNotContain("HKCU", line, StringComparison.Ordinal);
            Assert.DoesNotContain("REG_SZ", line, StringComparison.Ordinal);
        });
        if (how.Contains("& calc", StringComparison.Ordinal))
        {
            Assert.Single(lines);
        }
        else
        {
            Assert.Empty(lines);
        }
    }

    [Fact]
    public void A_subcategory_list_with_something_other_than_names_does_not_become_commands()
    {
        var lines = AiRuntimeGuidance.CommandLines(
            "auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable (also User Account Management, Evil$(x), Sensitive Privilege Use)");

        // The pattern for the parenthetical admits only letters, spaces and commas, so the whole tail is not read.
        Assert.Equal(
            new[] { "auditpol /set /subcategory:\"Process Creation\" /success:enable /failure:enable" },
            lines.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no json here")]
    [InlineData("{}")]
    [InlineData("""{"os":"windows"}""")]
    [InlineData("""{"grants":{}}""")]
    [InlineData("[1,2]")]
    [InlineData("{ not json }")]
    public void Output_that_is_not_the_permissions_document_is_a_sentence_not_a_guess(string? output)
    {
        var read = AiRuntimePermissionsReader.Parse(output);

        Assert.False(read.IsOk);
        Assert.Null(read.Permissions);
        Assert.NotEmpty(read.Message);
    }

    [Fact]
    public void A_banner_line_before_the_json_is_skipped()
    {
        var read = AiRuntimePermissionsReader.Parse("note: using the installed runtime\n" + Permissions());

        Assert.True(read.IsOk, read.Message);
        Assert.Equal(5, read.Permissions!.Grants.Count);
    }

    [Fact]
    public void A_grant_that_the_runtime_marks_off_is_not_a_gap_and_an_unknown_is_not_a_failure()
    {
        const string json = """
            {"os":"linux","checked_this_host":false,"grants":[
              {"plane":"shadow egress (B), DNS naming","needs":"CAP_NET_RAW","why":"w","how":"setcap","granted":null,"off":true},
              {"plane":"agent actions (C)","needs":"n","why":"w","how":null,"granted":null},
              {"plane":"inference heartbeat (A)","needs":"nothing","why":"w","how":null,"granted":true}]}
            """;

        var permissions = AiRuntimePermissionsReader.Parse(json).Permissions!;

        Assert.False(permissions.IsWindows);
        Assert.False(permissions.CheckedThisHost);
        Assert.Equal(new[] { "off", "unknown", "granted" }, permissions.Grants.Select(g => g.StateText).ToArray());
        Assert.Equal(0, permissions.MissingCount);
        Assert.Equal(string.Empty, permissions.Grants[1].How);
    }

    [Fact]
    public void Text_in_the_guidance_is_masked_like_everything_else_the_runtime_prints()
    {
        const string json = """{"os":"windows","grants":[{"plane":"p","needs":"n","why":"set password=synthetic-synthetic first","how":"x","granted":false}]}""";

        var grant = AiRuntimePermissionsReader.Parse(json).Permissions!.Grants.Single();

        Assert.DoesNotContain("synthetic-synthetic", grant.Why, StringComparison.Ordinal);
        Assert.Contains("[redacted]", grant.Why, StringComparison.Ordinal);
    }
}
