using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using DefenseClaw.Core.IO;

namespace DefenseClaw.App.Services.Updates.SelfUpdate;

internal enum AppUpdateState
{
    /// <summary>Before the first check completes.</summary>
    Unknown = 0,

    UpToDate,

    /// <summary>A newer release exists and has the files to install it from.</summary>
    UpdateAvailable,

    /// <summary>This is a <c>-dev</c> build: never offered an update, but a manual check says what the latest release is.</summary>
    DevBuild,

    /// <summary>GitHub answered, and this app has no published release yet. A real answer, not a failure: nothing to install, nothing wrong.</summary>
    NoRelease,

    /// <summary>No trustworthy answer (unreachable, rate limited, unreadable, a newer release that cannot be installed). Never the same as up to date.</summary>
    Failed,
}

/// <summary>The outcome of one check for this app's update.</summary>
internal sealed record AppUpdateCheck
{
    public static readonly AppUpdateCheck NotChecked = new() { State = AppUpdateState.Unknown, Message = "Not checked yet." };

    public AppUpdateState State { get; init; }

    /// <summary>The running version, as shown (<c>1.0.0</c>); null when it could not be read.</summary>
    public string? Running { get; init; }

    public SelfUpdateRelease? Release { get; init; }

    /// <summary>One honest sentence for the status line.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The answer came from the stored copy (throttled or 304) rather than a fresh body.</summary>
    public bool FromCache { get; init; }

    /// <summary>The live request failed; <see cref="Release"/> (if any) is the last one seen.</summary>
    public bool LiveFailed { get; init; }

    public bool IsUpdateAvailable => State == AppUpdateState.UpdateAvailable;
}

/// <summary>
/// Asks GitHub for the newest release of this app and compares it with the running version. One GET of <c>releases/latest</c>, an ETag on
/// every repeat (a 304 is free against the rate limit), and a persisted six-hour throttle for the automatic checks: a launch within six
/// hours of the last answer reads the stored release instead of asking. A manual check always asks.
/// </summary>
internal sealed class AppUpdateChecker : IDisposable
{
    /// <summary>The automatic checks' spacing (the Mac's "every six hours").</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SelfUpdateStore _store;
    private readonly AppVersion? _running;
    private readonly TimeProvider _time;

    /// <param name="http">The client; null makes a real one. A test passes one over a fake handler: nothing here may reach the real API in a test.</param>
    /// <param name="running">The running version; null means it could not be determined.</param>
    public AppUpdateChecker(SelfUpdateStore store, AppVersion? running, HttpClient? http = null, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _running = running;
        _http = http ?? CreateHttpClient();
        _ownsHttp = http is null;
        _time = time ?? TimeProvider.System;
    }

