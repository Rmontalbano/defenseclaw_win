Synthetic fixtures for the ACP Guard wizard (CUST-294), written by hand from the DefenseClaw 1.0.4 source (tag 1.0.4). Not captures; every value that is
not a protocol constant is made up.

- `acp/acp-setup-help.synthetic.txt`: `defenseclaw acp setup --help` as Click renders the options of `setup_cmd` (cli/defenseclaw/commands/cmd_acp.py:1012-1043);
  the `--client` and `--agent` choices are `sorted(ACP_CLIENT_IDS)` and `sorted(ACP_AGENT_ENTRY_POINTS)` (cli/defenseclaw/acp_catalog.py).
- `acp/acp-catalog.synthetic.json`: `GET /v1/acp/catalog`, the `acp.Catalog` struct (internal/acp/catalog.go) with the 1.0.4 built-in clients and agents; URLs and the
  schema digest are placeholders.
- `acp/acp-profiles.synthetic.json`: `GET /v1/acp/profiles`, the map `handleACPProfiles` writes (internal/gateway/acp.go) with `config.ACPProfile` entries
  (internal/config/config.go); two invented profiles.
