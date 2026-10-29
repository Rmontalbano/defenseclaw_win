using System.Text.Json;

namespace DefenseClaw.Core.Gateway;

/// <summary>
/// The gateway's own <c>gateway.pid</c> as a 1.x gateway writes it on Windows: one JSON object with <c>pid</c>, <c>executable</c>,
/// <c>data_dir</c>, <c>start_time</c> and <c>start_identity</c> (upstream <c>internal/daemon</c> <c>pidInfo</c>, written once by the daemon child
/// as it starts, <c>RegisterCurrentProcess</c>). A bare number (the pre-1.0 file) names no executable and is not a record here, the same rule
/// upstream applies before a pid record may say who owns the API port.
/// <para>
/// The file sits in the user's data folder, so it is what the gateway <i>claimed</i>, never proof on its own: any program of the same account can
/// write it. Whoever reads it pairs it with something that account cannot write.
/// </para>
/// </summary>
/// <param name="Pid">The gateway's process id.</param>
/// <param name="Executable">The full path the gateway process runs from, as it saw it.</param>
/// <param name="DataDirectory">The data folder the gateway was started for; null when the record has none.</param>
/// <param name="StartTime">A Unix-seconds lower bound on the start; null when absent.</param>
/// <param name="StartIdentity">The creation time Windows reported for the process, as an opaque string; null when absent.</param>
public sealed record GatewayPidRecord(int Pid, string Executable, string? DataDirectory, long? StartTime, string? StartIdentity)
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "gateway.pid";

    /// <summary>The largest file read as a record (upstream reads at most 16 KiB of it).</summary>
    public const int MaxBytes = 16 * 1024;

    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 4 };

    /// <summary>The record in <paramref name="text"/>, or null when it is not a JSON object with a positive <c>pid</c> and a non-empty <c>executable</c>.</summary>
    public static GatewayPidRecord? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text, Options);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var id) || id <= 0 ||
                String(root, "executable") is not { } executable)
            {
                return null;
            }

            long? startTime = root.TryGetProperty("start_time", out var start) && start.ValueKind == JsonValueKind.Number && start.TryGetInt64(out var seconds)
                ? seconds
                : null;
            return new GatewayPidRecord(id, executable, String(root, "data_dir"), startTime, String(root, "start_identity"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
