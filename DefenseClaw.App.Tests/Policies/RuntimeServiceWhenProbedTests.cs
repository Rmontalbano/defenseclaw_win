using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Runtime;
using static DefenseClaw.App.Tests.TestSupport.PolicyModelTestSupport;

namespace DefenseClaw.App.Tests.Policies;

/// <summary>
/// <see cref="RuntimeService.WhenProbedAsync"/>: what a panel that has to choose its surface from the runtime waits for (CUST-293). In the app the
/// service is started at launch and the panel waits for its first probe; a service nothing started has no probe coming, and must answer at once so
/// that a panel never waits for it.
/// </summary>
public sealed class RuntimeServiceWhenProbedTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<RuntimeService> _services = new();
    private int _probes;

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        _temp.Dispose();
    }

    private RuntimeService Create()
    {
        var answers = ProbeRunner(Pinned);
        var service = new RuntimeService(
            TestServices.IsolatedPaths(_temp.Path),
            (arguments, token) =>
            {
                _ = Interlocked.Increment(ref _probes);
                return answers(arguments, token);
            });
        _services.Add(service);
        return service;
    }

    [Fact]
    public async Task A_service_nothing_started_answers_at_once_with_what_it_has_and_never_probes()
    {
        var service = Create();

        var answer = service.WhenProbedAsync();

        Assert.True(answer.IsCompleted);
        Assert.Same(RuntimeSnapshot.NotProbed, await answer);
        Assert.False(service.IsStarted);
        Assert.Equal(0, Volatile.Read(ref _probes));
    }

    [Fact]
    public async Task A_started_service_that_has_not_answered_yet_waits_for_its_first_probe_and_returns_it()
    {
        var service = Create();
        service.Start();
        Assert.True(service.IsStarted);

        var snapshot = await service.WhenProbedAsync();

        Assert.True(snapshot.IsKnown);
        Assert.True(snapshot.Capabilities.Has(RuntimeCapability.PolicyModel));
        Assert.Equal("1.0.0", snapshot.Identity!.Version);
    }

    [Fact]
    public async Task Once_the_runtime_has_been_probed_it_answers_at_once_and_does_not_probe_again()
    {
        var service = Create();
        _ = await service.RefreshAsync();
        var probes = Volatile.Read(ref _probes);

        var answer = service.WhenProbedAsync();

        Assert.True(answer.IsCompleted);
        Assert.True((await answer).IsKnown);
        Assert.Equal(probes, Volatile.Read(ref _probes));
    }
}
