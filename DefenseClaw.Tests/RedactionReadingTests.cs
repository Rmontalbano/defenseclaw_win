using DefenseClaw.Core.Redaction;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// What the advanced redaction editor reads back (CUST-295): the status, the profile and route lists, and the JSON of a preview or an
/// apply, over the synthetic fixtures made from the Docker capture of DefenseClaw source commit 95159fd
/// (<c>Fixtures/runtime-95159fd/redaction</c>). Nothing here starts a process.
/// </summary>
public sealed class RedactionReadingTests
{
    private static string Fixture(string name) => RuntimeFixtures.Read("redaction/" + name);

    private static RedactionStatus Status(string name)
    {
        Assert.True(RedactionStatusParser.TryParse(Fixture(name), out var status, out var error), error);
        return status!;
    }

    private static RedactionResult Result(string name)
    {
        Assert.True(RedactionResultParser.TryParse(Fixture(name), out var result, out var error), error);
        return result!;
    }

    // ------------------------------------------------------------------ status

    [Fact]
    public void A_fresh_install_sends_everything_unredacted_and_says_so()
    {
        var status = Status("status-fresh.json");

        Assert.Equal(14, status.Buckets.Count);
        Assert.Equal(RedactionVocabulary.Buckets, status.Buckets.Select(static b => b.Name).ToArray());
        Assert.All(status.Buckets, static b =>
        {
            Assert.Equal("none", b.Profile);
            Assert.Equal("logs · traces · metrics", b.SignalsText);
            Assert.Equal("Warn", b.ProfileTone);
        });
        Assert.Equal(1, status.CatalogVersion);
        Assert.Equal(@"C:\Users\operator\.defenseclaw\config.yaml", status.ConfigPath);
        Assert.Matches("^[0-9a-f]{64}$", status.PlanDigest);

        Assert.True(status.IsUnredacted);
        Assert.Equal("none", status.UniformProfile);
        Assert.Equal("Redaction is off: every bucket is sent as recorded.", status.Summary);
        Assert.Equal("Redaction: none", status.ChipText);
        Assert.Equal("Warn", status.Tone);

        var local = Assert.Single(status.Destinations);
        Assert.Equal("local-sqlite", local.Name);
        Assert.True(local.Generated);
        Assert.False(local.IsConfigurable);
        Assert.Equal("built-in policy", local.PolicyFormText);
        Assert.Equal("unredacted (none)", local.Label);
        Assert.Equal("Built-in: its policy is generated, so it is read-only here.", local.Lock);
        Assert.Empty(status.ConfigurableDestinations);
        Assert.Empty(status.RoutedDestinations);

        Assert.True(status.JudgeBodies.Capture);
        Assert.Equal(7, status.JudgeBodies.RetentionDays);
        Assert.Contains("kept unredacted (for 7 days)", status.JudgeBodies.Disclosure, StringComparison.Ordinal);
        Assert.Contains("No redaction profile covers this store.", status.JudgeBodies.Disclosure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_destination_is_one_the_operator_can_send_inherit_and_route_and_its_warnings_are_kept()
    {
        var status = Status("status-with-destination.json");

        Assert.Equal(["local-sqlite", "example-otlp"], status.Destinations.Select(static d => d.Name).ToArray());
        var otlp = status.Destinations[1];
        Assert.True(otlp.IsConfigurable);
        Assert.Equal("otlp", otlp.Kind);
        Assert.Equal("everything it can take", otlp.PolicyFormText);
        Assert.Equal("logs · traces · metrics", otlp.SignalsText);
        Assert.Equal(string.Empty, otlp.Lock);
        Assert.Equal(["example-otlp"], status.ConfigurableDestinations.Select(static d => d.Name).ToArray());
        Assert.Empty(status.RoutedDestinations); // capability-default: no ordered routes to list

        Assert.Equal(2, status.Warnings.Count);
        Assert.Equal("tls_verification_disabled", status.Warnings[0].Code);
        Assert.Contains("example-otlp", status.Warnings[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Mixed_profiles_are_summed_up_and_the_managed_destination_is_locked()
    {
        var status = Status("status-mixed.json");

        Assert.False(status.IsUnredacted);
        Assert.True(status.HasUnredactedBucket);
        Assert.Equal(string.Empty, status.UniformProfile);
        Assert.Equal(["none", "sensitive", "content"], status.BucketProfiles.ToArray());
        Assert.Equal("Mixed profiles: none on 1, sensitive on 12, content on 1 of 14 buckets.", status.Summary);
        Assert.Equal("Redaction: mixed", status.ChipText);
        Assert.Equal("Warn", status.Tone);
        Assert.Equal("logs · traces", status.Buckets.Single(static b => b.Name == "model.io").SignalsText);

        var routed = Assert.Single(status.RoutedDestinations);
        Assert.Equal("example-otlp", routed.Name);
        Assert.Equal("ordered routes", routed.PolicyFormText);
        Assert.Equal("Ok", routed.Tone);

        var managed = status.Destinations.Single(static d => d.IsManaged);
        Assert.Equal("Managed by your organisation: locked.", managed.Lock);
        Assert.False(managed.IsConfigurable);
        Assert.Equal("Warn", status.Destinations[0].Tone); // local-sqlite: mixed, with none among them
    }

    [Fact]
    public void A_profile_everywhere_is_ok_not_a_warning()
    {
        var fresh = Status("status-fresh.json");
        var redacted = fresh with { Buckets = fresh.Buckets.Select(static b => b with { Profile = "strict" }).ToArray() };

        Assert.Equal("strict", redacted.UniformProfile);
        Assert.Equal("Every bucket uses the strict profile.", redacted.Summary);
        Assert.Equal("Ok", redacted.Tone);
        Assert.Equal("Redaction: strict", redacted.ChipText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"buckets": []}""")] // no plan digest
    [InlineData("""{"plan_digest": "abc"}""")] // no buckets
    public void Anything_that_is_not_a_status_is_refused_with_a_reason_and_never_throws(string text)
    {
        Assert.False(RedactionStatusParser.TryParse(text, out var status, out var error));

        Assert.Null(status);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void A_notice_before_the_json_does_not_hide_it()
    {
        var text = "[warn] config drift detected\nnote: something\n" + Fixture("status-fresh.json");

        Assert.True(RedactionStatusParser.TryParse(text, out var status, out var error), error);
        Assert.Equal(14, status!.Buckets.Count);
    }

    [Fact]
    public void A_status_with_no_destinations_or_judge_block_is_still_a_status()
    {
        Assert.True(RedactionStatusParser.TryParse("""{"plan_digest": "d", "buckets": [{"name": "model.io", "redaction_profile": "sensitive", "signals": ["logs"]}]}""", out var status, out _));

        Assert.Empty(status!.Destinations);
        Assert.False(status.JudgeBodies.Capture);
        Assert.Contains("is not kept", status.JudgeBodies.Disclosure, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ profiles and routes

    [Fact]
    public void Profiles_list_built_ins_first_and_show_what_they_do_to_each_field_class()
    {
        Assert.True(RedactionReadings.TryParseProfileList(Fixture("profile-list-custom.json"), out var list, out var error), error);
        Assert.Equal(["none", "sensitive", "content", "strict", "example-profile"], list!.Names.ToArray());
        Assert.Equal(["example-profile"], list.Custom.ToArray());
        Assert.True(list.Contains("strict"));

        Assert.True(RedactionReadings.TryParseProfile(Fixture("profile-show-custom.json"), out var custom, out error), error);
        Assert.False(custom!.BuiltIn);
        Assert.Equal("custom, extends sensitive", custom.Kind);
        Assert.Equal("pii", custom.DetectorsText);
        Assert.Equal(RedactionVocabulary.FieldClasses, custom.FieldModes.Select(static m => m.Class).ToArray());
        Assert.Equal("hash", custom.FieldModes.Single(static m => m.Class == "path").Mode);

        Assert.True(RedactionReadings.TryParseProfile(Fixture("profile-show-strict.json"), out var strict, out error), error);
        Assert.True(strict!.BuiltIn);
        Assert.Equal("built-in", strict.Kind);
        Assert.Equal("remove", strict.FieldModes.Single(static m => m.Class == "content").Mode);
    }

    [Fact]
    public void Routes_come_back_in_order_with_what_they_match()
    {
        Assert.True(RedactionReadings.TryParseRoutes(Fixture("route-list-empty.json"), out var none, out var error), error);
        Assert.Equal("example-otlp", none!.Destination);
        Assert.Empty(none.Routes);

        Assert.True(RedactionReadings.TryParseRoutes(Fixture("route-list-two.json"), out var two, out error), error);
        Assert.Equal([1, 2], two!.Routes.Select(static r => r.Position).ToArray());

        var drop = two.Routes[0];
        Assert.True(drop.IsDrop);
        Assert.Equal("drop logs · buckets tool.activity · HIGH and above", drop.Summary);

        var send = two.Routes[1];
        Assert.False(send.IsDrop);
        Assert.Equal("send logs, traces with strict · buckets model.io, tool.activity · connectors claudecode · MEDIUM and above", send.Summary);
        Assert.Equal("everything", RedactionSelector.Any.Text);
    }

    [Theory]
    [InlineData("""{"destination": "d", "routes": [{"name": "r", "action": "send", "signals": ["logs"]}]}""", "send logs with each bucket's profile · everything")]
    [InlineData("""{"destination": "d", "routes": [{"name": "r", "action": "send", "signals": ["metrics"]}]}""", "send metrics · everything")]
    [InlineData("""{"destination": "d", "routes": [{"name": "r", "action": "drop", "signals": ["logs", "traces"], "redaction_profile": "strict"}]}""", "drop logs, traces · everything")]
    [InlineData("""{"destination": "d", "routes": [{"name": "r"}]}""", "send no signals · everything")]
    public void A_route_says_which_profile_what_it_sends_gets(string json, string summary)
    {
        Assert.True(RedactionReadings.TryParseRoutes(json, out var routes, out var error), error);

        Assert.Equal(summary, Assert.Single(routes!.Routes).Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"profiles": "x"}""")]
    public void A_profile_list_that_is_not_one_is_refused(string text)
    {
        Assert.False(RedactionReadings.TryParseProfileList(text, out var list, out var error));
        Assert.Null(list);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"built_in": true}""")]
    public void A_profile_with_no_name_is_refused(string text) =>
        Assert.False(RedactionReadings.TryParseProfile(text, out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData("""{"destination": "d"}""")]
    public void A_route_list_with_no_routes_member_is_refused(string text) =>
        Assert.False(RedactionReadings.TryParseRoutes(text, out _, out _));

    // ------------------------------------------------------------------ previews and applies

    [Fact]
    public void Applying_a_profile_everywhere_redacts_every_bucket_of_the_local_store()
    {
        var result = Result("dry-apply-everywhere-sensitive.json");

        Assert.True(result.DryRun);
        Assert.False(result.Applied);
        Assert.True(result.Changed);
        Assert.False(result.Restarted);
        Assert.Equal(14, result.ChangedLegs);
        Assert.Equal(14, result.NoLongerUnredacted);
        Assert.Equal(0, result.NewlyUnredacted);
        Assert.False(result.PlanUnchanged);

        var group = Assert.Single(result.Groups);
        Assert.Equal("local-sqlite / all-collected-logs-and-mandatory-floor", group.Target);
        Assert.Equal("logs", group.Signal);
        Assert.Equal(("none", "sensitive"), (group.BeforeText, group.AfterText));
        Assert.Equal("all 14 buckets", group.BucketsText);
        Assert.Equal(RedactionLegKind.StartsRedacting, group.Kind);
        Assert.Equal("starts redacting", group.KindText);
        Assert.Equal("Ok", group.Tone);
        Assert.False(group.NewlyUnredacted);

        Assert.Equal("14 delivery legs would change.", result.Headline);
        Assert.Equal("14 start redacting", result.Breakdown);
    }

    [Fact]
    public void Removing_a_route_starts_unredacted_delivery_and_is_the_change_a_review_asks_about()
    {
        var result = Result("dry-route-remove.json");

        Assert.Equal(28, result.ChangedLegs);
        Assert.Equal(28, result.NewlyUnredacted);
        Assert.Equal(0, result.NoLongerUnredacted);

        Assert.Equal(2, result.Groups.Count); // logs and traces
        Assert.All(result.Groups, static g =>
        {
            Assert.Equal("example-otlp / capability-default", g.Target);
            Assert.Equal(("not delivered", "none"), (g.BeforeText, g.AfterText));
            Assert.Equal(RedactionLegKind.StartsDelivering, g.Kind);
            Assert.Equal("starts delivering, unredacted", g.KindText);
            Assert.Equal("Bad", g.Tone);
            Assert.True(g.NewlyUnredacted);
        });
        Assert.Equal(["logs", "traces"], result.Groups.Select(static g => g.Signal).ToArray());
        Assert.Equal("28 start delivering", result.Breakdown);
    }

    [Fact]
    public void A_send_policy_swaps_the_default_legs_for_one_and_is_folded_into_three_rows()
    {
        var result = Result("dry-destination-send.json");

        Assert.Equal(29, result.ChangedLegs);
        Assert.Equal(28, result.NoLongerUnredacted); // none to not-delivered counts, as the CLI counts it
        Assert.Equal(0, result.NewlyUnredacted);
        Assert.Equal(3, result.Groups.Count);

        var sends = result.Groups.Single(static g => g.Route == "send");
        Assert.Equal(["security.finding"], sends.Buckets.ToArray());
        Assert.Equal("security.finding", sends.BucketsText);
        Assert.Equal(("not delivered", "sensitive"), (sends.BeforeText, sends.AfterText));
        Assert.Equal("starts delivering", sends.KindText);
        Assert.Equal("Neutral", sends.Tone);

        Assert.All(result.Groups.Where(static g => g.Route == "capability-default"), static g =>
        {
            Assert.Equal(RedactionLegKind.StopsDelivering, g.Kind);
            Assert.Equal("stops delivering", g.KindText);
            Assert.Equal(14, g.Buckets.Count);
        });
        Assert.Equal("1 start delivering · 28 stop delivering", result.Breakdown);
    }

    [Fact]
    public void A_single_bucket_change_names_the_bucket()
    {
        var result = Result("dry-bucket-set.json");

        var group = Assert.Single(result.Groups);
        Assert.Equal(["model.io"], group.Buckets.ToArray());
        Assert.Equal(("none", "content"), (group.BeforeText, group.AfterText));
        Assert.Equal("1 delivery leg would change.", result.Headline);
    }

    [Fact]
    public void A_replaced_route_for_one_bucket_is_one_row()
    {
        var result = Result("dry-route-set.json");

        var group = Assert.Single(result.Groups);
        Assert.Equal("example-otlp / example-route", group.Target);
        Assert.Equal(("not delivered", "strict"), (group.BeforeText, group.AfterText));
        Assert.Equal(["tool.activity"], group.Buckets.ToArray());
    }

    [Theory]
    [InlineData("dry-bucket-reset.json")]
    [InlineData("dry-defaults-reset.json")]
    [InlineData("dry-destination-inherit.json")]
    [InlineData("dry-route-move.json")]
    public void A_change_the_configuration_already_has_says_there_is_nothing_to_change(string file)
    {
        var result = Result(file);

        Assert.False(result.Changed);
        Assert.Equal(0, result.ChangedLegs);
        Assert.Empty(result.Groups);
        Assert.Equal("Nothing to change: the configuration already says this.", result.Headline);
        Assert.True(result.PlanUnchanged);
    }

    [Theory]
    [InlineData("dry-profile-set.json", false)]
    [InlineData("dry-profile-remove.json", false)]
    [InlineData("dry-remove-all.json", true)]
    public void A_change_that_rewrites_the_file_without_moving_a_leg_says_that_instead(string file, bool samePlan)
    {
        var result = Result(file);

        Assert.True(result.Changed);
        Assert.Equal(0, result.ChangedLegs);
        Assert.Empty(result.Groups);
        Assert.Equal("The configuration file would change, but no delivery leg would be redacted differently.", result.Headline);
        Assert.Equal(samePlan, result.PlanUnchanged);
        Assert.Equal(string.Empty, result.Breakdown);
    }

    [Fact]
    public void An_apply_carries_the_backup_and_the_check_that_what_was_written_is_what_was_shown()
    {
        var route = Result("applied-route-add.json");
        Assert.False(route.DryRun);
        Assert.True(route.Applied);
        Assert.True(route.IsVerified);
        Assert.Equal(route.AfterPlanDigest, route.VerifiedPlanDigest);
        Assert.StartsWith(@"C:\Users\operator\.defenseclaw\backups\config.yaml.before-redaction-", route.BackupPath, StringComparison.Ordinal);
        Assert.Equal("28 delivery legs changed.", route.Headline);

        var profile = Result("applied-profile-set.json");
        Assert.True(profile.IsVerified);
        Assert.Equal("The configuration file changed, but no delivery leg is redacted differently.", profile.Headline);

        // a preview is never "verified": nothing was written
        Assert.False(Result("dry-route-add.json").IsVerified);

        // an apply whose digest check is missing or differs is not verified
        Assert.False((route with { VerifiedPlanDigest = string.Empty }).IsVerified);
        Assert.False((route with { VerifiedPlanDigest = "other" }).IsVerified);
    }

    [Fact]
    public void Warnings_and_locked_profiles_come_through()
    {
        var result = Result("dry-destination-inherit.json");
        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, static w => Assert.NotEmpty(w.Summary));

        const string withLocked = """
            {"dry_run": true, "applied": false, "changed": true, "changed_legs": 0, "changes": [],
             "locked_profiles": [{"destination": "managed-enterprise-ai-defense", "profiles": ["strict"]}],
             "before_plan_digest": "a", "after_plan_digest": "b", "newly_unredacted": 0, "no_longer_unredacted": 0, "warnings": []}
            """;
        Assert.True(RedactionResultParser.TryParse(withLocked, out var parsed, out var error), error);
        var locked = Assert.Single(parsed!.Locked);
        Assert.Equal("managed-enterprise-ai-defense", locked.Destination);
        Assert.Equal(["strict"], locked.Profiles.ToArray());
    }

    [Fact]
    public void A_result_whose_list_is_not_the_length_it_reports_is_not_trusted()
    {
        const string short_ = """
            {"dry_run": true, "applied": false, "changed": true, "changed_legs": 3, "changes": [],
             "before_plan_digest": "a", "after_plan_digest": "b"}
            """;

        Assert.False(RedactionResultParser.TryParse(short_, out var result, out var error));
        Assert.Null(result);
        Assert.Contains("3 legs change but lists 0", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_content_that_starts_to_flow_is_counted_from_the_list_even_when_the_cli_count_is_short()
    {
        const string text = """
            {"dry_run": true, "applied": false, "changed": true, "changed_legs": 1,
             "changes": [{"destination": "d", "route": "r", "bucket": "model.io", "signal": "logs", "before": "strict", "after": "none"}],
             "before_plan_digest": "a", "after_plan_digest": "b", "newly_unredacted": 0, "no_longer_unredacted": 0}
            """;

        Assert.True(RedactionResultParser.TryParse(text, out var result, out var error), error);
        Assert.Equal(0, result!.ReportedNewlyUnredacted);
        Assert.Equal(1, result.NewlyUnredacted);
        Assert.Equal("stops redacting", result.Groups.Single().KindText);
        Assert.Equal("Bad", result.Groups.Single().Tone);
        Assert.Single(result.Groups.Single().Buckets);
    }

    [Fact]
    public void Rows_that_start_unredacted_delivery_come_first()
    {
        const string text = """
            {"dry_run": true, "applied": false, "changed": true, "changed_legs": 3,
             "changes": [
               {"destination": "a", "route": "r", "bucket": "model.io", "signal": "logs", "before": "sensitive", "after": "strict"},
               {"destination": "a", "route": "r", "bucket": "model.io", "signal": "traces", "before": "none", "after": "sensitive"},
               {"destination": "z", "route": "r", "bucket": "model.io", "signal": "logs", "before": "sensitive", "after": "none"}],
             "before_plan_digest": "a", "after_plan_digest": "b"}
            """;

        Assert.True(RedactionResultParser.TryParse(text, out var result, out var error), error);

        Assert.Equal(
            [RedactionLegKind.StopsRedacting, RedactionLegKind.StartsRedacting, RedactionLegKind.ChangesProfile],
            result!.Groups.Select(static g => g.Kind).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("nothing here")]
    [InlineData("[]")]
    [InlineData("""{"changed": true}""")] // no dry_run
    [InlineData("""{"dry_run": true}""")] // no changed
    [InlineData("""{"dry_run": "yes", "changed": true}""")]
    public void Output_that_is_not_the_result_of_a_change_is_refused(string text)
    {
        Assert.False(RedactionResultParser.TryParse(text, out var result, out var error));
        Assert.Null(result);
        Assert.NotEmpty(error);
    }

    // ------------------------------------------------------------------ the fixtures

    [Fact]
    public void Every_redaction_fixture_reads_with_its_parser_and_carries_no_real_name_path_or_host()
    {
        var root = Path.Combine(FixtureFiles.Directory, RuntimeFixtures.Directory, "redaction");
        var files = Directory.GetFiles(root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.True(files.Length >= 33, $"expected the redaction fixture set, found {files.Length} files");

        foreach (var file in files)
        {
            var text = Fixture(file!);
            Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("spike", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);

            if (file!.StartsWith("status-", StringComparison.Ordinal) && file.EndsWith(".json", StringComparison.Ordinal))
            {
                Assert.True(RedactionStatusParser.TryParse(text, out _, out var error), $"{file}: {error}");
            }
            else if (file.StartsWith("dry-", StringComparison.Ordinal) || file.StartsWith("applied-", StringComparison.Ordinal))
            {
                Assert.True(RedactionResultParser.TryParse(text, out _, out var error), $"{file}: {error}");
            }
            else if (file.StartsWith("profile-list", StringComparison.Ordinal))
            {
                Assert.True(RedactionReadings.TryParseProfileList(text, out _, out var error), $"{file}: {error}");
            }
            else if (file.StartsWith("profile-show", StringComparison.Ordinal))
            {
                Assert.True(RedactionReadings.TryParseProfile(text, out _, out var error), $"{file}: {error}");
            }
            else if (file.StartsWith("route-list", StringComparison.Ordinal))
            {
                Assert.True(RedactionReadings.TryParseRoutes(text, out _, out var error), $"{file}: {error}");
            }
        }
    }

    [Fact]
    public void The_vocabulary_is_the_runtime_s_own_down_to_the_bucket_order()
    {
        // The status of a fresh install lists the 14 buckets in catalog order; the editor offers them in that order.
        Assert.Equal(Status("status-fresh.json").Buckets.Select(static b => b.Name), RedactionVocabulary.Buckets);
        Assert.Equal(RedactionVocabulary.FieldClasses, new[] { "metadata", "identifier", "content", "reason", "evidence", "error", "path", "credential" });
        Assert.All(RedactionVocabulary.BuiltInProfiles, static p => Assert.False(string.IsNullOrWhiteSpace(RedactionVocabulary.Describe(p))));
        Assert.Equal("A custom profile.", RedactionVocabulary.Describe("example-profile"));
    }
}
