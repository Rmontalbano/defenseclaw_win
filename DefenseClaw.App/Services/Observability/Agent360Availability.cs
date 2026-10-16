using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Observability;

/// <summary>One cheap look at the local Grafana: does it answer at <paramref name="baseUrl"/>? A fake in a test, <see cref="HttpGrafanaProbe"/> in the app.</summary>
internal interface IGrafanaProbe
{
    /// <summary>True when Grafana answered. Never throws for an unreachable address; that is the answer false.</summary>
    Task<bool> ProbeAsync(string baseUrl, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IGrafanaProbe"/> that always gives one answer: the stand-in an isolated composition uses (it may not open a socket).</summary>
internal sealed class FixedGrafanaProbe : IGrafanaProbe
{
    private readonly bool _answer;

    public FixedGrafanaProbe(bool answer)
    {
        _answer = answer;
    }

    public Task<bool> ProbeAsync(string baseUrl, CancellationToken cancellationToken) => Task.FromResult(_answer);
}

/// <summary>
/// The probe: <c>GET &lt;base&gt;/api/health</c> (Grafana's unauthenticated health endpoint) with a short timeout, on a loopback address only,
/// redirects not followed (so an answer cannot send the request anywhere else). Any 2xx is "the stack answers"; nothing is read from the body or sent.
/// </summary>
internal sealed class HttpGrafanaProbe : IGrafanaProbe
{
    /// <summary>How long Grafana gets to answer before it counts as not there.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly HttpMessageHandler _handler;
    private readonly TimeSpan _timeout;

    /// <param name="handler">A fake in a test; the default creates a handler that does not follow redirects and ignores any proxy.</param>
    /// <param name="timeout">The answer time; <see cref="DefaultTimeout"/> when null.</param>
    public HttpGrafanaProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _handler = handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<bool> ProbeAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (Agent360Link.TryNormalizeBase(baseUrl) is not { } root)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var client = new HttpClient(_handler, disposeHandler: false);
            using var response = await client.GetAsync(new Uri(root + "/api/health"), HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return (int)response.StatusCode is >= 200 and < 300;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Where the local Grafana is: the <c>grafana_url</c> that <c>setup local-observability url --json</c> prints (a read-only leaf, in both runtimes), and
/// <see cref="Agent360Link.DefaultBaseUrl"/> when that cannot be read or is not a loopback address.
/// </summary>
internal static class GrafanaEndpoint
{
    /// <summary>The command's arguments. A test holds them to the read-only tier.</summary>
    public static IReadOnlyList<string> Argv { get; } = Array.AsReadOnly(new[] { "setup", "local-observability", "url", "--json" });

    /// <summary>The base address in the command's standard output, or null when there is none worth using (not JSON, no <c>grafana_url</c>, not loopback).</summary>
    public static string? ParseBase(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        // The command may print a Docker warning before the document; the document is the line that opens an object.
        foreach (var line in stdout.Split('\n').AsEnumerable().Reverse())
        {
            var text = line.Trim();
            if (!text.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("grafana_url", out var url) &&
                    url.ValueKind == JsonValueKind.String)
                {
                    return Agent360Link.TryNormalizeBase(url.GetString());
                }
            }
            catch (JsonException)
            {
                // Not the document; the lines before it are tried in turn.
            }
        }

        return null;
    }

    /// <summary>A resolver that runs the command through <paramref name="cli"/> (so it is in Activity like every command) and returns its standard output, or null on failure.</summary>
    public static Func<CancellationToken, Task<string?>> ForCli(CliRunner cli)
    {
        ArgumentNullException.ThrowIfNull(cli);
        if (CommandTiers.Classify(Argv) != CommandTier.ReadOnly)
        {
            throw new InvalidOperationException("The local observability url command is not a read.");
        }

        return async cancellationToken =>
        {
            var run = await cli.RunAsync(Argv, cancellationToken: cancellationToken, options: CliRunOptions.JsonRead with { Timeout = TimeSpan.FromSeconds(30) }).ConfigureAwait(false);
            return run.ExitCode == 0
                ? string.Join('\n', run.OutputLines.Where(static l => l.Stream == CliStream.StandardOutput).Select(static l => l.Text))
                : null;
        };
    }
}

/// <summary>
/// Whether Agent360 may be offered right now: one read-only look at the local Grafana, shared by the Overview card, its connector rows and the command
/// palette so they cannot disagree. Same shape as <see cref="Wizards.LocalStackAvailability"/>.
/// <para>
/// <b>Not a poll.</b> Nothing runs on a timer. A surface asks when it needs the answer (<see cref="EnsureFreshAsync"/>), which looks only when there is
/// no answer or the one held is old (<see cref="FreshWhenReachable"/>; the shorter <see cref="FreshWhenNot"/> for a "no", which someone who has just
/// started the stack is about to contradict). The address is resolved once (the CLI's <c>url --json</c>, then the default) and kept; the look is a
/// single GET of Grafana's health endpoint (see <see cref="HttpGrafanaProbe"/>).
/// </para>
/// </summary>
internal sealed class Agent360Availability : IDisposable
{
    public static readonly TimeSpan FreshWhenReachable = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan FreshWhenNot = TimeSpan.FromSeconds(15);

    /// <summary>What the feature is closed with before the first answer has landed.</summary>
    public const string CheckingReason = "Checking for the local Grafana…";

    /// <summary>The words that send the operator to the wizard that starts the stack.</summary>
    public const string HowToStart =
        "Agent360 is a dashboard in the local observability stack, and nothing answered at {0}. " +
        "Start the stack with the Local OTel wizard (Setup, Local observability stack), then check again.";

    private readonly Func<CancellationToken, Task<string?>>? _resolve;
    private readonly IGrafanaProbe _probe;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private string _baseUrl = Agent360Link.DefaultBaseUrl;
    private bool _resolved;
    private bool? _reachable;
    private long _answeredAt;
    private Task? _inFlight;
    private bool _disposed;

    /// <param name="resolve">Reads <c>url --json</c>; null keeps the default address (an isolated composition).</param>
    /// <param name="probe">The one look at Grafana.</param>
    /// <param name="time">Clock for the freshness windows.</param>
    public Agent360Availability(Func<CancellationToken, Task<string?>>? resolve, IGrafanaProbe probe, TimeProvider? time = null)
    {
        _resolve = resolve;
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The Grafana address in use: the default until the resolver has answered.</summary>
    public string BaseUrl
    {
        get
        {
            lock (_gate)
            {
                return _baseUrl;
            }
        }
    }

    /// <summary>True/false once a look has landed; null before.</summary>
    public bool? Reachable
    {
        get
        {
            lock (_gate)
            {
                return _reachable;
            }
        }
    }

    /// <summary>Open when Grafana answered, otherwise closed with a sentence that points to the Local OTel wizard (or says it is still looking).</summary>
    public GateDecision Decision
    {
        get
        {
            lock (_gate)
            {
                return _reachable switch
                {
                    null => new GateDecision(false, CheckingReason),
                    true => GateDecision.Open,
                    false => new GateDecision(false, string.Format(System.Globalization.CultureInfo.InvariantCulture, HowToStart, _baseUrl)),
                };
            }
        }
    }

    /// <summary>Raised after a look changed the answer (the first one included), on the thread that finished it - not the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The address of the dashboard for a connector and agent, or null while Grafana has not answered (nothing is offered then).</summary>
    public string? UrlFor(string? connector, string? agent, Agent360Range? range = null)
    {
        lock (_gate)
        {
            return _reachable == true ? Agent360Link.Build(_baseUrl, connector, agent, range) : null;
        }
    }

    /// <summary>Looks now, or joins the look already running. Never faults.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            load = _inFlight ??= Task.Run(LookAsync);
        }

        return cancellationToken.CanBeCanceled ? load.WaitAsync(cancellationToken) : load;
    }

    /// <summary><see cref="RefreshAsync"/> unless the answer held is still fresh and nothing is running.</summary>
    public Task EnsureFreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_disposed && _inFlight is null && _reachable is { } held &&
                _time.GetElapsedTime(_answeredAt) < (held ? FreshWhenReachable : FreshWhenNot))
            {
                return Task.CompletedTask;
            }
        }

