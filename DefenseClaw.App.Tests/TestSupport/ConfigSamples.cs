namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// One config.yaml (as it sits on disk, secrets and all) and the masked projection
/// <c>defenseclaw config show --source --format yaml</c> would print for it. Synthetic: no value here is
/// a real credential, host or account.
/// </summary>
internal static class ConfigSamples
{
    /// <summary>The file on disk (LF; <see cref="LineEndings.With"/> gives the CRLF form).</summary>
    public static readonly string Raw = LineEndings.Normalize("""
        config_version: 8
        gateway:
          host: 127.0.0.1
          api_port: 18970
          token: sample-not-a-real-token
          token_env: DEFENSECLAW_GATEWAY_TOKEN
        llm:
          provider: openai
          model: gpt-4o
          api_key_env: OPENAI_API_KEY
          max_tokens: 4096
          temperature: 0.2
          endpoint: https://user:pw@api.example.test/v1/chat?key=abc
          headers:
            Authorization: sample-auth-value
            X-Team: platform
        guardrail:
          mode: observe
          enabled: true
          scan_roots:
          - C:\src
          - D:\repos
          api_keys:
          - key-one
          - key-two
          allowed_hosts:
          - ok.example.test
          - https://h.example.test/secret-path
        observability: {}

        """);

    /// <summary>What the CLI prints for <see cref="Raw"/>: sorted keys, secrets and header values masked.</summary>
    public static readonly string MaskedSource = LineEndings.Normalize("""
        config_version: 8
        gateway:
          api_port: 18970
          host: 127.0.0.1
          token: '[REDACTED]'
          token_env: DEFENSECLAW_GATEWAY_TOKEN
        guardrail:
          allowed_hosts:
          - ok.example.test
          - https://h.example.test/[REDACTED]
          api_keys:
          - '[REDACTED]'
          - '[REDACTED]'
          enabled: true
          mode: observe
          scan_roots:
          - C:\src
          - D:\repos
        llm:
          api_key_env: OPENAI_API_KEY
          endpoint: https://h.example.test/[REDACTED]
          headers:
            Accept: application/json
            Authorization: '[REDACTED]'
            X-Team: '[REDACTED]'
          max_tokens: 4096
          model: gpt-4o
          provider: openai
          temperature: 0.2
        observability: {}

        """);
}
