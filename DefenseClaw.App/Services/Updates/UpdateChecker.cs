using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Services.Updates;

/// <summary>Outcome bucket the Updates window keys its layout off.</summary>
public enum UpdateCheckState
{
    /// <summary>Before the first check completes.</summary>
    Unknown = 0,

    /// <summary>Installed version is the latest release (or newer — e.g. a pre-release build).</summary>
    UpToDate,

    /// <summary>A newer release is published on GitHub.</summary>
    UpdateAvailable,

    /// <summary>
    /// The check could not produce a trustworthy comparison — GitHub was unreachable, rate
    /// limited, or the installed version could not be determined. Never conflated with
    /// <see cref="UpToDate"/>: silence about updates is not the same as confirming there are
    /// none.
    /// </summary>
    CheckFailed,
}

/// <summary>One release asset, straight off the GitHub API — name and size only, never a
/// download of the bytes themselves unless a caller explicitly fetches a known-small sidecar.</summary>
public sealed record ReleaseAsset
{
    public required string Name { get; init; }

    public long Size { get; init; }

    public string? DownloadUrl { get; init; }

    public string? ContentType { get; init; }
}

/// <summary>Result of one <see cref="UpdateChecker.CheckAsync"/> call.</summary>
public sealed record UpdateCheckResult
{
    public static readonly UpdateCheckResult NotCheckedYet = new()
    {
        State = UpdateCheckState.Unknown,
        Detail = "Update status has not been checked yet.",
    };

    public UpdateCheckState State { get; init; } = UpdateCheckState.Unknown;

    /// <summary>e.g. <c>0.8.7</c>. Null when it could not be determined.</summary>
    public string? InstalledVersion { get; init; }

    /// <summary>The latest release's tag, e.g. <c>v0.8.7</c> or <c>0.8.7</c>.</summary>
    public string? LatestVersion { get; init; }

    public string? ReleaseName { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public string? HtmlUrl { get; init; }

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = Array.Empty<ReleaseAsset>();

    /// <summary>True when the last GitHub fetch hit the API's rate limit (unauthenticated: 60/hour/IP).</summary>
    public bool IsRateLimited { get; init; }

    /// <summary>Human-readable explanation, always set for <see cref="UpdateCheckState.CheckFailed"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional context — e.g. why comparison was ambiguous, or that data is stale.</summary>
    public string Detail { get; init; } = string.Empty;

    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>True when this result came from the 24h on-disk cache rather than a live fetch.</summary>
    public bool FromCache { get; init; }

    public bool IsUpdateAvailable => State == UpdateCheckState.UpdateAvailable;
}

/// <summary>
/// Checks the installed DefenseClaw version against the latest GitHub release.
/// <para>
/// Reads only: one GET against <c>/releases/latest</c>, cached for 24h in
/// <c>%LOCALAPPDATA%</c> so a shell that polls this on every open does not burn through the
/// unauthenticated GitHub rate limit (60 requests/hour/IP). A 403/429 is treated as an
/// ordinary "try later" outcome, never an exception the caller has to guard against — and
/// falls back to a stale cache entry when one exists, rather than going blank.
/// </para>
/// <para>
/// The installed version comes from whichever of two read-only sources answers first: the
/// gateway's own <c>/health</c> provenance (already polled by <see cref="GatewayMonitor"/>,
/// so this is free), or the CLI via <see cref="CliRunner"/> when the gateway is not answering —
/// preferring the structured <c>defenseclaw --version-json</c> (0.8.10+) and falling back to
/// the older <c>defenseclaw --version</c> text only when that flag is not understood. Neither
/// path writes anything.
/// </para>
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string RepoOwner = "cisco-ai-defense";
    public const string RepoName = "defenseclaw";
    public const string UserAgentValue = "DefenseClaw-Windows-Companion";

    public static readonly Uri LatestReleaseEndpoint =
        new($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");

    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// How far ahead of this machine's clock a cache entry's <c>FetchedAt</c> may be before the entry is not trusted.
    /// A <c>FetchedAt</c> in the future makes "now minus fetched" negative, which is always inside
    /// <see cref="CacheLifetime"/>: such an entry would be served as fresh forever.
    /// </summary>
    public static readonly TimeSpan CacheClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The only place a release link may point: this repository, on github.com, over https.</summary>
    public static string RepoUrlPrefix => $"https://github.com/{RepoOwner}/{RepoName}/";

    /// <summary>
    /// The normalised form of <paramref name="url"/> when it is an https link into this repository
    /// (<see cref="RepoUrlPrefix"/>), otherwise <c>null</c>. Release links come from GitHub's JSON or from the
    /// cache file on disk, and are then opened with the shell, pasted into copyable commands and, for the sidecar
    /// files, fetched: none of that should follow a link that names another scheme (<c>file:</c>, <c>ms-…:</c>),
    /// another host or another repository. Checked on the parsed address rather than the text, so dot segments,
    /// user-info tricks (<c>https://github.com@evil.example/…</c>) and a non-default port cannot pass a prefix test.
    /// </summary>
    public static string? TrustedRepoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            uri.UserInfo.Length > 0 ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith($"/{RepoOwner}/{RepoName}/", StringComparison.Ordinal))
        {
            return null;
        }

