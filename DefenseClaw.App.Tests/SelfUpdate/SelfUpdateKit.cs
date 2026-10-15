using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DefenseClaw.App.Services.Updates.SelfUpdate;

namespace DefenseClaw.App.Tests.SelfUpdate;

/// <summary>Synthetic releases, zips and a fake GitHub for the self-update tests. Nothing here reaches the network or the real HKCU.</summary>
internal static class SelfUpdateKit
{
    public const string Repo = "https://github.com/Rmontalbano/defenseclaw_win";

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name);
                // A fixed timestamp keeps the bytes (and so the SHA-256) the same however often a test builds the zip;
                // with the default "now" two builds a second apart hash differently.
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(data);
            }
        }

        return stream.ToArray();
    }

    public static byte[] GoodZip(string appText = "new-app", string helperText = "new-helper") =>
        Zip((SelfUpdatePackage.AppExeName, Bytes(appText)), (SelfUpdatePackage.HelperExeName, Bytes(helperText)));

    public static string ZipName(string version) => $"DefenseClawWin-{version}-win-x64.zip";

    public static string AssetUrl(string version, string name) => $"{Repo}/releases/download/v{version}/{name}";

    /// <summary>The <c>releases/latest</c> JSON for <paramref name="version"/> with a zip of <paramref name="zip"/> and a checksums.txt.</summary>
    public static string ReleaseJson(
        string version,
        byte[]? zip = null,
        string? digest = "auto",
        bool prerelease = false,
        bool draft = false,
        string? notes = "## Highlights\n- Faster **start**",
        bool includeZip = true,
        bool includeChecksums = true,
        string? zipNameOverride = null,
        string? zipUrlOverride = null)
    {
        zip ??= GoodZip();
        var zipName = zipNameOverride ?? ZipName(version);
        var assets = new List<string>();
        if (includeZip)
        {
            var digestText = digest == "auto" ? "sha256:" + Sha(zip) : digest;
            assets.Add($$"""{"name":"{{zipName}}","size":{{zip.Length}},"browser_download_url":"{{zipUrlOverride ?? AssetUrl(version, zipName)}}","digest":{{(digestText is null ? "null" : "\"" + digestText + "\"")}} }""");
        }

        if (includeChecksums)
        {
            assets.Add($$"""{"name":"checksums.txt","size":0,"browser_download_url":"{{AssetUrl(version, "checksums.txt")}}"}""");
        }

        var body = notes is null ? "null" : System.Text.Json.JsonSerializer.Serialize(notes);
        return $$"""
            {"tag_name":"v{{version}}","name":"DefenseClaw for Windows {{version}}","body":{{body}},"draft":{{(draft ? "true" : "false")}},"prerelease":{{(prerelease ? "true" : "false")}},
             "published_at":"2026-10-20T12:00:00Z","html_url":"{{Repo}}/releases/tag/v{{version}}","assets":[{{string.Join(",", assets)}}]}
            """;
    }

    public static string Checksums(string version, byte[] zip) => $"{Sha(zip)}  {ZipName(version)}\n";

    public static SelfUpdateRelease Release(string version, byte[]? zip = null, string? digest = "auto", string? notes = "- notes")
    {
        var parsed = SelfUpdateRelease.Parse(ReleaseJson(version, zip, digest, notes: notes), out var problem);
        return parsed ?? throw new InvalidOperationException(problem);
    }

    /// <summary>A fake GitHub: serves bodies by exact URL, answers If-None-Match with 304, records every request.</summary>
    public sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Urls { get; } = [];

        public FakeGitHub Serve(string url, byte[] body, string? etag = null)
        {
            _routes[url] = request =>
            {
                if (etag is not null && request.Headers.IfNoneMatch.Any(t => t.ToString() == etag))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                if (etag is not null)
                {
                    response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
                }

                return response;
            };
            return this;
        }

        public FakeGitHub Serve(string url, string body, string? etag = null) => Serve(url, Bytes(body), etag);

        public FakeGitHub ServeStatus(string url, HttpStatusCode status)
        {
            _routes[url] = _ => new HttpResponseMessage(status);
            return this;
        }

        public FakeGitHub Throw(string url, Exception exception)
        {
            _routes[url] = _ => throw exception;
            return this;
        }

        public FakeGitHub ServeRelease(string version, byte[]? zip = null, string? digest = "auto", string? etag = null)
        {
            zip ??= GoodZip();
            _ = Serve(SelfUpdateRelease.LatestEndpoint.ToString(), ReleaseJson(version, zip, digest), etag);
            _ = Serve(AssetUrl(version, "checksums.txt"), Checksums(version, zip));
            return Serve(AssetUrl(version, ZipName(version)), zip);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            return Task.FromResult(_routes.TryGetValue(url, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
