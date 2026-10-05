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
/// <b>The launch window.</b> .NET starts a child running; it cannot create it suspended (the TUI does). The child is assigned
/// right after <see cref="Process.Start()"/> returns, so a grandchild spawned in the first milliseconds is not in the job.
/// Cancel and timeout still reach it, because <see cref="CliRunner"/> follows the job termination with the
/// <c>Kill(entireProcessTree)</c> walk; only the crash guarantee has the gap. A CLI that starts a grandchild before its own
/// interpreter has finished starting is not a case that occurs.
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
    private const int JobObjectExtendedLimitInformation = 9;

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
