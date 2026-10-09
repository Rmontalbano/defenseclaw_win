using System.Text.Json.Nodes;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.App.Tests.Settings;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Unread;

/// <summary>
/// Where the operator last looked in each stream panel (CUST-265), as the settings file keeps it: a number under the panel's id in a <c>seen</c>
/// section. The acceptance is that the marker survives a restart, so the round trip is through a second store over the same file - what a new
/// process sees - and the rest is what a hand-edited or newer file must not be able to do to it.
/// </summary>
public sealed class SeenSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string NewPath() => Path.Combine(_temp.Path, "DefenseClaw.App", Guid.NewGuid().ToString("N") + ".json");

    private static AppSettingsStore Fresh(string path) => AppSettingsStore.OpenFresh(path);

    private static void WriteFile(string path, string content)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static SeenSettings Markers(params (string Panel, long Marker)[] markers) =>
        new() { Markers = markers.ToDictionary(m => m.Panel, m => m.Marker) };

    // ------------------------------------------------------------------ persistence

    [Fact]
    public void A_fresh_install_has_looked_at_nothing()
    {
        var settings = Fresh(NewPath()).Current;

        Assert.Empty(settings.Seen.Markers);
        Assert.False(settings.Seen.TryGet(UnreadCountsService.AuditId, out _));
        Assert.Equal(AppSettings.Defaults.Seen, settings.Seen);
    }

    [Fact]
    public void The_markers_survive_a_restart_as_numbers_under_the_panels_ids()
    {
        var path = NewPath();
        var seen = Markers((UnreadCountsService.AuditId, 612_345), (UnreadCountsService.ActivityId, 639_000_000_000_000_000), (UnreadCountsService.AiDiscoveryId, 639_000_000_000_000_001));

        Assert.True(Fresh(path).Update(s => s with { Seen = seen }));

        // A new process: nothing shared with the store that wrote it.
        var reloaded = Fresh(path).Current.Seen;
        Assert.Equal(seen, reloaded);
        Assert.True(reloaded.TryGet(UnreadCountsService.AuditId, out var audit));
        Assert.Equal(612_345, audit);

        var section = ReadJson(path)["seen"]!.AsObject();
        Assert.Equal(612_345, (long)section["audit"]!);
        Assert.Equal(639_000_000_000_000_000, (long)section["activity"]!);
        Assert.Equal(639_000_000_000_000_001, (long)section["ai-discovery"]!);
    }

    [Fact]
    public void A_marker_of_zero_is_a_panel_that_was_looked_at_while_empty_not_one_that_was_never_looked_at()
    {
        var path = NewPath();

        _ = Fresh(path).Update(s => s with { Seen = Markers((UnreadCountsService.AuditId, 0)) });

        var reloaded = Fresh(path).Current.Seen;
        Assert.True(reloaded.TryGet(UnreadCountsService.AuditId, out var marker));
        Assert.Equal(0, marker);
        Assert.False(reloaded.TryGet(UnreadCountsService.ActivityId, out _));
    }

    [Fact]
    public void Moving_one_marker_writes_only_the_seen_section_and_leaves_every_other_member_where_it_was()
    {
        var path = NewPath();
        WriteFile(path, """
            {
              "schemaVersion": 1,
              "monitoring": { "healthIntervalSeconds": 9, "somethingNew": true },
              "seen": { "audit": 10, "logs": 77 },
              "fromANewerBuild": { "keep": "me" }
            }
            """);

        Assert.True(Fresh(path).Update(s => s with { Seen = s.Seen.With(UnreadCountsService.AuditId, 25) }));

        var json = ReadJson(path);
        Assert.Equal(25, (long)json["seen"]!["audit"]!);

        // A panel this build does not track keeps its member, and so does everything outside the section.
        Assert.Equal(77, (long)json["seen"]!["logs"]!);
        Assert.True((bool)json["monitoring"]!["somethingNew"]!);
        Assert.Equal(9, (int)json["monitoring"]!["healthIntervalSeconds"]!);
        Assert.Equal("me", (string?)json["fromANewerBuild"]!["keep"]);
    }

    [Fact]
    public void Writing_the_markers_the_store_already_holds_is_not_a_change_so_nothing_is_written_or_announced()
    {
        var path = NewPath();
        var store = Fresh(path);
        _ = store.Update(s => s with { Seen = Markers((UnreadCountsService.AuditId, 5)) });
        var written = File.GetLastWriteTimeUtc(path);
        var announced = 0;
        store.Changed += (_, _) => announced++;

        // The same markers in another dictionary, in another order.
        Assert.True(store.Update(s => s with { Seen = Markers((UnreadCountsService.AuditId, 5)) }));
        Assert.True(store.Update(s => s with { Seen = s.Seen.With(UnreadCountsService.AuditId, 5) }));

        Assert.Equal(0, announced);
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void A_moved_marker_is_announced_as_the_seen_section_and_nothing_else()
    {
        var store = Fresh(NewPath());
        AppSettingsChangedEventArgs? seen = null;
        store.Changed += (_, e) => seen = e;

        _ = store.Update(s => s with { Seen = s.Seen.With(UnreadCountsService.ActivityId, 123) });

        Assert.NotNull(seen);
        Assert.Equal(AppSettingsSections.Seen, seen!.Sections);
        Assert.True(AppSettingsSections.All.HasFlag(AppSettingsSections.Seen));
    }

    // ------------------------------------------------------------------ a file that is not what it should be

    [Fact]
    public void A_hand_edited_section_keeps_what_is_sound_and_drops_the_rest()
    {
        var path = NewPath();
        WriteFile(path, """
            {
              "seen": {
                "audit": 42,
                "activity": "tomorrow",
                "ai-discovery": -5,
                "": 9,
                "nested": { "x": 1 },
                "list": [ 1, 2 ],
                "float": 7.0,
                "flag": true
              }
            }
            """);

        var seen = Fresh(path).Current.Seen;

        Assert.True(seen.TryGet("audit", out var audit));
        Assert.Equal(42, audit);
        Assert.False(seen.TryGet("activity", out _));      // wrong type: not looked at, not 0
        Assert.False(seen.TryGet("ai-discovery", out _));  // negative: not a position
        Assert.False(seen.TryGet(string.Empty, out _));
        Assert.False(seen.TryGet("nested", out _));
        Assert.False(seen.TryGet("list", out _));
        Assert.False(seen.TryGet("flag", out _));
        Assert.True(seen.TryGet("float", out var rounded));
        Assert.Equal(7, rounded);
    }

    [Theory]
    [InlineData("""{ "seen": [ 1, 2, 3 ] }""")]
    [InlineData("""{ "seen": "audit" }""")]
    [InlineData("""{ "seen": null }""")]
    [InlineData("""{ "seen": 5 }""")]
    public void A_section_that_is_not_an_object_reads_as_nothing_looked_at(string json)
    {
        var path = NewPath();
        WriteFile(path, json);

        Assert.Empty(Fresh(path).Current.Seen.Markers);
    }

    [Fact]
    public void The_section_cannot_grow_without_bound_and_its_ids_cannot_be_long()
    {
        var markers = new Dictionary<string, long>();
        for (var i = 0; i < SeenSettings.MaxPanels + 20; i++)
        {
            markers["panel-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)] = i;
        }

        markers[new string('x', SeenSettings.MaxIdLength + 1)] = 1;
        markers[new string('y', SeenSettings.MaxIdLength)] = 2;

        var seen = new SeenSettings { Markers = markers };

        Assert.True(seen.Markers.Count <= SeenSettings.MaxPanels);
        Assert.DoesNotContain(seen.Markers.Keys, key => key.Length > SeenSettings.MaxIdLength);
        Assert.Equal(32, SeenSettings.MaxPanels);
    }

    [Fact]
    public void Equality_is_by_content_whatever_order_the_markers_were_added_in()
    {
        var a = new SeenSettings { Markers = new Dictionary<string, long> { ["audit"] = 1, ["activity"] = 2 } };
        var b = new SeenSettings { Markers = new Dictionary<string, long> { ["activity"] = 2, ["audit"] = 1 } };
        var c = new SeenSettings { Markers = new Dictionary<string, long> { ["activity"] = 2, ["audit"] = 3 } };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.Same(a, a.With("audit", 1));
        Assert.NotSame(a, a.With("audit", 4));
        Assert.Equal(1, a.Markers["audit"]);
    }
}

/// <summary>
/// The markers move in the background (a tick baselines a panel, a panel is left), so the Settings page - which copies the store into its form
/// whenever the store changes - must not take a marker for a change to anything it shows, or a half-typed field would be wiped by a sidebar count.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SettingsPageSeenMarkersTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public SettingsPageSeenMarkersTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private SettingsPanelViewModel OpenPage()
    {
        var page = UiThread.Run(() => new SettingsPanelViewModel(_services, hooks: null, new FakePlatform().Build()));
        UiThread.Run(() => page.SetActive(true));
        return page;
    }

    private static string Settled(SettingsPanelViewModel page) =>
        UiThread.Run(() =>
        {
            UiThread.Settle();
            return page.ArchivePathText;
        });

    [Fact]
    public void A_marker_moved_while_a_path_is_half_typed_leaves_the_field_alone()
    {
        var page = OpenPage();
        try
        {
            UiThread.Run(() => page.ArchivePathText = @"D:\archive\half-typ");

            _ = _services.Settings.Update(s => s with { Seen = s.Seen.With(UnreadCountsService.AuditId, 99) });
            _ = _services.Settings.Update(s => s with { Seen = s.Seen.With(UnreadCountsService.ActivityId, 1), Palette = new PaletteSettings { RecentCommandIds = new[] { "cli.doctor" } } });

            Assert.Equal(@"D:\archive\half-typ", Settled(page));
        }
        finally
        {
            UiThread.Run(() => page.SetActive(false));
        }
    }

    [Fact]
    public void A_change_to_anything_the_page_shows_still_reaches_it_even_beside_a_marker()
    {
        var page = OpenPage();
        try
        {
            _ = _services.Settings.Update(s => s with
            {
                Seen = s.Seen.With(UnreadCountsService.AuditId, 7),
                Archive = new ArchiveSettings { Path = @"D:\archive\audit.db" },
            });

            Assert.Equal(@"D:\archive\audit.db", Settled(page));
        }
        finally
        {
            UiThread.Run(() => page.SetActive(false));
        }
    }
}

