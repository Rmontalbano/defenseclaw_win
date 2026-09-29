using System.Net;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Serves canned release assets by file name (the last path segment) and records what was asked for, in
/// order. Anything unrouted is a 404, like a release that does not carry the asset. No request leaves the process.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requested { get; } = new();

    public StubHttpHandler Serve(string assetName, byte[] body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[assetName] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        return this;
    }

    public StubHttpHandler Serve(string assetName, string body) => Serve(assetName, System.Text.Encoding.UTF8.GetBytes(body));

    public StubHttpHandler ServeStatus(string assetName, HttpStatusCode status)
    {
        _routes[assetName] = () => new HttpResponseMessage(status);
        return this;
    }

    public StubHttpHandler Throw(string assetName, Exception exception)
    {
        _routes[assetName] = () => throw exception;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var name = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
        Requested.Add(name);

        return Task.FromResult(
            _routes.TryGetValue(name, out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
