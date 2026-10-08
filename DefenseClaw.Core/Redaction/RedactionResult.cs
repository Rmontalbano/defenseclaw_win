using System.Globalization;
using System.Text.Json;

namespace DefenseClaw.Core.Redaction;

/// <summary>What one leg of delivery does with its events once a change is in force, against what it does now.</summary>
public enum RedactionLegKind
{
    /// <summary>It carried events unredacted and will redact them.</summary>
    StartsRedacting,

    /// <summary>It redacted events and will carry them unredacted: raw content starts to flow. The one kind a review asks about.</summary>
    StopsRedacting,

    /// <summary>One redacting profile to another.</summary>
    ChangesProfile,

    /// <summary>It delivered nothing and will deliver (redacted or not: <see cref="RedactionLegChange.NewlyUnredacted"/> says which).</summary>
    StartsDelivering,

    /// <summary>It delivered and will deliver nothing.</summary>
    StopsDelivering,
}

/// <summary>
/// One leg of the effective plan whose profile changes: a bucket's logs or traces, on their way to one destination through one route.
/// <see cref="Before"/> and <see cref="After"/> are a profile name, or <see cref="RedactionVocabulary.NotDelivered"/> for a leg that does not exist
/// on that side.
/// </summary>
public sealed record RedactionLegChange(string Destination, string Route, string Bucket, string Signal, string Before, string After)
{
    /// <summary>Raw content will start to flow on this leg: its profile becomes <c>none</c> (counted in the CLI's <c>newly_unredacted</c>).</summary>
    public bool NewlyUnredacted =>
        string.Equals(After, RedactionVocabulary.NoRedaction, StringComparison.Ordinal) && !string.Equals(Before, RedactionVocabulary.NoRedaction, StringComparison.Ordinal);

    /// <summary>The leg stops being unredacted (counted in <c>no_longer_unredacted</c>).</summary>
    public bool NoLongerUnredacted =>
        string.Equals(Before, RedactionVocabulary.NoRedaction, StringComparison.Ordinal) && !string.Equals(After, RedactionVocabulary.NoRedaction, StringComparison.Ordinal);

    public RedactionLegKind Kind
    {
        get
        {
            if (string.Equals(Before, RedactionVocabulary.NotDelivered, StringComparison.Ordinal))
            {
                return RedactionLegKind.StartsDelivering;
            }

            if (string.Equals(After, RedactionVocabulary.NotDelivered, StringComparison.Ordinal))
            {
                return RedactionLegKind.StopsDelivering;
            }

            if (NewlyUnredacted)
            {
                return RedactionLegKind.StopsRedacting;
            }

            return NoLongerUnredacted ? RedactionLegKind.StartsRedacting : RedactionLegKind.ChangesProfile;
        }
    }
}

/// <summary>A destination whose policy the change cannot touch, and the profiles it keeps (the managed enterprise destination).</summary>
public sealed record RedactionLocked(string Destination, IReadOnlyList<string> Profiles);