/// <summary>How the count is worded: the capsule, the sentence a screen reader appends to the entry's name, and the cap.</summary>
public sealed class UnreadPresentationTests
{
    [Theory]
    [InlineData(-3, "")]
    [InlineData(0, "")]
    [InlineData(1, "1")]
    [InlineData(7, "7")]
    [InlineData(98, "98")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(250, "99+")]
    public void The_capsule_shows_the_count_to_99_then_99_plus_and_nothing_for_zero(int count, string expected)
    {
        Assert.Equal(expected, UnreadPresentation.Badge(count));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 new since last visit")]
    [InlineData(7, "7 new since last visit")]
    [InlineData(99, "99 new since last visit")]
    [InlineData(100, "99+ new since last visit")]
    public void The_sentence_a_screen_reader_hears_is_N_new_since_last_visit(int count, string expected)
    {
        Assert.Equal(expected, UnreadPresentation.Sentence(count));
    }

    [Theory]
    [InlineData("Audit", 0, "Audit")]
    [InlineData("Audit", 7, "Audit, 7 new since last visit")]
    [InlineData("Activity", 1, "Activity, 1 new since last visit")]
    [InlineData("AI Discovery", 100, "AI Discovery, 99+ new since last visit")]
    public void The_entrys_name_carries_the_sentence_and_is_plain_when_nothing_is_new(string title, int count, string expected)
    {
        Assert.Equal(expected, UnreadPresentation.NavigationName(title, count));
    }

    [Fact]
    public void The_state_is_the_capsule_and_the_sentence_together_and_none_for_zero()
    {
        Assert.False(UnreadPresentation.StateOf(0).IsShown);
        Assert.Equal(UnreadBadgeState.None, UnreadPresentation.StateOf(0));

        var seven = UnreadPresentation.StateOf(7);
        Assert.True(seven.IsShown);
        Assert.Equal("7", seven.Text);
        Assert.Equal("7 new since last visit", seven.Description);

        Assert.Equal(99, UnreadCountsService.Cap);
        Assert.Equal(100, UnreadCountsService.CountLimit);
    }
}

/// <summary>
/// When each AI Discovery component was first seen, read from the state file's text (CUST-265): the number the AI Discovery capsule is counted from.
/// The grouping is the panel's own (a product per vendor and product, a model per model id), and the time is <c>first_seen</c>, which the scanner
/// carries forward - not <c>state</c>, which says only what changed since the previous scan.
/// </summary>
public sealed class AiDiscoveryNoveltyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Signal(string vendor, string product, DateTimeOffset? firstSeen, string state = "seen", string category = "ai_cli", string? model = null, string detector = "process") =>
        $$"""
        {
          "signal_id": "{{Guid.NewGuid():N}}",
          "vendor": "{{vendor}}",
          "product": "{{product}}",
          "category": "{{category}}",
          "detector": "{{detector}}",
          "state": "{{state}}",
          {{(firstSeen is { } at ? $"\"first_seen\": \"{at.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffffff}Z\"," : string.Empty)}}
          {{(model is null ? string.Empty : $"\"model\": {{ \"id\": \"{model}\", \"status\": \"installed\" }},")}}
          "confidence": 0.9
        }
        """;

