using System.Text.Json;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The Alerts panel's severity chips act on the normalized colour key, not on the spelling the gateway stored,
/// so WARN / WARNING / MODERATE count as Medium and FATAL as Critical, and every alert has exactly one chip.
/// The panel owns a <c>DispatcherTimer</c>, so each test runs on an STA thread.
/// </summary>
public class AlertsFilterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static GatewayAlert Alert(string id, string? severity, string? rule = null, string action = "block", string? title = null, DateTimeOffset? at = null)
    {
        var structured = new Dictionary<string, JsonElement>();
        if (rule is not null)
        {
            structured[GatewayAlert.Keys.RuleId] = JsonSerializer.SerializeToElement(rule);
        }

        if (title is not null)
        {
            structured[GatewayAlert.Keys.Title] = JsonSerializer.SerializeToElement(title);
        }

        return new GatewayAlert
        {
            Id = id,
            Timestamp = at ?? Now.AddMinutes(-1),
            Severity = severity,
            Action = action,
            Structured = structured,
        };
    }

    private static GatewaySnapshot Snapshot(params GatewayAlert[] alerts) => new()
    {
        State = AppGatewayState.Running,
        PolledAt = DateTimeOffset.UtcNow,
        AlertsFetchedAt = DateTimeOffset.UtcNow,
        RecentAlerts = alerts,
    };

    /// <summary>Builds the panel on an STA thread, feeds it one snapshot, and runs <paramref name="body"/> there too.</summary>
    private static void WithPanel(GatewayAlert[] alerts, Action<AlertsPanelViewModel> body)
    {
        StaThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var vm = new AlertsPanelViewModel(services);
            vm.Apply(Snapshot(alerts));
            body(vm);
        });
    }

    private static GatewayAlert[] OneOfEverySpelling() => new[]
    {
        Alert("critical", "CRITICAL"),
        Alert("fatal", "FATAL"),
        Alert("high", "HIGH"),
        Alert("medium", "MEDIUM"),
        Alert("warn", "WARN"),
        Alert("warning", "Warning"),
        Alert("moderate", "moderate"),
        Alert("low", "LOW"),
        Alert("info", "INFO"),
        Alert("blank", null),
        Alert("odd", "SOMETHING-NEW"),
    };

    private static string[] Ids(AlertsPanelViewModel vm) => vm.Alerts.Select(a => a.Key).ToArray();

    private static SeverityFilter Chip(AlertsPanelViewModel vm, string severity) => vm.SeverityFilters.Single(f => f.Severity == severity);

    // ------------------------------------------------------------------ the key

    [Theory]
    [InlineData("CRITICAL", "Critical")]
    [InlineData("FATAL", "Critical")]
    [InlineData("fatal", "Critical")]
    [InlineData("HIGH", "High")]
    [InlineData("high", "High")]
    [InlineData("MEDIUM", "Medium")]
    [InlineData("MODERATE", "Medium")]
    [InlineData("WARN", "Medium")]
    [InlineData("warn", "Medium")]
    [InlineData("WARNING", "Medium")]
    [InlineData("  Warning  ", "Medium")]
    [InlineData("LOW", "Low")]
    [InlineData("INFO", "Info")]
    [InlineData("ERROR", "Info")]
    [InlineData("DEBUG", "Info")]
    [InlineData("SOMETHING-NEW", "Info")]
    [InlineData("", "Info")]
    [InlineData("   ", "Info")]
    [InlineData(null, "Info")]
    public void Every_spelling_folds_into_one_of_five_keys(string? severity, string expected)
    {
        Assert.Equal(expected, AlertItem.KeyFor(severity));
    }

    [Fact]
    public void The_five_chips_carry_the_five_keys()
    {
        StaThread.Run(() =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var vm = new AlertsPanelViewModel(services);

            Assert.Equal(
                new[] { ("CRITICAL", "Critical"), ("HIGH", "High"), ("MEDIUM", "Medium"), ("LOW", "Low"), ("INFO", "Info") },
                vm.SeverityFilters.Select(f => (f.Severity, f.SeverityKey)).ToArray());
            Assert.All(vm.SeverityFilters, f => Assert.True(f.IsEnabled));
        });
    }

    [Fact]
    public void A_gateway_alert_is_normalized_when_it_becomes_a_row()
    {
        Assert.Equal("INFO", AlertItem.FromGateway(Alert("a", null)).Severity);
        Assert.Equal("INFO", AlertItem.FromGateway(Alert("a", "   ")).Severity);
        Assert.Equal("WARN", AlertItem.FromGateway(Alert("a", "  warn ")).Severity);
        Assert.Equal("Medium", AlertItem.FromGateway(Alert("a", "  warn ")).SeverityKey);
    }

    // ------------------------------------------------------------------ the chips count and filter by key

    [Fact]
    public void Warn_and_its_synonyms_count_as_medium_and_fatal_as_critical()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            Assert.Equal(11, vm.Alerts.Count);
            Assert.Equal(2, Chip(vm, "CRITICAL").Count);
            Assert.Equal(1, Chip(vm, "HIGH").Count);
            Assert.Equal(4, Chip(vm, "MEDIUM").Count);
            Assert.Equal(1, Chip(vm, "LOW").Count);
            Assert.Equal(3, Chip(vm, "INFO").Count);
            Assert.Equal(11, vm.SeverityFilters.Sum(f => f.Count));
        });
    }

    [Fact]
    public void Switching_medium_off_hides_every_medium_spelling_including_warn()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            Chip(vm, "MEDIUM").IsEnabled = false;

            var ids = Ids(vm);
            Assert.DoesNotContain("medium", ids);
            Assert.DoesNotContain("warn", ids);
            Assert.DoesNotContain("warning", ids);
            Assert.DoesNotContain("moderate", ids);
            Assert.Equal(7, ids.Length);
            Assert.Contains("high", ids);
            Assert.Contains("critical", ids);

            // A chip that is off still says how many alerts it hides.
            Assert.Equal(4, Chip(vm, "MEDIUM").Count);
        });
    }

    [Fact]
    public void Switching_high_off_hides_only_high_and_leaves_warn_visible()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            Chip(vm, "HIGH").IsEnabled = false;

            var ids = Ids(vm);
            Assert.DoesNotContain("high", ids);
            Assert.Contains("warn", ids);
            Assert.Equal(10, ids.Length);
        });
    }

    [Fact]
    public void Switching_critical_off_hides_fatal_too()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            Chip(vm, "CRITICAL").IsEnabled = false;

            Assert.DoesNotContain("critical", Ids(vm));
            Assert.DoesNotContain("fatal", Ids(vm));
            Assert.Equal(9, vm.Alerts.Count);
        });
    }

    [Fact]
    public void An_alert_with_no_severity_or_an_unknown_one_lives_under_the_info_chip_and_cannot_vanish()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            foreach (var chip in vm.SeverityFilters.Where(f => f.Severity != "INFO"))
            {
                chip.IsEnabled = false;
            }

            Assert.Equal(new[] { "info", "blank", "odd" }, Ids(vm));

            Chip(vm, "INFO").IsEnabled = false;
            Assert.Empty(vm.Alerts);
        });
    }

    [Fact]
    public void With_every_chip_on_nothing_is_hidden_even_when_the_stored_spelling_matches_no_chip()
    {
        WithPanel(OneOfEverySpelling(), vm => Assert.Equal(11, vm.Alerts.Count));
    }

    [Fact]
    public void Switching_a_chip_back_on_restores_its_alerts()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            Chip(vm, "MEDIUM").IsEnabled = false;
            Assert.Equal(7, vm.Alerts.Count);

            Chip(vm, "MEDIUM").IsEnabled = true;
            Assert.Equal(11, vm.Alerts.Count);
            Assert.Contains("warn", Ids(vm));
        });
    }

    [Fact]
    public void Clearing_the_filters_turns_every_chip_back_on_and_empties_the_search()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            Chip(vm, "LOW").IsEnabled = false;
            Chip(vm, "MEDIUM").IsEnabled = false;
            vm.FilterText = "nothing matches this";
            Assert.Empty(vm.Alerts);

            vm.ClearFiltersCommand.Execute(null);

            Assert.Equal(11, vm.Alerts.Count);
            Assert.Equal(string.Empty, vm.FilterText);
            Assert.All(vm.SeverityFilters, f => Assert.True(f.IsEnabled));
        });
    }

    // ------------------------------------------------------------------ text filter, collapse, summary

    [Fact]
    public void The_text_filter_and_the_chips_combine()
    {
        var alerts = new[]
        {
            Alert("a", "WARN", rule: "CMD-ENV-DUMP", title: "Environment variable dump"),
            Alert("b", "HIGH", rule: "CMD-ENV-DUMP", title: "Environment variable dump"),
            Alert("c", "WARN", rule: "SEC-BEARER", title: "Bearer token"),
        };

        WithPanel(alerts, vm =>
        {
            vm.FilterText = "env-dump";
            Assert.Equal(new[] { "a", "b" }, Ids(vm));

            Chip(vm, "HIGH").IsEnabled = false;
            Assert.Equal(new[] { "a" }, Ids(vm));

            vm.FilterText = "BEARER";
            Assert.Equal(new[] { "c" }, Ids(vm));
        });
    }

    [Fact]
    public void The_summary_says_how_many_of_the_loaded_alerts_are_showing()
    {
        WithPanel(OneOfEverySpelling(), vm =>
        {
            Assert.Equal("11 of 11 alerts", vm.CountSummary);

            Chip(vm, "MEDIUM").IsEnabled = false;
            Assert.Equal("7 of 11 alerts", vm.CountSummary);
        });
    }

    [Fact]
    public void Repeats_collapse_on_rule_and_action_and_the_group_counts_itself()
    {
        var alerts = new[]
        {
            Alert("1", "WARN", rule: "CMD-ENV-DUMP", action: "block"),
            Alert("2", "WARN", rule: "CMD-ENV-DUMP", action: "block"),
            Alert("3", "HIGH", rule: "CMD-ENV-DUMP", action: "block"),
            Alert("4", "WARN", rule: "CMD-ENV-DUMP", action: "allow"),
            Alert("5", "LOW", rule: "OTHER", action: "block"),
        };

        WithPanel(alerts, vm =>
        {
            vm.CollapseRepeats = true;

            Assert.Equal(3, vm.Alerts.Count);
            Assert.Equal(3, vm.Alerts.First(a => a.RuleId == "CMD-ENV-DUMP" && a.Action == "block").RepeatCount);
            Assert.True(vm.Alerts.First(a => a.Action == "block" && a.RuleId == "CMD-ENV-DUMP").IsRepeated);
            Assert.Equal(1, vm.Alerts.Single(a => a.RuleId == "OTHER").RepeatCount);
            Assert.Equal("3 group(s) · 5 of 5 alerts", vm.CountSummary);

            vm.CollapseRepeats = false;
            Assert.Equal(5, vm.Alerts.Count);
        });
    }

    [Fact]
    public void An_empty_gateway_answer_says_so_and_a_hidden_list_says_it_is_hidden()
    {
        WithPanel(Array.Empty<GatewayAlert>(), vm =>
        {
            Assert.True(vm.IsEmpty);
            Assert.Equal("No alerts in the last poll", vm.EmptyTitle);
        });

        WithPanel(OneOfEverySpelling(), vm =>
        {
            vm.FilterText = "zzz-no-match";

            Assert.True(vm.IsEmpty);
            Assert.Equal("No alerts match the current filters", vm.EmptyTitle);
            Assert.Contains("11 alert(s) are loaded", vm.EmptyDetail, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void An_alert_row_reads_as_one_sentence_for_a_screen_reader()
    {
        var item = AlertItem.FromGateway(Alert("a", "warn", rule: "CMD-ENV-DUMP", title: "Environment variable dump"));

        Assert.StartsWith("WARN alert. CMD-ENV-DUMP. Environment variable dump.", item.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_chip_states_its_count_and_whether_it_is_shown()
    {
        var chip = new SeverityFilter("HIGH") { Count = 25 };

        Assert.Equal("HIGH severity filter: 25 alerts, shown", chip.AutomationName);

        chip.IsEnabled = false;
        chip.Count = 1;
        Assert.Equal("HIGH severity filter: 1 alert, hidden", chip.AutomationName);
        Assert.Equal(chip.AutomationName, chip.ToString());
    }

    // ------------------------------------------------------------------ from the gateway payload

    [Fact]
    public void The_headline_falls_back_from_title_to_details_to_action_to_a_placeholder()
    {
        Assert.Equal("Titled", AlertItem.FromGateway(Alert("a", "LOW", title: "Titled")).Headline);

        var withDetails = new GatewayAlert { Id = "b", Timestamp = Now, Details = "some details", Action = "block" };
        Assert.Equal("some details", AlertItem.FromGateway(withDetails).Headline);

        var withAction = new GatewayAlert { Id = "c", Timestamp = Now, Action = "block" };
        Assert.Equal("block", AlertItem.FromGateway(withAction).Headline);

        Assert.Equal("(finding)", AlertItem.FromGateway(new GatewayAlert { Id = "d", Timestamp = Now }).Headline);
    }

    [Fact]
    public void An_alert_with_no_id_still_gets_a_unique_key()
    {
        var a = AlertItem.FromGateway(new GatewayAlert { Timestamp = Now, Severity = "LOW" });
        var b = AlertItem.FromGateway(new GatewayAlert { Timestamp = Now, Severity = "LOW" });

        Assert.False(string.IsNullOrEmpty(a.Key));
        Assert.NotEqual(a.Key, b.Key);
    }

    [Theory]
    [InlineData(-2, "never")]
    [InlineData(0, "just now")]
    [InlineData(30, "30s ago")]
    [InlineData(300, "5m ago")]
    [InlineData(7200, "2h ago")]
    [InlineData(172800, "2d ago")]
    public void Relative_times_are_stated_in_the_largest_whole_unit(int secondsAgo, string expected)
    {
        var value = secondsAgo == -2 ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddSeconds(-secondsAgo);

        Assert.Equal(expected, AlertsPanelViewModel.Relative(value));
    }
}
