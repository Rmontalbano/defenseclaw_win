using DefenseClaw.App.Services;
using DefenseClaw.Core.Observability;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// "What needs attention" while the audit store is over its size cap (runtime 1.0.7): <c>/health</c> <c>telemetry.details.retention_state</c> is <c>warning</c>
/// and says how much the store holds and why a completed retention pass could not trim it. The CLI's status prints it as "Audit retention: Warning: ..." and
/// nothing is blocked (v8_status.py). A Medium row with the CLI's sentence and one button that opens the config editor on
/// <c>observability.local.max_store_mb</c>; nothing is saved until it is saved there. Only while the runtime has <see cref="RuntimeCapability.AuditStoreCapWarning"/>.
/// Re-derived from the snapshot on every poll; no I/O.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>The id of the row's button: open the config editor on the cap.</summary>
    internal const string OpenAuditCapConfigActionId = "open-config-max-store-mb";

    /// <summary>The row's title.</summary>
    internal const string AuditCapTitle = "Audit store is over its size cap";

    /// <summary>Adds the row. Nothing without the capability, a gateway answer or a warning.</summary>
    private void AppendAuditCapAttention(List<AttentionRow> rows)
    {
        if (!Services.Runtime.Capabilities.Has(RuntimeCapability.AuditStoreCapWarning) ||
            _snapshot.Health?.Telemetry?.Details is not { } details)
        {
            return;
        }

        var retention = DestinationHealthReader.ReadRetention(details);
        if (!retention.IsCapWarning)
        {
            return;
        }

        rows.Add(new AttentionRow
        {
            Title = AuditCapTitle,
            Detail = "DefenseClaw status reports: Warning: " + retention.CapWarningSentence +
                     ". It does not block tool calls.",
            SeverityKey = "Medium",
            Actions = new AttentionActions(new[]
            {
                new AttentionAction(
                    OpenAuditCapConfigActionId,
                    "Edit max_store_mb",
                    "Opens the config editor on observability.local.max_store_mb. Nothing is saved until you save it there."),
            }),
        });
    }

    /// <summary>Opens the Setup page's config editor with the search on the cap. Changes nothing.</summary>
    private void OpenAuditCapConfig() => RequestNavigation("setup", new ConfigEditorFocus { Search = "observability.local.max_store_mb" });
}
