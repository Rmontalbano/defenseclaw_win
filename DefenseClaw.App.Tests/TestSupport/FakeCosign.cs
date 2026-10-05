using DefenseClaw.App.Services.Updates;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A stand-in for cosign that starts no process: it answers <c>version</c> and <c>verify-blob</c> from canned values and records every call, so a test can
/// read exactly what the verifier asked and decide what cosign "said". <see cref="OnVerify"/> sees the call while its files still exist (the verifier
/// removes them afterwards).
/// </summary>
internal sealed class FakeCosign
{
    /// <summary>An absolute path that exists nowhere: the fake never starts it.</summary>
    public const string Path = @"C:\fake-tools\cosign.exe";

    public List<(string Path, string[] Argv, TimeSpan Timeout)> Calls { get; } = new();

    /// <summary>The line <c>cosign version</c> prints.</summary>
    public string VersionLine { get; set; } = "GitVersion:    v2.6.3";

    /// <summary>Set to make <c>cosign version</c> not finish (the reason), or to a non-zero exit code.</summary>
    public string? VersionFailure { get; set; }

    public int VersionExitCode { get; set; }

    public int VerifyExitCode { get; set; }

    public string[] VerifyOutput { get; set; } = Array.Empty<string>();

    /// <summary>Set to make <c>verify-blob</c> not finish: no exit code, and this as the reason.</summary>
    public string? VerifyFailure { get; set; }

    /// <summary>False makes an unfinished <c>verify-blob</c> one that never started (the program could not be launched) rather than one that timed out.</summary>
    public bool VerifyStarted { get; set; } = true;

    /// <summary>Called with the argv of each <c>verify-blob</c>, before it answers.</summary>
    public Action<string[]>? OnVerify { get; set; }

    public int VerifyCalls => Calls.Count(c => c.Argv.Length > 0 && c.Argv[0] == "verify-blob");

    public CosignExecutor Executor => (path, argv, timeout, cancellationToken) =>
    {
        var copy = argv.ToArray();
        lock (Calls)
        {
            Calls.Add((path, copy, timeout));
        }

        if (copy.Length > 0 && copy[0] == "version")
        {
            return Task.FromResult(new CosignRun(
                VersionFailure is null ? VersionExitCode : null,
                VersionFailure,
                new[] { "cosign: A tool for Container Signing, Verification and Storage in an OCI registry.", string.Empty, VersionLine, "GitCommit:     0000" }));
        }

        OnVerify?.Invoke(copy);
        return Task.FromResult(new CosignRun(VerifyFailure is null ? VerifyExitCode : null, VerifyFailure, VerifyOutput, VerifyStarted));
    };

    public ChecksumsSignatureVerifier VerifierOver(HttpMessageHandler http, string scratchRoot) =>
        new(new HttpClient(http), scratchRoot, Executor);
}
