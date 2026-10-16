Synthetic fixtures for the Agents panel (CUST-355), written by hand from the DefenseClaw 1.0.x gateway source (tag 1.0.0). Not captures; every id, name and time is made up.

- `correlation-graph.*.synthetic.json`: `GET /api/v1/correlation/graph` as `audit.CorrelationGraph` (internal/audit/correlation_query_v8.go) marshals it,
  including Go's `null` for an empty slice and `0001-01-01T00:00:00Z` for an unset time. `root.page1` is truncated with a next cursor; `root.page2` is the last page.
- `correlation-conflicts.synthetic.json`: `GET /api/v1/correlation/conflicts` (`audit.CorrelationConflicts`).
- `agents-identities*.synthetic.json`: `GET /api/v1/agents/identities` (`handleAgentIdentities`, internal/gateway/agent_identity_recorder.go).
