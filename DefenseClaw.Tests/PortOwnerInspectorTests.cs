using System.Net;
using System.Net.Sockets;
using DefenseClaw.Core.Net;

namespace DefenseClaw.Tests;

public class PortOwnerInspectorTests
{
    [Fact]
    public void Finds_the_process_owning_a_listening_socket()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var owner = new PortOwnerInspector().FindListener(port);

            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner!.Pid);
            Assert.Equal(port, owner.Port);
            Assert.False(owner.IsWslRelay);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Returns_null_for_a_port_nobody_is_listening_on()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var inspector = new PortOwnerInspector();

        // Derive a free port from the listener table rather than binding and releasing
        // one — a released socket can linger in the table long enough to race.
        var taken = inspector.ListListeners().Select(l => l.Port).ToHashSet();
        Assert.NotEmpty(taken);

        // Stay below Windows' dynamic port range (49152+), or an ephemeral socket opened
        // by another test can claim the port between the two lookups.
        var free = Enumerable.Range(20_000, 1_000).First(port => !taken.Contains(port));

        Assert.Null(inspector.FindListener(free));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Rejects_out_of_range_ports(int port)
    {
        Assert.Null(new PortOwnerInspector().FindListener(port));
    }

    [Fact]
    public void Enumerates_listeners_without_shelling_out()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var listeners = new PortOwnerInspector().ListListeners();

            Assert.NotEmpty(listeners);
            Assert.Contains(listeners, l => l.Pid == Environment.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }
}
