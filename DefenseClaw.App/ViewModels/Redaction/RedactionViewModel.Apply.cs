using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Core.Text;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.ViewModels.Redaction;

/// <summary>Applying: a fresh preview, then the shared review, then the command.</summary>
public sealed partial class RedactionViewModel
{
    /// <summary>
    /// Starts an apply: nothing runs that changes configuration until the operator confirms the review this opens. First a <b>fresh preview</b>
    /// of exactly the command about to be offered (the same inputs, <c>--dry-run</c>), so the review states what this change does to the plan
    /// as it is <i>now</i>, not as it was when a preview was last looked at. A change that comes to nothing is not offered at all. Then the review:
    /// the command, the consequence in words, whether the gateway restarts, and, when raw content would start to flow, a tick that must be given.
    /// </summary>
    internal async Task BeginApplyAsync(RedactionOperation operation, RedactionInputs inputs, bool restart, bool fromQuick)
    {
        if (!CanAct)
        {
            return;
        }

        if (!StatusIsCurrent)
        {
            ShowNotice("Changes are off", ChangesBlockedReason);
            return;
        }

        if (RedactionArgv.Problems(operation, inputs) is { Count: > 0 } problems)
        {
            ShowNotice("Complete the form", problems[0]);
            return;
        }

        IsBusy = true;
        try
        {
            var preview = await PreviewCoreAsync(operation, inputs, fromQuick).ConfigureAwait(true);
            if (preview is null)
            {
                return;
            }

            if (!preview.Changed)
            {
                ShowNotice("Nothing to apply", "The configuration already says this, so there is no command to review.", InfoBarSeverity.Informational);
                return;
            }

            OpenApplyReview(operation, inputs, restart, preview, fromQuick);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TraceFault("apply", ex);
            ShowNotice("The change could not be prepared", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenApplyReview(RedactionOperation operation, RedactionInputs inputs, bool restart, RedactionResult preview, bool fromQuick)
    {
        var info = RedactionOperations.Info(operation);
        var argv = RedactionArgv.Apply(operation, inputs, restart);
        var raw = preview.NewlyUnredacted > 0;
        var none = RedactionArgv.SetsNoRedaction(operation, inputs);
        var tier = info.IsDestructive || raw || none ? CommandTier.Destructive : CommandTier.StateChanging;

        Review.Open(
            heading: ReviewTitle(operation, inputs),
            explanation: ReviewExplanation(info, preview),
            steps: [new DiscoverStep(argv, "Apply: " + info.Title, tier, ApplyTimeout, Verify: VerifyApplied, RetainFullOutput: true)],
            onFinished: result => AfterApplyAsync(info, restart, result),
            restartsGateway: restart,
            primaryText: "Apply change",
            // Back to a preview: the form must not stay one click from an apply it was only asked about.
            onCancelled: ResetToPreview,
            extraWarnings: ReviewWarnings(preview, restart, none),
            acknowledgement: raw ? RawAcknowledgement : none ? NoneAcknowledgement : null);
    }

    /// <summary>The step's own check of an exit-0 run: the CLI says it applied, and that the plan it wrote is the plan it previewed.</summary>
    internal static string? VerifyApplied(CliInvocation invocation)
    {
        if (!RedactionResultParser.TryParse(Stdout(invocation), out var result, out var error) || result is null)
        {
            return "The command exited 0, but what it printed is not a redaction result: " + error;
        }

        if (!result.Changed)
        {
            return null;
        }

        if (!result.Applied)
        {
            return "The CLI reported that nothing was applied.";
        }

        return result.IsVerified
            ? null
            : "The configuration was written, but its effective plan was not checked against the preview" + (result.BackupPath.Length > 0 ? $". The backup is {result.BackupPath}." : ".");
    }

    private async Task AfterApplyAsync(RedactionOperationInfo info, bool restart, DiscoverReviewResult result)
    {
        var invocation = result.Invocations.LastOrDefault();
        var command = invocation is null ? string.Empty : CommandReview.CommandLine(CommandReview.DefaultExecutable, invocation.Argv);

        RedactionResult? applied = null;
        if (invocation is not null)
        {
            _ = RedactionResultParser.TryParse(Stdout(invocation), out applied, out _);
        }

        if (result.Succeeded && applied is not null)
        {
            Outcome = AppliedOutcome(info, command, applied, restart);
        }
        else if (result.Succeeded)
        {
            Outcome = Failed(info, command, "The command exited 0, but its result could not be read. The Activity panel has the output.", invocation is null ? string.Empty : Transcript(invocation));
        }
        else
        {
            Outcome = Failed(
                info,
                command,
                "The change did not finish. The review has the output, and the Activity panel has all of it. Check the policy below before trying again.",
                invocation is null ? string.Empty : Transcript(invocation));
        }

        // The configuration may have changed whether or not the command succeeded (an apply that failed its own check still wrote the file): read it again.
        await RefreshAsync().ConfigureAwait(true);

        if (result.Succeeded)
        {
            Form.Select(info.Operation);
        }

        ResetToPreview();
    }

    private static RedactionOutcomeViewModel AppliedOutcome(RedactionOperationInfo info, string command, RedactionResult applied, bool restart)
    {
        // The configuration changed between the preview and the apply (something else made the same change): the CLI found nothing to do.
        if (!applied.Changed)
        {
            return new RedactionOutcomeViewModel
            {
                Kind = RedactionOutcomeKind.Applied,
                Heading = info.Title,
                Badge = "Nothing written",
                Command = command,
                Headline = applied.Headline,
                Notes = ["The configuration already said this, so nothing was written and the gateway was not restarted."],
                Warnings = applied.Warnings.Select(static w => new RedactionWarningRow(w)).ToArray(),
                Result = applied,
            };
        }

        var notes = new List<string>();
        if (applied.BackupPath.Length > 0)
        {
            notes.Add("A copy of config.yaml was saved first: " + DisplayNames.Visible(applied.BackupPath));
        }

        notes.Add(applied.IsVerified
            ? $"The plan that was written was checked against the preview and matches (digest {Short(applied.VerifiedPlanDigest)})."
            : "The written plan could not be checked against the preview.");
        notes.Add(applied.Restarted
            ? "The gateway was restarted."
            : restart
                ? "The gateway restart was requested but the CLI did not report it."
                : "The gateway was not restarted. It hot-reloads the policy changes it supports; restart it from the Overview if a change does not take effect.");
        notes.AddRange(applied.Locked.Select(static l => DisplayNames.Visible($"Managed policy stays locked: {l.Destination} ({string.Join(", ", l.Profiles)}).")));

        return new RedactionOutcomeViewModel
        {
            Kind = RedactionOutcomeKind.Applied,
            Heading = "Applied: " + info.Title,
            Badge = applied.IsVerified ? "Applied and verified" : "Applied, not verified",
            Tone = applied.IsVerified ? "Ok" : "Warn",
            Command = command,
            Headline = applied.Headline,
            Breakdown = applied.Breakdown,
            Diff = applied.Groups.Select(static g => new RedactionDiffRow(g)).ToArray(),
            Notes = notes,
            Warnings = applied.Warnings.Select(static w => new RedactionWarningRow(w)).ToArray(),
            Result = applied,
        };
    }

    // ------------------------------------------------------------------ what the review says

    private static string ReviewTitle(RedactionOperation operation, RedactionInputs inputs) => operation switch
    {
        RedactionOperation.RemoveAll => "Remove all configurable redaction?",
        RedactionOperation.ApplyEverywhere => $"Apply the {inputs.Profile} profile everywhere?",
        RedactionOperation.ApplyDefaults => $"Make {inputs.Profile} the default profile?",
        RedactionOperation.DefaultsSet => "Change the global defaults?",
        RedactionOperation.DefaultsReset => "Reset the global defaults?",
        RedactionOperation.BucketSet => $"Change the policy of {inputs.Bucket}?",
        RedactionOperation.BucketReset => $"Reset the policy of {inputs.Bucket}?",
        RedactionOperation.ProfileSet => $"{(inputs.IsNewProfile ? "Create" : "Update")} the profile {inputs.ProfileName}?",
        RedactionOperation.ProfileRemove => $"Remove the profile {inputs.ProfileName}?",
        RedactionOperation.DestinationSend => $"Set the send policy of {inputs.Destination}?",
        RedactionOperation.DestinationInherit => $"Restore {inputs.Destination} to the defaults?",
        RedactionOperation.RouteAdd => $"Add the route {inputs.RouteName} to {inputs.Destination}?",
        RedactionOperation.RouteSet => $"Replace the route {inputs.RouteName} of {inputs.Destination}?",
        RedactionOperation.RouteMove => $"Move the route {inputs.RouteName} of {inputs.Destination} to position {(inputs.Position ?? 0).ToString(CultureInfo.InvariantCulture)}?",
        _ => $"Remove the route {inputs.RouteName} from {inputs.Destination}?",
    };

    private static string ReviewExplanation(RedactionOperationInfo info, RedactionResult preview)
    {
        var effect = preview.Changes.Count == 0
            ? "No delivery leg is redacted differently, but the configuration file is rewritten."
            : $"{preview.Headline} {(preview.Breakdown.Length > 0 ? "(" + preview.Breakdown + ")" : string.Empty)}".Trim();
        return $"{info.Summary} {effect} A copy of config.yaml is saved to the backups folder first, and the plan that is written is checked against this preview.";
    }

    private static IReadOnlyList<CommandReviewWarning> ReviewWarnings(RedactionResult preview, bool restart, bool setsNone)
    {
        var warnings = new List<CommandReviewWarning>();

        if (preview.NewlyUnredacted > 0)
        {
            var where = DisplayNames.Visible(string.Join("; ", preview.Groups.Where(static g => g.NewlyUnredacted).Take(3).Select(static g => $"{g.Target} {g.Signal} ({g.BucketsText})")));
            warnings.Add(new CommandReviewWarning(
                "Raw content will flow",
                $"{Legs(preview.NewlyUnredacted)} will carry raw, unredacted content: {where}. What they carry can include prompts, tool input and output, file paths and secrets."));
        }
        else if (setsNone)
        {
            warnings.Add(new CommandReviewWarning(
                "Profile none: no redaction",
                "No leg changes today, but profile none sends content as recorded. Whatever follows it, including a bucket or a destination you add later, will send raw content too."));
        }

        if (!restart)
        {
            warnings.Add(new CommandReviewWarning(
                "Gateway not restarted",
                "The command says --no-restart: the gateway keeps running as it is. It hot-reloads the policy changes it supports; restart it from the Overview if a change does not take effect."));
        }

        foreach (var locked in preview.Locked)
        {
            warnings.Add(new CommandReviewWarning(
                "Locked destination",
                DisplayNames.Visible($"{locked.Destination} keeps its own profile ({string.Join(", ", locked.Profiles)}): its policy belongs to the release and this change cannot touch it.")));
        }

        return warnings;
    }
}
