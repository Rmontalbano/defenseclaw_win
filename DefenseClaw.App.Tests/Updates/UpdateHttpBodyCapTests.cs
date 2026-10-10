using System.Net;
using System.Text;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.Core.IO;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>CUST-250: the update clients buffer a response body only up to <see cref="ReadLimits.HttpBodyBytes"/>.</summary>
public sealed class UpdateHttpBodyCapTests
{
    private sealed class Fixed(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    [Fact]
    public void The_github_client_carries_the_shared_body_cap()
    {
        using var http = UpdateChecker.CreateHttpClient();

        Assert.Equal(ReadLimits.HttpBodyBytes, http.MaxResponseContentBufferSize);
    }

    [Fact]
    public async Task A_body_over_the_cap_fails_the_buffered_read_instead_of_filling_memory()
    {
        using var http = new HttpClient(new Fixed(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 4096), Encoding.UTF8, "application/json"),
        }))
        {
            MaxResponseContentBufferSize = 1024,
        };

        _ = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetStringAsync("http://127.0.0.1:1/"));
    }
}