/// <summary>
/// Legs that change the same way, folded into one row of the readable diff: <c>local-sqlite / all-collected-logs-and-mandatory-floor / logs:
/// none to sensitive on 14 buckets</c>.
/// </summary>
public sealed record RedactionDiffGroup(
    string Destination,
    string Route,
    string Signal,
    string Before,
    string After,
    IReadOnlyList<string> Buckets,
    RedactionLegKind Kind)
{
    /// <summary>"local-sqlite" and the route, the way the CLI names the pair.</summary>
    public string Target => $"{Destination} / {Route}";

    public string BeforeText => Describe(Before);

    public string AfterText => Describe(After);

    /// <summary>"all 14 buckets", "tool.activity", "model.io, tool.activity", "model.io, tool.activity and 3 more".</summary>
    public string BucketsText
    {
        get
        {
            if (Buckets.Count == RedactionVocabulary.Buckets.Count)
            {
                return $"all {Buckets.Count.ToString(CultureInfo.InvariantCulture)} buckets";
            }

            return Buckets.Count <= 4
                ? string.Join(", ", Buckets)
                : string.Join(", ", Buckets.Take(3)) + $" and {(Buckets.Count - 3).ToString(CultureInfo.InvariantCulture)} more";
        }
    }

    /// <summary>The words that name what happens, so the tone is never the only cue.</summary>
    public string KindText => Kind switch
    {
        RedactionLegKind.StartsRedacting => "starts redacting",
        RedactionLegKind.StopsRedacting => "stops redacting",
        RedactionLegKind.ChangesProfile => "changes profile",
        RedactionLegKind.StartsDelivering => string.Equals(After, RedactionVocabulary.NoRedaction, StringComparison.Ordinal) ? "starts delivering, unredacted" : "starts delivering",
        _ => "stops delivering",
    };

    /// <summary>Red for raw content that starts to flow, green for content that starts to be redacted, neutral for the rest.</summary>
    public string Tone => Kind switch
    {
        RedactionLegKind.StopsRedacting => "Bad",
        RedactionLegKind.StartsDelivering when string.Equals(After, RedactionVocabulary.NoRedaction, StringComparison.Ordinal) => "Bad",
        RedactionLegKind.StartsRedacting => "Ok",
        _ => "Neutral",
    };

    /// <summary>Raw content flows on these legs once the change is in force.</summary>
    public bool NewlyUnredacted => string.Equals(After, RedactionVocabulary.NoRedaction, StringComparison.Ordinal) && !string.Equals(Before, RedactionVocabulary.NoRedaction, StringComparison.Ordinal);

    private static string Describe(string profile) => string.Equals(profile, RedactionVocabulary.NotDelivered, StringComparison.Ordinal) ? "not delivered" : profile;

    internal static int Order(RedactionDiffGroup group) => group.NewlyUnredacted ? 0 : group.Kind == RedactionLegKind.ChangesProfile ? 2 : 1;
}

