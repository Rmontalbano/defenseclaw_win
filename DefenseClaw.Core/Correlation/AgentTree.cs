namespace DefenseClaw.Core.Correlation;

/// <summary>The words and tones the app uses for how a relationship was established and where it stands. Nothing here hides an edge.</summary>
public static class RelationshipDisplay
{
    /// <summary>The method as a badge: <c>reported</c>, <c>trace exact</c>, <c>derived</c>, <c>inferred</c>, or the gateway's own word.</summary>
    public static string MethodLabel(CorrelationRelationshipRow edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Method switch
        {
            RelationshipMethod.Reported => "reported",
            RelationshipMethod.TraceExact => "trace exact",
            RelationshipMethod.Derived => "derived",
            RelationshipMethod.Inferred => "inferred",
            _ => edge.MethodText.Length > 0 ? edge.MethodText : "method not reported",
        };
    }

    /// <summary>The status as a badge. A candidate is the gateway saying it has not resolved the edge, and is worded that way.</summary>
    public static string StatusLabel(CorrelationRelationshipRow edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Status switch
        {
            RelationshipStatus.Active => "active",
            RelationshipStatus.Candidate => "unresolved",
            RelationshipStatus.Superseded => "superseded",
            RelationshipStatus.Rejected => "rejected",
            RelationshipStatus.Conflicted => "conflicted",
            _ => edge.StatusText.Length > 0 ? edge.StatusText : "status not reported",
        };
    }

    /// <summary>The tone key of the method badge (a <c>DcBadge</c> <c>Tag</c>): an inferred edge is a guess and reads as one.</summary>
    public static string MethodTone(CorrelationRelationshipRow edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Method switch
        {
            RelationshipMethod.Reported or RelationshipMethod.TraceExact => "Ok",
            RelationshipMethod.Derived => "Low",
            RelationshipMethod.Inferred => "Medium",
            _ => "Neutral",
        };
    }

    /// <summary>The tone key of the status badge.</summary>
    public static string StatusTone(CorrelationRelationshipRow edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Status switch
        {
            RelationshipStatus.Active => "Ok",
            RelationshipStatus.Candidate => "Warn",
            RelationshipStatus.Conflicted => "High",
            _ => "Neutral",
        };
    }

    /// <summary>
    /// True when nothing about the edge needs a second look: reported or trace-exact, and active. Everything else keeps its badges on screen
    /// and its sentence in <see cref="Sentence"/>.
    /// </summary>
    public static bool IsSettled(CorrelationRelationshipRow edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Status == RelationshipStatus.Active && edge.Method is RelationshipMethod.Reported or RelationshipMethod.TraceExact;
    }

    /// <summary>One sentence for the tooltip and the screen reader: method, status, confidence and evidence.</summary>
    public static string Sentence(CorrelationRelationshipRow edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var evidence = edge.EvidenceCount == 1 ? "1 piece of evidence" : edge.EvidenceCount + " pieces of evidence";
        return $"{MethodLabel(edge)}, {StatusLabel(edge)}, confidence {edge.Confidence}, {evidence}" +
            (edge.RuleId.Length > 0 ? $", rule {edge.RuleId}" : string.Empty);
    }
}

/// <summary>One agent in the tree, the edge that puts it under its parent (null for the root), and its children.</summary>
public sealed record AgentTreeNode(string AgentId, int Depth, CorrelationRelationshipRow? Edge, IReadOnlyList<AgentTreeNode> Children)
{
    /// <summary>The id as the tree shows it.</summary>
    public string ShortId => AgentTree.Short(AgentId);
}

/// <summary>The tree and whether it was cut.</summary>
public sealed record AgentTreeResult(AgentTreeNode Root, int NodeCount, bool Truncated)
{
    /// <summary>Every node, parents before children, in tree order.</summary>
    public IEnumerable<AgentTreeNode> Flatten()
    {
        var stack = new Stack<AgentTreeNode>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }
}

/// <summary>
/// Builds the descendant tree of an agent from the relationships the ledger holds. A child is an agent the root (or another descendant) is the
/// <c>parent_of</c>, or one that is <c>delegated_by</c> it; either edge puts it there. Where several edges join the same pair the most settled
/// one is the one shown (a conflicted edge outranks an active one: a disagreement must not be hidden behind a quiet duplicate), and no edge is
/// dropped for being unsettled - an inferred or unresolved child is in the tree, badged.
/// </summary>
public static class AgentTree
{
    /// <summary>The deepest level the tree goes.</summary>
    public const int MaxDepth = 8;

    /// <summary>The most agents in one tree.</summary>
    public const int MaxNodes = 200;

    /// <summary>A long id as <c>first12…last6</c>; a short one whole.</summary>
    public static string Short(string id) =>
        id.Length <= 24 ? id : string.Concat(id.AsSpan(0, 12), "…", id.AsSpan(id.Length - 6));

    /// <summary>The agent ids that are children of <paramref name="parentId"/> in <paramref name="relationships"/>, in tree order.</summary>
    public static IReadOnlyList<string> ChildrenOf(string parentId, IEnumerable<CorrelationRelationshipRow> relationships) =>
        Edges(relationships).Where(g => g.Key.Parent == parentId).Select(g => g.Key.Child).ToArray();

