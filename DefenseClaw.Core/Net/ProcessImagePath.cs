using System.Runtime.InteropServices;

namespace DefenseClaw.Core.Net;

/// <summary>The executable a process runs, and how that was learned.</summary>
/// <param name="Path">The full Win32 path of the executable.</param>
/// <param name="WithoutHandle">
/// True when the process could not be opened (another account's process, an elevated one, or one a security product guards) and the path came
/// from the kernel's own process list instead. The path is still the kernel's, but nothing else about the process was read first-hand, so the
/// peer check also wants it to run in this app's Windows session.
/// </param>
public sealed record ProcessImage(string Path, bool WithoutHandle);

/// <summary>A drive letter and the NT device it names (<c>QueryDosDevice</c>), for turning a kernel path into a Win32 one.</summary>
/// <param name="Drive">For example <c>C:</c>.</param>
/// <param name="Target">For example <c>\Device\HarddiskVolume3</c>; a <c>subst</c> drive names <c>\??\...</c> instead.</param>
public sealed record DosDevice(string Drive, string Target);

/// <summary>
/// Which file a process runs, read in the two ways Windows offers to an unelevated program (CUST-362).
/// <list type="number">
/// <item><b>With a handle</b>: <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and <c>QueryFullProcessImageNameW</c>, the buffer grown from 1,024 up to
/// 32,768 characters. A path longer than that is refused outright, never treated as unreadable.</item>
/// <item><b>Without one</b>, when the process cannot be opened: <c>NtQuerySystemInformation(SystemProcessIdInformation)</c>, which names the
/// image of any process by its id, and the NT device path it gives is turned into a drive-letter path through the drive table
/// (<see cref="ToDosPath"/>). WMI's <c>ExecutablePath</c> and <c>Process.MainModule</c> need the process's memory and come back empty for an
/// elevated gateway; this does not.</item>
/// </list>
/// If neither gives a path, the answer is null and the caller refuses the process.
/// </summary>
public static class ProcessImagePath
{
    /// <summary>The first buffer, in characters.</summary>
    public const int FirstCapacity = 1024;

    /// <summary>The largest buffer, in characters: the longest path Windows has.</summary>
    public const int MaxCapacity = 32768;

    /// <summary>What one handle-based read found.</summary>
    public enum HandleStatus
    {
        /// <summary>The path was read.</summary>
        Read,

        /// <summary>The buffer was too small for the path.</summary>
        BufferTooSmall,

        /// <summary>The process could not be opened with the limited query right.</summary>
        NotOpened,

        /// <summary>It was opened, but the name could not be read for another reason.</summary>
        Failed,
    }

    /// <summary>One handle-based read with a buffer of <paramref name="capacity"/> characters.</summary>
    public delegate (HandleStatus Status, string? Path) HandleReader(int pid, int capacity);

    /// <summary>The kernel's NT path of <paramref name="pid"/>'s image (<c>\Device\...</c>), or null.</summary>
    public delegate string? KernelReader(int pid);

    /// <summary>The image of <paramref name="pid"/>, read the real way.</summary>
    public static ProcessImage? Read(int pid) => Read(pid, ReadWithHandle, ReadKernelImageName, ReadDosDevices);

    /// <summary>The image of <paramref name="pid"/> through the given readers (a test hands in its own).</summary>
    public static ProcessImage? Read(int pid, HandleReader withHandle, KernelReader kernel, Func<IReadOnlyList<DosDevice>> dosDevices)
    {
        ArgumentNullException.ThrowIfNull(withHandle);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(dosDevices);

        if (pid <= 0)
        {
            return null;
        }

        for (var capacity = FirstCapacity; ; capacity = Math.Min(capacity * 4, MaxCapacity))
        {
            var (status, path) = withHandle(pid, capacity);
            switch (status)
            {
                case HandleStatus.Read:
                    return string.IsNullOrWhiteSpace(path) ? null : new ProcessImage(path, WithoutHandle: false);

                case HandleStatus.BufferTooSmall when capacity < MaxCapacity:
                    continue;

                case HandleStatus.BufferTooSmall:
                    // Longer than any path Windows has: refused, not a reason to look elsewhere.
                    return null;
            }

            break;
        }

        // Not openable: ask the kernel's process list, which needs no handle.
        return ToDosPath(kernel(pid), dosDevices()) is { } dosPath ? new ProcessImage(dosPath, WithoutHandle: true) : null;
    }

