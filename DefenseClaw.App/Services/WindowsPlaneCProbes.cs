using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DefenseClaw.Core.AiRuntime;
using Microsoft.Win32;

namespace DefenseClaw.App.Services;

/// <summary>
/// The real probes behind the Plane C readiness card (CUST-324): Win32 and .NET reads of THIS app's token and of one registry value. It never starts
/// a process, never elevates, and never runs <c>auditpol</c> or <c>reg</c> (the audit policy is shown as unreadable, with a line to copy). Every
/// failure is a <see cref="ProbeReading{T}.Failed"/>, never a quiet "no".
/// </summary>
internal sealed class WindowsPlaneCProbes : IPlaneCProbes
{
    private const int TokenElevationTypeClass = 18;
    private const int ErrorAccessDenied = 5;
    private const string AuditKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit";
    private const string AuditValue = "ProcessCreationIncludeCmdLine_Enabled";
    private static readonly SecurityIdentifier EventLogReaders = new("S-1-5-32-573");

    public ProbeReading<TokenElevation> ReadElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(identity.AccessToken.DangerousGetHandle(), TokenElevationTypeClass, buffer, sizeof(int), out _))
                {
                    return ProbeReading<TokenElevation>.Failed("GetTokenInformation failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }

                // TokenElevationDefault 1, TokenElevationTypeFull 2, TokenElevationTypeLimited 3.
                return Marshal.ReadInt32(buffer) switch
                {
                    2 => ProbeReading<TokenElevation>.Ok(TokenElevation.Full),
                    3 => ProbeReading<TokenElevation>.Ok(TokenElevation.Limited),
                    1 => ProbeReading<TokenElevation>.Ok(TokenElevation.Standard),
                    _ => ProbeReading<TokenElevation>.Failed("an unknown elevation type"),
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
#pragma warning disable CA1031 // A failed probe is a state of the check.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ProbeReading<TokenElevation>.Failed(ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    public ProbeReading<bool> ReadEventLogReadersMembership()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return ProbeReading<bool>.Ok(new WindowsPrincipal(identity).IsInRole(EventLogReaders));
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ProbeReading<bool>.Failed(ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>A query that can match nothing (event id 0), so opening the channel is all it does.</summary>
    public ProbeReading<bool> ReadSecurityChannelOpens()
    {
        try
        {
            var query = new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=0)]]");
            using var reader = new EventLogReader(query);
            using var first = reader.ReadEvent();
            return ProbeReading<bool>.Ok(true);
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeReading<bool>.Ok(false);
        }
        catch (EventLogException ex) when (ex.HResult == unchecked((int)0x80070005) || ex.InnerException is Win32Exception { NativeErrorCode: ErrorAccessDenied })
        {
            return ProbeReading<bool>.Ok(false);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ProbeReading<bool>.Failed(ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    public ProbeReading<int?> ReadCommandLineAudit()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(AuditKey, writable: false);
            if (key?.GetValue(AuditValue) is not { } value)
            {
                return ProbeReading<int?>.Ok(null);
            }

            return key.GetValueKind(AuditValue) == RegistryValueKind.DWord && value is int number
                ? ProbeReading<int?>.Ok(number)
                : ProbeReading<int?>.Failed("the value is not a DWORD");
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ProbeReading<int?>.Failed(ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int length, out int returnLength);
}