    /// <summary>The agent ids that <paramref name="childId"/> is a child of in <paramref name="relationships"/>.</summary>
    public static IReadOnlyList<string> ParentsOf(string childId, IEnumerable<CorrelationRelationshipRow> relationships) =>
        Edges(relationships).Where(g => g.Key.Child == childId).Select(g => g.Key.Parent).ToArray();

    /// <summary>
    /// Of <paramref name="agents"/>, the ones with no parent among them: the tops of the trees they form. An agent whose parent is not in the
    /// set is a top too (its parent was not loaded, and the tree starts where the data does). Order of <paramref name="agents"/> is kept.
    /// </summary>
    public static IReadOnlyList<string> RootsOf(IEnumerable<string> agents, IEnumerable<CorrelationRelationshipRow> relationships)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(relationships);

        var set = new HashSet<string>(agents, StringComparer.Ordinal);
        var withParent = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Edges(relationships))
        {
            if (set.Contains(group.Key.Parent) && set.Contains(group.Key.Child))
            {
                _ = withParent.Add(group.Key.Child);
            }
        }

        // A cycle has no top; its members would all have parents. Better to show one of them than nothing.
        var roots = agents.Where(a => set.Contains(a) && !withParent.Contains(a)).Distinct(StringComparer.Ordinal).ToList();
        if (roots.Count == 0 && set.Count > 0)
        {
            roots.Add(agents.First());
        }

        return roots;
    }

    /// <summary>Builds the tree under <paramref name="rootId"/>.</summary>
    public static AgentTreeResult Build(string rootId, IEnumerable<CorrelationRelationshipRow> relationships)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootId);
        ArgumentNullException.ThrowIfNull(relationships);

        var children = new Dictionary<string, List<(string Child, CorrelationRelationshipRow Edge)>>(StringComparer.Ordinal);
        foreach (var group in Edges(relationships))
        {
            if (!children.TryGetValue(group.Key.Parent, out var list))
            {
                list = [];
                children[group.Key.Parent] = list;
            }

            list.Add((group.Key.Child, group.Value));
        }

        var count = 0;
        var truncated = false;
        var onPath = new HashSet<string>(StringComparer.Ordinal);

        AgentTreeNode Make(string id, CorrelationRelationshipRow? edge, int depth)
        {
            count++;
            _ = onPath.Add(id);
            var kids = new List<AgentTreeNode>();
            if (children.TryGetValue(id, out var list))
            {
                foreach (var (child, childEdge) in list)
                {
                    if (onPath.Contains(child))
                    {
                        // A loop in the ledger (an agent its own ancestor): the edge is real, the second visit is not drawn.
                        continue;
                    }

                    if (depth >= MaxDepth || count >= MaxNodes)
                    {
                        truncated = true;
                        continue;
                    }

                    kids.Add(Make(child, childEdge, depth + 1));
                }
            }

            _ = onPath.Remove(id);
            return new AgentTreeNode(id, depth, edge, kids);
        }

        var root = Make(rootId, null, 0);
        return new AgentTreeResult(root, count, truncated);
    }

    /// <summary>
    /// One edge per (parent, child) pair, parent order by first creation: the most settled of the edges that join them. Children of one parent
    /// come in the order their edges were created, ties by id.
    /// </summary>
    private static IEnumerable<KeyValuePair<(string Parent, string Child), CorrelationRelationshipRow>> Edges(IEnumerable<CorrelationRelationshipRow> relationships)
    {
        var best = new Dictionary<(string, string), CorrelationRelationshipRow>();
        foreach (var edge in relationships)
        {
            if (edge.FromKind != "agent" || edge.ToKind != "agent" || edge.FromId == edge.ToId)
            {
                continue;
            }

            (string Parent, string Child)? pair = edge.Type switch
            {
                "parent_of" => (edge.FromId, edge.ToId),
                "delegated_by" => (edge.ToId, edge.FromId),
                _ => null,
            };
            if (pair is not { } key)
            {
                continue;
            }

            if (!best.TryGetValue(key, out var current) || Rank(edge) < Rank(current))
            {
                best[key] = edge;
            }
        }

        return best
            .OrderBy(kv => kv.Value.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(kv => kv.Key.Item2, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<(string Parent, string Child), CorrelationRelationshipRow>(kv.Key, kv.Value));
    }

    /// <summary>Lower is shown in preference: conflicted, then active, then unresolved, then unknown, then the ones kept only for the record; ties by trust of method.</summary>
    private static int Rank(CorrelationRelationshipRow edge)
    {
        var status = edge.Status switch
        {
            RelationshipStatus.Conflicted => 0,
            RelationshipStatus.Active => 1,
            RelationshipStatus.Candidate => 2,
            RelationshipStatus.Unknown => 3,
            RelationshipStatus.Superseded => 4,
            _ => 5,
        };
        return (status * 10) + (int)edge.Method;
    }
}
