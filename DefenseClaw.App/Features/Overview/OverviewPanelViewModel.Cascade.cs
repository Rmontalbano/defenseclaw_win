using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway.Models;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The guardrail cascade on the Overview (runtime 1.0.10). Two pieces, both re-derived without I/O:
/// <list type="bullet">
///   <item>A "Cascade" row on the Effective policy card: <c>default</c>, <c>set (see Policies)</c>, or <c>set, invalid</c> when <c>/health</c> says the
///     gateway fell back (<see cref="PolicyReloadHealth"/>). Only on a runtime with <see cref="RuntimeCapability.GuardrailCascade"/>; older runtimes
///     get exactly the rows they had.</item>
///   <item>Attention rows for an invalid cascade or a refused reload that names it. Only with <see cref="RuntimeCapability.GuardrailCascade"/>:
///     <c>policy.last_reload_error</c> exists before 1.0.10, and an older runtime has no <c>guardrail cascade explain</c> to point at. The owner's
///     machine, where nothing is wrong, sees nothing.</item>
/// </list>
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The label of the card's row.</summary>
    internal const string CascadeRowLabel = "Cascade";

    /// <summary>The title of the row for a cascade the gateway could not apply as written.</summary>
    internal const string CascadeInvalidTitle = "Guardrail cascade is invalid";

    /// <summary>The title of the row for a config reload the gateway refused because of the cascade.</summary>
    internal const string CascadeReloadRefusedTitle = "Config reload refused: guardrail.cascade is invalid";

    private const string CascadeCheckHint = " Check it with: defenseclaw guardrail cascade explain";

    private ConfigDocument? _cascadeRowSource;
    private string _cascadeRowText = "default";

    /// <summary>The card's rows with the Cascade row added; the rows unchanged on a runtime without the cascade.</summary>
    private List<ConfigRow> WithCascadeRow(List<ConfigRow> rows)
    {
        if (!Services.Runtime.Capabilities.Has(RuntimeCapability.GuardrailCascade))
        {
            return rows;
        }

        var config = Services.Config;
        if (!ReferenceEquals(config, _cascadeRowSource))
        {
            _cascadeRowText = GuardrailCascade.From(config).Summary;
            _cascadeRowSource = config;
        }

        var invalid = _snapshot.Health?.PolicyReload is { CascadeInvalid.Length: > 0 };
        var value = invalid ? "set, invalid" : _cascadeRowText;
        rows.Add(new ConfigRow
        {
            Label = CascadeRowLabel,
            Value = value,
            Alternate = rows.Count % 2 == 1,
            ToneKey = invalid ? "Warn" : "Neutral",
        });
        return rows;
    }

    /// <summary>Adds the cascade rows. Called once from <see cref="BuildAttention"/>. Nothing unless the gateway reports one.</summary>
    private void AppendCascadeAttention(List<AttentionRow> rows)
    {
        // Only a runtime with the cascade: an older one has no `guardrail cascade explain` to point at, and its reload errors keep showing as they did (nowhere).
        if (!Services.Runtime.Capabilities.Has(RuntimeCapability.GuardrailCascade) || _snapshot.Health?.PolicyReload is not { } reload)
        {
            return;
        }

        if (reload.CascadeInvalid.Length > 0)
        {
            // The text is cut for display; the severity was judged on the whole text, so say it when the cut dropped the gateway's own sentence.
            var weakerSentence = reload.IsWeaker && !reload.CascadeInvalid.Contains(PolicyReloadHealth.WeakerMark, StringComparison.Ordinal)
                ? " This is WEAKER than the configuration you wrote."
                : string.Empty;
            rows.Add(new AttentionRow
            {
                Title = CascadeInvalidTitle,
                Detail = reload.CascadeInvalid + weakerSentence + CascadeCheckHint,
                SeverityKey = reload.IsWeaker ? "High" : "Medium",
            });
        }

        if (reload.CascadeReloadError.Length > 0 && !string.Equals(reload.CascadeReloadError, reload.CascadeInvalid, StringComparison.Ordinal))
        {
            rows.Add(new AttentionRow
            {
                Title = CascadeReloadRefusedTitle,
                Detail = reload.CascadeReloadError + CascadeCheckHint,
                SeverityKey = "Medium",
            });
        }
    }
}
