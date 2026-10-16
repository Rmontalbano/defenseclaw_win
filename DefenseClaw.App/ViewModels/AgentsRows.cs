using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.Core.Correlation;

namespace DefenseClaw.App.ViewModels;

/// <summary>One agent identity in the directory list.</summary>
public sealed class AgentDirectoryRowVm
{
    public AgentDirectoryRowVm(AgentIdentityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
    }

    public AgentIdentityRow Row { get; }

    public string AgentId => Row.AgentId;

    public string ShortId => AgentTree.Short(Row.AgentId);

    /// <summary>The group header: the connector, or a statement that none was reported.</summary>
    public string Connector => Row.Connector.Length > 0 ? Row.Connector : "(connector not reported)";

    public string UserName => Row.UserName;

    public string LastSessionId => Row.LastSessionId;

    public DateTimeOffset? LastSeen => Row.LastSeen;

    public string LastSeenText => Row.LastSeen is { } at ? "last seen " + AgentsFormat.Moment(at) : "last seen: not reported";

    public string SessionsText => Row.SessionsSeen == 1 ? "1 session" : Row.SessionsSeen.ToString(CultureInfo.InvariantCulture) + " sessions";

    /// <summary>True when the account that owned this identity no longer exists on the machine; the row is kept and says so.</summary>
    public bool Retired => Row.Retired;

    public string RetiredText => Row.Retired ? "retired account" : string.Empty;

    /// <summary>The line under the last-seen time: sessions, then the user, then the retired marker, with nothing dangling when one is absent.</summary>
    public string MetaText => string.Join(" · ", new[] { SessionsText, Row.UserName, RetiredText }.Where(s => s.Length > 0));

    public string AutomationName =>
        $"{Connector} agent {ShortId}, {(Row.UserName.Length > 0 ? Row.UserName + ", " : string.Empty)}{LastSeenText}, {SessionsText}{(Row.Retired ? ", retired account" : string.Empty)}";

    /// <summary>What the filter box matches: connector, user, id and last session.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0 ||
        filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            Connector.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            UserName.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            AgentId.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            LastSessionId.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One agent in the tree, with the badges of the edge that puts it under its parent. They stay visible whatever the edge's state.</summary>
public sealed partial class AgentTreeNodeVm : ObservableObject
{
    public AgentTreeNodeVm(AgentTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Node = node;
        foreach (var child in node.Children)
        {
            Children.Add(new AgentTreeNodeVm(child));
        }
    }

    public AgentTreeNode Node { get; }

    public string AgentId => Node.AgentId;

    public string ShortId => Node.ShortId;

    public ObservableCollection<AgentTreeNodeVm> Children { get; } = new();

    /// <summary>False for a root: it hangs from nothing.</summary>
    public bool HasEdge => Node.Edge is not null;

    public string MethodLabel => Node.Edge is { } e ? RelationshipDisplay.MethodLabel(e) : string.Empty;

    public string MethodTone => Node.Edge is { } e ? RelationshipDisplay.MethodTone(e) : "Neutral";

    public string StatusLabel => Node.Edge is { } e ? RelationshipDisplay.StatusLabel(e) : string.Empty;

    public string StatusTone => Node.Edge is { } e ? RelationshipDisplay.StatusTone(e) : "Neutral";

    public string EdgeSentence => Node.Edge is { } e ? RelationshipDisplay.Sentence(e) : "Top of this tree";

    public string AutomationName =>
        Node.Edge is { } e ? $"Agent {ShortId}, {RelationshipDisplay.Sentence(e)}" : $"Agent {ShortId}, top of this tree";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>This node and every node under it.</summary>
    public IEnumerable<string> SubtreeIds()
    {
        yield return AgentId;
        foreach (var child in Children)
        {
            foreach (var id in child.SubtreeIds())
            {
                yield return id;
            }
        }
    }

    public AgentTreeNodeVm? Find(string agentId)
    {
        if (AgentId == agentId)
        {
            return this;
        }

        foreach (var child in Children)
        {
            if (child.Find(agentId) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>One line of the timeline: a turn header, or an entry.</summary>
public sealed class TimelineRowVm
{
    private TimelineRowVm()
    {
    }

    public bool IsHeader { get; private init; }

    public bool IsEntry => !IsHeader;

    public string HeaderText { get; private init; } = string.Empty;

    public string TimeText { get; private init; } = string.Empty;

    public string KindLabel { get; private init; } = string.Empty;

    public string KindTone { get; private init; } = "Neutral";

    public string Title { get; private init; } = string.Empty;

    public string Detail { get; private init; } = string.Empty;

    public string AutomationName { get; private init; } = string.Empty;

    public TimelineEntry? Entry { get; private init; }

    public static TimelineRowVm Header(TimelineGroup group, int shown) => new()
    {
        IsHeader = true,
        HeaderText = $"{group.Label} · {shown.ToString(CultureInfo.InvariantCulture)} {(shown == 1 ? "entry" : "entries")}",
        AutomationName = $"{group.Label}, {shown.ToString(CultureInfo.InvariantCulture)} entries",
    };

    public static TimelineRowVm ForEntry(TimelineEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var detail = string.Join(
            " · ",
            new[] { entry.Bucket.Length > 0 ? entry.Bucket : null, entry.EventName.Length > 0 && entry.EventName != entry.Title ? entry.EventName : null, entry.Signal.Length > 0 ? entry.Signal : null, entry.Rail.Length > 0 ? "via " + entry.Rail : null }
                .Where(s => s is not null));
        var time = entry.Time is { } at ? AgentsFormat.Moment(at, seconds: true) : "time not reported";
        var kind = AgentsFormat.KindLabel(entry.Kind);
        return new TimelineRowVm
        {
            Entry = entry,
            TimeText = time,
            KindLabel = kind,
            KindTone = AgentsFormat.KindTone(entry.Kind),
            Title = entry.Title,
            Detail = detail,
            AutomationName = $"{time}, {kind}, {entry.Title}",
        };
    }
}

/// <summary>One tile above the timeline. A figure the ledger does not carry is <see cref="IsReported"/> false and says "Not reported".</summary>
public sealed record KpiTile(string Label, string Value, string Tone, string Note, bool IsReported)
{
    public string AutomationName => $"{Label}: {Value}. {Note}";
}

internal static class AgentsFormat
{
    public static string Moment(DateTimeOffset at, bool seconds = false) =>
        at.ToLocalTime().ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string KindLabel(TimelineKind kind) => kind switch
    {
        TimelineKind.Prompt => "Prompt",
        TimelineKind.Model => "Model",
        TimelineKind.Tool => "Tool",
        TimelineKind.Approval => "Approval",
        TimelineKind.Guardrail => "Guardrail",
        TimelineKind.Block => "Block",
        TimelineKind.Enforcement => "Enforcement",
        TimelineKind.Outcome => "Outcome",
        TimelineKind.Lifecycle => "Lifecycle",
        _ => "Event",
    };

    public static string KindTone(TimelineKind kind) => kind switch
    {
        TimelineKind.Block => "Critical",
        TimelineKind.Enforcement => "High",
        TimelineKind.Approval => "Medium",
        TimelineKind.Guardrail => "Low",
        TimelineKind.Outcome => "Ok",
        _ => "Neutral",
    };
}
