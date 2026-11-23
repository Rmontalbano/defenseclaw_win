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
///   <item>Attention rows for an invalid cascade or a refused reload that names it. No capability gate: the keys are absent before 1.0.10 and while
///     nothing is wrong, and the owner's machine, where nothing is wrong, sees nothing.</item>
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
        if (_snapshot.Health?.PolicyReload is not { } reload)
        {
            return;
        }

        if (reload.CascadeInvalid.Length > 0)
        {
            rows.Add(new AttentionRow
            {
                Title = CascadeInvalidTitle,
                Detail = reload.CascadeInvalid + CascadeCheckHint,
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