        // A dot segment, spelled out or percent-encoded (%2e%2e), would be resolved by whatever opens the link — a
        // browser reads %2e as a dot — to a path outside the repository that the prefix test above never saw.
        foreach (var segment in uri.AbsolutePath.Split('/'))
        {
            if (Uri.UnescapeDataString(segment) is "." or "..")
            {
                return null;
            }
        }

        return uri.AbsoluteUri;
    }

    /// <summary>
    /// <paramref name="value"/> as a PowerShell single-quoted string, quotes included: nothing inside single quotes is
    /// expanded, so a <c>$(…)</c>, a backtick or a <c>"</c> in a URL is text, not code. The single quote itself is
    /// doubled — and so are the four typographic single quotes, which PowerShell also treats as quote characters.
    /// </summary>
    internal static string PowerShellSingleQuoted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new System.Text.StringBuilder(value.Length + 4).Append('\'');
        foreach (var c in value)
        {
            builder.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                builder.Append(c);
            }
        }

        return builder.Append('\'').ToString();
    }

    private static readonly Regex VersionPattern =
        new(@"\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.]+)?", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly AppServices _services;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _cacheFilePath;

    public UpdateChecker(
        AppServices services,
        HttpClient httpClient,
        bool ownsHttpClient = false,
        string? cacheFilePath = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
        _cacheFilePath = cacheFilePath ?? DefaultCacheFilePath();
    }

    /// <summary>Builds an <see cref="HttpClient"/> with the headers GitHub's API requires/prefers.</summary>
    public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    public static string DefaultCacheFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DefenseClaw.App",
        "updates",
        "latest-release-cache.json");

    /// <summary>
    /// Runs the check. Uses the on-disk cache when it is fresh, unless
    /// <paramref name="forceRefresh"/> is set (the window's "Check again" action).
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var installedVersion = await ResolveInstalledVersionAsync(cancellationToken).ConfigureAwait(false);

        // TryReadCache already drops an entry stamped more than CacheClockSkew ahead of this clock (a clock stepped
        // back, or a forged file), so whatever survives may sit slightly in the future and still counts as fresh.
        var cached = TryReadCache();
        var cacheIsFresh = cached is not null && DateTimeOffset.UtcNow - cached.FetchedAt < CacheLifetime;

        if (!forceRefresh && cacheIsFresh)
        {
            return BuildResult(installedVersion, cached!, fromCache: true, isRateLimited: false, fetchError: null);
        }

        var (release, isRateLimited, fetchError) = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);

        if (release is not null)
        {
            TryWriteCache(release);
            return BuildResult(installedVersion, release, fromCache: false, isRateLimited: false, fetchError: null);
        }

        // Live fetch failed. A stale cache beats nothing — including when the cache is
        // stale-but-present and the failure was a rate limit, which is the common case for
        // "checked a moment ago, checking again too soon".
        if (cached is not null)
        {
            var result = BuildResult(installedVersion, cached, fromCache: true, isRateLimited, fetchError);
            var cachedAtText = cached.FetchedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            return result with
            {
                Detail = isRateLimited
                    ? $"GitHub's API rate limit was hit; showing cached data from {cachedAtText}."
                    : $"The live check failed ({fetchError}); showing cached data from {cachedAtText}.",
            };
        }

        return new UpdateCheckResult
        {
            State = UpdateCheckState.CheckFailed,
            InstalledVersion = installedVersion,
            IsRateLimited = isRateLimited,
            ErrorMessage = fetchError ?? "The release check failed for an unknown reason.",
            Detail = isRateLimited
                ? "GitHub's unauthenticated API rate limit (60 requests/hour/IP) was hit. Try again later."
                : fetchError ?? "The release check failed for an unknown reason.",
            CheckedAt = DateTimeOffset.UtcNow,
            FromCache = false,
        };
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private UpdateCheckResult BuildResult(
        string? installedVersion,
        CachedRelease release,
        bool fromCache,
        bool isRateLimited,
        string? fetchError)
    {
        var (state, detail) = Compare(installedVersion, release.TagName);

        // Every link is re-checked on its way out, whether the release came from GitHub just now or from the cache file:
        // one that is not in this repository is dropped (the page cannot be opened, the asset cannot be downloaded)
        // and the detail line says so, rather than the window quietly working from an address nobody vetted.
        var htmlUrl = TrustedRepoUrl(release.HtmlUrl);
        var dropped = release.HtmlUrl is { Length: > 0 } && htmlUrl is null ? 1 : 0;
        var assets = new List<ReleaseAsset>();
        foreach (var asset in release.Assets ?? Array.Empty<ReleaseAsset>())
        {
            var downloadUrl = TrustedRepoUrl(asset.DownloadUrl);
            if (asset.DownloadUrl is { Length: > 0 } && downloadUrl is null)
            {
                dropped++;
            }

            assets.Add(asset with { DownloadUrl = downloadUrl });
        }

        if (dropped > 0)
        {
            detail += $" {dropped} release link{(dropped == 1 ? " was" : "s were")} not on {RepoUrlPrefix} and {(dropped == 1 ? "was" : "were")} ignored.";
        }

        return new UpdateCheckResult
        {
            State = state,
            InstalledVersion = installedVersion,
            LatestVersion = release.TagName,
            ReleaseName = release.Name,
            PublishedAt = release.PublishedAt,
            HtmlUrl = htmlUrl,
            Assets = assets,
            IsRateLimited = isRateLimited,
            ErrorMessage = state == UpdateCheckState.CheckFailed ? (fetchError ?? detail) : fetchError,
            Detail = detail,
            CheckedAt = release.FetchedAt,
            FromCache = fromCache,
        };
    }

    /// <summary>
    /// Compares the installed version against the latest tag. Never guesses: an unparsable
    /// pair that is not an exact string match comes back as <see cref="UpdateCheckState.CheckFailed"/>
    /// with an honest explanation, rather than silently defaulting either way.
    /// </summary>
    internal static (UpdateCheckState State, string Detail) Compare(string? installed, string? latestTag)
    {
        if (string.IsNullOrWhiteSpace(latestTag))
        {
            return (UpdateCheckState.CheckFailed, "The latest release had no version tag.");
        }

        if (string.IsNullOrWhiteSpace(installed))
        {
            return (UpdateCheckState.CheckFailed,
                "Could not determine the installed version (gateway health carried none, and " +
                "'defenseclaw --version' did not return a parseable version).");
        }

        var normalizedInstalled = NormalizeVersion(installed);
        var normalizedLatest = NormalizeVersion(latestTag);

        if (string.Equals(normalizedInstalled, normalizedLatest, StringComparison.OrdinalIgnoreCase))
        {
            return (UpdateCheckState.UpToDate, $"Installed version {installed} matches the latest release.");
        }

        if (Version.TryParse(normalizedInstalled, out var installedVersion) &&
            Version.TryParse(normalizedLatest, out var latestVersion))
        {
            var comparison = installedVersion.CompareTo(latestVersion);
            if (comparison >= 0)
            {
                return (UpdateCheckState.UpToDate,
                    $"Installed version {installed} is the same as or newer than the latest release ({latestTag}).");
            }

            return (UpdateCheckState.UpdateAvailable,
                $"A newer release ({latestTag}) is available; installed is {installed}.");
        }

        return (UpdateCheckState.CheckFailed,
            $"Installed version '{installed}' and latest tag '{latestTag}' could not be compared reliably.");
    }

    private static string NormalizeVersion(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..];
        }

        var cut = trimmed.IndexOfAny(['-', '+']);
        return cut >= 0 ? trimmed[..cut] : trimmed;
    }

    /// <summary>
    /// Best-effort, read-only. Prefers the gateway's own answer (already polled, no extra
    /// process), but only when the port owner verified as the gateway: an unverified peer's
    /// <c>/health</c> version is never used (<see cref="GatewaySnapshot.TrustedBinaryVersion"/>).
    /// Falls back to a CLI call only when the gateway has never reported a trusted version —
    /// and within that fallback, prefers <c>defenseclaw --version-json</c> (0.8.10+) since it is
    /// structured and unambiguous; only when that misses (older CLIs predate the flag) does it
    /// fall back further to the human-readable <c>--version</c> text and regex extraction.
    /// </summary>
    private async Task<string?> ResolveInstalledVersionAsync(CancellationToken cancellationToken)
    {
        var fromHealth = _services.Monitor.Current.TrustedBinaryVersion;
        if (!string.IsNullOrWhiteSpace(fromHealth))
        {
            return fromHealth.Trim();
        }

        var fromVersionJson = await TryResolveVersionViaVersionJsonAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromVersionJson))
        {
            return fromVersionJson;
        }

        return await TryResolveVersionViaVersionFlagAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>defenseclaw --version-json</c> (supported starting with the 0.8.10 CLI) and
    /// parses its output via <see cref="TryParseVersionJson"/>. A non-zero exit code, unparsable
    /// output, or a blank <c>version</c> field are all treated as a miss — not an error — so the
    /// caller can fall back to <see cref="TryResolveVersionViaVersionFlagAsync"/> for older CLIs
    /// that predate the flag.
    /// </summary>
    private async Task<string?> TryResolveVersionViaVersionJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var invocation = await _services.Cli
                .RunAsync(["--version-json"], cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (invocation.ExitCode != 0)
            {
                return null;
            }

            var text = string.Join(
                Environment.NewLine,
                invocation.OutputLines.Select(l => l.Text));

            return TryParseVersionJson(text);
        }
        catch (CliNotFoundException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Best-effort version probe; any failure just means "unknown".
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Parses the output of <c>defenseclaw --version-json</c>, e.g.
    /// <c>{"name":"defenseclaw-cli","schema_version":1,"version":"0.8.10"}</c>. Tolerates extra
    /// fields (schema may grow); returns null for anything that is not a JSON object with a
    /// non-blank string <c>version</c> property, so the caller can fall back cleanly rather than
    /// propagate a parse exception.
    /// </summary>
    internal static string? TryParseVersionJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var version = versionElement.GetString();
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the older, human-readable <c>defenseclaw --version</c> and regex-extracts the
    /// version substring. Kept as the fallback for CLIs older than 0.8.10, which do not
    /// understand <c>--version-json</c> and would otherwise just error or print usage.
    /// </summary>
    private async Task<string?> TryResolveVersionViaVersionFlagAsync(CancellationToken cancellationToken)
    {
        try
        {
            var invocation = await _services.Cli
                .RunAsync(["--version"], cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (invocation.ExitCode != 0)
            {
                return null;
            }

            var text = string.Join(
                Environment.NewLine,
                invocation.OutputLines.Select(l => l.Text));

            var match = VersionPattern.Match(text);
            return match.Success ? match.Value : null;
        }
        catch (CliNotFoundException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // Best-effort version probe; any failure just means "unknown".
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task<(CachedRelease? Release, bool IsRateLimited, string? Error)> FetchLatestReleaseAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return (null, false, $"GitHub was unreachable: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, false, $"The request to GitHub timed out: {ex.Message}");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                return (null, true, DescribeRateLimit(response, body));
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, false, $"GitHub returned HTTP {(int)response.StatusCode}: {SummarizeErrorBody(body)}");
            }

            GitHubRelease? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<GitHubRelease>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                return (null, false, $"Could not parse the GitHub response: {ex.Message}");
            }

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.TagName))
            {
                return (null, false, "GitHub's response had no usable release tag.");
            }

            var assets = (parsed.Assets ?? [])
                .Select(a => new ReleaseAsset
                {
                    Name = a.Name ?? "(unnamed asset)",
                    Size = a.Size,
                    DownloadUrl = a.BrowserDownloadUrl,
                    ContentType = a.ContentType,
                })
                .ToArray();

            var release = new CachedRelease
            {
                TagName = parsed.TagName,
                Name = parsed.Name,
                PublishedAt = parsed.PublishedAt,
                HtmlUrl = parsed.HtmlUrl,
                Assets = assets,
                FetchedAt = DateTimeOffset.UtcNow,
            };

            return (release, false, null);
        }
    }

    private static string DescribeRateLimit(HttpResponseMessage response, string body)
    {
        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
            ? values.FirstOrDefault()
            : null;

        var message = SummarizeErrorBody(body);
        return remaining == "0"
            ? $"GitHub's unauthenticated API rate limit (60/hour/IP) was hit. {message}".Trim()
            : $"GitHub rejected the request (HTTP {(int)response.StatusCode}). {message}".Trim();
    }

    private static string SummarizeErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not a JSON error envelope; fall through to a truncated raw body.
        }

        return body.Length > 200 ? body[..200] + "…" : body;
    }

    private CachedRelease? TryReadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_cacheFilePath);
            var cached = JsonSerializer.Deserialize<CachedRelease>(json, JsonOptions);

            // Not "fresh forever": see CacheClockSkew. Ignored altogether, so it is not even a stale fallback.
            return cached is not null && cached.FetchedAt > DateTimeOffset.UtcNow + CacheClockSkew ? null : cached;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void TryWriteCache(CachedRelease release)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(release, JsonOptions);
            File.WriteAllText(_cacheFilePath, json);
        }
        catch (IOException)
        {
            // Caching is an optimization; failing to write it is not fatal.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>What the 24h cache file holds — the fetched release, nothing about comparison.</summary>
    private sealed record CachedRelease
    {
        public required string TagName { get; init; }

        public string? Name { get; init; }

        public DateTimeOffset? PublishedAt { get; init; }

        public string? HtmlUrl { get; init; }

        public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

        public DateTimeOffset FetchedAt { get; init; }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

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

        [JsonPropertyName("content_type")]
        public string? ContentType { get; init; }
    }
}
