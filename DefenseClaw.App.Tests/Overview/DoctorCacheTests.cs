using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// <c>doctor_cache.json</c>, the file <c>defenseclaw doctor</c> rewrites at the end of every run, as the
/// Overview doctor card reads it: schema, tolerance, and the fifteen-minute stale rule.
/// </summary>
public sealed class DoctorCacheTests : IDisposable
{
    private const string RealisticCache = """
        {
          "passed": 24, "failed": 1, "warned": 2, "skipped": 3,
          "captured_at": "2026-09-29T08:15:30Z",
          "checks": [
            {"status": "pass", "label": "Config file", "detail": "config.yaml parses"},
            {"status": "warn", "label": "Splunk HEC", "detail": "token env var is unset"},
            {"status": "fail", "label": "Gateway API", "detail": "connection refused"},
            {"status": "skip", "label": "Docker"},
            {"status": "warn", "label": "Scanner", "detail": ""}
          ]
        }
        """;

    private readonly TempDirectory _temp = new();
    private readonly DefenseClaw.App.Services.AppServices _services;

    public DoctorCacheTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static string CacheJson(int passed, int failed, int warned, int skipped, DateTimeOffset? capturedAt, string checks = "[]")
    {
        var captured = capturedAt is { } at
            ? "\"captured_at\": \"" + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\", "
            : string.Empty;

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"passed\": {passed}, \"failed\": {failed}, \"warned\": {warned}, \"skipped\": {skipped}, {captured}\"checks\": {checks}}}");
    }

    private static string Checks(params (string Status, string Label)[] rows) =>
        "[" + string.Join(",", rows.Select(r => $"{{\"status\": \"{r.Status}\", \"label\": \"{r.Label}\", \"detail\": \"d\"}}")) + "]";

    private async Task<OverviewPanelViewModel> LoadAsync(string? json)
    {
        if (json is not null)
        {
            _ = _temp.WriteFile("doctor_cache.json", json);
        }

        var vm = new OverviewPanelViewModel(_services);
        await vm.ReloadDoctorCacheAsync(CancellationToken.None);
        return vm;
    }

    // ------------------------------------------------------------------ the reader

    [Fact]
    public void The_realistic_cache_is_read_with_its_counts_time_and_checks()
    {
        var snapshot = DoctorCacheReader.Parse(RealisticCache);

        Assert.Equal((24, 1, 2, 3), (snapshot.Passed, snapshot.Failed, snapshot.Warned, snapshot.Skipped));
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 8, 15, 30, TimeSpan.Zero), snapshot.CapturedAt);
        Assert.Equal(
            new[] { "Config file", "Splunk HEC", "Gateway API", "Docker", "Scanner" },
            snapshot.Checks.Select(c => c.Label).ToArray());
        Assert.Equal(new[] { "pass", "warn", "fail", "skip", "warn" }, snapshot.Checks.Select(c => c.Status).ToArray());
        Assert.Equal("connection refused", snapshot.Checks[2].Detail);
    }

    [Fact]
    public void Problems_are_the_failures_then_the_warnings_each_in_file_order()
    {
        var snapshot = DoctorCacheReader.Parse(RealisticCache);

        Assert.Equal(new[] { "Gateway API", "Splunk HEC", "Scanner" }, snapshot.Problems().Select(c => c.Label).ToArray());
    }

    [Theory]
    [InlineData("pass", "Ok", "PASS")]
    [InlineData("fail", "Bad", "FAIL")]
    [InlineData("warn", "Warn", "WARN")]
    [InlineData("skip", "Neutral", "SKIP")]
    public void A_check_carries_its_tone_and_upper_case_status(string status, string key, string text)
    {
        var row = new DoctorCheckRow { Label = "x", Status = status };

        Assert.Equal(key, row.StatusKey);
        Assert.Equal(text, row.StatusText);
    }

    [Fact]
    public void A_check_reads_as_a_sentence_with_or_without_detail()
    {
        Assert.Equal("FAIL: Gateway API. connection refused", new DoctorCheckRow { Label = "Gateway API", Status = "fail", Detail = "connection refused" }.ToString());
        Assert.Equal("WARN: Scanner", new DoctorCheckRow { Label = "Scanner", Status = "warn" }.ToString());
    }

    [Fact]
    public void Status_words_are_lower_cased_and_unknown_or_absent_ones_become_skip()
    {
        var snapshot = DoctorCacheReader.Parse("""
            {"checks": [
              {"status": "FAIL", "label": "upper"},
              {"status": "Pass", "label": "mixed"},
              {"status": "error", "label": "unknown word"},
              {"status": 3, "label": "not a string"},
              {"label": "no status"}
            ]}
            """);

        Assert.Equal(new[] { "fail", "pass", "skip", "skip", "skip" }, snapshot.Checks.Select(c => c.Status).ToArray());
    }

    [Fact]
    public void A_check_with_no_label_is_counted_by_the_cli_but_never_listed_and_junk_entries_are_skipped()
    {
        var snapshot = DoctorCacheReader.Parse("""
            {"passed": 3, "checks": [
              {"status": "pass", "label": ""},
              {"status": "pass"},
              "a string",
              42,
              null,
              [1, 2],
              {"status": "pass", "label": "real one"}
            ]}
            """);

        Assert.Equal(3, snapshot.Passed);
        Assert.Equal(new[] { "real one" }, snapshot.Checks.Select(c => c.Label).ToArray());
    }

    [Fact]
    public void An_empty_object_is_a_snapshot_of_nothing()
    {
        var snapshot = DoctorCacheReader.Parse("{}");

        Assert.Equal((0, 0, 0, 0), (snapshot.Passed, snapshot.Failed, snapshot.Warned, snapshot.Skipped));
        Assert.Null(snapshot.CapturedAt);
        Assert.Empty(snapshot.Checks);
    }

    [Fact]
    public void Counts_that_are_negative_fractional_or_not_numbers_read_as_zero()
    {
        var snapshot = DoctorCacheReader.Parse("""{"passed": -4, "failed": 1.5, "warned": "2", "skipped": null}""");

        Assert.Equal((0, 0, 0, 0), (snapshot.Passed, snapshot.Failed, snapshot.Warned, snapshot.Skipped));
    }

    [Theory]
    [InlineData("2026-09-29T08:15:30Z", "2026-09-29T08:15:30+00:00")]
    [InlineData("2026-09-29T08:15:30", "2026-09-29T08:15:30+00:00")]
    [InlineData("2026-09-29T10:15:30+02:00", "2026-09-29T10:15:30+02:00")]
    [InlineData("2026-09-29T08:15:30.123456Z", "2026-09-29T08:15:30.1234560+00:00")]
    public void The_capture_time_is_read_as_utc_unless_it_carries_an_offset(string captured, string expected)
    {
        var snapshot = DoctorCacheReader.Parse($$"""{"captured_at": "{{captured}}"}""");

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), snapshot.CapturedAt);
    }

    [Theory]
    [InlineData("""{"captured_at": "yesterday"}""")]
    [InlineData("""{"captured_at": ""}""")]
    [InlineData("""{"captured_at": 1759133730}""")]
    [InlineData("""{"captured_at": null}""")]
    public void An_unusable_capture_time_is_unknown_rather_than_an_error(string json)
    {
        Assert.Null(DoctorCacheReader.Parse(json).CapturedAt);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Valid_json_that_is_not_an_object_is_an_error(string json)
    {
        Assert.Throws<JsonException>(() => DoctorCacheReader.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("{\"passed\": 1,")]
    public void Text_that_is_not_json_is_an_error(string text)
    {
        Assert.ThrowsAny<JsonException>(() => DoctorCacheReader.Parse(text));
    }

    [Fact]
    public void Parsing_requires_text()
    {
        Assert.Throws<ArgumentNullException>(() => DoctorCacheReader.Parse(null!));
    }

    // ------------------------------------------------------------------ stale after fifteen minutes

    [Fact]
    public void The_stale_window_is_fifteen_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), OverviewPanelViewModel.DoctorStaleAfter);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(14, false)]
    [InlineData(15, false)]
    [InlineData(15.01, true)]
    [InlineData(16, true)]
    [InlineData(600, true)]
    [InlineData(-3, false)]
    public void Results_are_stale_only_once_older_than_the_window(double minutesAgo, bool stale)
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new DoctorCacheSnapshot(1, 0, 0, 0, now - TimeSpan.FromMinutes(minutesAgo), Array.Empty<DoctorCheckRow>());

        Assert.Equal(stale, snapshot.IsStale(now, OverviewPanelViewModel.DoctorStaleAfter));
    }

    [Fact]
    public void A_snapshot_with_no_capture_time_is_treated_as_stale()
    {
        var snapshot = new DoctorCacheSnapshot(1, 0, 0, 0, null, Array.Empty<DoctorCheckRow>());

        Assert.True(snapshot.IsStale(DateTimeOffset.UtcNow, OverviewPanelViewModel.DoctorStaleAfter));
    }

    // ------------------------------------------------------------------ the card

    [Fact]
    public async Task With_no_cache_file_the_card_says_doctor_was_never_run_without_an_error()
    {
        var vm = await LoadAsync(null);

        Assert.True(vm.DoctorIsEmpty);
        Assert.False(vm.DoctorHasData);
        Assert.False(vm.DoctorHasReadError);
        Assert.Equal(string.Empty, vm.DoctorVerdict);
        Assert.Equal("Neutral", vm.DoctorStateKey);
        Assert.Empty(vm.DoctorChecks);
    }

    [Fact]
    public async Task A_recent_cache_with_a_failure_is_bad_lists_its_problems_and_is_not_stale()
    {
        var checks = Checks(("pass", "ok one"), ("warn", "warn one"), ("fail", "fail one"));
        var vm = await LoadAsync(CacheJson(24, 1, 1, 0, DateTimeOffset.UtcNow.AddMinutes(-3), checks));

        Assert.True(vm.DoctorHasData);
        Assert.False(vm.DoctorIsEmpty);
        Assert.False(vm.DoctorIsStale);
        Assert.Equal("1 check failed", vm.DoctorVerdict);
        Assert.Equal("Bad", vm.DoctorStateKey);
        Assert.Equal("24 pass · 1 fail · 1 warn · 0 skip", vm.DoctorSummary);
        Assert.Equal(new[] { "fail one", "warn one" }, vm.DoctorChecks.Select(c => c.Label).ToArray());
        Assert.True(vm.DoctorHasProblems);
        Assert.Equal(new[] { "PASS", "FAIL", "WARN", "SKIP" }, vm.DoctorTiles.Select(t => t.Label).ToArray());
        Assert.Equal(new[] { "24", "1", "1", "0" }, vm.DoctorTiles.Select(t => t.Value).ToArray());
    }

    [Theory]
    [InlineData(2, 0, "2 checks failed", "Bad")]
    [InlineData(0, 1, "1 warning", "Warn")]
    [InlineData(0, 4, "4 warnings", "Warn")]
    public async Task The_verdict_counts_failures_before_warnings(int failed, int warned, string verdict, string key)
    {
        var vm = await LoadAsync(CacheJson(10, failed, warned, 0, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(verdict, vm.DoctorVerdict);
        Assert.Equal(key, vm.DoctorStateKey);
    }

    [Fact]
    public async Task A_fresh_all_green_is_ok()
    {
        var vm = await LoadAsync(CacheJson(30, 0, 0, 2, DateTimeOffset.UtcNow.AddMinutes(-2)));

        Assert.Equal("All checks passed", vm.DoctorVerdict);
        Assert.Equal("Ok", vm.DoctorStateKey);
        Assert.False(vm.DoctorIsStale);
        Assert.False(vm.DoctorHasProblems);
    }

    [Fact]
    public async Task An_all_green_older_than_fifteen_minutes_is_stale_and_neutral_not_green()
    {
        var vm = await LoadAsync(CacheJson(30, 0, 0, 2, DateTimeOffset.UtcNow.AddMinutes(-16)));

        Assert.True(vm.DoctorIsStale);
        Assert.Equal("All checks passed", vm.DoctorVerdict);
        Assert.Equal("Neutral", vm.DoctorStateKey);
    }

    [Fact]
    public async Task A_failure_stays_bad_when_stale_and_the_card_still_says_so()
    {
        var vm = await LoadAsync(CacheJson(30, 1, 0, 0, DateTimeOffset.UtcNow.AddHours(-3)));

        Assert.True(vm.DoctorIsStale);
        Assert.Equal("Bad", vm.DoctorStateKey);
    }

    [Fact]
    public async Task A_cache_just_inside_the_window_is_not_stale()
    {
        var vm = await LoadAsync(CacheJson(30, 0, 0, 0, DateTimeOffset.UtcNow.AddMinutes(-14)));

        Assert.False(vm.DoctorIsStale);
        Assert.Equal("Ok", vm.DoctorStateKey);
    }

    [Fact]
    public async Task A_cache_with_no_capture_time_is_stale_and_says_the_time_is_unknown()
    {
        var vm = await LoadAsync(CacheJson(30, 0, 0, 0, capturedAt: null));

        Assert.True(vm.DoctorIsStale);
        Assert.Equal("capture time unknown", vm.DoctorAsOfText);
        Assert.Equal("Neutral", vm.DoctorStateKey);
    }

    [Fact]
    public async Task A_cache_of_zero_checks_says_none_were_recorded()
    {
        var vm = await LoadAsync(CacheJson(0, 0, 0, 0, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal("No checks recorded", vm.DoctorVerdict);
        Assert.Equal("Neutral", vm.DoctorStateKey);
    }

    [Fact]
    public async Task More_problems_than_the_card_shows_are_summarised_as_and_n_more()
    {
        var rows = Enumerable.Range(1, 11).Select(i => ("fail", $"check {i}")).ToArray();
        var vm = await LoadAsync(CacheJson(0, 11, 0, 0, DateTimeOffset.UtcNow.AddMinutes(-1), Checks(rows)));

        Assert.Equal(8, vm.DoctorChecks.Count);
        Assert.StartsWith("and 3 more", vm.DoctorProblemsNote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public async Task A_cache_that_cannot_be_read_is_an_error_state_distinct_from_never_run(string json)
    {
        var vm = await LoadAsync(json);

        Assert.True(vm.DoctorHasReadError);
        Assert.False(vm.DoctorIsEmpty);
        Assert.False(vm.DoctorHasData);
        Assert.Contains("doctor_cache.json", vm.DoctorReadError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_good_cache_replaces_an_earlier_read_error_and_a_deleted_one_clears_the_card()
    {
        var vm = await LoadAsync("{not json");
        Assert.True(vm.DoctorHasReadError);

        _ = _temp.WriteFile("doctor_cache.json", CacheJson(5, 0, 0, 0, DateTimeOffset.UtcNow.AddMinutes(-1)));
        await vm.ReloadDoctorCacheAsync(CancellationToken.None);
        Assert.False(vm.DoctorHasReadError);
        Assert.True(vm.DoctorHasData);

        File.Delete(_temp.File("doctor_cache.json"));
        await vm.ReloadDoctorCacheAsync(CancellationToken.None);
        Assert.False(vm.DoctorHasData);
        Assert.True(vm.DoctorIsEmpty);
    }

    [Fact]
    public async Task The_run_doctor_command_is_plain_doctor_never_fix()
    {
        var vm = await LoadAsync(null);

        Assert.Equal("defenseclaw doctor", vm.DoctorCommandText);
        Assert.DoesNotContain("--fix", vm.DoctorCommandText, StringComparison.Ordinal);
    }
}
