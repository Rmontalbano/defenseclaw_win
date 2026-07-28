using System.Net;
using System.Text;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// Serves canned responses per request path and records every request, so tests can
/// assert on the headers the client sent (chiefly: bearer auth on everything but /health).
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<HttpRequestMessage> _requests = new();

    public IReadOnlyList<HttpRequestMessage> Requests => _requests;

    /// <summary>Thrown for any path with no route, to simulate a dead sidecar.</summary>
    public Exception? DefaultException { get; set; }

    public FakeHttpMessageHandler Map(string path, string body, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _routes[Normalize(path)] = _ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        return this;
    }

    public FakeHttpMessageHandler MapFixture(string path, string fixtureFileName, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        Map(path, FixtureFiles.ReadText(fixtureFileName), statusCode);

    public FakeHttpMessageHandler MapThrow(string path, Exception exception)
    {
        _routes[Normalize(path)] = _ => throw exception;
        return this;
    }

    /// <summary>Returns the recorded request for a path, or null.</summary>
    public HttpRequestMessage? RequestFor(string path) =>
        _requests.FirstOrDefault(r => Normalize(r.RequestUri?.AbsolutePath ?? string.Empty) == Normalize(path));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request);

        var path = Normalize(request.RequestUri?.AbsolutePath ?? string.Empty);
        if (_routes.TryGetValue(path, out var handler))
        {
            return Task.FromResult(handler(request));
        }

        if (DefaultException is not null)
        {
            throw DefaultException;
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"not found\"}", Encoding.UTF8, "application/json"),
        });
    }

    private static string Normalize(string path) => "/" + path.Trim('/');
}
