using System.Text.Json;

namespace DefenseClaw.Core.Setup;

/// <summary>What <see cref="InitReportValidator"/> made of a <c>defenseclaw init --json-summary</c> run.</summary>
public enum InitReportVerdict
{
    /// <summary>The report is complete: status ready or partial, every setup and readiness step pass, warn or skip.</summary>
    Accepted = 0,

    /// <summary>The report says setup needs attention: a failed step, or a status that is not ready or partial.</summary>
    NeedsAttention,

    /// <summary>There is no report, or it is not the shape the CLI documents: nothing may be assumed about what happened.</summary>
    Unreadable,
}

/// <summary>One setup or readiness step of the report.</summary>
/// <param name="Section">"setup" or "readiness".</param>
/// <param name="Name">The step's name as the CLI prints it (<c>Config</c>, <c>Sidecar</c>, ...).</param>
/// <param name="Status"><c>pass</c>, <c>warn</c>, <c>skip</c> or <c>fail</c>, lower-cased.</param>
/// <param name="Detail">The CLI's one-line explanation; may be empty.</param>
/// <param name="NextCommand">The command the CLI suggests for the step; may be empty.</param>
public sealed record InitReportStep(string Section, string Name, string Status, string Detail, string NextCommand);

/// <summary>The validated shape of the report; <see cref="Verdict"/> is the only thing a caller needs to branch on.</summary>
public sealed record InitReportResult
{
    public required InitReportVerdict Verdict { get; init; }

    /// <summary>One sentence for the operator: why the report was refused, or that it was accepted.</summary>
    public required string Message { get; init; }

    /// <summary>The report's own <c>status</c> (<c>ready</c>, <c>partial</c>, <c>needs_attention</c>); empty when unreadable.</summary>
    public string Status { get; init; } = string.Empty;

    public IReadOnlyList<InitReportStep> Steps { get; init; } = Array.Empty<InitReportStep>();

    /// <summary>The steps that did not pass cleanly (fail first, then warn): what the window lists under the verdict.</summary>
    public IReadOnlyList<InitReportStep> Attention => Steps
        .Where(static s => s.Status is "fail" or "warn")
        .OrderBy(static s => s.Status == "fail" ? 0 : 1)
        .ToArray();

    public IReadOnlyList<string> NextCommands { get; init; } = Array.Empty<string>();

    /// <summary>The connectors the report says were configured, when it names more than one.</summary>
    public IReadOnlyList<string> Connectors { get; init; } = Array.Empty<string>();

    public bool IsAccepted => Verdict == InitReportVerdict.Accepted;
}

/// <summary>
/// Decides whether a <c>defenseclaw init --non-interactive --json-summary</c> run actually set DefenseClaw up. <b>Exit code 0 is not
/// success</b>: with <c>--json-summary</c> the CLI prints the report and returns normally even when a step failed, so the report is the
/// result, and nothing after it (starting the gateway, adding further connectors) may run on an exit code alone.
/// <para>
/// The shape is read from the CLI source, not from a captured run (init is never run to see its output):
/// <c>defenseclaw/commands/cmd_init.py</c> <c>_run_first_run_cmd</c> prints <c>json.dumps(report.to_dict(), indent=2)</c> (an extra
/// <c>connectors</c> list when more than one was activated, and <c>connector_mode_warnings</c> when there are any), and
/// <c>defenseclaw/bootstrap.py</c> defines <c>FirstRunReport.to_dict</c> (<c>status</c>, <c>config_file</c>, <c>data_dir</c>,
/// <c>connector</c>, <c>profile</c>, <c>setup[]</c>, <c>readiness[]</c>, <c>next_commands[]</c>), <c>StepResult.to_dict</c>
/// (<c>name</c>, <c>status</c>, <c>detail</c>, <c>next_command</c>) and <c>_rollup_status</c> (any fail: needs_attention; any warn:
/// partial; else ready). Rules, as the Mac app's <c>ConnectorOnboarding.initializationFailure</c> applies them: status must be ready or
/// partial; both step lists must be present; every step must be pass, warn or skip. Diagnostic text around the JSON is tolerated; an
/// ambiguous or incomplete report is never accepted.
/// </para>
/// </summary>
public static class InitReportValidator
{
    private static readonly HashSet<string> AcceptedStepStatuses = new(StringComparer.Ordinal) { "pass", "warn", "skip" };