    private static string StateFile(params string[] signals) =>
        "{ \"updated_at\": \"2026-10-01T12:00:00Z\", \"signals\": { " +
        string.Join(", ", signals.Select((s, i) => $"\"fp{i}\": {s}")) + " } }";

    [Fact]
    public void One_component_per_product_however_many_signals_back_it_and_the_earliest_first_seen_counts()
    {
        var json = StateFile(
            Signal("Anthropic", "Claude Code", T0.AddDays(-30), detector: "process"),
            Signal("Anthropic", "Claude Code", T0.AddDays(-2), detector: "config"),
            Signal("ANTHROPIC", "claude code", T0.AddDays(-1), detector: "mcp"),
            Signal("Cursor", "Cursor", T0.AddHours(-1), state: "new"));

        var head = AiDiscoveryNovelty.Parse(json);

        Assert.Equal(2, head.FirstSeenTicks.Count);
        Assert.Equal(T0.AddDays(-30).UtcTicks, head.FirstSeenTicks[0]);
        Assert.Equal(T0.AddHours(-1).UtcTicks, head.FirstSeenTicks[1]);
    }

    [Fact]
    public void A_new_detector_on_a_long_known_product_does_not_make_the_product_new()
    {
        var json = StateFile(
            Signal("Anthropic", "Claude Code", T0.AddDays(-30)),
            Signal("Anthropic", "Claude Code", T0.AddMinutes(-1), state: "new", detector: "mcp"));

        var head = AiDiscoveryNovelty.Parse(json);

        Assert.Equal(0, head.CountAfter(T0.AddDays(-1).UtcTicks));
        Assert.Equal(1, head.CountAfter(T0.AddDays(-31).UtcTicks));
    }

