using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DefenseClaw.Core.Net;

/// <summary>The process holding a listening socket.</summary>
/// <param name="Pid">Owning process id.</param>
/// <param name="ProcessName">Image name without extension; null when the process is gone or inaccessible.</param>
/// <param name="LocalAddress">Address the socket is bound to.</param>
/// <param name="Port">Port the socket is listening on.</param>
/// <param name="ImagePath">
/// Full path of the owning executable; null when it could not be read (process gone, or not
/// openable). Together with <paramref name="ProcessName"/> this is what says whether the
/// listener really is the DefenseClaw gateway — a name alone is anyone's to pick.
/// </param>
public sealed record PortOwner(int Pid, string? ProcessName, string LocalAddress, int Port = 0, string? ImagePath = null)
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

    /// <summary>
    /// Every listener row on <paramref name="port"/>. A wildcard-bound port can be shared by several sockets (SO_REUSEADDR), and the
    /// first row is not necessarily the one that answers, so a trust decision looks at all of them. The default is the single
    /// <see cref="FindListener"/> answer, for inspectors that only know one.
    /// </summary>
    IReadOnlyList<PortOwner> FindListeners(int port) =>
        FindListener(port) is { } owner ? new[] { owner } : Array.Empty<PortOwner>();
}

/// <summary>
/// Finds the process on the far side of one established loopback connection, from the TCP table's row for that very connection
/// (<c>TCP_TABLE_OWNER_PID_CONNECTIONS</c>) - not from whoever happens to be listening on the port.
/// </summary>
public interface IConnectionOwnerInspector
{
    /// <summary>
    /// The owner of the socket whose local end is <paramref name="server"/> and whose remote end is <paramref name="client"/>
    /// (the connecting side's own endpoints, swapped). Null when there is no such row, or more than one row with different owners:
    /// an answer that cannot be tied to exactly one process is no answer.
    /// </summary>
    PortOwner? FindConnectionOwner(IPEndPoint server, IPEndPoint client);
}