    public const string UnreadableMessage =
        "Setup did not return a valid completion report. Review the command output before trying again. The gateway was not started.";

    public const string NeedsAttentionMessage =
        "Setup needs attention. Review the output and resolve the reported failures before trying again. The gateway was not started.";

    public static InitReportResult Validate(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || !TryParseReport(output, out var document))
        {
            return Unreadable();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "status") is not { Length: > 0 } status)
            {
                return Unreadable();
            }

            if (!TryReadSteps(root, "setup", out var setup) || !TryReadSteps(root, "readiness", out var readiness))
            {
                return Unreadable();
            }

            var steps = setup.Concat(readiness).ToArray();
            var next = ReadStrings(root, "next_commands");
            var connectors = ReadStrings(root, "connectors");

            // A step with a status the CLI does not define makes the whole report ambiguous.
            if (steps.Any(static s => s.Status != "fail" && !AcceptedStepStatuses.Contains(s.Status)))
            {
                return Unreadable();
            }

            if (steps.Any(static s => s.Status == "fail") || status == "needs_attention")
            {
                return new InitReportResult
                {
                    Verdict = InitReportVerdict.NeedsAttention,
                    Message = NeedsAttentionMessage,
                    Status = status,
                    Steps = steps,
                    NextCommands = next,
                    Connectors = connectors,
                };
            }

            if (status is not ("ready" or "partial"))
            {
                return Unreadable();
            }

            return new InitReportResult
            {
                Verdict = InitReportVerdict.Accepted,
                Message = status == "ready"
                    ? "Setup finished and every check passed."
                    : "Setup finished with warnings. Review them below.",
                Status = status,
                Steps = steps,
                NextCommands = next,
                Connectors = connectors,
            };
        }
    }

    private static InitReportResult Unreadable() => new()
    {
        Verdict = InitReportVerdict.Unreadable,
        Message = UnreadableMessage,
    };

    /// <summary>
    /// The report is the JSON object in the output. The CLI prints it at column 0 (<c>indent=2</c>); a banner or warning may precede it,
    /// so the first attempt starts at the first line that opens an object, the fallback at the first brace anywhere.
    /// </summary>
    private static bool TryParseReport(string output, out JsonDocument document)
    {
        var last = output.LastIndexOf('}');
        foreach (var start in CandidateStarts(output))
        {
            if (start >= last)
            {
                continue;
            }

            try
            {
                document = JsonDocument.Parse(output[start..(last + 1)]);
                return true;
            }
            catch (JsonException)
            {
                // Try the next candidate.
            }
        }

        document = null!;
        return false;
    }

    private static IEnumerable<int> CandidateStarts(string output)
    {
        var lineStart = 0;
        while (lineStart < output.Length)
        {
            if (output[lineStart] == '{')
            {
                yield return lineStart;
            }

            var newline = output.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                break;
            }

            lineStart = newline + 1;
        }

        var first = output.IndexOf('{');
        if (first >= 0)
        {
            yield return first;
        }
    }

    private static bool TryReadSteps(JsonElement root, string section, out IReadOnlyList<InitReportStep> steps)
    {
        steps = Array.Empty<InitReportStep>();
        if (!root.TryGetProperty(section, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<InitReportStep>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "status") is not { Length: > 0 } status)
            {
                return false;
            }

            list.Add(new InitReportStep(section, Text(item, "name"), status.ToLowerInvariant(), Text(item, "detail"), Text(item, "next_command")));
        }

        steps = list;
        return true;
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.String).Select(static e => e.GetString() ?? string.Empty).Where(static s => s.Length > 0).ToArray()
            : Array.Empty<string>();

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Trim() : string.Empty;
}