    [Fact]
    public void A_local_model_is_one_component_per_model_id_and_a_product_of_the_same_name_is_another()
    {
        var json = StateFile(
            Signal("Ollama", "Ollama", T0.AddDays(-5)),
            Signal("Meta", "llama", T0.AddDays(-3), category: "local_model", model: "Llama-3.1-8B"),
            Signal("Meta", "llama", T0.AddHours(-2), category: "local_model", model: "llama-3.1-8b", detector: "model_api"),
            Signal("Mistral", "mistral", T0.AddHours(-1), category: "local_model", model: "mistral-7b"));

        var head = AiDiscoveryNovelty.Parse(json);

        // Ollama, llama-3.1-8b (the two spellings are one model), mistral-7b.
        Assert.Equal(3, head.FirstSeenTicks.Count);
        Assert.Equal(1, head.CountAfter(T0.AddDays(-1).UtcTicks));
        Assert.Equal(2, head.CountAfter(T0.AddDays(-4).UtcTicks));
    }

    [Fact]
    public void A_local_model_signal_with_no_model_id_is_a_product_as_the_panel_lists_it()
    {
        var json = StateFile(Signal("Acme", "ModelThing", T0.AddHours(-3), category: "local_model"));

        var head = AiDiscoveryNovelty.Parse(json);

        Assert.Single(head.FirstSeenTicks);
    }

