using System.Net;
using System.Text;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Tests.Agents;

/// <summary>
/// A gateway in memory for the Agents panel (CUST-355): serves the synthetic fixtures under <c>Fixtures/runtime-1.0</c> the way the 1.0.x handlers
/// answer, through the real <see cref="GatewayClient"/> (so the bearer token, the client header, the bounded read and the status mapping are the
/// production ones). Nothing opens a socket.
/// </summary>
internal sealed class AgentsScene : IDisposable
{
    public const string Root = "agent-root-0001-aaaaaaaaaaaaaaaa";
    public const string Child = "agent-child-0001-bbbbbbbbbbbbbbb";
    public const string InferredChild = "agent-child-0002-ccccccccccccccc";
    public const string Session = "session-0001";

    private readonly TempDirectory _temp = new();
    private readonly GatewayClient _client;

    public AgentsScene()
    {
        Handler = new Gateway();
        _client = new GatewayClient(
            new HttpClient(Handler) { BaseAddress = new Uri("http://127.0.0.1:18970/") },
            () => new SecretValue("fixture-bearer-token-0123456789"),
            ownsHttpClient: true);
        Services = AppServices.CreateIsolated(
            TestServices.IsolatedPaths(_temp.Path),
            claudeSettingsPath: _temp.File("claude-settings.json"),
            runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
    }

    public Gateway Handler { get; }

    public AppServices Services { get; }

    public AgentsPanelViewModel Vm { get; private set; } = null!;

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0", name));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Builds the view-model on the UI thread, wired to the in-memory gateway, and runs its first read to the end.</summary>
    public async Task<AgentsPanelViewModel> OpenAsync(Action<AgentsPanelViewModel>? beforeInitialize = null)
    {
        _ = await Services.Runtime.RefreshAsync();
        var (vm, init) = UiThread.Run(() =>
        {
            var model = new AgentsPanelViewModel(Services)
            {
                Fetch = (path, max, ct) => _client.GetBoundedJsonAsync(path, max, true, ct),
            };
            beforeInitialize?.Invoke(model);
            return (model, model.InitializeAsync());
        });
        UiThread.WaitFor(() => init.IsCompleted, "the first directory read");
        Vm = vm;
        return vm;
    }

    /// <summary>Picks the identity with this agent id and waits until the walk has ended.</summary>
    public void Select(string agentId)
    {
        UiThread.Run(() => Vm.SelectedAgent = Vm.Agents.Single(a => a.AgentId == agentId));
        UiThread.WaitFor(() => !Vm.IsOpening && (Vm.HasLedger || Vm.HasOpenMessage), "the agent to open");
        UiThread.WaitFor(() => Vm.HasConflicts || Vm.HasOpenMessage, "the conflicts read");
    }

    public T Ui<T>(Func<T> read) => UiThread.Run(read);

    public void Dispose()
    {
        Services.Dispose();
        _client.Dispose();
        _temp.Dispose();
    }

    public sealed class Gateway : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>Overrides an answer: return null to fall through to the fixtures. May throw <see cref="HttpRequestException"/> to be unreachable.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

        public IEnumerable<string> Paths => Requests.Select(r => r.RequestUri!.PathAndQuery.TrimStart('/'));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Override?.Invoke(request) ?? Answer(request));
        }

        private static HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            switch (path)
            {
                case "api/v1/agents/identities":
                    return Json(Read("agents-identities.synthetic.json"));
                case "api/v1/correlation/conflicts":
                    return Json(Read("correlation-conflicts.synthetic.json"));
                case "api/v1/correlation/graph":
                    var agent = query["agent_id"];
                    if (query["session_id"] == Session || agent == Root)
                    {
                        return Json(Read(query["after_id"] is null ? "correlation-graph.root.page1.synthetic.json" : "correlation-graph.root.page2.synthetic.json"));
                    }

                    return Json(Read(agent == Child ? "correlation-graph.child.synthetic.json" : "correlation-graph.empty.synthetic.json"));
                default:
                    return Json("{\"error\":\"not found\"}", HttpStatusCode.NotFound);
            }
        }
    }
}
