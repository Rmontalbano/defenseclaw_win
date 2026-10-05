using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// The watcher-driven reload: a malformed config.yaml raises the banner update once for that file, not once per retry or
/// poll, and fixing the file raises it again so the banner can clear.
/// </summary>
public class AppServicesConfigWatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void A_permanently_malformed_config_raises_one_update_and_a_fix_raises_another()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, "config_version: 8\n");
        var raised = 0;
        services.ConfigReloaded += (_, _) => Interlocked.Increment(ref raised);

        _ = temp.WriteFile("config.yaml", "gateway: [unclosed\n  api_port: 18971\n");

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref raised) >= 1 && services.ConfigLoadError is not null, Timeout),
            "the malformed config was never reported");

        // Longer than the whole retry schedule (0.3 + 0.6 + 1.2 + 2.4 + 4 s).
        Thread.Sleep(10_000);
        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.NotNull(services.ConfigLoadError);

        _ = temp.WriteFile("config.yaml", "config_version: 8\ngateway:\n  api_port: 18972\n");

        Assert.True(SpinWait.SpinUntil(() => services.ConfigLoadError is null && Volatile.Read(ref raised) >= 2, Timeout),
            "the fix was never applied");
    }
}
