using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin;

/// <summary>Who started the helper and in what state, as Windows reports it. Nothing in it comes from the command line.</summary>
/// <param name="UserSid">The SID of the account the helper runs as.</param>
/// <param name="UserName">The account name without its domain.</param>
/// <param name="ProfileDirectory">That account's profile folder (read from the system, cross-checked in two places).</param>
/// <param name="IsElevated">The token is elevated.</param>
/// <param name="IsSplitTokenElevation">The token is the full half of a split token (the user's own limited token has a linked elevated one): the account that asked for elevation is the account that got it. False when another administrator's credentials were typed into the prompt, or UAC is off.</param>
/// <param name="ParentUserSid">The SID of the account the parent process runs as; null when the parent is gone or cannot be read.</param>
/// <param name="ImagePath">The file the helper is running from.</param>
/// <param name="SystemRoot">The Windows folder.</param>
/// <param name="ProgramFiles">The 64-bit Program Files folder.</param>
/// <param name="EnvironmentNames">The names of the variables the helper was started with.</param>
internal sealed record CallerIdentity(
    string UserSid,
    string UserName,
    string ProfileDirectory,
    bool IsElevated,
    bool IsSplitTokenElevation,
    string? ParentUserSid,
    string ImagePath,
    string SystemRoot,
    string ProgramFiles,
    IReadOnlyList<string> EnvironmentNames);

internal interface ICallerIdentity
{
    /// <summary>Reads the identity; throws <see cref="InvalidOperationException"/> with a reason when it cannot be established.</summary>
    CallerIdentity Read();
}

/// <summary>The helper's own yes/no, put to the operator in a window an unelevated program cannot click (UIPI).</summary>
internal interface IConsent
{
    bool Confirm(string title, string text);
}

internal sealed record ProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    int MaxOutputBytes);

internal sealed record ProcessOutcome(bool Started, int? ExitCode, bool TimedOut, IReadOnlyList<string> Lines, string? Failure)
{
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

internal interface IProcessRunner
{
    /// <summary>Runs the executable with exactly this argument list and exactly this environment, and waits.</summary>
    ProcessOutcome Run(ProcessRequest request);
}

/// <summary>Sets the default owner of this process's token for as long as the returned scope lives, so files the gateway creates are owned by the user.</summary>
internal interface ITokenOwner
{
    IDisposable Use(string ownerSid);
}

internal interface IRunningGateway
{
    /// <summary>True when the data folder's <c>gateway.pid</c> names a live gateway process.</summary>
    bool IsRunning(AdminLayout layout);
}

internal interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>What the repair walker needs of a folder tree; the real one never follows a link.</summary>
internal interface IOwnerFileSystem
{
    OwnerNode? Probe(string path);

    /// <summary>The full paths of the entries directly inside <paramref name="directory"/> (not following links).</summary>
    IReadOnlyList<string> Children(string directory);

    /// <summary>Sets the owner of one object (opened without following a link) to <paramref name="ownerSid"/>. Throws on failure.</summary>
    void SetOwner(string path, string ownerSid);
}

internal sealed record OwnerNode(bool IsDirectory, bool IsReparsePoint, string? OwnerSid);

internal sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>What one helper run came to.</summary>
internal sealed record HelperResult(int ExitCode, IReadOnlyList<string> Lines);
