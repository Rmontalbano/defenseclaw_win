using System.Runtime.InteropServices;

namespace DefenseClaw.Core.Net;

/// <summary>What the kernel's process list says of one process, read without opening it.</summary>
/// <param name="Pid">The process id.</param>
/// <param name="Name">Image name without the <c>.exe</c> extension, as <see cref="System.Diagnostics.Process.ProcessName"/> spells it; null when there is none.</param>
/// <param name="SessionId">The Windows session it runs in.</param>
/// <param name="CreatedUtc">When the process was created (the kernel's <c>CreateTime</c>, UTC); null when the list gave no usable time.</param>
public sealed record ProcessFacts(int Pid, string? Name, int SessionId, DateTime? CreatedUtc);

/// <summary>
/// One process's entry in <c>NtQuerySystemInformation(SystemProcessInformation)</c>, the list Task Manager and <see cref="System.Diagnostics.Process"/>
/// read: name, session and creation time with no handle, so they are known for a process this app may not open (another account's, an elevated
/// one). The creation time is what <see cref="System.Diagnostics.Process.StartTime"/> would need a handle for, and what the peer check compares with a
/// socket's own creation time (CUST-392): a socket older than the process that holds its pid was opened by another process that had that pid.
/// </summary>
public static class ProcessList
{
    /// <summary>The first buffer, in bytes; grown to what the kernel says it needs.</summary>
    private const int FirstBufferBytes = 512 * 1024;

    /// <summary>The largest buffer this will allocate, in bytes.</summary>
    private const int MaxBufferBytes = 64 * 1024 * 1024;

    private const int SystemProcessInformationClass = 5;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>The entry for <paramref name="pid"/> in the process list as it is now; null when there is none or the list cannot be read.</summary>
    public static ProcessFacts? Read(int pid)
    {
        if (pid < 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var bytes = FirstBufferBytes;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformationClass, buffer, bytes, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    // Processes start between the calls: ask again with room to spare.
                    var next = Math.Max(needed, bytes) + (64 * 1024);
                    if (next > MaxBufferBytes)
                    {
                        return null;
                    }

                    bytes = next;
                    continue;
                }

                return status < 0 ? null : Find(buffer, Math.Min(needed > 0 ? needed : bytes, bytes), pid);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    /// <summary>Walks the list in <paramref name="buffer"/> (<paramref name="length"/> bytes) for <paramref name="pid"/>, never past its end.</summary>
    private static ProcessFacts? Find(IntPtr buffer, int length, int pid)
    {
        var entrySize = Marshal.SizeOf<SystemProcessInformation>();
        var offset = 0L;
        while (offset >= 0 && offset + entrySize <= length)
        {
            var entry = Marshal.PtrToStructure<SystemProcessInformation>(buffer + (nint)offset);
            if ((long)entry.UniqueProcessId == pid)
            {
                return new ProcessFacts(pid, NameOf(entry, buffer, length, pid), (int)entry.SessionId, ToUtc(entry.CreateTime));
            }

            if (entry.NextEntryOffset == 0)
            {
                break;
            }

            offset += entry.NextEntryOffset;
        }

        return null;
    }

    /// <summary>The short name .NET gives a process: the file name of the image without <c>.exe</c>; "Idle" and "System" for pids 0 and 4.</summary>
    private static string? NameOf(SystemProcessInformation entry, IntPtr buffer, int length, int pid)
    {
        var name = entry.ImageName;
        var start = (long)name.Buffer - (long)buffer;
        if (name.Buffer != IntPtr.Zero && name.Length > 0 && start >= 0 && start + name.Length <= length)
        {
            var text = Marshal.PtrToStringUni(name.Buffer, name.Length / 2);
            text = text[(text.LastIndexOf('\\') + 1)..];
            return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text;
        }

        return pid switch
        {
            0 => "Idle",
            4 => "System",
            _ => null,
        };
    }

    /// <summary>A FILETIME (100 ns since 1601, UTC) as a UTC time; null for zero or a value no <see cref="DateTime"/> holds.</summary>
    public static DateTime? ToUtc(long fileTime) =>
        fileTime > 0 && fileTime <= DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(fileTime) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>The head of <c>SYSTEM_PROCESS_INFORMATION</c>, as far as the session; the rest of the entry is not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemProcessInformation
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UnicodeString ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);
}
