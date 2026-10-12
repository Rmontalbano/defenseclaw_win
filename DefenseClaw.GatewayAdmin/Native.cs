using System.Runtime.InteropServices;

namespace DefenseClaw.GatewayAdmin;

/// <summary>The Win32 calls the helper makes, in one place. Everything here is reached only through the interfaces of <see cref="HelperPorts"/>'s file.</summary>
internal static class Native
{
    public const uint TokenQuery = 0x0008;
    public const uint TokenAdjustDefault = 0x0080;
    public const uint TokenAdjustPrivileges = 0x0020;
    public const int TokenUser = 1;
    public const int TokenOwner = 4;
    public const int TokenElevationType = 18;
    public const int TokenElevation = 20;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    public const uint WriteOwner = 0x00080000;
    public const uint FileShareAll = 7;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const int SeFileObject = 1;
    public const uint OwnerSecurityInformation = 1;
    public const uint SePrivilegeEnabled = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenOwnerInfo
    {
        public IntPtr Owner;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int length, IntPtr previous, IntPtr returned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int SetSecurityInfo(IntPtr handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandle(IntPtr file, out ByHandleFileInformation info);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>Reads a fixed-size DWORD token property.</summary>
    public static int ReadTokenDword(IntPtr token, int infoClass)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(token, infoClass, buffer, sizeof(int), out _))
            {
                throw new InvalidOperationException("GetTokenInformation failed (" + Marshal.GetLastWin32Error() + ").");
            }

            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The user SID of a token, as a string.</summary>
    public static string ReadTokenUserSid(IntPtr token)
    {
        _ = GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
        {
            throw new InvalidOperationException("The token's user could not be read.");
        }

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(token, TokenUser, buffer, needed, out _))
            {
                throw new InvalidOperationException("GetTokenInformation(TokenUser) failed (" + Marshal.GetLastWin32Error() + ").");
            }

            // TOKEN_USER begins with a SID_AND_ATTRIBUTES whose first member is the SID pointer.
            var sidPointer = Marshal.ReadIntPtr(buffer);
            return new System.Security.Principal.SecurityIdentifier(sidPointer).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
