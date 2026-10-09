using DefenseClaw.App.Services;
using DefenseClaw.Core.Audit;

namespace DefenseClaw.App.Tests.Branding;

/// <summary>
/// Which shield the tray shows: the Mac's menu-bar precedence (<c>AppState.menuBarState</c>: paused, scanning, offline, alerting,
/// degraded, healthy), the count bucketed to what a 16 px icon can say, and the key that makes one icon of every distinct look.
/// Pure logic: nothing here draws.
/// </summary>
public sealed class TrayShieldStateTests
{
    private static GatewaySnapshot Snapshot(AppGatewayState state, bool paused = false, bool criticalInList = false) =>
        new() { State = state, IsPaused = paused, CriticalAlertCount = criticalInList ? 1 : 0 };

    private static AlertCounts Counts(int total, bool hasMore = false)
    {
        var at = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        return new AlertCounts(
            Enumerable.Range(0, total).Select(i => new AlertQueueItem("id-" + i, AuditSeverity.High, "scan-finding", null, "claudecode", at.AddSeconds(-i))).ToArray(),
            hasMore);
    }

    // ---- StateFor: the precedence table ---------------------------------------------------------------------------------

    /// <summary>
    /// One row per claim about the precedence. Columns: the gateway's state, paused, a scan running, the unacknowledged count
    /// (null: not read yet), a CRITICAL in the gateway's last alert list, and the shield that must win.
    /// </summary>
    public static TheoryData<AppGatewayState, bool, bool, int?, bool, ShieldState> Precedence => new()
    {
        // Paused beats everything: a scan, a stopped gateway, findings.
        { AppGatewayState.Running, true, true, 5, true, ShieldState.Paused },
        { AppGatewayState.GatewayStopped, true, false, 5, true, ShieldState.Paused },
        { AppGatewayState.Running, true, false, null, false, ShieldState.Paused },
        { AppGatewayState.Degraded, true, true, 0, false, ShieldState.Paused },

        // Scanning beats offline, alerting, degraded and healthy.
        { AppGatewayState.GatewayStopped, false, true, 5, true, ShieldState.Scanning },
        { AppGatewayState.NotInstalled, false, true, null, false, ShieldState.Scanning },
        { AppGatewayState.Running, false, true, 5, false, ShieldState.Scanning },
        { AppGatewayState.Degraded, false, true, 0, false, ShieldState.Scanning },
        { AppGatewayState.Running, false, true, 0, false, ShieldState.Scanning },

        // Offline beats alerting, however many findings and even a CRITICAL in the list.
        { AppGatewayState.GatewayStopped, false, false, 5, false, ShieldState.Stopped },
        { AppGatewayState.NotInstalled, false, false, 5, true, ShieldState.Stopped },
        { AppGatewayState.NotInitialized, false, false, 3, false, ShieldState.Stopped },
        { AppGatewayState.Unknown, false, false, 3, true, ShieldState.Stopped },
        { AppGatewayState.GatewayStopped, false, false, null, true, ShieldState.Stopped },
        { AppGatewayState.GatewayStopped, false, false, 0, false, ShieldState.Stopped },

        // Alerting beats degraded and healthy.
        { AppGatewayState.Running, false, false, 1, false, ShieldState.Critical },
        { AppGatewayState.Running, false, false, 500, false, ShieldState.Critical },
        { AppGatewayState.Degraded, false, false, 2, false, ShieldState.Critical },
        { AppGatewayState.WslGatewayDetected, false, false, 7, false, ShieldState.Critical },

        // Degraded beats healthy.
        { AppGatewayState.Degraded, false, false, 0, false, ShieldState.Warning },
        { AppGatewayState.WslGatewayDetected, false, false, 0, false, ShieldState.Warning },
        { AppGatewayState.Degraded, false, false, null, false, ShieldState.Warning },

        // Healthy.
        { AppGatewayState.Running, false, false, 0, false, ShieldState.Running },
        { AppGatewayState.Running, false, false, null, false, ShieldState.Running },

        // The count is the whole answer once it is known; until then the gateway's own last alert poll stands in for it.
        { AppGatewayState.Running, false, false, null, true, ShieldState.Critical },
        { AppGatewayState.Degraded, false, false, null, true, ShieldState.Critical },
        { AppGatewayState.Running, false, false, 0, true, ShieldState.Running },
        { AppGatewayState.Degraded, false, false, 0, true, ShieldState.Warning },
    };

