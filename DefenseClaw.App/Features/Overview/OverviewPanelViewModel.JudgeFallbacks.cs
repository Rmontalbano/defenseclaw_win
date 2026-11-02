using System.Globalization;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" while the LLM judge has been giving no verdict in time (CUST-426, runtime 1.0.6): the rules decided those hook checks instead, which
/// <c>/health</c> now counts (<see cref="JudgeFallbackHealth"/>). The CLI prints it as a yellow "Judge fallbacks" row (cmd_status.py
/// <c>_print_judge_fallbacks</c>), so it is a Medium row here, in the CLI's words, with its suggestion of <c>guardrail.judge.max_parallel</c> only while that is
/// off (<c>judge_max_parallel_hint</c>). Nothing probes the runtime: the field is simply absent before 1.0.6. Re-derived from the snapshot on every poll; no I/O.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The row title starts with this; the count follows.</summary>
    internal const string JudgeFallbacksTitlePrefix = "Judge fallbacks: the rules decided ";

    private ConfigDocument? _judgeConfigSource;
    private bool _judgeMaxParallelOff = true;

    /// <summary>
    /// True while <c>guardrail.judge.max_parallel</c> is off: absent or zero or below. A value that is there and not a whole number says nothing either way (the
    /// CLI reads it with its own rules), so it suggests nothing. Parsed once per parsed config and kept until it is replaced.
    /// </summary>
    private bool JudgeMaxParallelIsOff()
    {
        var config = Services.Config;
        if (!ReferenceEquals(config, _judgeConfigSource))
        {
            var reader = ConfigYamlReader.From(config);
            _judgeMaxParallelOff = reader.Integer("guardrail", "judge", "max_parallel") is { } value
                ? value <= 0
                : !reader.Has("guardrail", "judge", "max_parallel");
            _judgeConfigSource = config;
        }

        return _judgeMaxParallelOff;
    }

    /// <summary>Adds the row. Called once from <see cref="BuildAttention"/>. Nothing without a gateway answer or without a fallback.</summary>
    private void AppendJudgeFallbackAttention(List<AttentionRow> rows)
    {
        if (JudgeFallbackHealth.From(_snapshot.Health?.Guardrail) is not { } fallbacks)
        {
            return;
        }

        var summary = fallbacks.Summary;
        rows.Add(new AttentionRow
        {
            Title = $"{JudgeFallbacksTitlePrefix}{fallbacks.Total.ToString("N0", CultureInfo.CurrentCulture)} hook check(s)",
            Detail = char.ToUpperInvariant(summary[0]) + summary[1..] + ". Counted since the judge started." +
                     (JudgeMaxParallelIsOff() ? " Suggestion: " + JudgeFallbackHealth.MaxParallelHint + "." : string.Empty),
            SeverityKey = "Medium",
        });
    }
}
