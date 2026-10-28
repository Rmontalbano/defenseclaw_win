namespace DefenseClaw.Core.Policy.Model;

/// <summary>What looking at an edited pack decided: nothing to re-pin, a problem, or a review to open.</summary>
/// <param name="Consequence">The review's text, with both digests; null when there is nothing to review.</param>
/// <param name="Problem">Why the re-pin was not offered (a notice's text); empty when <paramref name="Consequence"/> is set.</param>
public sealed record PackRepinDecision(PolicyConsequence? Consequence, string Problem)
{
    /// <summary>True when the review may open.</summary>
    public bool CanReview => Consequence is not null;
}

/// <summary>
/// The decision behind "Re-pin edited pack" (runtime 1.0.4, <c>guardrail use-pack --repin</c>): after <c>validate-pack --json</c> the digest of the
/// pack's own files (<c>summary.files_digest</c>, which is what <c>guardrail.custom_packs</c> pins) is compared with the digest config.yaml pins. If
/// they differ the review states the old and the new one; if they do not there is nothing to re-pin, and the command is never run.
/// </summary>
public static class PackRepin
{
    /// <summary>Decides from the action (it carries the pinned digest) and the validation that was just read.</summary>
    public static PackRepinDecision Decide(PolicyAction action, PackValidation validation)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(validation);

        if (!validation.IsValid)
        {
            return new PackRepinDecision(null, $"'{action.Title}' was not offered because the pack did not validate ({validation.Summary}). The output is below; fix the pack, then try again.");
        }

        var now = PackPin.Spelled(validation.FilesDigest);
        if (now.Length == 0)
        {
            return new PackRepinDecision(null, "The validator did not report the digest of the pack's files, so the new digest cannot be shown and nothing is re-pinned.");
        }

        var pinned = PackPin.Spelled(action.PinnedDigest);
        if (string.Equals(pinned, now, StringComparison.Ordinal))
        {
            return new PackRepinDecision(null, $"The pin already matches the pack ({now}). There is nothing to re-pin.");
        }

        var old = pinned.Length > 0 ? pinned : "(none)";
        var consequence = action.Consequence with
        {
            Summary = $"Pinned digest {old} → {now}.",
            Details = new[] { $"Pinned now: {old}", $"The pack now: {now}" }.Concat(action.Consequence.Details).ToArray(),
        };
        return new PackRepinDecision(consequence, string.Empty);
    }
}
