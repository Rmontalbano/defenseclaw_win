using System.Diagnostics;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// The facts a trusted gateway owner carries since CUST-392, for tests that build <see cref="Core.Net.PortOwner"/> values by hand: a process that
/// started before it opened its socket, and this test process's own Windows session (the one a verifier built without a session reads).
/// </summary>
internal static class PeerFixtures
{
    /// <summary>When the synthetic gateway process started.</summary>
    public static readonly DateTime Started = new(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>When it opened its socket: after it started.</summary>
    public static readonly DateTime Bound = Started.AddMilliseconds(400);

    /// <summary>This test process's Windows session.</summary>
    public static int ThisSession { get; } = ReadSession();

    private static int ReadSession()
    {
        using var self = Process.GetCurrentProcess();
        return self.SessionId;
    }
}
