using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>One request a <see cref="RawHttpListener"/> received: what was asked and every header it carried.</summary>
public sealed record RawRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>The <c>Authorization</c> header, or null when the request carried none.</summary>
    public string? Authorization => Headers.TryGetValue("Authorization", out var value) ? value : null;
}

/// <summary>
/// A bare loopback TCP listener that speaks just enough HTTP/1.1 to answer a client and record what
/// it sent. Deliberately not an <c>HttpListener</c> or a fake handler: the point of the tests that use
/// it is that <i>something else</i> owns the port — what a stray server on the gateway's port would see
/// on the wire, headers and all.
/// </summary>
public sealed class RawHttpListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<RawRequest, string> _respond;
    private readonly List<RawRequest> _requests = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    /// <param name="respond">Builds the complete raw HTTP response for a request; see <see cref="Response"/>.</param>
    public RawHttpListener(Func<RawRequest, string> respond)
    {
        _respond = respond;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public int Port { get; }

    /// <summary>Every request received so far, oldest first.</summary>
    public IReadOnlyList<RawRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>A raw HTTP response with a JSON body (and any extra headers, e.g. <c>Location</c>).</summary>
    public static string Response(int status, string body, params (string Name, string Value)[] headers)
    {
        var text = new StringBuilder();
        _ = text.Append(StatusLine(status)).Append("\r\nContent-Type: application/json\r\n");
        foreach (var (name, value) in headers)
        {
            _ = text.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        _ = text.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\nConnection: close\r\n\r\n").Append(body);
        return text.ToString();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private static string StatusLine(int status) =>
        $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : status is >= 300 and < 400 ? "Found" : "Status")}";

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1024];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return;
                    }

                    _ = head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var lines = head.ToString().Split("\r\n", StringSplitOptions.None);
                var requestLine = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).TakeWhile(l => l.Length > 0))
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    if (colon > 0)
                    {
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                    }
                }

                var request = new RawRequest(requestLine[0], requestLine[1], headers);
                lock (_gate)
                {
                    _requests.Add(request);
                }

                var response = Encoding.UTF8.GetBytes(_respond(request));
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client hung up, or the test ended: nothing to report.
            }
        }
    }
}
