using System.Text.Json;
using System.Text.Json.Nodes;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Redaction;

namespace DefenseClaw.App.Tests.Redaction;

/// <summary>
/// A stand-in for <c>defenseclaw</c> that answers the redaction commands from the synthetic fixtures made from the Docker capture of source
/// commit 95159fd (<c>Fixtures/runtime-95159fd/redaction</c>), and records every command it is handed. It never starts a process, and a command
/// that is not a read or a preview is not answered here at all: the apply goes through the review's <c>RunStep</c> seam, which a test sets to
/// <see cref="Step"/>.
/// </summary>
internal sealed class FakeRedactionCli
{
    /// <summary>The status the next read answers.</summary>
    public string Status { get; set; } = Fixture("status-fresh.json");

    public string ProfileList { get; set; } = Fixture("profile-list.json");

    /// <summary>Routes by destination name; a destination not here answers with none.</summary>
    public Dictionary<string, string> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>Exit code of every read and preview. Non-zero answers with an error on stderr.</summary>
    public int ExitCode { get; set; }

    /// <summary>Replaces the answer to a preview (the text it prints).</summary>
    public Func<RedactionOperation, string>? PreviewAnswer { get; set; }

    /// <summary>Every command a read or a preview was asked to run, in order.</summary>
    public List<string[]> Ran { get; } = new();

    /// <summary>Every command the review would have run (a confirmed apply), in order.</summary>
    public List<string[]> Applied { get; } = new();

    /// <summary>The exit code an apply answers with.</summary>
    public int ApplyExitCode { get; set; }

    /// <summary>Replaces the answer to an apply (the text it prints); null answers with the preview turned into an applied result.</summary>
    public Func<RedactionOperation, string>? ApplyAnswer { get; set; }

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-95159fd", "redaction", name));

    /// <summary>The seam <c>RedactionViewModel.RunCli</c> takes.</summary>
    public Task<CliInvocation> Run(IReadOnlyList<string> argv, CliRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Ran.Add(argv.ToArray());

        if (ExitCode != 0)
        {
            return Task.FromResult(Result(argv, ExitCode, stderr: "Error: no effective destination named 'x'"));
        }

        var text = Answer(argv);
        return Task.FromResult(text is null
            ? Result(argv, 2, stderr: "Error: unexpected command")
            : Result(argv, 0, stdout: text));
    }

    /// <summary>The seam <c>DiscoverActionReview.RunStep</c> takes: records the apply and answers with the dry-run result turned into an applied one.</summary>
    public Task<CliInvocation> Step(string executable, IReadOnlyList<string> argv, CliRunOptions? options)
    {
        _ = options;
        Assert.Equal("defenseclaw", executable);
        Applied.Add(argv.ToArray());

        if (ApplyExitCode != 0)
        {
            return Task.FromResult(Result(argv, ApplyExitCode, stderr: "Error: configuration was written but the verified effective plan differs from the preview; restore the backup to roll back"));
        }

        var operation = RedactionArgv.Identify(argv)!.Value;
        return Task.FromResult(Result(argv, 0, stdout: ApplyAnswer is { } custom ? custom(operation) : AppliedText(operation)));
    }

    /// <summary>What an apply prints when the configuration already said what it was asked to say: nothing changed, nothing written.</summary>
    public static string NothingToApplyText()
    {
        var node = JsonNode.Parse(Fixture("dry-defaults-reset.json"))!.AsObject();
        node["dry_run"] = false;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private string? Answer(IReadOnlyList<string> argv)
    {
        if (RedactionArgv.IsRead(argv))
        {
            return RedactionArgv.Identify(argv) switch
            {
                RedactionOperation.Status => Status,
                RedactionOperation.ProfileList => ProfileList,
                RedactionOperation.ProfileShow => Fixture(argv[4] switch { "sensitive" => "profile-show-sensitive.json", "strict" => "profile-show-strict.json", _ => "profile-show-custom.json" }),
                RedactionOperation.RouteList => Routes.GetValueOrDefault(argv[4]) ?? Fixture("route-list-empty.json"),
                RedactionOperation.BucketList => Fixture("bucket-list.txt"),
                RedactionOperation.DestinationShow => Fixture(argv[4] == "local-sqlite" ? "destination-show-local.txt" : "destination-show-otlp.txt"),
                _ => null,
            };
        }

        if (RedactionArgv.IsPreview(argv))
        {
            var operation = RedactionArgv.Identify(argv)!.Value;
            return PreviewAnswer is { } custom ? custom(operation) : Fixture(PreviewFile(operation));
        }

        return null;
    }

    /// <summary>The fixture of the capture's dry run for an operation.</summary>
    public static string PreviewFile(RedactionOperation operation) => operation switch
    {
        RedactionOperation.RemoveAll => "dry-remove-all.json",
        RedactionOperation.ApplyEverywhere => "dry-apply-everywhere-sensitive.json",
        RedactionOperation.ApplyDefaults => "dry-apply-defaults-strict.json",
        RedactionOperation.DefaultsSet => "dry-defaults-set.json",
        RedactionOperation.DefaultsReset => "dry-defaults-reset.json",
        RedactionOperation.BucketSet => "dry-bucket-set.json",
        RedactionOperation.BucketReset => "dry-bucket-reset.json",
        RedactionOperation.ProfileSet => "dry-profile-set.json",
        RedactionOperation.ProfileRemove => "dry-profile-remove.json",
        RedactionOperation.DestinationSend => "dry-destination-send.json",
        RedactionOperation.DestinationInherit => "dry-destination-inherit.json",
        RedactionOperation.RouteAdd => "dry-route-add.json",
        RedactionOperation.RouteSet => "dry-route-set.json",
        RedactionOperation.RouteMove => "dry-route-move.json",
        RedactionOperation.RouteRemove => "dry-route-remove.json",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "no dry run for a read"),
    };

    /// <summary>The preview's JSON with what an apply changes: it was written (with a backup) and the written plan is the previewed one.</summary>
    public string AppliedText(RedactionOperation operation)
    {
        var node = JsonNode.Parse(PreviewAnswer is { } custom ? custom(operation) : Fixture(PreviewFile(operation)))!.AsObject();
        node["dry_run"] = false;
        node["applied"] = node["changed"]!.GetValue<bool>();
        node["verified_plan_digest"] = node["after_plan_digest"]!.GetValue<string>();
        node["backup_path"] = @"C:\Users\operator\.defenseclaw\backups\config.yaml.before-redaction-1790000000000000001";
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>A finished invocation with the given output.</summary>
    public static CliInvocation Result(IReadOnlyList<string> argv, int exit, string stdout = "", string stderr = "")
    {
        var invocation = InvocationFactory.Create(retainFullOutput: true, argv.ToArray());
        foreach (var line in stdout.Split('\n'))
        {
            if (line.Length > 0)
            {
                InvocationFactory.Append(invocation, line.TrimEnd('\r'));
            }
        }

        if (stderr.Length > 0)
        {
            InvocationFactory.Append(invocation, stderr, CliStream.StandardError);
        }

        InvocationFactory.Finish(invocation, exit);
        return invocation;
    }
}
