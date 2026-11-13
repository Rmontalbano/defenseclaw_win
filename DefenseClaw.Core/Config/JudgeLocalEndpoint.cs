using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Config;

/// <summary>
/// Whether the LLM judge talks to a local or private model server, by the runtime's own test (DefenseClaw 1.0.9, <c>judgeParallelLimit</c> and
/// <c>localJudgeEndpoint</c> in internal/gateway/llm_judge.go, <c>LLMConfig.IsLocalProvider</c> in internal/config/config.go): a judge like that gets
/// <c>guardrail.judge.max_parallel</c> 1 when the key is left out. The judge's LLM is <c>guardrail.judge.llm</c> over the top-level <c>llm</c>, field by field,
/// with the deprecated <c>guardrail.judge.model</c> / <c>api_base</c> standing in for the judge block. Read from config.yaml text only; nothing is run.
/// </summary>
public static class JudgeLocalEndpoint
{
    private static readonly string[] LocalProviders = { "ollama", "vllm", "lm_studio", "lmstudio", "local" };

    private static readonly Regex BedrockModelId = new(
        @"^(?:(?:us|eu|apac|jp|au|ca|global|us-gov)\.)?(?:anthropic|amazon|meta|mistral|cohere|ai21|openai|deepseek|qwen|writer|twelvelabs|stability|luma|google|nvidia|minimax|moonshot|moonshotai)\.[a-z0-9]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True when the judge's endpoint is local or private by the runtime's test, false when it is not, null when this app cannot tell (the judge is bound to a
    /// custom provider instance whose address lives in <c>llm_providers.custom</c>, which is not read here).
    /// </summary>
    public static bool? IsLocal(ConfigYamlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var provider = First(reader.Text("guardrail", "judge", "llm", "provider"), reader.Text("llm", "provider"));
        var model = First(
            reader.Text("guardrail", "judge", "llm", "model"),
            reader.Text("guardrail", "judge", "model"),
            reader.Text("llm", "model"),
            reader.Text("default_llm_model"));
        var baseUrl = First(
            reader.Text("guardrail", "judge", "llm", "base_url"),
            reader.Text("guardrail", "judge", "api_base"),
            reader.Text("llm", "base_url"));
        var instance = First(reader.Text("guardrail", "judge", "llm", "instance_name"), reader.Text("llm", "instance_name"));

        if (baseUrl.Length == 0 && instance.Length > 0)
        {
            return null;
        }

        return baseUrl.Length > 0
            ? LocalEndpoint(baseUrl)
            : IsLocalProvider(provider, model, baseUrl);
    }

    /// <summary>The runtime's <c>IsLocalProvider</c> for an endpoint-less judge (the base URL is empty here, so only the provider names decide).</summary>
    internal static bool IsLocalProvider(string provider, string model, string baseUrl)
    {
        if (LocalProviders.Contains(ProviderPrefix(provider, model), StringComparer.Ordinal))
        {
            return true;
        }

        return baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
               baseUrl.Contains("[::1]", StringComparison.Ordinal) ||
               baseUrl.StartsWith("unix:", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ProviderPrefix(string provider, string model)
    {
        if (provider.Length > 0)
        {
            return provider.ToLowerInvariant();
        }

        var slash = model.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            return model[..slash].ToLowerInvariant();
        }

        return BedrockModelId.IsMatch(model.ToLowerInvariant()) ? "bedrock" : string.Empty;
    }

    /// <summary>The runtime's <c>localJudgeEndpoint</c>: a unix socket, localhost names, or a loopback, private or link-local address.</summary>
    internal static bool LocalEndpoint(string endpoint)
    {
        if (endpoint.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return false;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" or "localhost.localdomain" ||
            host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".localhost.localdomain", StringComparison.Ordinal))
        {
            return true;
        }

        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }

        var zone = host.IndexOf('%', StringComparison.Ordinal);
        if (zone >= 0)
        {
            host = host[..zone];
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 127 || b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) || (b[0] == 224 && b[1] == 0 && b[2] == 0);
        }

        return IPAddress.IsLoopback(address) || (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) ||
               (b[0] == 0xFF && (b[1] & 0x0F) == 0x02);
    }

    private static string First(params string?[] values) =>
        values.FirstOrDefault(static v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