    [Theory]
    [MemberData(nameof(Precedence))]
    public void The_shield_follows_the_Macs_precedence(AppGatewayState gateway, bool paused, bool scanning, int? unacknowledged, bool criticalInList, ShieldState expected)
    {
        var snapshot = Snapshot(gateway, paused, criticalInList);

        Assert.Equal(expected, ShieldIconFactory.StateFor(snapshot, unacknowledged, scanning));
    }

    [Fact]
    public void Every_combination_of_inputs_gets_the_highest_ranked_state_that_applies()
    {
        // The whole input space against the precedence written out as a list: the first that applies wins.
        var gateways = Enum.GetValues<AppGatewayState>();
        var counts = new int?[] { null, 0, 1, 9, 10, 500 };
        var combinations = 0;

        foreach (var gateway in gateways)
        {
            foreach (var paused in new[] { false, true })
            {
                foreach (var scanning in new[] { false, true })
                {
                    foreach (var count in counts)
                    {
                        foreach (var critical in new[] { false, true })
                        {
                            var online = gateway is AppGatewayState.Running or AppGatewayState.Degraded or AppGatewayState.WslGatewayDetected;
                            var alerting = count.HasValue ? count.Value > 0 : critical;
                            var ranked = new (bool Applies, ShieldState State)[]
                            {
                                (paused, ShieldState.Paused),
                                (scanning, ShieldState.Scanning),
                                (!online, ShieldState.Stopped),
                                (alerting, ShieldState.Critical),
                                (gateway != AppGatewayState.Running, ShieldState.Warning),
                                (true, ShieldState.Running),
                            };

                            var expected = ranked.First(rank => rank.Applies).State;
                            var actual = ShieldIconFactory.StateFor(Snapshot(gateway, paused, critical), count, scanning);

                            Assert.True(expected == actual, $"{gateway}, paused {paused}, scanning {scanning}, {count?.ToString() ?? "no count"}, critical {critical}: expected {expected}, got {actual}");
                            combinations++;
                        }
                    }
                }
            }
        }

        Assert.Equal(gateways.Length * 2 * 2 * counts.Length * 2, combinations);
    }

    [Fact]
    public void The_gateway_alone_decides_for_the_taskbar_button_which_knows_nothing_of_counts_or_scans()
    {
        Assert.Equal(ShieldState.Running, ShieldIconFactory.StateFor(Snapshot(AppGatewayState.Running)));
        Assert.Equal(ShieldState.Warning, ShieldIconFactory.StateFor(Snapshot(AppGatewayState.Degraded)));
        Assert.Equal(ShieldState.Stopped, ShieldIconFactory.StateFor(Snapshot(AppGatewayState.GatewayStopped)));
        Assert.Equal(ShieldState.Critical, ShieldIconFactory.StateFor(Snapshot(AppGatewayState.Running, criticalInList: true)));
        Assert.Equal(ShieldState.Paused, ShieldIconFactory.StateFor(Snapshot(AppGatewayState.Running, paused: true)));
        Assert.Equal(ShieldState.Stopped, ShieldIconFactory.StateFor(Snapshot(AppGatewayState.GatewayStopped, criticalInList: true)));
    }

