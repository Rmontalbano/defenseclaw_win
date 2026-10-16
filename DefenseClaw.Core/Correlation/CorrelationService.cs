using System.Text.Json;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Core.Correlation;

/// <summary>What loading an agent's tree ended with.</summary>
/// <param name="Status">The first failure that stopped the load, or <see cref="CorrelationReadStatus.Ok"/> when every agent that was asked for was read.</param>
/// <param name="Message">One sentence for the operator when it did not end Ok.</param>
/// <param name="AgentsRead">How many agents' first pages were read.</param>
/// <param name="Capped">True when the walk stopped at <see cref="AgentLedger.MaxAgents"/> agents.</param>
/// <param name="Notes">Reads of single descendants that failed while the rest went on.</param>
public sealed record AgentLoadOutcome(CorrelationReadStatus Status, string Message, int AgentsRead, bool Capped, IReadOnlyList<string> Notes)
{
    public bool IsOk => Status == CorrelationReadStatus.Ok;
}

/// <summary>
/// The reads of the agent view, over a fetch seam: <c>(path, maxBytes, token) -&gt; the gateway's JSON</c>. The app wires it to
/// <see cref="GatewayClient.GetBoundedJsonAsync"/> (authenticated, bounded, and only over a connection whose owner was verified as the gateway);
/// tests wire it to a fake. Every call is a GET; nothing here can change state.
/// </summary>
public sealed class CorrelationService(Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>> fetch)
{
    private readonly Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>> _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));

    /// <summary>The most directory pages read in one go.</summary>
    public const int MaxDirectoryPages = 5;

    /// <summary>One page of the graph around <paramref name="anchor"/>.</summary>
    public Task<CorrelationRead<CorrelationGraphPage>> ReadGraphAsync(
        CorrelationAnchor anchor, int limit, string? afterTime, string? afterId, CancellationToken cancellationToken) =>
        ReadAsync(
            () => CorrelationRoutes.Query(CorrelationRoutes.Graph, anchor, limit, afterTime, afterId),
            CorrelationRoutes.MaxGraphBytes,
            CorrelationReader.ParseGraph,
            cancellationToken);

    /// <summary>The conflicted edges and contradicting deliveries around <paramref name="anchor"/>.</summary>
    public Task<CorrelationRead<CorrelationConflictsPage>> ReadConflictsAsync(CorrelationAnchor anchor, int limit, CancellationToken cancellationToken) =>
        ReadAsync(
            () => CorrelationRoutes.Query(CorrelationRoutes.Conflicts, anchor, limit),
            CorrelationRoutes.MaxGraphBytes,
            CorrelationReader.ParseConflicts,
            cancellationToken);

    /// <summary>One page of the agent directory.</summary>
    public Task<CorrelationRead<AgentDirectoryPage>> ReadDirectoryAsync(int limit, string? cursor, CancellationToken cancellationToken) =>
        ReadAsync(
            () => CorrelationRoutes.Directory(limit, cursor),
            CorrelationRoutes.MaxDirectoryBytes,
            CorrelationReader.ParseDirectory,
            cancellationToken);

    /// <summary>
    /// The directory, page after page until the gateway has no next cursor or <see cref="MaxDirectoryPages"/> pages are in. A failure on a later page
    /// keeps what was read and says the list is partial; a failure on the first is the failure.
    /// </summary>
    public async Task<CorrelationRead<AgentDirectoryPage>> ReadDirectoryAsync(int pageSize, CancellationToken cancellationToken)
    {
        var first = await ReadDirectoryAsync(pageSize, null, cancellationToken).ConfigureAwait(false);
        if (!first.IsOk || first.Value is not { Enabled: true } head)
        {
            return first;
        }

        var rows = head.Rows.ToList();
        var skipped = head.Skipped;
        var next = head.NextCursor;
        var pages = 1;
        var note = string.Empty;
        while (next.Length > 0 && pages < MaxDirectoryPages && rows.Count < CorrelationReader.MaxIdentities)
        {
            var more = await ReadDirectoryAsync(pageSize, next, cancellationToken).ConfigureAwait(false);
            if (!more.IsOk || more.Value is null)
            {
                note = "The list is partial: " + more.Message;
                next = string.Empty;
                break;
            }

            rows.AddRange(more.Value.Rows);
            skipped += more.Value.Skipped;
            next = more.Value.NextCursor;
            pages++;
        }

        var merged = head with
        {
            Rows = rows.Take(CorrelationReader.MaxIdentities).ToArray(),
            NextCursor = next,
            Skipped = skipped,
            PersistError = head.PersistError.Length > 0 ? head.PersistError : note,
        };
        return new CorrelationRead<AgentDirectoryPage>(CorrelationReadStatus.Ok, merged, note);
    }

    /// <summary>
    /// Reads what is needed to draw the agents around a starting point into <paramref name="ledger"/>.
    /// <para>
    /// The directory lists agent <i>identities</i> (one per harness install and user); the ledger's agents are per session, so an identity is
    /// opened through the session it last reported: that session's first page names the agents in it. From the agents found (or from
    /// <paramref name="startAgents"/>, when the caller already has an agent id) the walk goes both ways along <c>parent_of</c> / <c>delegated_by</c>
    /// edges, reading each agent's first page, until it has the whole connected family or <see cref="AgentLedger.MaxAgents"/> agents.
    /// </para>
    /// <para>
    /// Stops at once on a failure that applies to every read (no route, refused token, nothing answering, no ledger yet); a single agent that
    /// cannot be read is a note. An agent already in the ledger is not read again.
    /// </para>
    /// </summary>
    public async Task<AgentLoadOutcome> LoadAsync(
        AgentLedger ledger, string? sessionId, IReadOnlyList<string> startAgents, int pageLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(startAgents);

        var notes = new List<string>();
        var read = 0;
        var capped = false;
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(sessionId))
        {
            var key = AgentLedger.SessionKey(sessionId);
            if (!ledger.Has(key))
            {
                var page = await ReadGraphAsync(CorrelationAnchor.ForSession(sessionId), pageLimit, null, null, cancellationToken).ConfigureAwait(false);
                if (!page.IsOk || page.Value is null)
                {
                    return new AgentLoadOutcome(page.Status, page.Message, read, capped, notes);
                }

                _ = ledger.AddPage(key, page.Value);
                read++;
            }

            foreach (var agent in ledger.AgentsOf(key))
            {
                if (seen.Add(agent))
                {
                    queue.Enqueue(agent);
                }
            }
        }

        foreach (var agent in startAgents)
        {
            if (seen.Add(agent))
            {
                queue.Enqueue(agent);
            }
        }

        var first = true;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!ledger.Has(id))
            {
                var page = await ReadGraphAsync(CorrelationAnchor.ForAgent(id), pageLimit, null, null, cancellationToken).ConfigureAwait(false);
                if (page.IsOk && page.Value is { } value)
                {
                    if (!ledger.AddPage(id, value))
                    {
                        capped = true;
                        continue;
                    }

                    read++;
                }
                else if (first || page.Status is CorrelationReadStatus.Unsupported or CorrelationReadStatus.Unauthorized or CorrelationReadStatus.Unreachable or CorrelationReadStatus.NotReady)
                {
                    return new AgentLoadOutcome(page.Status, page.Message, read, capped, notes);
                }
                else
                {
                    notes.Add("Could not read agent " + AgentTree.Short(id) + ": " + page.Message);
                    continue;
                }
            }

            first = false;
            foreach (var neighbour in AgentTree.ChildrenOf(id, ledger.Relationships).Concat(AgentTree.ParentsOf(id, ledger.Relationships)))
            {
                if (seen.Contains(neighbour))
                {
                    continue;
                }

                if (seen.Count >= AgentLedger.MaxAgents)
                {
                    capped = true;
                    continue;
                }

                _ = seen.Add(neighbour);
                queue.Enqueue(neighbour);
            }
        }

        return new AgentLoadOutcome(CorrelationReadStatus.Ok, string.Empty, read, capped, notes);
    }

    private async Task<CorrelationRead<T>> ReadAsync<T>(Func<string> path, int maxBytes, Func<JsonElement, CorrelationRead<T>> parse, CancellationToken cancellationToken)
        where T : class
    {
        string route;
        try
        {
            route = path();
        }
        catch (ArgumentException ex)
        {
            // An anchor the gateway would refuse never leaves the app; the sentence is the check's own.
            return new CorrelationRead<T>(CorrelationReadStatus.Rejected, null, ex.Message);
        }

        var result = await _fetch(route, maxBytes, cancellationToken).ConfigureAwait(false);

        // Parsing is linear in the answer and runs off the caller's thread; the document is disposed with it.
        return await Task.Run(
            () =>
            {
                try
                {
                    return CorrelationReader.FromGateway(result, parse);
                }
                finally
                {
                    result.Value?.Dispose();
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