/// <summary>
/// What a redaction command printed with <c>--json</c>: the preview of <c>--dry-run</c> (nothing written) or the result of an apply. Both
/// have the same shape (<c>_preview_json</c> in <c>cmd_setup_redaction.py</c>).
/// </summary>
public sealed record RedactionResult(
    bool DryRun,
    bool Applied,
    bool Changed,
    bool Restarted,
    int ReportedLegs,
    int ReportedNewlyUnredacted,
    int ReportedNoLongerUnredacted,
    string BackupPath,
    string BeforePlanDigest,
    string AfterPlanDigest,
    string VerifiedPlanDigest,
    IReadOnlyList<RedactionLegChange> Changes,
    IReadOnlyList<RedactionLocked> Locked,
    IReadOnlyList<RedactionWarning> Warnings)
{
    /// <summary>Legs that change. Counted from the list, which is what the diff shows.</summary>
    public int ChangedLegs => Changes.Count;

    /// <summary>
    /// Legs on which raw content starts to flow: the larger of what the CLI counted and what the list shows, so a count that is
    /// short can never talk a review out of asking.
    /// </summary>
    public int NewlyUnredacted => Math.Max(ReportedNewlyUnredacted, Changes.Count(static c => c.NewlyUnredacted));

    public int NoLongerUnredacted => Math.Max(ReportedNoLongerUnredacted, Changes.Count(static c => c.NoLongerUnredacted));

    /// <summary>True for an apply whose written plan is the plan it showed: the CLI re-reads the effective plan after writing and compares digests.</summary>
    public bool IsVerified => Applied && VerifiedPlanDigest.Length > 0 && string.Equals(VerifiedPlanDigest, AfterPlanDigest, StringComparison.Ordinal);

    /// <summary>True when the effective plan is the same before and after (a change that rewrites the file but delivers nothing differently).</summary>
    public bool PlanUnchanged => BeforePlanDigest.Length > 0 && string.Equals(BeforePlanDigest, AfterPlanDigest, StringComparison.Ordinal);

    /// <summary>The legs folded into rows: those that start flowing raw content first, then the rest.</summary>
    public IReadOnlyList<RedactionDiffGroup> Groups =>
        Changes
            .GroupBy(static c => (c.Destination, c.Route, c.Signal, c.Before, c.After))
            .Select(static g => new RedactionDiffGroup(
                g.Key.Destination,
                g.Key.Route,
                g.Key.Signal,
                g.Key.Before,
                g.Key.After,
                g.Select(static c => c.Bucket).Distinct(StringComparer.Ordinal).ToArray(),
                g.First().Kind))
            .OrderBy(RedactionDiffGroup.Order)
            .ThenBy(static g => g.Destination, StringComparer.Ordinal)
            .ThenBy(static g => g.Route, StringComparer.Ordinal)
            .ThenBy(static g => g.Signal, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The first sentence: what the change comes to.</summary>
    public string Headline
    {
        get
        {
            if (!Changed)
            {
                return "Nothing to change: the configuration already says this.";
            }

            if (Changes.Count == 0)
            {
                return DryRun
                    ? "The configuration file would change, but no delivery leg would be redacted differently."
                    : "The configuration file changed, but no delivery leg is redacted differently.";
            }

            var legs = Changes.Count.ToString(CultureInfo.InvariantCulture);
            var noun = Changes.Count == 1 ? "delivery leg" : "delivery legs";
            return DryRun ? $"{legs} {noun} would change." : $"{legs} {noun} changed.";
        }
    }

    /// <summary>Counts by kind of change: "14 start redacting · 28 stop delivering"; empty when no leg changes.</summary>
    public string Breakdown
    {
        get
        {
            var parts = new List<string>();
            foreach (var (kind, text) in new[]
                     {
                         (RedactionLegKind.StopsRedacting, "stop redacting"),
                         (RedactionLegKind.StartsRedacting, "start redacting"),
                         (RedactionLegKind.ChangesProfile, "change profile"),
                         (RedactionLegKind.StartsDelivering, "start delivering"),
                         (RedactionLegKind.StopsDelivering, "stop delivering"),
                     })
            {
                var count = Changes.Count(c => c.Kind == kind);
                if (count > 0)
                {
                    parts.Add($"{count.ToString(CultureInfo.InvariantCulture)} {text}");
                }
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Reads the JSON of a redaction mutation (a preview or an apply).</summary>
public static class RedactionResultParser
{
    /// <summary>
    /// Parses <paramref name="text"/>. Fails closed: a list of changes that is not the length the CLI reported, or an output with no
    /// <c>changed</c> or <c>dry_run</c>, is not trusted (a review's question depends on it).
    /// </summary>
    public static bool TryParse(string? text, out RedactionResult? result, out string error)
    {
        result = null;
        if (!RedactionJson.TryReadObject(text, out var root, out error))
        {
            return false;
        }

        if (!root.TryGetProperty("changed", out var changed) || changed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("dry_run", out var dry) || dry.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            error = "The output is not the result of a redaction change (no 'changed' or 'dry_run').";
            return false;
        }

        var changes = new List<RedactionLegChange>();
        if (root.TryGetProperty("changes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object))
            {
                changes.Add(new RedactionLegChange(
                    RedactionJson.Text(item, "destination"),
                    RedactionJson.Text(item, "route"),
                    RedactionJson.Text(item, "bucket"),
                    RedactionJson.Text(item, "signal"),
                    RedactionJson.Text(item, "before"),
                    RedactionJson.Text(item, "after")));
            }
        }

        var reported = RedactionJson.Number(root, "changed_legs");
        if (reported != changes.Count)
        {
            error = $"The result says {reported.ToString(CultureInfo.InvariantCulture)} legs change but lists {changes.Count.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        var locked = new List<RedactionLocked>();
        if (root.TryGetProperty("locked_profiles", out var lockedList) && lockedList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in lockedList.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object))
            {
                locked.Add(new RedactionLocked(RedactionJson.Text(item, "destination"), RedactionJson.TextList(item, "profiles")));
            }
        }

        result = new RedactionResult(
            DryRun: dry.ValueKind == JsonValueKind.True,
            Applied: RedactionJson.Flag(root, "applied"),
            Changed: changed.ValueKind == JsonValueKind.True,
            Restarted: RedactionJson.Flag(root, "restarted"),
            ReportedLegs: reported,
            ReportedNewlyUnredacted: RedactionJson.Number(root, "newly_unredacted"),
            ReportedNoLongerUnredacted: RedactionJson.Number(root, "no_longer_unredacted"),
            BackupPath: RedactionJson.Text(root, "backup_path"),
            BeforePlanDigest: RedactionJson.Text(root, "before_plan_digest"),
            AfterPlanDigest: RedactionJson.Text(root, "after_plan_digest"),
            VerifiedPlanDigest: RedactionJson.Text(root, "verified_plan_digest"),
            Changes: changes,
            Locked: locked,
            Warnings: RedactionJson.Warnings(root));
        return true;
    }
}