    [Fact]
    public void A_null_snapshot_is_refused()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ShieldIconFactory.StateFor(null!));
        _ = Assert.Throws<ArgumentNullException>(() => ShieldIconFactory.StateFor(null!, 3, scanning: false));
    }

    // ---- The count's bucket ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(8, 8)]
    [InlineData(9, 9)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(99, 10)]
    [InlineData(500, 10)]
    [InlineData(int.MaxValue, 10)]
    public void A_count_is_none_one_to_nine_or_ten_and_more(int count, int bucket)
    {
        Assert.Equal(bucket, AlertBucket.For(count));
    }

    [Fact]
    public void Every_count_lands_in_one_of_eleven_buckets_and_ten_of_them_have_a_number()
    {
        var buckets = Enumerable.Range(-5, 1_000).Select(AlertBucket.For).Distinct().Order().ToArray();

        Assert.Equal(Enumerable.Range(0, 11), buckets);
        Assert.Equal(AlertBucket.MaxDigit + 1, AlertBucket.Overflow);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-3, "")]
    [InlineData(1, "1")]
    [InlineData(7, "7")]
    [InlineData(9, "9")]
    [InlineData(10, "9+")]
    [InlineData(441, "9+")]
    public void The_badge_says_the_digit_or_nine_plus(int count, string text)
    {
        Assert.Equal(text, AlertBucket.Text(count));
    }

    // ---- The key: one icon per distinct look ---------------------------------------------------------------------------

    [Theory]
    [InlineData(ShieldState.Running)]
    [InlineData(ShieldState.Stopped)]
    [InlineData(ShieldState.Warning)]
    [InlineData(ShieldState.Paused)]
    [InlineData(ShieldState.Scanning)]
    public void A_state_whose_badge_is_its_own_glyph_carries_no_number(ShieldState state)
    {
        Assert.Equal(new TrayShieldKey(state, 0), new TrayShieldKey(state, 3));
        Assert.Equal(AlertBucket.None, new TrayShieldKey(state, 500).Bucket);
    }

    [Fact]
    public void The_alerting_state_keeps_the_bucket_of_its_count()
    {
        Assert.Equal(0, new TrayShieldKey(ShieldState.Critical, 0).Bucket);
        Assert.Equal(3, new TrayShieldKey(ShieldState.Critical, 3).Bucket);
        Assert.Equal(AlertBucket.Overflow, new TrayShieldKey(ShieldState.Critical, 10).Bucket);
        Assert.Equal(new TrayShieldKey(ShieldState.Critical, 10), new TrayShieldKey(ShieldState.Critical, 500));
        Assert.NotEqual(new TrayShieldKey(ShieldState.Critical, 9), new TrayShieldKey(ShieldState.Critical, 10));
        Assert.NotEqual(new TrayShieldKey(ShieldState.Critical, 0), new TrayShieldKey(ShieldState.Critical, 1));
    }

    [Fact]
    public void A_bucket_passed_for_a_count_means_the_same_thing()
    {
        // The artwork and the key take "a count or a bucket": capping is idempotent.
        foreach (var bucket in Enumerable.Range(0, 11))
        {
            Assert.Equal(bucket, new TrayShieldKey(ShieldState.Critical, bucket).Bucket);
        }
    }

    [Fact]
    public void The_icons_a_tray_can_ever_need_are_sixteen_whatever_the_counts()
    {
        var gateways = Enum.GetValues<AppGatewayState>();
        var keys = new HashSet<TrayShieldKey>();
        foreach (var gateway in gateways)
        {
            foreach (var paused in new[] { false, true })
            {
                foreach (var scanning in new[] { false, true })
                {
                    foreach (var count in new int?[] { null }.Concat(Enumerable.Range(0, 1_200).Select(n => (int?)n)))
                    {
                        _ = keys.Add(ShieldIconFactory.KeyFor(Snapshot(gateway, paused, criticalInList: count is null && gateway == AppGatewayState.Running), count, scanning));
                    }
                }
            }
        }

        // Five states with one icon each, and the alerting state's bare "!", nine numbers and "9+".
        Assert.Equal(Enum.GetValues<ShieldState>().Length + AlertBucket.Overflow, keys.Count);
    }

    [Fact]
    public void KeyFor_puts_the_number_on_the_alerting_state_only()
    {
        var running = Snapshot(AppGatewayState.Running);

        Assert.Equal(new TrayShieldKey(ShieldState.Critical, 7), ShieldIconFactory.KeyFor(running, 7, scanning: false));
        Assert.Equal(AlertBucket.Overflow, ShieldIconFactory.KeyFor(running, 441, scanning: false).Bucket);
        Assert.Equal(new TrayShieldKey(ShieldState.Paused, 0), ShieldIconFactory.KeyFor(Snapshot(AppGatewayState.Running, paused: true), 7, scanning: false));
        Assert.Equal(new TrayShieldKey(ShieldState.Scanning, 0), ShieldIconFactory.KeyFor(running, 7, scanning: true));
        Assert.Equal(new TrayShieldKey(ShieldState.Stopped, 0), ShieldIconFactory.KeyFor(Snapshot(AppGatewayState.GatewayStopped), 7, scanning: false));
        Assert.Equal(new TrayShieldKey(ShieldState.Running, 0), ShieldIconFactory.KeyFor(running, 0, scanning: false));

        // No count read yet, and a CRITICAL in the gateway's list: alerting, with the bare mark.
        Assert.Equal(new TrayShieldKey(ShieldState.Critical, 0), ShieldIconFactory.KeyFor(Snapshot(AppGatewayState.Running, criticalInList: true), unacknowledged: null, scanning: false));
    }

    [Fact]
    public void Paused_and_scanning_have_colours_of_their_own()
    {
        Assert.Equal(ShieldArtwork.StoppedSlate, ShieldIconFactory.ColorFor(ShieldState.Paused));
        Assert.Equal(ShieldArtwork.ScanningBlue, ShieldIconFactory.ColorFor(ShieldState.Scanning));
        Assert.Equal(ShieldArtwork.CriticalRed, ShieldIconFactory.ColorFor(ShieldState.Critical));
    }

    // ---- The tooltip: the state and the count --------------------------------------------------------------------------

    [Fact]
    public void The_tooltip_names_the_state_and_the_real_count_when_the_icon_stops_at_nine_plus()
    {
        var running = Snapshot(AppGatewayState.Running);

        Assert.Equal("DefenseClaw — 3 unacknowledged findings\nRunning", AlertCountPresentation.TrayTooltip(running, Counts(3)));
        Assert.Equal("DefenseClaw — 441 unacknowledged findings\nRunning", AlertCountPresentation.TrayTooltip(running, Counts(441)));
        Assert.Equal("DefenseClaw — 500+ unacknowledged findings\nRunning", AlertCountPresentation.TrayTooltip(running, Counts(500, hasMore: true)));
        Assert.Equal("DefenseClaw — no unacknowledged findings\nGateway stopped", AlertCountPresentation.TrayTooltip(Snapshot(AppGatewayState.GatewayStopped), Counts(0)));
    }

    [Fact]
    public void While_a_scan_runs_the_tooltip_says_so_before_the_gateway_state()
    {
        var running = Snapshot(AppGatewayState.Running);

        Assert.Equal("DefenseClaw — 3 unacknowledged findings\nScanning — Running", AlertCountPresentation.TrayTooltip(running, Counts(3), scanning: true));
        Assert.Equal("DefenseClaw — Scanning — Running", AlertCountPresentation.TrayTooltip(running, counts: null, scanning: true));
        Assert.Equal("DefenseClaw — no unacknowledged findings\nScanning — Gateway stopped", AlertCountPresentation.TrayTooltip(Snapshot(AppGatewayState.GatewayStopped), Counts(0), scanning: true));
    }

    [Fact]
    public void A_pause_outranks_a_scan_in_the_tooltip_as_it_does_on_the_icon()
    {
        var paused = Snapshot(AppGatewayState.Running, paused: true);

        var tooltip = AlertCountPresentation.TrayTooltip(paused, Counts(3), scanning: true);

        Assert.Equal("DefenseClaw — 3 unacknowledged findings\nMonitoring paused", tooltip);
        Assert.DoesNotContain("Scanning", tooltip, StringComparison.Ordinal);
        Assert.Equal("DefenseClaw — Monitoring paused", AlertCountPresentation.TrayTooltip(paused, counts: null, scanning: true));
    }

    [Fact]
    public void The_longest_tooltip_is_well_inside_the_127_characters_the_shell_keeps()
    {
        var longest = AlertCountPresentation.TrayTooltip(Snapshot(AppGatewayState.WslGatewayDetected), Counts(500, hasMore: true), scanning: true);

        Assert.True(longest.Length < 90, longest);
    }

    [Fact]
    public void Every_icon_state_has_a_tooltip_that_says_which_it_is()
    {
        // For each look the tray can have, the words say the same thing as the glyph does.
        var cases = new (GatewaySnapshot Snapshot, AlertCounts? Counts, bool Scanning, string State)[]
        {
            (Snapshot(AppGatewayState.Running), Counts(0), false, "Running"),
            (Snapshot(AppGatewayState.Degraded), Counts(0), false, "Degraded"),
            (Snapshot(AppGatewayState.GatewayStopped), Counts(0), false, "Gateway stopped"),
            (Snapshot(AppGatewayState.Running, paused: true), Counts(0), false, "Monitoring paused"),
            (Snapshot(AppGatewayState.Running), Counts(0), true, "Scanning"),
            (Snapshot(AppGatewayState.Running), Counts(12), false, "12 unacknowledged findings"),
        };

        foreach (var (snapshot, counts, scanning, state) in cases)
        {
            Assert.Contains(state, AlertCountPresentation.TrayTooltip(snapshot, counts, scanning), StringComparison.Ordinal);
        }
    }
}