/// <summary>
/// Resolves a TCP listener to its owning process via the <c>GetExtendedTcpTable</c>
/// Win32 API — no shelling out to <c>netstat</c>, so no console window, no parsing of
/// localized output, and no extra process for DefenseClaw's own scanners to flag.
/// Returns null on non-Windows platforms.
/// <para>
/// <b>Size, then fetch, then retry.</b> The listener table is asked for in two calls: one to
/// learn how big it is, one to read it. A socket that opens in between makes the second call
/// answer <c>ERROR_INSUFFICIENT_BUFFER</c> again, and treating that as "no table" reported no
/// owner for one poll — which flickers the state and the WSL banner. The pair is retried a few
/// times instead; every retry sizes the table afresh.
/// </para>
/// </summary>
public sealed class PortOwnerInspector : IPortOwnerInspector, IConnectionOwnerInspector
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>TCP_TABLE_OWNER_PID_LISTENER.</summary>
    private const int TcpTableOwnerPidListener = 3;

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION: enough for the image path, granted even for elevated processes.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>How many size-then-fetch rounds are tried before the table is given up on.</summary>
    public const int MaxTableReadAttempts = 5;

    /// <summary>TCP_TABLE_OWNER_PID_CONNECTIONS: every non-listening socket with its owner, both ends of a loopback connection included.</summary>
    private const int TcpTableOwnerPidConnections = 4;

    private readonly TcpTableReader _readTable;
    private readonly TcpTableReader _readConnectionTable;

    public PortOwnerInspector()
        : this(NativeReadTable, NativeReadConnectionTable)
    {
    }

    /// <param name="readTable">
    /// The <c>GetExtendedTcpTable</c> call, so a test can make the table grow between the size
    /// query and the fetch.
    /// </param>
    public PortOwnerInspector(TcpTableReader readTable)
        : this(readTable, NativeReadConnectionTable)
    {
    }

    /// <param name="readConnectionTable">The same call for the connections table (a test hands it a synthetic buffer).</param>
    public PortOwnerInspector(TcpTableReader readTable, TcpTableReader readConnectionTable)
    {
        _readTable = readTable ?? throw new ArgumentNullException(nameof(readTable));
        _readConnectionTable = readConnectionTable ?? throw new ArgumentNullException(nameof(readConnectionTable));
    }

    /// <summary>
    /// One <c>GetExtendedTcpTable</c> call for <paramref name="addressFamily"/>: with a null
    /// <paramref name="table"/> it reports the size needed, otherwise it fills the buffer.
    /// Returns the Win32 status (0 = <c>NO_ERROR</c>, 122 = <c>ERROR_INSUFFICIENT_BUFFER</c>).
    /// </summary>
    public delegate uint TcpTableReader(IntPtr table, ref int size, int addressFamily);

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
                return Describe(entry);
            }
        }

        return null;
    }

    public IReadOnlyList<PortOwner> FindListeners(int port)
    {
        if (port is <= 0 or > 65535 || !OperatingSystem.IsWindows())
        {
            return Array.Empty<PortOwner>();
        }

        return Enumerate(AfInet)
            .Concat(Enumerate(AfInet6))
            .Where(entry => entry.Port == port)
            .Select(Describe)
            .ToArray();
    }

    public PortOwner? FindConnectionOwner(IPEndPoint server, IPEndPoint client)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(client);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var serverAddress = Unmap(server.Address);
        var clientAddress = Unmap(client.Address);
        var pids = new HashSet<int>();
        foreach (var row in EnumerateConnections(AfInet).Concat(EnumerateConnections(AfInet6)))
        {
            if (row.LocalPort == server.Port && row.RemotePort == client.Port &&
                Unmap(row.LocalAddress).Equals(serverAddress) && Unmap(row.RemoteAddress).Equals(clientAddress))
            {
                pids.Add(row.Pid);
            }
        }

        // Exactly one owner, or none: a row set that names two processes cannot say who is on the other end.
        return pids.Count == 1
            ? Describe((pids.First(), server.Port, server.Address.ToString()))
            : null;
    }

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>All listening sockets, for a diagnostics view.</summary>
    public IReadOnlyList<PortOwner> ListListeners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<PortOwner>();
        }

        return Enumerate(AfInet)
            .Concat(Enumerate(AfInet6))
            .Select(Describe)
            .ToArray();
    }

    private static PortOwner Describe((int Pid, int Port, string Address) entry) =>
        new(entry.Pid, ResolveProcessName(entry.Pid), entry.Address, entry.Port, ResolveImagePath(entry.Pid));

    /// <summary>
    /// Full path of the process's executable, or null when it cannot be read. Uses the limited
    /// query right rather than <c>Process.MainModule</c>, which needs more access, walks the
    /// module list and fails outright across bitness.
    /// </summary>
    private static string? ResolveImagePath(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var capacity = 1024;
            var buffer = new char[capacity];
            return QueryFullProcessImageName(handle, 0, buffer, ref capacity)
                ? new string(buffer, 0, capacity)
                : null;
        }
        finally
        {
            _ = CloseHandle(handle);
        }
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

    private IEnumerable<(int Pid, int Port, string Address)> Enumerate(int addressFamily)
    {
        var buffer = ReadTable(addressFamily);
        if (buffer == IntPtr.Zero)
        {
            yield break;
        }

        try
        {
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

    private IEnumerable<(int Pid, IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort)> EnumerateConnections(int addressFamily)
    {
        var buffer = ReadTable(_readConnectionTable, addressFamily);
        if (buffer == IntPtr.Zero)
        {
            yield break;
        }

        try
        {
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
                    yield return ((int)row.OwningPid, new IPAddress(BitConverter.GetBytes(row.LocalAddr)), DecodePort(row.LocalPort),
                        new IPAddress(BitConverter.GetBytes(row.RemoteAddr)), DecodePort(row.RemotePort));
                }
                else
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(cursor);
                    yield return ((int)row.OwningPid, new IPAddress(row.LocalAddr), DecodePort(row.LocalPort),
                        new IPAddress(row.RemoteAddr), DecodePort(row.RemotePort));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The size-then-fetch pair, retried while the table outgrows the buffer between the two
    /// calls. Returns an unmanaged buffer holding the table (the caller frees it), or
    /// <see cref="IntPtr.Zero"/> when there is none to read.
    /// </summary>
    private IntPtr ReadTable(int addressFamily) => ReadTable(_readTable, addressFamily);

    private static IntPtr ReadTable(TcpTableReader readTable, int addressFamily)
    {
        for (var attempt = 0; attempt < MaxTableReadAttempts; attempt++)
        {
            var size = 0;
            var status = readTable(IntPtr.Zero, ref size, addressFamily);
            if ((status != ErrorInsufficientBuffer && status != NoError) || size <= 0)
            {
                return IntPtr.Zero;
            }

            var buffer = Marshal.AllocHGlobal(size);
            status = readTable(buffer, ref size, addressFamily);
            if (status == NoError)
            {
                return buffer;
            }

            Marshal.FreeHGlobal(buffer);
            if (status != ErrorInsufficientBuffer)
            {
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private static uint NativeReadTable(IntPtr table, ref int size, int addressFamily) =>
        GetExtendedTcpTable(table, ref size, false, addressFamily, TcpTableOwnerPidListener, 0);

    private static uint NativeReadConnectionTable(IntPtr table, ref int size, int addressFamily) =>
        GetExtendedTcpTable(table, ref size, false, addressFamily, TcpTableOwnerPidConnections, 0);

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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        [Out] char[] exeName,
        ref int size);

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
