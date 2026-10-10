using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// A Windows Job Object that owns one CLI child and everything it starts, with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when the last handle to the job goes away - this app exiting, crashing or
/// being <c>taskkill /F</c>'d included - Windows ends every process still in it. <see cref="Process.Kill(bool)"/> only
/// covers the paths where this app is alive to call it (cancel, timeout, shutdown).
/// <para>
/// Modelled on the TUI's <c>windows_process.py</c> (0.8.10), with the same two limit flags. <c>BREAKAWAY_OK</c> is set so
/// a descendant that asks for <c>CREATE_BREAKAWAY_FROM_JOB</c> - the CLI starting the gateway or watchdog daemon, which are
/// meant to outlive the command - can leave the job; everything else stays in it. If this app is itself inside a job, the
/// new one nests (Windows 8 and later), so nothing here needs the outer job's breakaway permission.
/// </para>
/// <para>
/// <b>The launch window.</b> .NET starts a child running; it can neither create it suspended (the TUI does) nor create it inside a job
/// (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>, which <see cref="Process.Start()"/> does not offer). The child is assigned right after
/// <see cref="Process.Start()"/> returns, so a process it starts before that is not in the job. Measured, from the return of
/// <c>Start</c> to the return of <see cref="TryAssign"/> (300 runs each): a median of 0.06 ms and a 99th percentile of 0.2 ms on a busy
/// laptop; a median of 0.1 ms, a 99th percentile of 1 ms and one run of 440 ms with 24 busy loops sharing the cores of the thread that
/// starts the child (the time that thread spent not running).
/// </para>
/// <para>
/// That is a case that occurs for the installed CLI, rarely. <c>defenseclaw.exe</c> is a small native launcher (a Go program that starts the
/// embedded <c>python.exe</c> as its child through <c>os/exec</c>, as the strings of the file show; the file was read, never run) with
/// little to do first, so it starts that child very soon after its own start;
/// on a starved machine that can come before the assignment, and the interpreter is then outside the job. (An earlier version of this comment
/// said that a CLI starting a grandchild that early is not a case that occurs.) Cancel, timeout and shutdown are not affected: <see cref="CliRunner"/>
/// follows the job termination with the <c>Kill(entireProcessTree)</c> walk, which finds a descendant whether it is in the job or not.
/// Only the crash guarantee has the gap: if the app is later killed outright while such a command runs, the launcher goes with the job and
/// the interpreter it started early may stay behind (whether it does depends on the launcher, which nothing here relies on). It is left open
/// on purpose: closing it needs a process created already inside the job, and sweeping a launcher's existing children into the job after the
/// fact by parent id would put whatever owns a reused id by then in a job that kills it.
/// </para>
/// <para>
/// Everything is best-effort: <see cref="TryCreate"/> and <see cref="TryAssign"/> report failure instead of throwing, and
/// the runner falls back to the tree kill alone.
/// </para>
/// </summary>
public sealed class WindowsJob : IDisposable
{
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int ErrorMoreData = 234;

    /// <summary>How many process ids <see cref="ProcessIds"/> has room for; a job of this app's (a CLI, its interpreter, a console host) is far smaller.</summary>
    internal const int MaxListedProcesses = 64;

    private readonly object _gate = new();
    private IntPtr _job;

    private WindowsJob(IntPtr job)
    {
        _job = job;
    }

    /// <summary>
    /// A new kill-on-close job, or <c>null</c> when this is not Windows or the job could not be created or configured.
    /// </summary>
    public static WindowsJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return null;
            }

            var limits = new JobObjectExtendedLimitInformationStruct();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk;
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    _ = CloseHandle(job);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return new WindowsJob(job);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts <paramref name="process"/> in the job; its descendants started from now on follow it. False when it could not
    /// (the process already ended, or is in a job that forbids nesting) - the caller then relies on the tree kill alone.
    /// </summary>
    public bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        lock (_gate)
        {
            if (_job == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return AssignProcessToJobObject(_job, process.SafeHandle.DangerousGetHandle());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process object has no handle (it exited and was reaped, or was never started).
                return false;
            }
        }
    }

    /// <summary>
    /// <see cref="TryAssign(Process)"/> for a child its starter holds the handle of and created <b>suspended</b> (<see cref="PseudoConsole"/>
    /// does): it joins the job before it runs a single instruction, so nothing it starts can be outside it - the launch window described on
    /// this type does not exist for such a child.
    /// </summary>
    internal bool TryAssign(IntPtr processHandle)
    {
        lock (_gate)
        {
            return _job != IntPtr.Zero && processHandle != IntPtr.Zero && AssignProcessToJobObject(_job, processHandle);
        }
    }

    /// <summary>Ends every process in the job at once. False if the job is closed or the call failed.</summary>
    public bool Terminate(uint exitCode = 130)
    {
        lock (_gate)
        {
            return _job != IntPtr.Zero && TerminateJobObject(_job, exitCode);
        }
    }

    /// <summary>How many processes are in the job right now, or -1 when it cannot be read (closed).</summary>
    public int ActiveProcessCount
    {
        get
        {
            lock (_gate)
            {
                if (_job == IntPtr.Zero)
                {
                    return -1;
                }

                var size = Marshal.SizeOf<JobObjectBasicAccountingInformationStruct>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    return QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, buffer, (uint)size, IntPtr.Zero)
                        ? (int)Marshal.PtrToStructure<JobObjectBasicAccountingInformationStruct>(buffer).ActiveProcesses
                        : -1;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
    }

    /// <summary>
    /// The ids of the processes in the job right now (up to <see cref="MaxListedProcesses"/>), or empty when the job is closed or cannot be read. For
    /// the tests, which must not count on the exact set: a console program started without a window gets a <c>conhost.exe</c>, and Windows puts it in
    /// the job of the program it serves a moment after that program was assigned (60 of 60 <c>ping.exe</c> runs, within 150 ms), so "the job holds
    /// one process" is true at one instant and false at the next. What a test can know is that the process it assigned is a member.
    /// </summary>
    internal IReadOnlyList<int> ProcessIds
    {
        get
        {
            lock (_gate)
            {
                if (_job == IntPtr.Zero)
                {
                    return Array.Empty<int>();
                }

                // JOBOBJECT_BASIC_PROCESS_ID_LIST: ULONG NumberOfAssignedProcesses; ULONG NumberOfProcessIdsInList; ULONG_PTR ProcessIdList[].
                var size = 8 + (IntPtr.Size * MaxListedProcesses);
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    // False with ERROR_MORE_DATA still fills the buffer with as many as fit, which is all a test asks for.
                    if (!QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, (uint)size, IntPtr.Zero) &&
                        Marshal.GetLastWin32Error() != ErrorMoreData)
                    {
                        return Array.Empty<int>();
                    }

                    var listed = Math.Min(Marshal.ReadInt32(buffer, 4), MaxListedProcesses);
                    var ids = new int[listed];
                    for (var i = 0; i < listed; i++)
                    {
                        ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + (i * IntPtr.Size));
                    }

                    return ids;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
    }

    /// <summary>Closes the handle; with kill-on-close that ends anything that stayed in the job. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_job == IntPtr.Zero)
            {
                return;
            }

            _ = CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformationStruct
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersStruct
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformationStruct BasicLimitInformation;
        public IoCountersStruct IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationStruct
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size, IntPtr returnLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
