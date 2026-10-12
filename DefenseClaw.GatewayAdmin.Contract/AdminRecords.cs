using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DefenseClaw.GatewayAdmin.Contract;

/// <summary>
/// What the helper wrote about the administrator copy when it installed it. It lives in the copy's admin-only folder, so it is the one hash
/// the later steps (start, the app's peer check) trust: nothing in a user-writable place ever decides what the helper runs.
/// </summary>
public sealed record CopyRecord(
    int Schema,
    string Sha256,
    string? Version,
    string InstalledUtc,
    string SourceSha256,
    string SignatureState)
{
    public const int CurrentSchema = 1;

    public static CopyRecord Create(string sha256, string? version, DateTime nowUtc, string sourceSha256, string signatureState) =>
        new(CurrentSchema, sha256, version, nowUtc.ToString("O", CultureInfo.InvariantCulture), sourceSha256, signatureState);

    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Options);

    public static CopyRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<CopyRecord>(json, AdminJson.Options);
            return record is { Schema: CurrentSchema } && Hex.IsSha256(record.Sha256) ? record with { Sha256 = record.Sha256.ToLowerInvariant() } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The hash the helper wrote for itself when it installed itself into Program Files.</summary>
public sealed record HelperRecord(int Schema, string Sha256, string InstalledUtc)
{
    public const int CurrentSchema = 1;

    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Options);

    public static HelperRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<HelperRecord>(json, AdminJson.Options);
            return record is { Schema: CurrentSchema } && Hex.IsSha256(record.Sha256) ? record with { Sha256 = record.Sha256.ToLowerInvariant() } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What the app writes before it asks for a copy: which gateway bytes it checked, against what. It sits in a folder the user can write, so
/// it is a <i>claim</i>, not a credential - a same-user program can write the same file. The helper therefore uses it only as a cross-check
/// (the gateway it is about to copy must hash to <see cref="GatewaySha256"/>; the manifest must be fresh and for this user) and shows its
/// content to the operator in its own confirmation, which a program running as the unelevated user cannot answer (UIPI).
/// </summary>
public sealed record ReleaseManifest(
    int Schema,
    string UserSid,
    string? Version,
    string GatewaySha256,
    string ZipSha256,
    string SignatureState,
    string CreatedUtc)
{
    public const int CurrentSchema = 1;

    /// <summary>How old a manifest may be when the helper reads it.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    /// <summary>How far in the future a manifest's clock may be (clock skew between the writer and the reader is nil; this is slack).</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public string ToJson() => JsonSerializer.Serialize(this, AdminJson.Options);

    public static ReleaseManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, AdminJson.Options);
            if (manifest is not { Schema: CurrentSchema } ||
                !Hex.IsSha256(manifest.GatewaySha256) ||
                !Hex.IsSha256(manifest.ZipSha256) ||
                !AdminLayout.IsValidSid(manifest.UserSid))
            {
                return null;
            }

            return manifest with { GatewaySha256 = manifest.GatewaySha256.ToLowerInvariant(), ZipSha256 = manifest.ZipSha256.ToLowerInvariant() };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Null when the manifest may be used by the user <paramref name="userSid"/> at <paramref name="nowUtc"/>; otherwise why not.</summary>
    public string? Problem(string userSid, DateTime nowUtc)
    {
        if (!string.Equals(UserSid, userSid, StringComparison.OrdinalIgnoreCase))
        {
            return "The manifest is for another user.";
        }

        if (!DateTime.TryParse(CreatedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created))
        {
            return "The manifest has no valid time.";
        }

        if (created > nowUtc + MaxSkew)
        {
            return "The manifest is dated in the future.";
        }

        return nowUtc - created > MaxAge ? "The manifest is older than 30 minutes; prepare the copy again." : null;
    }
}

/// <summary>Hex helpers for SHA-256 strings.</summary>
public static partial class Hex
{
    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Shape();

    public static bool IsSha256(string? value) => value is { Length: 64 } && Sha256Shape().IsMatch(value);

    /// <summary>Lower-case hex SHA-256 of everything left in <paramref name="stream"/>.</summary>
    public static string HashOf(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>The first <paramref name="length"/> characters, for a review line.</summary>
    public static string Short(string? sha256, int length = 16) =>
        string.IsNullOrEmpty(sha256) ? "(none)" : sha256.Length <= length ? sha256 : sha256[..length] + "...";
}

internal static class AdminJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
