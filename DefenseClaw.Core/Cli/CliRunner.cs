using System.Diagnostics;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// Raised when a secret would have been placed on the command line. Argv is visible to
/// every process on the box (Task Manager, WMI, ETW) and to DefenseClaw's own hook
/// scanners, so this is a hard failure rather than a warning.
/// </summary>
public sealed class SecretInArgumentException : InvalidOperationException
{
    public SecretInArgumentException(int argumentIndex)
        : base($"Refusing to run: argument at index {argumentIndex} contains a secret. " +
               "Pass secrets via the stdinSecret parameter instead — never on the command line.")
    {
        ArgumentIndex = argumentIndex;
    }

    public int ArgumentIndex { get; }
}

/// <summary>Thrown when the requested DefenseClaw executable is not on PATH or in the install dir.</summary>
public sealed class CliNotFoundException : FileNotFoundException
{
    public CliNotFoundException(string executableName, IEnumerable<string> probed)
        : base($"Could not find '{executableName}'. Probed: {string.Join("; ", probed)}")
    {
        ExecutableName = executableName;
    }

    public string ExecutableName { get; }
}

/// <summary>
/// Runs the DefenseClaw CLIs and records every invocation.
/// <para>
/// This is the app's only write path: the GUI never edits DefenseClaw state directly, so
/// each mutation is a subprocess whose exact argv, live output and exit code land in
/// <see cref="Activity"/> for the Activity panel to show.
/// </para>
/// <para>
/// Secrets are accepted only through <c>stdinSecret</c>. Anything that would put one in
/// argv throws <see cref="SecretInArgumentException"/>.
/// </para>
/// </summary>
public sealed class CliRunner
{
    /// <summary>
    /// Substring checks only kick in for secrets at least this long — shorter values
    /// produce false positives against ordinary arguments.
    /// </summary>
    private const int MinimumSubstringGuardLength = 8;

    private readonly DefenseClawPaths _paths;
    private readonly object _gate = new();
    private readonly LinkedList<CliInvocation> _activity = new();
    private readonly List<SecretValue> _knownSecrets = new();

