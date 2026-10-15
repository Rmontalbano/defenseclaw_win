using System.Text.Json;
using System.Text.Json.Serialization;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

/// <summary>One file of a release this app may download, already vetted: a link into this app's repository whose last segment is the asset's name.</summary>
internal sealed record SelfUpdateAsset(string Name, long Size, string Url, string? Digest);

/// <summary>
/// The newest published release of this app, reduced to what updating needs. Parsed from GitHub's <c>releases/latest</c> JSON by
/// <see cref="Parse"/>, which is also where every refusal to treat a release as an update is decided (draft, prerelease, a tag that is not
/// <c>vX.Y.Z</c>, a missing or mis-named asset).
/// </summary>
internal sealed record SelfUpdateRelease
{
    /// <summary>The repository this app's releases live in (the release contract).</summary>
    public const string RepoOwner = "Rmontalbano";

    public const string RepoName = "defenseclaw_win";

    public const string UserAgentValue = "DefenseClaw-for-Windows-SelfUpdate";

    public static readonly Uri LatestEndpoint = new($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

    /// <summary>The self-update asset: exactly the app and the admin helper at the root.</summary>
    public static string ZipName(AppVersion version) => $"DefenseClawWin-{version}-win-x64.zip";

    public const string ChecksumsName = "checksums.txt";

    public required AppVersion Version { get; init; }

    public required string Tag { get; init; }

    public string? Name { get; init; }

    public string? Notes { get; init; }

    /// <summary>The release page, vetted by <see cref="UpdateChecker.TrustedRepoUrl(string?, string, string)"/>; null when it did not pass.</summary>
    public string? HtmlUrl { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>The zip, or null when the release has none by the exact name.</summary>
    public SelfUpdateAsset? Zip { get; init; }

    public SelfUpdateAsset? Checksums { get; init; }

    /// <summary>Why this release cannot be installed from here (a file is missing), or null when it can.</summary>
    public string? NotInstallableReason
    {
        get
        {
            if (Zip is null)
            {
                return $"The release has no {ZipName(Version)} to install from.";
            }

            return Checksums is null ? $"The release has no {ChecksumsName} to check the download against." : null;
        }
    }

    /// <summary>
    /// Reads the JSON. Null with <paramref name="problem"/> when the answer is not a usable release; a release that is merely not installable
    /// (a file missing) is still returned, with <see cref="NotInstallableReason"/>.
    /// </summary>
    public static SelfUpdateRelease? Parse(string json, out string? problem)
    {
        problem = null;
        GitHubRelease? raw;
        try
        {
            raw = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            problem = $"Could not parse the GitHub response: {ex.Message}";
            return null;
        }

        if (raw is null || string.IsNullOrWhiteSpace(raw.TagName))
        {
            problem = "GitHub's response had no release tag.";
            return null;
        }

        if (raw.Draft || raw.Prerelease)
        {
            problem = $"The latest release ({raw.TagName}) is a draft or a prerelease and is ignored.";
            return null;
        }

        var tag = raw.TagName.Trim();
        if (tag.Length < 6 || tag[0] != 'v' || !AppVersion.TryParse(tag, out var version) || version.IsPrerelease)
        {
            problem = $"The latest release tag '{Truncate(tag)}' is not of the form vX.Y.Z.";
            return null;
        }

        return new SelfUpdateRelease
        {
            Version = version,
            Tag = tag,
            Name = raw.Name,
            Notes = raw.Body,
            HtmlUrl = UpdateChecker.TrustedRepoUrl(raw.HtmlUrl, RepoOwner, RepoName),
            PublishedAt = raw.PublishedAt,
            Zip = Pick(raw.Assets, ZipName(version), tag),
            Checksums = Pick(raw.Assets, ChecksumsName, tag),
        };
    }

    /// <summary>The asset with exactly this name (not a look-alike) whose link is in this repository's release download folder for the tag.</summary>
    private static SelfUpdateAsset? Pick(List<GitHubAsset>? assets, string name, string tag)
    {
        foreach (var asset in assets ?? [])
        {
            if (!string.Equals(asset.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            var url = UpdateChecker.TrustedRepoUrl(asset.BrowserDownloadUrl, RepoOwner, RepoName);
            if (url is null || !new Uri(url).AbsolutePath.Equals($"/{RepoOwner}/{RepoName}/releases/download/{tag}/{name}", StringComparison.Ordinal))
            {
                return null;
            }

            return new SelfUpdateAsset(name, asset.Size, url, string.IsNullOrWhiteSpace(asset.Digest) ? null : asset.Digest.Trim());
        }

        return null;
    }

    private static string Truncate(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}
