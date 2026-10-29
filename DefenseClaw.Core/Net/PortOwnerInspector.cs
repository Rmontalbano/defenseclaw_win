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
/// Full path of the owning executable; null when it could not be read (process gone, or Windows gave no path either way, see
/// <see cref="ProcessImagePath"/>). Together with <paramref name="ProcessName"/> this is what says whether the
/// listener really is the DefenseClaw gateway — a name alone is anyone's to pick.
/// </param>
/// <param name="SessionId">The Windows session the process runs in, from the process list (no handle needed); null when unknown.</param>
/// <param name="ImageWithoutHandle">
/// True when <paramref name="ImagePath"/> came from the kernel's process list because the process could not be opened (another account's,
/// or an elevated one); see <see cref="ProcessImage.WithoutHandle"/>.
/// </param>
/// <param name="SocketCreatedUtc">
/// When the socket was opened (the TCP table's <c>liCreateTimestamp</c>, UTC); for a connection, the earliest of its own row and the listener
/// rows of the same process on that port. Null when Windows gave no time.
/// </param>
/// <param name="ProcessCreatedUtc">When the process that holds <paramref name="Pid"/> now was created (<see cref="ProcessList"/>, no handle); null when unknown.</param>
public sealed record PortOwner(
    int Pid,
    string? ProcessName,
    string LocalAddress,
    int Port = 0,
    string? ImagePath = null,
    int? SessionId = null,
    bool ImageWithoutHandle = false,
    DateTime? SocketCreatedUtc = null,
    DateTime? ProcessCreatedUtc = null)
{
    /// <summary>
    /// True when the process that holds <see cref="Pid"/> now is the one that opened the socket: both times are known and the process is not
    /// the younger (equal counts, for the clock's granularity). Windows keeps naming a socket's creator after it exits, also while a child it
    /// handed the socket to serves it, so a younger process with that pid has only reused it (CUST-392).
    /// </summary>
    public bool PidMatchesSocket => ProcessCreatedUtc is { } process && SocketCreatedUtc is { } socket && process <= socket;

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
/// (<c>TCP_TABLE_OWNER_MODULE_CONNECTIONS</c>) - not from whoever happens to be listening on the port.
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
/// <para>
/// <b>Whose socket (CUST-392).</b> Windows names a socket by the pid of the process that opened it, and keeps that pid after the process exits
/// while a child it handed the socket to still serves it. A later process can reuse the pid. So the tables are the <c>OWNER_MODULE</c> ones,
/// which carry each socket's creation time, and every owner carries its process's creation time from <see cref="ProcessList"/> (no handle):
/// the peer check refuses an owner whose process is younger than its socket (<see cref="PortOwner.PidMatchesSocket"/>). The image path is
/// read before the process list, so the process the list then shows, alive and no younger than the socket, is the one whose path was read.
/// </para>
/// </summary>
public sealed class PortOwnerInspector : IPortOwnerInspector, IConnectionOwnerInspector
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;

    /// <summary>TCP_TABLE_OWNER_MODULE_LISTENER: every listening socket with its owner and creation time.</summary>
    private const int TcpTableOwnerModuleListener = 6;

    /// <summary>TCP_TABLE_OWNER_MODULE_CONNECTIONS: every non-listening socket with its owner and creation time, both ends of a loopback connection included.</summary>
    private const int TcpTableOwnerModuleConnections = 7;

    /// <summary>How many size-then-fetch rounds are tried before the table is given up on.</summary>
    public const int MaxTableReadAttempts = 5;

    /// <summary>
    /// Where the rows start in a <c>MIB_TCPTABLE_OWNER_MODULE</c> (and its IPv6 twin): after the dword count, padded to the rows' 8-byte
    /// alignment (they hold a <c>LARGE_INTEGER</c>).
    /// </summary>
    public const int TableRowsOffset = 8;

    /// <summary>Size of one <c>MIB_TCPROW_OWNER_MODULE</c>: six dwords, the creation time, sixteen module-info quadwords.</summary>
    public const int Ipv4RowBytes = 160;

    /// <summary>Size of one <c>MIB_TCP6ROW_OWNER_MODULE</c>.</summary>
    public const int Ipv6RowBytes = 192;

    private readonly TcpTableReader _readTable;
    private readonly TcpTableReader _readConnectionTable;
    private readonly ImagePathReader _readImagePath;
    private readonly ProcessReader _readProcess;

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
        : this(readTable, readConnectionTable, ProcessImagePath.Read)
    {
    }

    /// <param name="readImagePath">
    /// How a row's pid becomes the file it runs (<see cref="ProcessImagePath.Read(int)"/> by default), so a test can play a process whose path
    /// Windows gives only without a handle, or not at all, without needing one (CUST-362).
    /// </param>
    public PortOwnerInspector(TcpTableReader readTable, TcpTableReader readConnectionTable, ImagePathReader readImagePath)
        : this(readTable, readConnectionTable, readImagePath, ProcessList.Read)
    {
    }

    /// <param name="readProcess">
    /// How a row's pid becomes its name, session and creation time (<see cref="ProcessList.Read"/> by default), so a test can play a pid that
    /// a younger process reused (CUST-392).
    /// </param>
    public PortOwnerInspector(TcpTableReader readTable, TcpTableReader readConnectionTable, ImagePathReader readImagePath, ProcessReader readProcess)
    {
        _readTable = readTable ?? throw new ArgumentNullException(nameof(readTable));
        _readConnectionTable = readConnectionTable ?? throw new ArgumentNullException(nameof(readConnectionTable));
        _readImagePath = readImagePath ?? throw new ArgumentNullException(nameof(readImagePath));
        _readProcess = readProcess ?? throw new ArgumentNullException(nameof(readProcess));
    }

    /// <summary>
    /// One <c>GetExtendedTcpTable</c> call for <paramref name="addressFamily"/>: with a null
    /// <paramref name="table"/> it reports the size needed, otherwise it fills the buffer.
    /// Returns the Win32 status (0 = <c>NO_ERROR</c>, 122 = <c>ERROR_INSUFFICIENT_BUFFER</c>).
    /// The buffer is laid out as an <c>OWNER_MODULE</c> table (<see cref="TableRowsOffset"/>, <see cref="Ipv4RowBytes"/>, <see cref="Ipv6RowBytes"/>).
    /// </summary>
    public delegate uint TcpTableReader(IntPtr table, ref int size, int addressFamily);

    /// <summary>The executable process <paramref name="pid"/> runs and how that was learned, or null when it cannot be read.</summary>
    public delegate ProcessImage? ImagePathReader(int pid);

    /// <summary>What the process list says of <paramref name="pid"/> now, or null when there is no such process.</summary>
    public delegate ProcessFacts? ProcessReader(int pid);

    /// <summary>One listening socket.</summary>
    private readonly record struct ListenerRow(int Pid, int Port, string Address, DateTime? Created);

    /// <summary>One connection's socket, seen from its local end.</summary>
    private readonly record struct ConnectionRow(int Pid, IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort, DateTime? Created);

    public PortOwner? FindListener(int port)
    {
        if (port is <= 0 or > 65535 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var row in AllListeners())
        {
            if (row.Port == port)
            {
                return Describe(row.Pid, row.Port, row.Address, row.Created);
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

        return AllListeners()
            .Where(row => row.Port == port)
            .Select(row => Describe(row.Pid, row.Port, row.Address, row.Created))
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
        var families = new HashSet<int>();
        var created = new List<DateTime?>();
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            foreach (var row in ReadConnections(family) ?? [])
            {
                if (row.LocalPort == server.Port && row.RemotePort == client.Port &&
                    Unmap(row.LocalAddress).Equals(serverAddress) && Unmap(row.RemoteAddress).Equals(clientAddress))
                {
                    pids.Add(row.Pid);
                    families.Add(family);
                    created.Add(row.Created);
                }
            }
        }

        // Exactly one owner, or none: a row set that names two processes cannot say who is on the other end.
        if (pids.Count != 1)
        {
            return null;
        }

        var pid = pids.First();

        // The socket the connection was accepted from counts too: a connection accepted from a listener another process opened can carry that
        // process's pid, and a connection made just now is younger than anything. So the earliest of the connection's rows and its process's
        // listener rows on the port (in the same table family) is the time the process is held to; a family whose listener table cannot be read
        // leaves no time at all (refused).
        foreach (var family in families)
        {
            if (ReadListeners(family) is not { } listeners)
            {
                created.Add(null);
                continue;
            }

            created.AddRange(listeners.Where(row => row.Port == server.Port && row.Pid == pid).Select(row => row.Created));
        }

        return Describe(pid, server.Port, server.Address.ToString(), Earliest(created));
    }

    /// <summary>The earliest time, or null when any of them is unknown (or there is none).</summary>
    private static DateTime? Earliest(IReadOnlyList<DateTime?> times) =>
        times.Count == 0 || times.Any(static t => t is null) ? null : times.Min();

    private static IPAddress Unmap(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>All listening sockets, for a diagnostics view.</summary>
    public IReadOnlyList<PortOwner> ListListeners()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<PortOwner>();
        }

        return AllListeners()
            .Select(row => Describe(row.Pid, row.Port, row.Address, row.Created))
            .ToArray();
    }

    /// <summary>
    /// The owner of one socket. The image is read first and the process list last: a process the list then shows with this pid, alive and created
    /// no later than the socket, has held the pid since before the socket existed, so the image read in between is its own (CUST-392).
    /// </summary>
    private PortOwner Describe(int pid, int port, string address, DateTime? socketCreated)
    {
        var image = _readImagePath(pid);
        var process = _readProcess(pid);
        return new PortOwner(
            pid,
            process?.Name,
            address,
            port,
            image?.Path,
            process?.SessionId,
            image?.WithoutHandle ?? false,
            socketCreated,
            process?.CreatedUtc);
    }

    private IEnumerable<ListenerRow> AllListeners() => (ReadListeners(AfInet) ?? []).Concat(ReadListeners(AfInet6) ?? []);

    /// <summary>The listening sockets of one address family; null when the table cannot be read.</summary>
    private IReadOnlyList<ListenerRow>? ReadListeners(int addressFamily)
    {
        var (buffer, size) = ReadTable(_readTable, addressFamily);
        if (buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var rows = new List<ListenerRow>();
            foreach (var cursor in Rows(buffer, size, addressFamily))
            {
                if (addressFamily == AfInet)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerModule>(cursor);
                    rows.Add(new ListenerRow((int)row.OwningPid, DecodePort(row.LocalPort), DecodeIpv4(row.LocalAddr), ProcessList.ToUtc(row.CreateTimestamp)));
                }
                else
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerModule>(cursor);
                    rows.Add(new ListenerRow((int)row.OwningPid, DecodePort(row.LocalPort), DecodeIpv6(row.LocalAddr), ProcessList.ToUtc(row.CreateTimestamp)));
                }
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The connections of one address family; null when the table cannot be read.</summary>
    private IReadOnlyList<ConnectionRow>? ReadConnections(int addressFamily)
    {
        var (buffer, size) = ReadTable(_readConnectionTable, addressFamily);
        if (buffer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var rows = new List<ConnectionRow>();
            foreach (var cursor in Rows(buffer, size, addressFamily))
            {
                if (addressFamily == AfInet)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerModule>(cursor);
                    rows.Add(new ConnectionRow((int)row.OwningPid, new IPAddress(BitConverter.GetBytes(row.LocalAddr)), DecodePort(row.LocalPort),
                        new IPAddress(BitConverter.GetBytes(row.RemoteAddr)), DecodePort(row.RemotePort), ProcessList.ToUtc(row.CreateTimestamp)));
                }
                else
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerModule>(cursor);
                    rows.Add(new ConnectionRow((int)row.OwningPid, new IPAddress(row.LocalAddr), DecodePort(row.LocalPort),
                        new IPAddress(row.RemoteAddr), DecodePort(row.RemotePort), ProcessList.ToUtc(row.CreateTimestamp)));
                }
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Where each row of the table in <paramref name="buffer"/> starts: as many as it says it holds, never past the <paramref name="size"/> bytes it was given.</summary>
    private static List<IntPtr> Rows(IntPtr buffer, int size, int addressFamily)
    {
        var rowSize = addressFamily == AfInet ? Ipv4RowBytes : Ipv6RowBytes;
        var starts = new List<IntPtr>();
        if (size < TableRowsOffset)
        {
            return starts;
        }

        var count = Math.Min(Marshal.ReadInt32(buffer), (size - TableRowsOffset) / rowSize);
        for (var i = 0; i < count; i++)
        {
            starts.Add(buffer + TableRowsOffset + (i * rowSize));
        }

        return starts;
    }

    /// <summary>
    /// The size-then-fetch pair, retried while the table outgrows the buffer between the two
    /// calls. Returns an unmanaged buffer holding the table and its size (the caller frees it), or
    /// <see cref="IntPtr.Zero"/> when there is none to read.
    /// </summary>
    private static (IntPtr Buffer, int Size) ReadTable(TcpTableReader readTable, int addressFamily)
    {
        for (var attempt = 0; attempt < MaxTableReadAttempts; attempt++)
        {
            var size = 0;
            var status = readTable(IntPtr.Zero, ref size, addressFamily);
            if ((status != ErrorInsufficientBuffer && status != NoError) || size <= 0)
            {
                return (IntPtr.Zero, 0);
            }

            var allocated = size;
            var buffer = Marshal.AllocHGlobal(allocated);
            status = readTable(buffer, ref size, addressFamily);
            if (status == NoError)
            {
                return (buffer, Math.Min(allocated, size > 0 ? size : allocated));
            }

            Marshal.FreeHGlobal(buffer);
            if (status != ErrorInsufficientBuffer)
            {
                return (IntPtr.Zero, 0);
            }
        }

        return (IntPtr.Zero, 0);
    }

    private static uint NativeReadTable(IntPtr table, ref int size, int addressFamily) =>
        GetExtendedTcpTable(table, ref size, false, addressFamily, TcpTableOwnerModuleListener, 0);

    private static uint NativeReadConnectionTable(IntPtr table, ref int size, int addressFamily) =>
        GetExtendedTcpTable(table, ref size, false, addressFamily, TcpTableOwnerModuleConnections, 0);

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

    /// <summary>The head of <c>MIB_TCPROW_OWNER_MODULE</c>; the module info after it (<see cref="Ipv4RowBytes"/> in all) is not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerModule
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
        public long CreateTimestamp;
    }

    /// <summary>The head of <c>MIB_TCP6ROW_OWNER_MODULE</c>; the module info after it (<see cref="Ipv6RowBytes"/> in all) is not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerModule
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
        public long CreateTimestamp;
    }
}