    public CliRunner(DefenseClawPaths paths, int activityCapacity = 200)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ActivityCapacity = activityCapacity > 0 ? activityCapacity : 200;
    }

    /// <summary>Bounded ring size for <see cref="Activity"/>.</summary>
    public int ActivityCapacity { get; }

    /// <summary>Fires per output line, as it arrives, for live wizard consoles.</summary>
    public event EventHandler<CliOutputLine>? OutputReceived;

    public event EventHandler<CliInvocation>? InvocationStarted;

    public event EventHandler<CliInvocation>? InvocationCompleted;

    /// <summary>Most recent invocations, newest first, capped at <see cref="ActivityCapacity"/>.</summary>
    public IReadOnlyList<CliInvocation> Activity
    {
        get
        {
            lock (_gate)
            {
                return _activity.ToArray();
            }
        }
    }

    /// <summary>
    /// Registers a secret the runner should refuse to see in argv and should scrub out of
    /// captured output — e.g. the resolved gateway token.
    /// </summary>
    public void RegisterSecret(SecretValue secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            _knownSecrets.Add(secret);
        }
    }

    public void ClearActivity()
    {
        lock (_gate)
        {
            _activity.Clear();
        }
    }

    /// <summary>Runs <c>defenseclaw</c>.</summary>
    public Task<CliInvocation> RunAsync(
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default) =>
        RunNamedAsync("defenseclaw", args, stdinSecret, cancellationToken);

    /// <summary>Runs <c>defenseclaw-gateway</c>.</summary>
    public Task<CliInvocation> RunGatewayAsync(
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default) =>
        RunNamedAsync("defenseclaw-gateway", args, stdinSecret, cancellationToken);

    /// <summary>Resolves <paramref name="executableName"/> through PATH then the install bin dir.</summary>
    public Task<CliInvocation> RunNamedAsync(
        string executableName,
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.FindExecutable(executableName)
            ?? throw new CliNotFoundException(executableName, _paths.CandidatesFor(executableName));

        return RunExecutableAsync(path, args, stdinSecret, cancellationToken);
    }

    /// <summary>
    /// Runs an explicit executable path. Uses <see cref="ProcessStartInfo.ArgumentList"/>,
    /// so no shell is involved and no quoting is required or performed.
    /// </summary>
    public async Task<CliInvocation> RunExecutableAsync(
        string executablePath,
        IReadOnlyList<string> args,
        SecretValue? stdinSecret = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(args);

        GuardArguments(args, stdinSecret);

        var invocation = new CliInvocation(executablePath, args.ToArray(), DateTimeOffset.UtcNow)
        {
            UsedStdinSecret = stdinSecret is { IsEmpty: false },
        };

        Record(invocation);
        InvocationStarted?.Invoke(this, invocation);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = _paths.DataDirectoryExists ? _paths.DataDirectory : Environment.CurrentDirectory,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // Both streams complete asynchronously; wait for them so no trailing output is lost.
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) => Capture(invocation, CliStream.StandardOutput, e.Data, stdoutDone);
        process.ErrorDataReceived += (_, e) => Capture(invocation, CliStream.StandardError, e.Data, stderrDone);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            invocation.FailureReason = ex.Message;
            invocation.FinishedAt = DateTimeOffset.UtcNow;
            InvocationCompleted?.Invoke(this, invocation);
            return invocation;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            if (stdinSecret is { IsEmpty: false })
            {
                await process.StandardInput.WriteAsync(stdinSecret.Reveal().AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The child may have exited before reading stdin; not fatal.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            invocation.ExitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            invocation.FailureReason = "cancelled";
            TryKill(process);
        }
        catch (TimeoutException)
        {
            // Streams did not close cleanly; the exit code is still meaningful.
            invocation.ExitCode = process.HasExited ? process.ExitCode : null;
        }

        invocation.FinishedAt = DateTimeOffset.UtcNow;
        InvocationCompleted?.Invoke(this, invocation);
        return invocation;
    }

    /// <summary>
    /// Rejects any argument that carries a secret. Checks the per-call
    /// <paramref name="stdinSecret"/> plus everything passed to
    /// <see cref="RegisterSecret"/>.
    /// </summary>
    private void GuardArguments(IReadOnlyList<string> args, SecretValue? stdinSecret)
    {
        List<SecretValue> secrets;
        lock (_gate)
        {
            secrets = new List<SecretValue>(_knownSecrets);
        }

        if (stdinSecret is not null)
        {
            secrets.Add(stdinSecret);
        }

        if (secrets.Count == 0)
        {
            return;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg))
            {
                continue;
            }

            foreach (var secret in secrets)
            {
                if (secret.IsEmpty)
                {
                    continue;
                }

                if (secret.Matches(arg) ||
                    (secret.Length >= MinimumSubstringGuardLength && secret.AppearsIn(arg)))
                {
                    // Note: the message names only the index — never the value.
                    throw new SecretInArgumentException(i);
                }
            }
        }
    }

    private void Capture(CliInvocation invocation, CliStream stream, string? data, TaskCompletionSource completion)
    {
        if (data is null)
        {
            completion.TrySetResult();
            return;
        }

        var line = new CliOutputLine(DateTimeOffset.UtcNow, stream, Scrub(data));
        invocation.Append(line);
        OutputReceived?.Invoke(this, line);
    }

    /// <summary>Removes any registered secret that leaked into subprocess output.</summary>
    private string Scrub(string text)
    {
        List<SecretValue> secrets;
        lock (_gate)
        {
            if (_knownSecrets.Count == 0)
            {
                return text;
            }

            secrets = new List<SecretValue>(_knownSecrets);
        }

        foreach (var secret in secrets)
        {
            text = secret.Scrub(text);
        }

        return text;
    }

    private void Record(CliInvocation invocation)
    {
        lock (_gate)
        {
            _activity.AddFirst(invocation);
            while (_activity.Count > ActivityCapacity)
            {
                _activity.RemoveLast();
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}
