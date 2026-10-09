using System.Runtime.InteropServices;

namespace DefenseClaw.Tests.TestSupport;

/// <summary>
/// Which processes are below another one. A test that starts a process tree and wants "the grandchild" has no pid for it, so it has to
/// look. Looking at every process of that name on the machine finds other people's too (the same suite running in another checkout,
/// another developer's tests, somebody's own ping), and a test that then waits for, or kills, "its" pid ends a run that is not its own:
/// the victim sees "Already finished" for a run that was never cancelled. A process's parent id is recorded when it starts and never
/// changes, so a walk down from a process reaches only what that process started. Walk down from the child a run started
/// (<c>CliInvocation.ProcessId</c>) and the answer is that run's own tree, whatever else is running: another checkout's tests, another
/// developer's, or another test class of this same run.
/// </summary>
public static class ProcessTree
{
    private const uint SnapshotProcesses = 0x2;

    /// <summary>
    /// The ids of the processes whose image is <paramref name="imageName"/> (<c>ping.exe</c>) that <paramref name="rootProcessId"/> started,
    /// directly or through others (a child's child counts); this process when null. A process whose parent has already exited is not reached
    /// - the walk needs the parent in the snapshot - so call this while the tree it is meant to find is alive.
    /// </summary>
    public static HashSet<int> DescendantsNamed(string imageName, int? rootProcessId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageName);

        var root = rootProcessId ?? Environment.ProcessId;
        var processes = Snapshot();
        var childrenOf = processes.ToLookup(p => p.ParentId);

        var below = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            foreach (var child in childrenOf[pending.Dequeue()])
            {
                if (child.Id != root && below.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return processes
            .Where(p => below.Contains(p.Id) && string.Equals(p.Image, imageName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToHashSet();
    }

    private readonly record struct Entry(int Id, int ParentId, string Image);

    private static List<Entry> Snapshot()
    {
        var entries = new List<Entry>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return entries;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                entries.Add(new Entry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            }
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return entries;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