    [Fact]
    public void A_component_whose_first_seen_time_is_unknown_cannot_be_called_new()
    {
        var json = StateFile(
            Signal("Acme", "NoTime", firstSeen: null),
            Signal("Acme", "Dated", T0.AddHours(-1)));

        var head = AiDiscoveryNovelty.Parse(json);

        Assert.Single(head.FirstSeenTicks);
        Assert.Equal(T0.AddHours(-1).UtcTicks, head.FirstSeenTicks[0]);
    }

    [Fact]
    public void The_scanners_own_state_is_not_what_decides_it_a_seen_signal_first_seen_after_the_marker_is_new()
    {
        // 'seen' is what a signal is on every scan after the first one; 'new' lasts a single scan. Neither says when it arrived.
        var json = StateFile(
            Signal("Acme", "Old", T0.AddDays(-40), state: "new"),
            Signal("Acme", "Recent", T0.AddHours(-3), state: "seen"));

        var head = AiDiscoveryNovelty.Parse(json);

        Assert.Equal(1, head.CountAfter(T0.AddDays(-1).UtcTicks));
        Assert.Equal(0, head.CountAfter(T0.UtcTicks));
    }

    [Fact]
    public void The_count_is_strictly_after_the_marker()
    {
        var head = AiDiscoveryNovelty.Parse(StateFile(Signal("Acme", "A", T0), Signal("Acme", "B", T0.AddSeconds(1))));

        Assert.Equal(1, head.CountAfter(T0.UtcTicks));
        Assert.Equal(2, head.CountAfter(T0.UtcTicks - 1));
        Assert.Equal(0, head.CountAfter(T0.AddSeconds(1).UtcTicks));
    }

    [Theory]
    [InlineData("""{ "signals": {} }""")]
    [InlineData("""{ "signals": [] }""")]
    [InlineData("""{ "other": 1 }""")]
    [InlineData("""{ "signals": "none" }""")]
    [InlineData("""{ "signals": { "a": 5, "b": "x", "c": null, "d": [ 1 ] } }""")]
    public void An_empty_or_oddly_shaped_signal_list_is_nothing_discovered_not_a_fault(string json)
    {
        Assert.Empty(AiDiscoveryNovelty.Parse(json).FirstSeenTicks);
        Assert.Same(AiDiscoveryHead.None, AiDiscoveryNovelty.Parse(json));
    }

    [Fact]
    public void An_array_of_signals_is_read_like_the_object_keyed_by_fingerprint()
    {
        var json = "{ \"signals\": [ " + Signal("Acme", "A", T0.AddHours(-1)) + ", " + Signal("Acme", "B", T0.AddHours(-2)) + " ] }";

        var head = AiDiscoveryNovelty.Parse(json);

        Assert.Equal(2, head.FirstSeenTicks.Count);
        Assert.True(head.FirstSeenTicks[0] < head.FirstSeenTicks[1]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("42")]
    public void Text_that_is_not_a_json_object_throws_the_json_exception_the_overview_reader_does(string text)
    {
        _ = Assert.ThrowsAny<System.Text.Json.JsonException>(() => AiDiscoveryNovelty.Parse(text));
    }

    [Fact]
    public void Go_style_timestamps_with_nanoseconds_and_offsets_are_read()
    {
        var json = """
            { "signals": {
                "a": { "vendor": "Acme", "product": "A", "first_seen": "2026-10-01T12:00:00.123456789Z" },
                "b": { "vendor": "Acme", "product": "B", "first_seen": "2026-10-01T05:00:00-07:00" }
            } }
            """;

        var head = AiDiscoveryNovelty.Parse(json);

        Assert.Equal(2, head.FirstSeenTicks.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).UtcTicks, head.FirstSeenTicks[0]);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).UtcTicks + 1_234_567, head.FirstSeenTicks[1]);
    }
}
