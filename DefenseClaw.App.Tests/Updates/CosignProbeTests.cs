using System.Diagnostics;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Updates;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The Updates window's cosign badge walks PATH. It is asked for when the section is built and again by the badge's
/// button, both on the UI thread, and one dead network entry on PATH makes a single probe take ~40 s.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class CosignProbeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public CosignProbeTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static readonly CosignStatus Ready = new()
    {
        Availability = CosignAvailability.OnPath,
        Path = @"C:\tools\cosign.exe",
        Detail = "cosign is on PATH.",
    };

    private static readonly CosignStatus Missing = new()
    {
        Availability = CosignAvailability.Missing,
        Detail = "cosign was not found.",
        InstallGuidance = "Install it.",
    };

    private UpgradeSectionViewModel Section(Func<Task<CosignStatus>> probe) =>
        new(_services, new UpgradeRunner(_services.Cli, new HttpClient(new StubHttpHandler()), _temp.File("staging")), probe);

    [Fact]
    public async Task DetectCosignAsync_runs_the_probe_off_the_calling_thread()
    {
        using var dead = new DeadPathProbe();

        var stopwatch = Stopwatch.StartNew();
        var task = UpgradeRunner.DetectCosignAsync(
            processPath: new[] { DeadPathProbe.DeadEntry },
            offPathDirectories: Array.Empty<string>(),
            fileExists: dead.Exists);
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"starting the probe took {stopwatch.ElapsedMilliseconds} ms");
        dead.WaitUntilBlocked();
        Assert.False(task.IsCompleted);

        dead.Release();
        Assert.Equal(CosignAvailability.Missing, (await task).Availability);
    }

    [Fact]
    public async Task A_quoted_PATH_entry_is_searched_for_cosign()
    {
        var status = await UpgradeRunner.DetectCosignAsync(
            processPath: new[] { "\"C:\\Program Files\\Sigstore\"" },
            offPathDirectories: Array.Empty<string>(),
            fileExists: path => path == Path.Combine(@"C:\Program Files\Sigstore", "cosign.exe"));

        Assert.Equal(CosignAvailability.OnPath, status.Availability);
    }

    [Fact]
    public void Building_the_section_does_not_wait_for_the_cosign_probe_and_the_badge_says_so()
    {
        var pending = new TaskCompletionSource<CosignStatus>();

        var section = UiThread.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var built = Section(() => pending.Task);
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 500, $"building the section took {stopwatch.ElapsedMilliseconds} ms");
            Assert.Equal("Checking…", built.CosignLabel);
            Assert.False(built.IsCosignReady);
            return built;
        });

        try
        {
            pending.SetResult(Ready);
            UiThread.WaitFor(() => section.CosignLabel == "cosign ready", "the cosign badge to resolve");

            UiThread.Run(() =>
            {
                Assert.True(section.IsCosignReady);
                Assert.Equal("Ok", section.CosignBadgeKey);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }

    [Fact]
    public async Task An_older_probe_answering_late_never_overwrites_a_newer_one()
    {
        var first = new TaskCompletionSource<CosignStatus>();
        var second = new TaskCompletionSource<CosignStatus>();
        var calls = 0;

        var section = UiThread.Run(() => Section(() => Interlocked.Increment(ref calls) == 1 ? first.Task : second.Task));

        try
        {
            // The button is pressed while the first probe is still out.
            Task refresh = UiThread.Run(() => section.RefreshCosignAsync());
            Assert.Equal("Checking…", UiThread.Run(() => section.CosignLabel));

            second.SetResult(Missing);
            UiThread.WaitFor(() => section.CosignLabel == "cosign missing", "the newer probe's answer");

            first.SetResult(Ready);
            await refresh;

            // Let the older continuation run, then check it was ignored.
            UiThread.Run(() => UiThread.Settle());
            UiThread.Run(() =>
            {
                Assert.Equal("cosign missing", section.CosignLabel);
                Assert.False(section.IsCosignReady);
            });
        }
        finally
        {
            UiThread.Run(section.Dispose);
        }
    }
}