    public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(15) }.WithBodyCap();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(SelfUpdateRelease.UserAgentValue);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    public async Task<AppUpdateCheck> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        var state = _store.Current;
        var now = _time.GetUtcNow();

        if (!manual && state.ReleaseJson is not null && state.LastCheckUnix > 0 &&
            now - DateTimeOffset.FromUnixTimeSeconds(Math.Min(state.LastCheckUnix, DateTimeOffset.MaxValue.ToUnixTimeSeconds())) is { } age &&
            age >= TimeSpan.Zero && age < Throttle)
        {
            return Evaluate(state.ReleaseJson, fromCache: true, liveFailed: false, manual, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, SelfUpdateRelease.LatestEndpoint);
        if (state.ETag is { Length: > 0 } etag && state.ReleaseJson is not null && EntityTagHeaderValue.TryParse(etag, out var parsed))
        {
            request.Headers.IfNoneMatch.Add(parsed);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Failed($"GitHub was unreachable: {ex.Message}", state, manual);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"The request to GitHub timed out: {ex.Message}", state, manual);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified && state.ReleaseJson is not null)
            {
                _ = _store.Update(s => s with { LastCheckUnix = now.ToUnixTimeSeconds() });
                return Evaluate(state.ReleaseJson, fromCache: true, liveFailed: false, manual, null);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // An authoritative answer (the repository has no release), so it counts as a check: stamp it, so the watcher waits its interval
                // instead of asking every minute, and drop any stored release that has since been withdrawn.
                _ = _store.Update(s => s with { LastCheckUnix = now.ToUnixTimeSeconds(), ETag = null, ReleaseJson = null });
                return new AppUpdateCheck { State = AppUpdateState.NoRelease, Running = _running?.ToString(), Message = "No release of DefenseClaw for Windows has been published yet." };
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                return Failed("GitHub's unauthenticated API rate limit was hit. Try again later.", state, manual);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"GitHub returned HTTP {(int)response.StatusCode}.", state, manual);
            }

            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return Failed($"GitHub's answer could not be read: {ex.Message}", state, manual);
            }

            var parsedRelease = SelfUpdateRelease.Parse(body, out var problem);
            if (parsedRelease is null)
            {
                return new AppUpdateCheck { State = AppUpdateState.Failed, Running = _running?.ToString(), Message = problem ?? "The release answer was not usable." };
            }

            var tag = response.Headers.ETag?.ToString();
            _ = _store.Update(s => s with { LastCheckUnix = now.ToUnixTimeSeconds(), ETag = tag, ReleaseJson = body });
            return Evaluate(body, fromCache: false, liveFailed: false, manual, null);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private AppUpdateCheck Failed(string reason, SelfUpdateState state, bool manual)
    {
        // A stored release still tells what was last seen, but the status says the live look failed.
        if (state.ReleaseJson is not null)
        {
            var stale = Evaluate(state.ReleaseJson, fromCache: true, liveFailed: true, manual, reason);
            return stale with { Message = reason + " Showing the last answer. " + stale.Message };
        }

        return new AppUpdateCheck { State = AppUpdateState.Failed, Running = _running?.ToString(), Message = reason, LiveFailed = true };
    }

    private AppUpdateCheck Evaluate(string json, bool fromCache, bool liveFailed, bool manual, string? note)
    {
        var release = SelfUpdateRelease.Parse(json, out var problem);
        if (release is null)
        {
            return new AppUpdateCheck { State = AppUpdateState.Failed, Running = _running?.ToString(), Message = problem ?? "The stored release was not usable.", FromCache = fromCache, LiveFailed = liveFailed };
        }

        if (_running is not { } running)
        {
            return new AppUpdateCheck { State = AppUpdateState.Failed, Release = release, Message = "This app could not read its own version, so it cannot tell whether a release is newer.", FromCache = fromCache, LiveFailed = liveFailed };
        }

        var shown = running.ToString();
        if (running.IsDev)
        {
            return new AppUpdateCheck
            {
                State = AppUpdateState.DevBuild,
                Running = shown,
                Release = release,
                Message = $"Development build ({shown}); latest release is {release.Version}. Development builds are not offered updates.",
                FromCache = fromCache,
                LiveFailed = liveFailed,
            };
        }

        if (release.Version <= running)
        {
            return new AppUpdateCheck { State = AppUpdateState.UpToDate, Running = shown, Release = release, Message = $"Up to date ({shown}).", FromCache = fromCache, LiveFailed = liveFailed };
        }

        if (release.NotInstallableReason is { } blocked)
        {
            return new AppUpdateCheck { State = AppUpdateState.Failed, Running = shown, Release = release, Message = $"Release {release.Version} is out, but it cannot be installed from here. {blocked}", FromCache = fromCache, LiveFailed = liveFailed };
        }

        return new AppUpdateCheck { State = AppUpdateState.UpdateAvailable, Running = shown, Release = release, Message = $"DefenseClaw for Windows {release.Version} is available.", FromCache = fromCache, LiveFailed = liveFailed };
    }
}
