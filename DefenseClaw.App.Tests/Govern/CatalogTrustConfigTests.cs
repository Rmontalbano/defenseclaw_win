using System.Reflection;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// <see cref="CatalogTrust"/> and the config rule (CUST-312): a list read before <c>config.yaml</c> or <c>.env</c> changed keeps its rows and loses
/// its say, a fresh complete read gives it back, and none of it looks at the disk except when something asks. The files are a script
/// (<see cref="Files"/>) handed to the trust as its signature source, so nothing here touches a disk, a timer or a clock.
/// </summary>
public sealed class CatalogTrustConfigTests
{
    private static readonly DateTime T0 = new(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>config.yaml and .env as a script: <see cref="Now"/> is a signature, an edit moves one of them, and every look is counted.</summary>
    private sealed class Files
    {
        private int _config;
        private int _env;

        public int Looks { get; private set; }

        public ConfigDiskSignature Now()
        {
            Looks++;
            return new ConfigDiskSignature(
                new FileStamp(@"C:\fixture\config.yaml", true, 100 + _config, T0.AddMinutes(_config)),
                new FileStamp(@"C:\fixture\.env", true, 20 + _env, T0.AddMinutes(_env)));
        }

        public void EditConfig() => _config++;

        public void EditEnv() => _env++;
    }

    private static CatalogTrust Watching(Files files, string readClause = CatalogTrust.ListRead, ManualClock? clock = null) =>
        new(time: clock, configSignature: files.Now, readClause: readClause);

    private static CatalogTrust CompleteRead(Files files, ManualClock? clock = null)
    {
        var trust = Watching(files, clock: clock);
        trust.BeginRead();
        trust.MarkComplete();
        return trust;
    }

    // ------------------------------------------------------------------ MarkStale

    [Fact]
    public void After_a_complete_read_MarkStale_turns_changes_off_with_that_reason_and_a_fresh_read_restores_them()
    {
        var trust = new CatalogTrust();
        trust.MarkComplete();
        Assert.True(trust.IsTrusted);
        Assert.False(trust.IsStale);

        trust.MarkStale("config moved");

        Assert.False(trust.IsTrusted);
        Assert.True(trust.IsStale);
        Assert.Equal("config moved", trust.Reason);

        trust.MarkComplete();

        Assert.True(trust.IsTrusted);
        Assert.False(trust.IsStale);
        Assert.Null(trust.Reason);
    }

    [Fact]
    public void A_partial_read_is_a_fresh_read_too_and_says_its_own_reason()
    {
        var trust = new CatalogTrust();
        trust.MarkComplete();
        trust.MarkStale("config moved");

        trust.MarkPartial(new[] { "d1" });

        Assert.False(trust.IsStale);
        Assert.True(trust.IsPartial);
        Assert.Contains("discovery was incomplete", trust.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_read_says_it_failed_before_it_says_the_config_moved_and_a_fresh_read_clears_both()
    {
        var trust = new CatalogTrust();
        trust.MarkComplete();
        trust.MarkStale("config moved");

        trust.MarkFailed("boom");

        Assert.False(trust.IsTrusted);
        Assert.Contains("the last read failed", trust.Reason, StringComparison.Ordinal);
        Assert.True(trust.LastReadFailed);

        trust.MarkComplete();
        Assert.True(trust.IsTrusted);
    }

    [Fact]
    public void A_stale_list_that_also_went_old_says_the_config_moved()
    {
        var clock = new ManualClock();
        var trust = new CatalogTrust(time: clock);
        trust.MarkComplete();
        trust.MarkStale("config moved");

        clock.Advance(CatalogTrust.DefaultFreshnessWindow + TimeSpan.FromMinutes(1));

        Assert.Equal("config moved", trust.Reason);
    }

    [Fact]
    public void There_is_nothing_to_mark_stale_before_a_read_has_delivered_rows()
    {
        var trust = new CatalogTrust();

        trust.MarkStale("config moved"); // nobody asked for a read: nothing on screen to be wrong about
        Assert.True(trust.IsTrusted);
        Assert.False(trust.IsStale);

        trust.MarkPending();
        trust.MarkStale("config moved"); // the first read is in flight and is judged against the files when it ends
        Assert.False(trust.IsStale);
        Assert.Contains("first read", trust.Reason, StringComparison.Ordinal);

        trust.MarkComplete();
        Assert.True(trust.IsTrusted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_reason_is_required(string? reason)
    {
        var trust = new CatalogTrust();
        trust.MarkComplete();

        _ = Assert.ThrowsAny<ArgumentException>(() => trust.MarkStale(reason!));
        Assert.True(trust.IsTrusted);
    }

    [Fact]
    public void The_config_reason_is_the_house_sentence_and_names_what_it_is_about()
    {
        Assert.Equal(
            "Changes are off: config.yaml or .env changed after this list was read. Refresh to act on current data.",
            CatalogTrust.ConfigChangedReason());
        Assert.Equal(
            "Changes are off: config.yaml or .env changed after these settings were read. Refresh to act on current data.",
            CatalogTrust.ConfigChangedReason("these settings were read"));
    }

    // ------------------------------------------------------------------ the files

    [Fact]
    public void A_config_change_after_a_complete_read_makes_the_list_stale_when_the_trust_looks()
    {
        var files = new Files();
        var trust = CompleteRead(files);
        Assert.False(trust.CheckConfig()); // nothing moved

        files.EditConfig();

        Assert.True(trust.IsTrusted); // the bound value knows only what has been noticed
        Assert.True(trust.CheckConfig());
        Assert.False(trust.IsTrusted);
        Assert.True(trust.IsStale);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), trust.Reason);
    }

    [Fact]
    public void A_change_to_env_alone_is_a_change_too()
    {
        var files = new Files();
        var trust = CompleteRead(files);

        files.EditEnv();

        Assert.True(trust.CheckConfig());
        Assert.Equal(CatalogTrust.ConfigChangedReason(), trust.Reason);
    }

    [Fact]
    public void The_clause_is_what_the_panel_calls_its_rows()
    {
        var files = new Files();
        var trust = Watching(files, "these settings were read");
        trust.BeginRead();
        trust.MarkComplete();

        files.EditConfig();
        _ = trust.CheckConfig();

        Assert.Equal(CatalogTrust.ConfigChangedReason("these settings were read"), trust.Reason);
    }

    [Fact]
    public void A_fresh_complete_read_restores_trust_and_is_judged_against_the_files_as_they_are_then()
    {
        var files = new Files();
        var trust = CompleteRead(files);
        files.EditConfig();
        Assert.True(trust.CheckConfig());

        trust.BeginRead();
        trust.MarkComplete();

        Assert.True(trust.IsTrusted);
        Assert.False(trust.IsStale);
        Assert.False(trust.CheckConfig());

        files.EditEnv();
        Assert.True(trust.CheckConfig()); // and it can go stale again
    }

    [Fact]
    public void A_read_that_began_after_the_change_is_not_stale_when_the_watcher_speaks_late()
    {
        // The panel re-reads straight after its own change; the watcher raises ConfigReloaded a few hundred milliseconds after that.
        var files = new Files();
        var trust = CompleteRead(files);

        files.EditConfig();    // the change
        trust.BeginRead();     // the re-read begins under the new files
        trust.MarkComplete();  // and ends
        var heardLate = trust.CheckConfig(); // ConfigReloaded arrives

        Assert.False(heardLate);
        Assert.True(trust.IsTrusted);
    }

    [Fact]
    public void Files_that_moved_while_a_read_was_running_make_it_stale_when_it_ends()
    {
        var files = new Files();
        var trust = CompleteRead(files);

        trust.BeginRead();
        files.EditConfig();    // nothing says which side of the edit the read saw
        trust.MarkComplete();

        Assert.False(trust.IsTrusted);
        Assert.True(trust.IsStale);
        Assert.Equal(CatalogTrust.ConfigChangedReason(), trust.Reason);

        trust.BeginRead();
        trust.MarkComplete();
        Assert.True(trust.IsTrusted);
    }

    [Fact]
    public void Files_that_moved_while_the_first_read_was_running_are_found_when_it_ends()
    {
        var files = new Files();
        var trust = Watching(files);

        trust.BeginRead();
        Assert.False(trust.CheckConfig()); // nothing on screen yet: nothing to mark
        files.EditConfig();
        trust.MarkComplete();

        Assert.True(trust.IsStale);
    }

    [Fact]
    public void A_partial_read_is_judged_against_the_files_like_a_complete_one()
    {
        var files = new Files();
        var trust = Watching(files);
        trust.BeginRead();
        trust.MarkPartial(new[] { "d1" });
        Assert.False(trust.CheckConfig());

        files.EditConfig();

        Assert.True(trust.CheckConfig());
        Assert.True(trust.IsStale);
        Assert.True(trust.IsPartial);
        Assert.Contains("discovery was incomplete", trust.Reason, StringComparison.Ordinal); // the partial read's own reason comes first
    }

    [Fact]
    public void A_failed_refresh_keeps_the_last_good_reads_signature_and_does_not_replace_it()
    {
        var files = new Files();
        var trust = CompleteRead(files);

        trust.BeginRead();
        files.EditConfig();
        trust.MarkFailed("boom");
        Assert.Contains("the last read failed", trust.Reason, StringComparison.Ordinal);

        // The rows are still the good read's: they are out of date, and the next complete read says so about nothing.
        Assert.True(trust.CheckConfig());
        trust.BeginRead();
        trust.MarkComplete();
        Assert.True(trust.IsTrusted);
        Assert.False(trust.CheckConfig());
    }

    [Fact]
    public void ReasonNow_sees_an_edit_the_watcher_has_not_reported_yet()
    {
        var files = new Files();
        var trust = CompleteRead(files);
        Assert.Null(trust.ReasonNow());

        files.EditConfig();

        Assert.True(trust.IsTrusted); // the bound property has not been told
        Assert.Equal(CatalogTrust.ConfigChangedReason(), trust.ReasonNow());
        Assert.False(trust.IsTrusted);
    }

    [Fact]
    public void A_trust_without_a_signature_source_has_no_config_rule()
    {
        var trust = new CatalogTrust();
        trust.BeginRead();
        trust.MarkComplete();

        Assert.False(trust.CheckConfig());
        Assert.Null(trust.ReasonNow());
        Assert.True(trust.IsTrusted);
    }

    [Fact]
    public void A_trust_for_an_installation_watches_that_installations_files()
    {
        using var temp = new TempDirectory();
        var paths = TestServices.IsolatedPaths(temp.Path);
        var trust = CatalogTrust.Watching(paths);
        trust.BeginRead();
        trust.MarkComplete();
        Assert.Null(trust.ReasonNow());

        _ = temp.WriteFile("config.yaml", "guardrail:\n  mode: observe\n");

        Assert.Equal(CatalogTrust.ConfigChangedReason(), trust.ReasonNow());
    }

    // ------------------------------------------------------------------ no polling

    [Fact]
    public void Only_a_read_a_look_and_a_request_touch_the_files_never_the_bound_properties()
    {
        var files = new Files();
        var trust = Watching(files);

        trust.BeginRead();
        Assert.Equal(1, files.Looks); // the read starts
        trust.MarkComplete();
        Assert.Equal(2, files.Looks); // and ends

        var before = files.Looks;
        for (var i = 0; i < 1_000; i++)
        {
            _ = trust.IsTrusted;
            _ = trust.Reason;
            _ = trust.IsStale;
            _ = trust.IsPartial;
            _ = trust.LastReadFailed;
        }

        Assert.Equal(before, files.Looks); // rows ask these for every cell, so they must never touch the disk

        _ = trust.CheckConfig();
        Assert.Equal(before + 1, files.Looks);
        _ = trust.ReasonNow();
        Assert.Equal(before + 2, files.Looks);

        files.EditConfig();
        _ = trust.CheckConfig();
        var stale = files.Looks;
        _ = trust.CheckConfig();
        _ = trust.ReasonNow();
        Assert.Equal(stale, files.Looks); // once it is stale it has nothing more to find out
    }

    [Fact]
    public void No_type_of_the_config_rule_owns_a_timer_a_watcher_or_a_thread()
    {
        var banned = new[]
        {
            typeof(System.Threading.Timer),
            typeof(System.Timers.Timer),
            typeof(System.Windows.Threading.DispatcherTimer),
            typeof(PeriodicTimer),
            typeof(FileSystemWatcher),
            typeof(Thread),
        };
        var types = new[]
        {
            typeof(CatalogTrust),
            typeof(ConfigDiskSignature),
            typeof(FileStamp),
            typeof(DiscoverActionReview),
            typeof(GovernPanelViewModelBase),
            typeof(RegistriesPanelViewModel),
            typeof(PoliciesPanelViewModel),
            typeof(PolicyModelViewModel),
            typeof(AiRuntimePanelViewModel),
        };

        foreach (var type in types)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var field in fields)
            {
                Assert.False(
                    banned.Any(b => b.IsAssignableFrom(field.FieldType)),
                    $"{type.Name}.{field.Name} is a {field.FieldType.Name}: the config rule reuses the existing watcher and adds no timer.");
            }
        }
    }
}
