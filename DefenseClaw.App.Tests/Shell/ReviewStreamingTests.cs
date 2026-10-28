using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// CUST-382: a review of several commands shows the running step and its output while it runs. The runner is a script handed to
/// <see cref="DiscoverActionReview.RunStep"/> that announces its invocation the way <see cref="CliRunner"/> does; nothing starts a process.
/// </summary>
public sealed class ReviewStreamingTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public ReviewStreamingTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static DiscoverStep Step(string argvText) => new(argvText.Split(' '), "does a thing", CommandTier.StateChanging);

    private static CliInvocation Started(DiscoverActionReview review, IReadOnlyList<string> argv)
    {
        var invocation = InvocationFactory.Create(false, argv.ToArray());
        review.OnStepInvocationStarted(null, invocation);
        return invocation;
    }

    [Fact]
    public async Task Lines_appear_while_the_step_runs_and_the_current_step_is_named()
    {
        var review = new DiscoverActionReview(_services);
        string? textDuringStep2 = null;
        string? outputDuringStep2 = null;
        review.RunStep = (_, argv, _) =>
        {
            var invocation = Started(review, argv);
            if (argv[1] == "two")
            {
                InvocationFactory.Append(invocation, "fetching index");
                InvocationFactory.Append(invocation, "warning: slow", CliStream.StandardError);
                review.PullLive();
                textDuringStep2 = review.ResultText;
                outputDuringStep2 = review.ResultOutput;
            }

            InvocationFactory.Finish(invocation, 0);
            return Task.FromResult(invocation);
        };
        review.Open("Run three", "Three.", new[] { Step("registry one"), Step("registry two"), Step("registry three") });

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.StartsWith("Step 2 of 3 is running", textDuringStep2, StringComparison.Ordinal);
        Assert.Contains("Step 2:", outputDuringStep2, StringComparison.Ordinal);
        Assert.Contains("fetching index", outputDuringStep2, StringComparison.Ordinal);
        Assert.Contains("warning: slow", outputDuringStep2, StringComparison.Ordinal);
        Assert.StartsWith("Done.", review.ResultText, StringComparison.Ordinal);
        Assert.All(review.CommandReview!.Steps, s => Assert.Equal("Ok", s.StatusKey));
    }

    [Fact]
    public async Task The_current_step_index_advances_and_earlier_output_stays()
    {
        var review = new DiscoverActionReview(_services);
        var seen = new List<string>();
        review.RunStep = (_, argv, _) =>
        {
            var invocation = Started(review, argv);
            InvocationFactory.Append(invocation, "out of " + argv[1]);
            review.PullLive();
            seen.Add(review.ResultText + "|" + review.ResultOutput);
            InvocationFactory.Finish(invocation, 0);
            return Task.FromResult(invocation);
        };
        review.Open("Run", "Two.", new[] { Step("registry one"), Step("registry two") });

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("Step 1 of 2 is running", seen[0], StringComparison.Ordinal);
        Assert.Contains("Step 2 of 2 is running", seen[1], StringComparison.Ordinal);
        Assert.Contains("out of one", seen[1], StringComparison.Ordinal);
        Assert.Contains("out of two", seen[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_step_still_stops_the_sequence()
    {
        var review = new DiscoverActionReview(_services);
        var ran = new List<string>();
        review.RunStep = (_, argv, _) =>
        {
            ran.Add(argv[1]);
            var invocation = Started(review, argv);
            InvocationFactory.Append(invocation, "boom", CliStream.StandardError);
            review.PullLive();
            InvocationFactory.Finish(invocation, argv[1] == "two" ? 2 : 0);
            return Task.FromResult(invocation);
        };
        review.Open("Run", "Three.", new[] { Step("registry one"), Step("registry two"), Step("registry three") });

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "one", "two" }, ran);
        var rows = review.CommandReview!.Steps;
        Assert.Equal("Bad", rows[1].StatusKey);
        Assert.StartsWith("Skipped", rows[2].StatusText, StringComparison.Ordinal);
        Assert.StartsWith("Did not finish.", review.ResultText, StringComparison.Ordinal);
        Assert.Equal("Bad", review.ResultKey);
    }

    [Fact]
    public async Task The_live_buffer_is_capped_and_says_how_much_was_left_out()
    {
        var review = new DiscoverActionReview(_services);
        string? output = null;
        review.RunStep = (_, argv, _) =>
        {
            var invocation = Started(review, argv);
            if (argv[1] == "one")
            {
                InvocationFactory.Finish(invocation, 0);
                return Task.FromResult(invocation);
            }

            for (var burst = 0; burst < 5; burst++)
            {
                InvocationFactory.AppendNumbered(invocation, 100, "chatty", first: burst * 100 + 1);
                review.PullLive();
            }

            InvocationFactory.Append(invocation, new string('x', 1000));
            review.PullLive();
            output = review.ResultOutput;
            InvocationFactory.Finish(invocation, 0);
            return Task.FromResult(invocation);
        };
        review.Open("Run", "Two.", new[] { Step("registry one"), Step("registry two") });

        await review.ConfirmCommand.ExecuteAsync(null);

        var lines = output!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= 35, $"{lines.Length} lines");
        Assert.Contains("471 earlier line(s) omitted", output, StringComparison.Ordinal);
        Assert.DoesNotContain("chatty 1" + Environment.NewLine, output, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 400), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_single_step_review_shows_no_live_output_and_no_step_sentence()
    {
        var review = new DiscoverActionReview(_services);
        string? during = "unset";
        string? outputDuring = "unset";
        review.RunStep = (_, argv, _) =>
        {
            var invocation = Started(review, argv);
            InvocationFactory.Append(invocation, "hello");
            review.PullLive();
            during = review.ResultText;
            outputDuring = review.ResultOutput;
            InvocationFactory.Finish(invocation, 0);
            return Task.FromResult(invocation);
        };
        review.Open("Run", "One.", new[] { Step("registry one") });

        await review.ConfirmCommand.ExecuteAsync(null);

        Assert.Null(during);
        Assert.Null(outputDuring);
        Assert.Equal("Done. The exact command and its full output are in the Activity panel.", review.ResultText);
        Assert.Equal("hello", review.ResultOutput);
    }

    [Fact]
    public async Task The_finished_report_replaces_the_live_text_with_the_same_per_step_tail()
    {
        var review = new DiscoverActionReview(_services);
        review.RunStep = (_, argv, _) =>
        {
            var invocation = Started(review, argv);
            InvocationFactory.Append(invocation, "done " + argv[1]);
            review.PullLive();
            InvocationFactory.Finish(invocation, 0);
            return Task.FromResult(invocation);
        };
        review.Open("Run", "Two.", new[] { Step("registry one"), Step("registry two") });

        await review.ConfirmCommand.ExecuteAsync(null);

        var nl = Environment.NewLine;
        Assert.Equal($"Step 1:{nl}done one{nl}Step 2:{nl}done two", review.ResultOutput);
    }
}
