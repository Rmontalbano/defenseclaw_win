using System.Globalization;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Overview;

/// <summary>
/// The acknowledge / dismiss dialog confirms what it previewed. <c>defenseclaw alerts acknowledge --severity HIGH</c> selects
/// whatever is active when it runs, and the confirm click can come long after the count was read, so an unpinned apply
/// takes alerts that arrived in between and the operator never saw. The dialog pins the apply to the moment of the
/// preview (<c>--before</c>), applies a fully listed preview as its exact ids (<c>--id</c>), and says so when the CLI
/// reports a different count. The CLI itself is a seam here (<see cref="AlertsPanelViewModel.RunCli"/>); the flags and the
/// wording of its output are the real 0.8.10 ones.
/// </summary>
public class AlertsReviewTests
{
    private const string Digest = "digest=sha256:v1:0123456789abcdef";

    /// <summary>What <c>alerts acknowledge --dry-run</c> prints: a count line, up to twenty ids, an "... and N more" line, an OK line.</summary>
    private static CliInvocation DryRun(int matched, params string[] listed)
    {
        var lines = new List<string> { $"Preview: {matched} alert(s) matched; {Digest}" };
        lines.AddRange(listed.Select(id => $"  {id} version=0"));
        if (matched > listed.Length)
        {
            lines.Add($"  … and {matched - listed.Length} more");
        }

        lines.Add("  OK Dry run complete; no alerts were changed.");
        return Invocation(0, lines.ToArray());
    }

    /// <summary>What the real run prints: the same preview block first, then the result line.</summary>
    private static CliInvocation Applied(int matchedWhenItRan, int applied, string verbed = "Acknowledged", params string[] listed)
    {
        var lines = new List<string> { $"Preview: {matchedWhenItRan} alert(s) matched; {Digest}" };
        lines.AddRange(listed.Select(id => $"  {id} version=0"));
        lines.Add($"  OK {verbed} {applied} alert(s).");
        return Invocation(0, lines.ToArray());
    }

    private static CliInvocation Invocation(int exitCode, string[] lines)
    {
        var invocation = InvocationFactory.Create(false, "alerts");
        foreach (var line in lines)
        {
            InvocationFactory.Append(invocation, line);
        }

        InvocationFactory.Finish(invocation, exitCode);
        return invocation;
    }

    private static string[] Ids(int count) => Enumerable.Range(1, count).Select(i => $"alert-{i:D3}").ToArray();

    private static string ValueAfter(string[] argv, string flag) => argv[Array.IndexOf(argv, flag) + 1];

    /// <summary>Runs <paramref name="body"/> against a panel whose CLI answers with <paramref name="answer"/> and records every call.</summary>
    private static void WithReview(
        Func<string[], CliInvocation> answer,
        Func<AlertsPanelViewModel, List<string[]>, Task> body)
    {
        StaThread.Run(async () =>
        {
            using var temp = new TempDirectory();
            using var services = TestServices.Create(temp);
            var calls = new List<string[]>();
            var vm = new AlertsPanelViewModel(services)
            {
                RunCli = (argv, _) =>
                {
                    var call = argv.ToArray();
                    calls.Add(call);
                    return Task.FromResult(answer(call));
                },

                // No gateway re-read: the isolated services would probe the real loopback port.
                AfterApply = () => Task.CompletedTask,
            };

            await body(vm, calls);
        });
    }

    // ------------------------------------------------------------------ the preview's moment

    [Fact]
    public void The_preview_and_the_apply_carry_the_same_moment_and_the_same_selector()
    {
        var before = DateTimeOffset.UtcNow;

        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(25, Ids(20)) : Applied(25, 25),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);

                var preview = Assert.Single(calls);
                Assert.Equal(new[] { "alerts", "acknowledge", "--severity", "CRITICAL" }, preview.Take(4));
                Assert.Contains("--dry-run", preview);
                var moment = ValueAfter(preview, "--before");

                // RFC 3339, UTC, taken when the preview started.
                var parsed = DateTimeOffset.ParseExact(moment, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                Assert.InRange(parsed, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));

                // 25 matches but only 20 ids are ever printed: the apply is the selector, pinned to that moment.
                Assert.True(vm.CanConfirmReview);
                Assert.Equal($"defenseclaw alerts acknowledge --severity CRITICAL --before {moment} --yes", vm.ConfirmCommandText);

                await vm.ConfirmReviewCommand.ExecuteAsync(null);