        return RefreshAsync(cancellationToken);
    }

    private async Task LookAsync()
    {
        bool? answer = null;
        try
        {
            await ResolveOnceAsync().ConfigureAwait(false);
            answer = await _probe.ProbeAsync(BaseUrl, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only Dispose cancels this token; there is nobody left to tell.
        }
#pragma warning disable CA1031 // A look that cannot run is "not reachable"; it must not fault the surface that asked.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"The Agent360 availability look failed: {ex.Message}");
            answer = false;
        }

        bool changed;
        lock (_gate)
        {
            changed = answer is not null && _reachable != answer;
            if (answer is not null)
            {
                _reachable = answer;
                _answeredAt = _time.GetTimestamp();
            }

            _inFlight = null;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private async Task ResolveOnceAsync()
    {
        lock (_gate)
        {
            if (_resolved || _resolve is null)
            {
                return;
            }
        }

        string? text = null;
        try
        {
            text = await _resolve(_lifetime.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // No CLI, a timeout: the default address is used, and the probe says whether anything is there.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Trace.TraceWarning($"The local observability url could not be read: {ex.Message}");
        }

        lock (_gate)
        {
            _resolved = true;
            if (GrafanaEndpoint.ParseBase(text) is { } address)
            {
                _baseUrl = address;
            }
        }
    }

    private void RaiseChanged()
    {
        foreach (var handler in (Changed?.GetInvocationList() ?? Array.Empty<Delegate>()).Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
#pragma warning disable CA1031 // One subscriber's bug must not stop the others hearing the answer, or fault the look.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceWarning($"A subscriber to the Agent360 availability change failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();
    }
}