    /// <summary>
    /// <paramref name="ntPath"/> (<c>\Device\HarddiskVolume3\Program Files\...</c>) as a drive-letter path, through the one drive whose device it is
    /// on. Null when it is not a device path, when no drive names its device (a network share, a volume with no letter), or when two drives do
    /// (an ambiguous table is not guessed at). <c>subst</c> drives name another drive's folder, not a device, and are never the answer.
    /// </summary>
    public static string? ToDosPath(string? ntPath, IReadOnlyList<DosDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (string.IsNullOrEmpty(ntPath) || !ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? match = null;
        foreach (var device in devices)
        {
            if (device.Drive is not { Length: 2 } drive || drive[1] != ':' || !char.IsAsciiLetter(drive[0]) ||
                !device.Target.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = device.Target.TrimEnd('\\');
            if (ntPath.Length > prefix.Length && ntPath[prefix.Length] == '\\' && ntPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (match is not null)
                {
                    return null;
                }

                match = drive + ntPath[prefix.Length..];
            }
        }

        return match;
    }

    /// <summary>The handle-based read, once, with a buffer of <paramref name="capacity"/> characters.</summary>
    public static (HandleStatus Status, string? Path) ReadWithHandle(int pid, int capacity)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows())
        {
            return (HandleStatus.NotOpened, null);
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return (HandleStatus.NotOpened, null);
        }

        try
        {
            var size = capacity;
            var buffer = new char[capacity];
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return (HandleStatus.Read, new string(buffer, 0, size));
            }

            return Marshal.GetLastPInvokeError() == ErrorInsufficientBuffer ? (HandleStatus.BufferTooSmall, null) : (HandleStatus.Failed, null);
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    /// <summary>
    /// The kernel's NT path of <paramref name="pid"/>'s image, from <c>NtQuerySystemInformation(SystemProcessIdInformation)</c>: no handle, so no
    /// access check on the process. Null when there is no such process or the call fails.
    /// </summary>
    public static string? ReadKernelImageName(int pid)
    {
        if (pid <= 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var bytes = 512;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                var info = new SystemProcessIdInformation
                {
                    ProcessId = (IntPtr)pid,
                    ImageName = new UnicodeString { Length = 0, MaximumLength = (ushort)bytes, Buffer = buffer },
                };
                var status = NtQuerySystemInformation(SystemProcessIdInformationClass, ref info, Marshal.SizeOf<SystemProcessIdInformation>(), out _);

                // Too small: the kernel puts the size it needs in MaximumLength; ask once more with that.
                if (status == StatusInfoLengthMismatch && info.ImageName.MaximumLength > bytes)
                {
                    bytes = info.ImageName.MaximumLength;
                    continue;
                }

                if (status < 0 || info.ImageName.Length == 0 || info.ImageName.Length > bytes)
                {
                    return null;
                }

                return Marshal.PtrToStringUni(buffer, info.ImageName.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    /// <summary>Every drive letter and the device it names, read now.</summary>
    public static IReadOnlyList<DosDevice> ReadDosDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<DosDevice>();
        }

        var devices = new List<DosDevice>();
        var mask = GetLogicalDrives();
        var buffer = new char[1024];
        for (var i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0)
            {
                continue;
            }

            var drive = $"{(char)('A' + i)}:";
            var length = QueryDosDevice(drive, buffer, (uint)buffer.Length);
            if (length == 0)
            {
                continue;
            }

            // A multi-string: the first entry is the current mapping.
            var target = new string(buffer, 0, (int)length).Split('\0')[0];
            if (target.Length > 0)
            {
                devices.Add(new DosDevice(drive, target));
            }
        }

        return devices;
    }

    /// <summary>PROCESS_QUERY_LIMITED_INFORMATION.</summary>
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const int ErrorInsufficientBuffer = 122;

    private const int SystemProcessIdInformationClass = 88;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessIdInformation
    {
        public IntPtr ProcessId;
        public UnicodeString ImageName;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SystemProcessIdInformation info, int length, out int returnLength);

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
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref int size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryDosDeviceW", SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint max);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();
}
