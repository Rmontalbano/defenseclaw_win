using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DefenseClaw.Core.Net;

/// <summary>The process holding a listening socket.</summary>
/// <param name="Pid">Owning process id.</param>
/// <param name="ProcessName">Image name without extension; null when the process is gone or inaccessible.</param>
/// <param name="LocalAddress">Address the socket is bound to.</param>
/// <param name="Port">Port the socket is listening on.</param>
public sealed record PortOwner(int Pid, string? ProcessName, string LocalAddress, int Port = 0)
{
    /// <summary>
    /// True when the listener is WSL's port relay. Port 18970 answering through
    /// <c>wslrelay.exe</c> means the gateway is running inside WSL, not natively — the
    /// collision the app must call out with a banner.
    /// </summary>
    public bool IsWslRelay =>
        ProcessName is not null &&
        ProcessName.StartsWith("wslrelay", StringComparison.OrdinalIgnoreCase);
}

public interface IPortOwnerInspector
{
    /// <summary>Returns the process listening on <paramref name="port"/>, or null.</summary>
    PortOwner? FindListener(int port);
}

/// <summary>
/// Resolves a TCP listener to its owning process via the <c>GetExtendedTcpTable</c>
/// Win32 API — no shelling out to <c>netstat</c>, so no console window, no parsing of
/// localized output, and no extra process for DefenseClaw's own scanners to flag.
/// Returns null on non-Windows platforms.
/// </summary>
public sealed class PortOwnerInspector : IPortOwnerInspector
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>TCP_TABLE_OWNER_PID_LISTENER.</summary>
    private const int TcpTableOwnerPidListener = 3;

    public PortOwner? FindListener(int port)
    {
        if (port is <= 0 or > 65535 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        // Note: FirstOrDefault over a value-tuple sequence yields default(T) rather than
        // null when nothing matches, so this must use an explicit enumerator check —
        // otherwise an unowned port reports a phantom owner with pid 0 ("Idle").
        foreach (var entry in Enumerate(AfInet).Concat(Enumerate(AfInet6)))
        {
            if (entry.Port == port)
            {
                return new PortOwner(entry.Pid, ResolveProcessName(entry.Pid), entry.Address, entry.Port);
            }
        }

        return null;
    }

    /// <summary>All listening sockets, for a diagnostics view.</summary>
    public IReadOnlyList<PortOwner> ListListeners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<PortOwner>();
        }

        return Enumerate(AfInet)
            .Concat(Enumerate(AfInet6))
            .Select(entry => new PortOwner(entry.Pid, ResolveProcessName(entry.Pid), entry.Address, entry.Port))
            .ToArray();
    }

    private static string? ResolveProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Process exited between enumeration and lookup.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<(int Pid, int Port, string Address)> Enumerate(int addressFamily)
    {
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
        if (status != ErrorInsufficientBuffer && status != NoError)
        {
            yield break;
        }

        if (size <= 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(buffer, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);
            if (status != NoError)
            {
                yield break;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = addressFamily == AfInet
                ? Marshal.SizeOf<MibTcpRowOwnerPid>()
                : Marshal.SizeOf<MibTcp6RowOwnerPid>();

            var cursor = buffer + sizeof(int);
            for (var i = 0; i < count; i++, cursor += rowSize)
            {
                if (addressFamily == AfInet)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
                    yield return ((int)row.OwningPid, DecodePort(row.LocalPort), DecodeIpv4(row.LocalAddr));
                }
                else
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(cursor);
                    yield return ((int)row.OwningPid, DecodePort(row.LocalPort), DecodeIpv6(row.LocalAddr));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The port sits in the low two bytes of the dword, in network byte order.</summary>
    private static int DecodePort(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

    private static string DecodeIpv4(uint value) =>
        $"{value & 0xFF}.{(value >> 8) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 24) & 0xFF}";

    private static string DecodeIpv6(byte[] value)
    {
        try
        {
            return new System.Net.IPAddress(value).ToString();
        }
        catch (ArgumentException)
        {
            return "::";
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr;

        public uint LocalScopeId;
        public uint LocalPort;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr;

        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }
}
