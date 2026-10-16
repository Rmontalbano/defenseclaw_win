using System.Net;
using System.Text;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.Tests.Correlation;

/// <summary>The synthetic fixtures under <c>Fixtures/runtime-1.0</c> (Agents panel, CUST-355) and an in-memory gateway that serves them.</summary>
internal static class CorrelationFixtures
{
    public const string RootAgent = "agent-root-0001-aaaaaaaaaaaaaaaa";
    public const string ChildAgent = "agent-child-0001-bbbbbbbbbbbbbbb";
    public const string InferredChild = "agent-child-0002-ccccccccccccccc";
    public const string ConflictedChild = "agent-child-0003-eeeeeeeeeeeeeee";
    public const string UnknownChild = "agent-child-0004-fffffffffffffff";
    public const string Grandchild = "agent-grand-0001-ddddddddddddddd";
    public const string Session = "session-0001";
    public const string Token = "fixture-bearer-token-0123456789";

    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-1.0", name));

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>
    /// A gateway that answers the correlation and directory routes from the fixtures, the way the 1.0.x handlers do: the first page of the root
    /// agent (and of its session) is truncated with a cursor, the second page is the last, the child has its own graph, and every other agent has an
    /// empty ledger. Every request is recorded; none leaves the process.
    /// </summary>
    public sealed class FakeGateway : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>Overrides an answer: return null to fall through to the fixtures.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Override?.Invoke(request) ?? Answer(request));
        }

        private static HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            if (path == "api/v1/agents/identities")
            {
                return Json(Read("agents-identities.synthetic.json"));
            }

            if (path == "api/v1/correlation/conflicts")
            {
                return Json(Read("correlation-conflicts.synthetic.json"));
            }

            if (path != "api/v1/correlation/graph")
            {
                return Json("{\"error\":\"not found\"}", HttpStatusCode.NotFound);
            }

            var agent = query["agent_id"];
            var session = query["session_id"];
            if (session == Session || agent == RootAgent)
            {
                return Json(Read(query["after_id"] is null ? "correlation-graph.root.page1.synthetic.json" : "correlation-graph.root.page2.synthetic.json"));
            }

            return Json(Read(agent == ChildAgent ? "correlation-graph.child.synthetic.json" : "correlation-graph.empty.synthetic.json"));
        }
    }

    public static (GatewayClient Client, FakeGateway Handler) Gateway(TimeSpan? timeout = null)
    {
        var handler = new FakeGateway();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:18970/"), Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return (new GatewayClient(http, () => new SecretValue(Token), ownsHttpClient: true), handler);
    }
}
