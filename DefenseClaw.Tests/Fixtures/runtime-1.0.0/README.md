Synthetic fixtures for the Runtime monitoring card (CUST-348), written by hand from the DefenseClaw 1.0.0 source (tag 1.0.0):

- `config/config.v9.runtime.synthetic.yaml`: `config_version: 9` with an `ai_discovery.runtime` block (internal/config/config.go `AIRuntimeConfig`).
- `rest/health.ai-runtime.*.synthetic.json`: the `ai_runtime` block of `GET /health` as `publishAIRuntimeHealth` (internal/gateway/ai_runtime.go) builds it,
  with the Windows plane reasons from internal/sensor/platform/windows.go. Not captures; every value is made up.