                Assert.Equal(2, calls.Count);
                var apply = calls[1];
                Assert.Equal(new[] { "alerts", "acknowledge", "--severity", "CRITICAL", "--before", moment, "--yes" }, apply);
                Assert.DoesNotContain("--dry-run", apply);
            });
    }

    [Fact]
    public void Choosing_another_severity_takes_a_fresh_preview_with_a_fresh_moment()
    {
        WithReview(
            argv => DryRun(3, Ids(3)),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
                var first = ValueAfter(calls[0], "--before");
                await Task.Delay(30);

                vm.ReviewSeverity = vm.ReviewSeverities.First(c => c.Value == "HIGH");
                await WaitAsync(() => calls.Count >= 2 && !vm.IsPreviewing);

                Assert.Equal("HIGH", ValueAfter(calls[1], "--severity"));
                Assert.NotEqual(first, ValueAfter(calls[1], "--before"));
            });
    }

    // ------------------------------------------------------------------ a preview that names every alert

    [Fact]
    public void A_preview_that_lists_every_match_is_applied_as_those_exact_ids()
    {
        var ids = Ids(3);

        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(3, ids) : Applied(3, 3, "Dismissed", ids),
            async (vm, calls) =>
            {
                await vm.OpenDismissCommand.ExecuteAsync(null);

                Assert.Equal("defenseclaw alerts dismiss --id alert-001 --id alert-002 --id alert-003 --yes", vm.ConfirmCommandText);
                Assert.Contains("exactly the alerts listed below", vm.PreviewSummary, StringComparison.Ordinal);

                await vm.ConfirmReviewCommand.ExecuteAsync(null);

                // --id cannot be combined with a selector, so there is neither --severity nor --before.
                Assert.Equal(new[] { "alerts", "dismiss", "--id", "alert-001", "--id", "alert-002", "--id", "alert-003", "--yes" }, calls[1]);
            });
    }

    [Fact]
    public void Twenty_listed_matches_are_by_id_and_twenty_one_are_a_pinned_selector()
    {
        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(20, Ids(20)) : Applied(20, 20),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
                Assert.Equal(20, vm.ConfirmCommandText.Split("--id").Length - 1);
                Assert.DoesNotContain("--before", vm.ConfirmCommandText, StringComparison.Ordinal);
            });

        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(21, Ids(20)) : Applied(21, 21),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
                Assert.DoesNotContain("--id", vm.ConfirmCommandText, StringComparison.Ordinal);
                Assert.Contains("--before", vm.ConfirmCommandText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void A_preview_whose_listing_does_not_add_up_falls_back_to_the_pinned_selector()
    {
        // Five matched, two ids printed (a wording change, a truncated read): naming two of five would be a different set.
        WithReview(
            argv => DryRun(5, Ids(2)),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);

                Assert.DoesNotContain("--id", vm.ConfirmCommandText, StringComparison.Ordinal);
                Assert.Contains("--before", vm.ConfirmCommandText, StringComparison.Ordinal);
            });
    }

    // ------------------------------------------------------------------ what was applied, against what was previewed

    [Fact]
    public void An_apply_that_matched_more_than_the_preview_says_so()
    {
        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(25, Ids(20)) : Applied(27, 27),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
                await vm.ConfirmReviewCommand.ExecuteAsync(null);

                Assert.True(vm.ShowActionSuccess);
                Assert.Contains("Acknowledged 27 alert(s).", vm.ActionBannerText, StringComparison.Ordinal);
                Assert.Contains("The preview showed 25 alert(s) but 27 were applied", vm.ActionBannerText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void An_apply_that_matches_the_preview_adds_nothing_to_the_result()
    {
        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(25, Ids(20)) : Applied(25, 25),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
                await vm.ConfirmReviewCommand.ExecuteAsync(null);

                Assert.Equal("OK Acknowledged 25 alert(s).", vm.ActionBannerText);
            });
    }

    [Fact]
    public void An_apply_that_did_fewer_than_the_preview_says_so_too()
    {
        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(25, Ids(20)) : Applied(25, 24, "Dismissed"),
            async (vm, calls) =>
            {
                await vm.OpenDismissCommand.ExecuteAsync(null);
                await vm.ConfirmReviewCommand.ExecuteAsync(null);

                Assert.Contains("The preview showed 25 alert(s) but 24 were applied", vm.ActionBannerText, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void An_output_the_panel_cannot_read_gets_no_invented_comparison()
    {
        WithReview(
            argv => argv.Contains("--dry-run") ? DryRun(25, Ids(20)) : Invocation(0, new[] { "  OK done." }),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);
                await vm.ConfirmReviewCommand.ExecuteAsync(null);

                Assert.Equal("OK done.", vm.ActionBannerText);
            });
    }

    // ------------------------------------------------------------------ nothing is applied without a preview

    [Fact]
    public void A_preview_that_failed_leaves_nothing_to_confirm()
    {
        WithReview(
            argv => Invocation(1, new[] { "Error: the gateway said no" }),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);

                Assert.False(vm.CanConfirmReview);
                await vm.ConfirmReviewCommand.ExecuteAsync(null);

                // Only the preview ever ran.
                _ = Assert.Single(calls);
            });
    }

    [Fact]
    public void A_preview_with_no_matches_leaves_nothing_to_confirm()
    {
        WithReview(
            argv => DryRun(0),
            async (vm, calls) =>
            {
                await vm.OpenAcknowledgeCommand.ExecuteAsync(null);

                Assert.False(vm.CanConfirmReview);
                await vm.ConfirmReviewCommand.ExecuteAsync(null);
                _ = Assert.Single(calls);
            });
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "timed out");
            await Task.Delay(10);
        }
    }
}
