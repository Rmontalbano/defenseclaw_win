using DefenseClaw.Core.Audit;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The protected-file watch's alert in the Alerts panel (CUST-422, runtime 1.0.6). The row's text is all there is to read: the app never opens the watched file.
/// </summary>
public sealed partial class AlertsPanelViewModel
{
    private bool _kindsListProtectedFiles;

    /// <summary>Re-offers the Kind popup when the runtime's answer changed what it lists; an older runtime's popup stays as it was.</summary>
    private void RefreshKindChoices()
    {
        var has = Services.Runtime.Capabilities.Has(RuntimeCapability.ProtectedFileWatch);
        if (has == _kindsListProtectedFiles)
        {
            return;
        }

        _kindsListProtectedFiles = has;
        OnPropertyChanged(nameof(KindChoices));
        if (!has && string.Equals(KindFilter, AlertKinds.ProtectedFiles, StringComparison.Ordinal))
        {
            KindFilter = AlertKinds.All;
        }
    }
}

public sealed partial class AlertItem
{
    /// <summary>What a <c>protected-file-changed</c> row says; null on every other row.</summary>
    public ProtectedFileChange? ProtectedFile { get; private set; }

    /// <summary>True when the inspector shows the "What changed" section.</summary>
    public bool IsProtectedFileChange => ProtectedFile is not null;

    /// <summary>The inspector's "What changed" rows: the file, who was running, and the hash prefixes. A value the row does not hold is left out, not invented.</summary>
    public IReadOnlyList<AlertField> ProtectedFileFacts
    {
        get
        {
            if (ProtectedFile is not { } change)
            {
                return Array.Empty<AlertField>();
            }

            var rows = new List<AlertField> { new("File", change.File) };
            Add(rows, "Agent", change.Connector);
            Add(rows, "Agent identity", change.AgentIdentity);
            Add(rows, "Session", change.Session);
            Add(rows, "Tool call", change.ToolCall);
            rows.Add(new AlertField("SHA-256 before", ProtectedFileAlert.DigestLabel(change.Before)));
            rows.Add(new AlertField("SHA-256 after", ProtectedFileAlert.DigestLabel(change.After)));
            if (change.Restored is { } restored)
            {
                rows.Add(new AlertField(
                    "Restore",
                    restored
                        ? "The last accepted content was put back."
                        : change.RestoreError.Length > 0 ? "Failed: " + change.RestoreError : "Failed."));
            }

            return rows;

            static void Add(List<AlertField> into, string name, string value)
            {
                if (value.Length > 0)
                {
                    into.Add(new AlertField(name, value));
                }
            }
        }
    }

    /// <summary>The limits the row must not hide: how the agent was named, and what the watch does and does not hold.</summary>
    public string ProtectedFileNote => ProtectedFile is null
        ? string.Empty
        : ProtectedFileWatchFacts.Attribution + " " +
          (ProtectedFile.Restored is true ? string.Empty : ProtectedFileWatchFacts.BlocksNothing + " ") +
          ProtectedFileWatchFacts.NoContent;

    /// <summary>
    /// Makes a <c>protected-file-changed</c> row first-class: the rule, a readable title and the facts of its details text, however the row was built. Any other
    /// row is returned as it is. Called on a row being built, before anything shows it.
    /// </summary>
    /// <param name="row">The runtime's own row for it, when there is one: its facts name the session and agent when the details do not.</param>
    internal AlertItem WithProtectedFile(RuntimeAlertRow? row = null)
    {
        if (!ProtectedFileAlert.IsAction(Action))
        {
            return this;
        }

        var target = RawTarget.Length > 0 ? RawTarget : TargetRef;
        var details = Details.Length > 0 ? Details : row?.Details;
        var facts = row?.Facts;
        ProtectedFile = ProtectedFileAlert.Parse(
            target.Length > 0 ? target : row?.Target,
            details,
            Connector,
            SessionId.Length > 0 ? SessionId : FactNamed(facts, "session_id"),
            FactNamed(facts, "agent_identity_id"));

        RuleId = ProtectedFileAlert.RuleId;
        Headline = "Protected file changed: " + ProtectedFileAlert.FileLabel(ProtectedFile.File);
        return this;

        static string? FactNamed(IReadOnlyList<RuntimeAlertFact>? list, string name) =>
            list?.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))?.Value;
    }
}
