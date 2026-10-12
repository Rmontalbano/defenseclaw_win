using System.Text.Json;

namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>
/// What the helper (or the app, reading the same places without any rights) can say about the setup. The <c>status</c> verb prints it as JSON.
/// </summary>
/// <param name="HelperInstalled">The helper is in Program Files.</param>
/// <param name="HelperTrusted">...in an admin-only place and unchanged since it was installed.</param>
/// <param name="CopyPresent">The administrator copy of the gateway is there.</param>
/// <param name="CopyTrusted">...admin-only, recorded and unchanged: the helper will run it, and the app will send it the gateway token.</param>
/// <param name="CopySha256">The copy's hash, when read.</param>
/// <param name="InstalledSha256">The installed gateway's hash, when read.</param>
/// <param name="CopyMatchesInstalled">Null when either hash is unknown.</param>
/// <param name="CopyVersion">The version the copy was recorded for.</param>
/// <param name="Problems">In words.</param>
public sealed record AdminStatusReport(
    bool HelperInstalled,
    bool HelperTrusted,
    bool CopyPresent,
    bool CopyTrusted,
    string? CopySha256,
    string? InstalledSha256,
    bool? CopyMatchesInstalled,
    string? CopyVersion,
    IReadOnlyList<string> Problems)
{
    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Options);

    public static AdminStatusReport? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AdminStatusReport>(json, AdminJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What the helper left for the app after a run: the exit code and the words, in its own admin-only folder.</summary>
public sealed record AdminRunResult(int Schema, string Verb, int ExitCode, string FinishedUtc, IReadOnlyList<string> Lines)
{
    public const int CurrentSchema = 1;

    /// <summary>Most lines kept (the Mac helper keeps 1 MiB of output).</summary>
    public const int MaxLines = 200;

    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Options);

    public static AdminRunResult? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AdminRunResult>(json, AdminJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
