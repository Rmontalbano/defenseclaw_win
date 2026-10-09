using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// A stand-in for <c>defenseclaw</c> that answers the Setup editors' reads from the synthetic fixtures written from the 0.8.10 source
/// (<c>Fixtures/setup-editors</c>), and records every command it is handed. It never starts a process. Like the real runner it applies a run's
/// <see cref="CliRunOptions.OutputLineFilter"/> to each line it "prints", so what a test sees in an invocation is what Activity would hold.
/// A change goes through the review's <c>RunStep</c> seam, which a test sets to <see cref="Step"/>.
/// </summary>
internal sealed class FakeSetupCli
{
    public string Observability { get; set; } = Fixture("observability-list.synthetic.json");

    public string Webhooks { get; set; } = Fixture("webhook-list.synthetic.json");

    public string TrustedPaths { get; set; } = Fixture("trusted-paths-list.synthetic.json");

    public string WebhookShow { get; set; } = Fixture("webhook-show.synthetic.json");

    /// <summary>Exit code of every read. Non-zero answers with an error on stderr.</summary>
    public int ReadExitCode { get; set; }

    /// <summary>What a failing read prints to stderr.</summary>
    public string ReadError { get; set; } = "Error: could not load config.yaml";

    /// <summary>Makes every read throw the "CLI not found" exception.</summary>
    public bool CliMissing { get; set; }

    /// <summary>Every read the editors were handed, in order.</summary>
    public List<string[]> Ran { get; } = new();

    /// <summary>The options of each read, in the same order as <see cref="Ran"/>.</summary>
    public List<CliRunOptions> RanOptions { get; } = new();

    /// <summary>Every change a confirmed review would have run, in order.</summary>
    public List<string[]> Applied { get; } = new();

    /// <summary>The options of each change, in the same order as <see cref="Applied"/>.</summary>
    public List<CliRunOptions?> AppliedOptions { get; } = new();

    /// <summary>The exit code a change answers with.</summary>
    public int ApplyExitCode { get; set; }

    /// <summary>What a change prints (stdout); null prints a short confirmation.</summary>
    public string? ApplyOutput { get; set; }

    /// <summary>What a change prints to stderr when it fails.</summary>
    public string ApplyError { get; set; } = "Error: the destination test failed";

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "setup-editors", name));

    /// <summary>The seam <c>SetupResourceViewModel.RunCli</c> takes.</summary>
    public Task<CliInvocation> Run(IReadOnlyList<string> argv, CliRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Ran.Add(argv.ToArray());
        RanOptions.Add(options);

        if (CliMissing)
        {
            throw new CliNotFoundException("defenseclaw", ["C:\\nowhere"]);
        }

        if (ReadExitCode != 0)
        {
            return Task.FromResult(Result(argv, ReadExitCode, options, stderr: ReadError));
        }

        var text = Answer(argv);
        return Task.FromResult(text is null
            ? Result(argv, 2, options, stderr: "Error: unexpected command")
            : Result(argv, 0, options, stdout: text));
    }

    /// <summary>The seam <c>DiscoverActionReview.RunStep</c> takes: records the change and answers it.</summary>
    public Task<CliInvocation> Step(string executable, IReadOnlyList<string> argv, CliRunOptions? options)
    {
        Assert.Equal("defenseclaw", executable);
        Applied.Add(argv.ToArray());
        AppliedOptions.Add(options);

        return Task.FromResult(ApplyExitCode != 0
            ? Result(argv, ApplyExitCode, options ?? CliRunOptions.Default, stderr: ApplyError)
            : Result(argv, 0, options ?? CliRunOptions.Default, stdout: ApplyOutput ?? "  done"));
    }

    private string? Answer(IReadOnlyList<string> argv)
    {
        if (!SetupResourceArgv.IsRead(argv))
        {
            return null;
        }

        return argv[2] switch
        {
            "show" => WebhookShow,
            _ => argv[1] switch
            {
                "observability" => Observability,
                "webhook" => Webhooks,
                "trusted-paths" => TrustedPaths,
                _ => null,
            },
        };
    }

    /// <summary>A finished invocation with the given output, every line passed through the run's filter as the runner does.</summary>
    public static CliInvocation Result(IReadOnlyList<string> argv, int exit, CliRunOptions options, string stdout = "", string stderr = "")
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        string Filter(string line) => options.OutputLineFilter is { } filter ? filter(line) : line;

        foreach (var line in stdout.Split('\n'))
        {
            if (line.Length > 0)
            {
                InvocationFactory.Append(invocation, Filter(line.TrimEnd('\r')));
            }
        }

        if (stderr.Length > 0)
        {
            InvocationFactory.Append(invocation, Filter(stderr), CliStream.StandardError);
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }
}
