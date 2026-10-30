Synthetic fixtures for the 1.0.5 parity round (CUST-401), written by hand from the DefenseClaw 1.0.5 source (tag 1.0.5). Not captures; every value that is not a
protocol constant is made up.

- `acp/acp-status.healthy.synthetic.json` and `acp/acp-status.attention.synthetic.json`: `defenseclaw acp status --json`, the payload `_status` builds
  (cli/defenseclaw/commands/cmd_acp.py:1814) with `json.dumps(..., indent=2, sort_keys=True)`. The problem sentences are the shapes `_pinned_entry_problem`
  writes (cmd_acp.py:1285). `acp verify` has no JSON form.
