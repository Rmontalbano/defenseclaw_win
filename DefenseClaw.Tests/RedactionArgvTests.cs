using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Redaction;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The command lines of the advanced redaction editor (CUST-295): the 21 operations against the Docker capture of DefenseClaw source commit
/// 95159fd (<c>Fixtures/runtime-95159fd/redaction/captured-commands.txt</c>, 14 mutating commands dry-run with <c>--json</c>) and its command tree
/// (<c>cli/cli-tree.json</c>). Nothing here starts a process.
/// </summary>
public sealed class RedactionArgvTests
{
    // ------------------------------------------------------------------ the inputs behind each captured command

    private static readonly RedactionInputs Sensitive = new() { Profile = "sensitive" };

    /// <summary>The operation and inputs that make each command of <c>captured-commands.txt</c>.</summary>
    private static readonly Dictionary<string, (RedactionOperation Operation, RedactionInputs Inputs)> Captured = new()
    {
        ["status"] = (RedactionOperation.Status, new()),
        ["profile-list"] = (RedactionOperation.ProfileList, new()),
        ["profile-show-sensitive"] = (RedactionOperation.ProfileShow, new() { ProfileName = "sensitive" }),
        ["profile-show-custom"] = (RedactionOperation.ProfileShow, new() { ProfileName = "example-profile" }),
        ["route-list"] = (RedactionOperation.RouteList, new() { Destination = "example-otlp" }),
        ["bucket-list"] = (RedactionOperation.BucketList, new()),
        ["destination-show"] = (RedactionOperation.DestinationShow, new() { Destination = "example-otlp" }),
        ["dry-apply-everywhere-sensitive"] = (RedactionOperation.ApplyEverywhere, Sensitive),
        ["dry-apply-defaults-strict"] = (RedactionOperation.ApplyDefaults, new() { Profile = "strict" }),
        ["dry-bucket-set"] = (RedactionOperation.BucketSet, new() { Bucket = "model.io", Profile = "content", CollectMetrics = false }),
        ["dry-bucket-reset"] = (RedactionOperation.BucketReset, new() { Bucket = "model.io" }),
        ["dry-defaults-set"] = (RedactionOperation.DefaultsSet, new() { Profile = "sensitive", CollectLogs = true }),
        ["dry-defaults-reset"] = (RedactionOperation.DefaultsReset, new()),
        ["dry-destination-send"] = (RedactionOperation.DestinationSend, new() { Destination = "example-otlp", Signals = ["logs"], Buckets = ["security.finding"], Profile = "sensitive" }),
        ["dry-destination-inherit"] = (RedactionOperation.DestinationInherit, new() { Destination = "example-otlp" }),
        ["dry-profile-set"] = (RedactionOperation.ProfileSet, new() { ProfileName = "example-profile", IsNewProfile = true, Extends = "sensitive", Detectors = ["pii"], FieldModes = [new("path", "hash")] }),
        ["dry-profile-remove"] = (RedactionOperation.ProfileRemove, new() { ProfileName = "example-profile" }),
        ["dry-route-add"] = (RedactionOperation.RouteAdd, new() { Destination = "example-otlp", RouteName = "example-route", Signals = ["logs"], Buckets = ["tool.activity"], MinSeverity = "HIGH", RouteAction = "drop" }),
        ["dry-route-set"] = (RedactionOperation.RouteSet, new() { Destination = "example-otlp", RouteName = "example-route", Signals = ["logs"], Buckets = ["tool.activity"], RouteAction = "send", Profile = "strict" }),
        ["dry-route-move"] = (RedactionOperation.RouteMove, new() { Destination = "example-otlp", RouteName = "example-route", Position = 1 }),
        ["dry-route-remove"] = (RedactionOperation.RouteRemove, new() { Destination = "example-otlp", RouteName = "example-route" }),
        ["dry-remove-all"] = (RedactionOperation.RemoveAll, new()),
    };

    private static IReadOnlyList<(string Name, string[] Argv)> CapturedCommands() =>
        RuntimeFixtures.Read("redaction/captured-commands.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static l => !l.StartsWith('#'))
            .Select(static l => l.Split('\t'))
            .Select(static p => (p[0], p[1].Split(' ')))
            .ToArray();

    private static readonly string[] ModeFlags = ["--yes", "--dry-run", "--json", "--restart", "--no-restart"];

    private static string[] WithoutMode(IEnumerable<string> argv) => argv.Where(static t => !ModeFlags.Contains(t)).ToArray();

    // ------------------------------------------------------------------ the 21 operations

    [Fact]
    public void There_are_21_operations_and_14_commands_that_change_configuration()
    {
        Assert.Equal(21, RedactionOperations.All.Count);
        Assert.Equal(RedactionOperations.All.Count, RedactionOperations.All.Select(static o => o.Operation).Distinct().Count());
        Assert.Equal(Enum.GetValues<RedactionOperation>().Length, RedactionOperations.All.Count);

        // apply is one command with two scopes: 15 operations, 14 commands (the capture dry-ran each of the 14, apply twice).
        Assert.Equal(15, RedactionOperations.Mutations.Count());
        Assert.Equal(14, RedactionOperations.Mutations.Select(static o => string.Join(' ', o.Path)).Distinct().Count());
        Assert.Equal(6, RedactionOperations.Reads.Count());
        Assert.All(RedactionOperations.Mutations, static o => Assert.True(o.Json, o.Title));
    }

    [Fact]
    public void Every_operation_of_the_capture_is_built_the_way_the_capture_ran_it()
    {
        var commands = CapturedCommands();
        Assert.True(commands.Count >= 23);

        var checkedCount = 0;
        foreach (var (name, argv) in commands)
        {
            if (name.StartsWith("applied-", StringComparison.Ordinal) || name == "status-text")
            {
                continue; // run for real inside the container / the plain-text status: covered below
            }

            Assert.True(Captured.TryGetValue(name, out var recipe), $"no recipe for the captured command '{name}'");
            var info = RedactionOperations.Info(recipe.Operation);
            var mine = info.IsMutation
                ? RedactionArgv.Preview(recipe.Operation, recipe.Inputs)
                : RedactionArgv.Read(recipe.Operation, recipe.Inputs);

            // The operation itself is the capture's, word for word and in order; only the mode flags differ (see the next test).
            Assert.Equal(WithoutMode(argv), WithoutMode(mine));
            checkedCount++;
        }

        Assert.Equal(Captured.Count, checkedCount);
    }

    [Fact]
    public void An_apply_is_the_captured_real_command_with_an_explicit_restart_choice()
    {
        var commands = CapturedCommands().ToDictionary(static c => c.Name, static c => c.Argv);

        var route = RedactionArgv.Apply(RedactionOperation.RouteAdd, Captured["dry-route-add"].Inputs, restart: false);
        Assert.Equal(WithoutMode(commands["applied-route-add"]), WithoutMode(route));
        Assert.Equal(["--yes", "--json", "--no-restart"], route[^3..]);

        var profile = RedactionArgv.Apply(RedactionOperation.ProfileSet, Captured["dry-profile-set"].Inputs, restart: true);
        Assert.Equal(WithoutMode(commands["applied-profile-set"]), WithoutMode(profile));
        Assert.Equal(["--yes", "--json", "--restart"], profile[^3..]);
    }

    // ------------------------------------------------------------------ a preview can neither write nor restart

    public static TheoryData<string> MutatingOperations()
    {
        var data = new TheoryData<string>();
        foreach (var o in RedactionOperations.Mutations)
        {
            data.Add(o.Operation.ToString());
        }

        return data;
    }

    private static (RedactionOperation Operation, RedactionInputs Inputs) Sample(string operation)
    {
        var op = Enum.Parse<RedactionOperation>(operation);
        var name = op switch
        {
            RedactionOperation.RemoveAll => "dry-remove-all",
            RedactionOperation.ApplyEverywhere => "dry-apply-everywhere-sensitive",
            RedactionOperation.ApplyDefaults => "dry-apply-defaults-strict",
            RedactionOperation.DefaultsSet => "dry-defaults-set",
            RedactionOperation.DefaultsReset => "dry-defaults-reset",
            RedactionOperation.BucketSet => "dry-bucket-set",
            RedactionOperation.BucketReset => "dry-bucket-reset",
            RedactionOperation.ProfileSet => "dry-profile-set",
            RedactionOperation.ProfileRemove => "dry-profile-remove",
            RedactionOperation.DestinationSend => "dry-destination-send",
            RedactionOperation.DestinationInherit => "dry-destination-inherit",
            RedactionOperation.RouteAdd => "dry-route-add",
            RedactionOperation.RouteSet => "dry-route-set",
            RedactionOperation.RouteMove => "dry-route-move",
            _ => "dry-route-remove",
        };
        return Captured[name];
    }

    [Theory]
    [MemberData(nameof(MutatingOperations))]
    public void A_preview_never_writes_never_restarts_and_reads_as_a_read_only_command(string operation)
    {
        var (op, inputs) = Sample(operation);

        var preview = RedactionArgv.Preview(op, inputs);

        Assert.Equal("--dry-run", preview[^1]);
        Assert.Equal("--json", preview[^2]);
        Assert.DoesNotContain("--yes", preview);
        Assert.DoesNotContain("--restart", preview);
        Assert.DoesNotContain("--no-restart", preview);
        Assert.All(preview, static t => Assert.False(RedactionArgv.IsRestartFlag(t), t));
        Assert.True(RedactionArgv.IsPreview(preview));
        Assert.False(RedactionArgv.IsRead(preview));
        Assert.Equal(op, RedactionArgv.Identify(preview));

        // The shared classifier agrees: a standalone --dry-run makes it a preview, which is read-only.
        Assert.Equal(CommandTier.ReadOnly, CommandTiers.Classify(preview));
    }

    [Theory]
    [MemberData(nameof(MutatingOperations))]
    public void An_apply_asks_nothing_names_its_restart_and_is_never_taken_for_a_preview_or_a_read(string operation)
    {
        var (op, inputs) = Sample(operation);

        foreach (var restart in new[] { false, true })
        {
            var apply = RedactionArgv.Apply(op, inputs, restart);

            Assert.Contains("--yes", apply);
            Assert.Contains("--json", apply);
            Assert.DoesNotContain("--dry-run", apply);
            Assert.Equal(restart ? "--restart" : "--no-restart", apply[^1]);
            Assert.Single(apply, static t => RedactionArgv.IsRestartFlag(t));
            Assert.False(RedactionArgv.IsPreview(apply));
            Assert.False(RedactionArgv.IsRead(apply));
            Assert.Equal(op, RedactionArgv.Identify(apply));
            Assert.NotEqual(CommandTier.ReadOnly, CommandTiers.Classify(apply));
        }
    }

    [Fact]
    public void A_command_that_is_not_exactly_a_preview_is_refused_as_one()
    {
        var good = RedactionArgv.Preview(RedactionOperation.BucketSet, new() { Bucket = "model.io", Profile = "strict" });
        Assert.True(RedactionArgv.IsPreview(good));

        // each way of turning a preview into something else
        Assert.False(RedactionArgv.IsPreview([.. good, "--restart"]));
        Assert.False(RedactionArgv.IsPreview([.. good.Take(good.Length - 2), "--yes", "--json", "--dry-run"]));
        Assert.False(RedactionArgv.IsPreview([.. good.Take(good.Length - 1)])); // the --dry-run is gone
        Assert.False(RedactionArgv.IsPreview([.. good.Take(good.Length - 2), "--no-restart", "--json", "--dry-run"]));
        Assert.False(RedactionArgv.IsPreview(["setup", "redaction", "status", "--json", "--dry-run"])); // a read has no preview
        Assert.False(RedactionArgv.IsPreview(["setup", "observability", "add", "--json", "--dry-run"])); // not this command
        Assert.False(RedactionArgv.IsPreview(["setup", "redaction", "--json", "--dry-run"]));
        Assert.False(RedactionArgv.IsPreview([]));
    }

    [Fact]
    public void A_read_is_exactly_the_read_command_and_nothing_that_changes_configuration()
    {
        foreach (var info in RedactionOperations.Reads)
        {
            var inputs = info.Operation switch
            {
                RedactionOperation.ProfileShow => new RedactionInputs { ProfileName = "strict" },
                RedactionOperation.DestinationShow or RedactionOperation.RouteList => new RedactionInputs { Destination = "example-otlp" },
                _ => new RedactionInputs(),
            };

            var read = RedactionArgv.Read(info.Operation, inputs);
            Assert.True(RedactionArgv.IsRead(read), string.Join(' ', read));
            Assert.False(RedactionArgv.IsPreview(read));
        }

        Assert.False(RedactionArgv.IsRead(["setup", "redaction", "status"])); // no --json: not the shape the app reads
        Assert.False(RedactionArgv.IsRead(["setup", "redaction", "status", "--json", "--yes"]));
        Assert.False(RedactionArgv.IsRead(["setup", "redaction", "profile", "show", "--evil", "--json"]));
        Assert.False(RedactionArgv.IsRead(["setup", "redaction", "remove-all", "--json"]));
        Assert.False(RedactionArgv.IsRead(["setup", "redaction", "apply", "--scope", "defaults", "--profile", "none", "--yes", "--json"]));
        Assert.False(RedactionArgv.IsRead(["setup", "redaction", "route", "remove", "example-otlp", "x", "--yes", "--json"]));
        Assert.False(RedactionArgv.IsRead(["setup", "redaction", "bucket", "list", "--restart"]));
        Assert.False(RedactionArgv.IsRead(["setup", "observability", "list", "--json"]));
        Assert.False(RedactionArgv.IsRead(["guardrail", "status"]));
    }

    [Fact]
    public void A_read_has_no_preview_and_a_change_has_no_read()
    {
        Assert.Throws<InvalidOperationException>(() => RedactionArgv.Preview(RedactionOperation.Status, new()));
        Assert.Throws<InvalidOperationException>(() => RedactionArgv.Apply(RedactionOperation.RouteList, new() { Destination = "example-otlp" }, restart: false));
        Assert.Throws<InvalidOperationException>(() => RedactionArgv.Read(RedactionOperation.RemoveAll, new()));
    }

    // ------------------------------------------------------------------ the pinned command tree

    private sealed record Option(string[] Names, bool Flag);

    private sealed record Command(string Path, bool Group, string Help, Option[] Options);

    private static readonly Command[] Tree = LoadTree();

    private static Command[] LoadTree()
    {
        using var document = JsonDocument.Parse(RuntimeFixtures.Read("cli/cli-tree.json"));
        return document.RootElement.GetProperty("commands").EnumerateArray().Select(static c => new Command(
                c.GetProperty("path").GetString()!,
                c.GetProperty("group").GetBoolean(),
                c.GetProperty("help").GetString() ?? string.Empty,
                c.GetProperty("options").EnumerateArray().Select(static o => new Option(
                    o.GetProperty("names").EnumerateArray().Select(static n => n.GetString()!).ToArray(),
                    o.GetProperty("flag").GetBoolean())).ToArray()))
            .ToArray();
    }

    /// <summary>Null when <paramref name="argv"/> is a command of the pinned tree with only options it has (and a value for each that takes one).</summary>
    private static string? Check(IReadOnlyList<string> argv)
    {
        var command = Enumerable.Range(1, Math.Min(argv.Count, 4)).Reverse()
            .Select(n => Tree.FirstOrDefault(c => c.Path == string.Join(' ', argv.Take(n))))
            .FirstOrDefault(static c => c is not null);
        if (command is null)
        {
            return $"no such command: {string.Join(' ', argv)}";
        }

        var depth = command.Path.Split(' ').Length;
        for (var i = depth; i < argv.Count; i++)
        {
            var token = argv[i];
            if (!token.StartsWith('-') || token.Length < 2)
            {
                continue;
            }

            var option = command.Options.FirstOrDefault(o => o.Names.Contains(token, StringComparer.Ordinal));
            if (option is null)
            {
                return $"'{command.Path}' has no option {token}";
            }

            if (!option.Flag)
            {
                if (i + 1 >= argv.Count)
                {
                    return $"{token} needs a value";
                }

                i++;
            }
        }

        return null;
    }

    /// <summary>Inputs that fill every member an operation reads, so every flag it can emit is emitted.</summary>
    private static RedactionInputs Everything(RedactionOperation op) => new()
    {
        Profile = "strict",
        InheritProfile = op == RedactionOperation.BucketSet,
        Bucket = "model.io",
        Destination = "example-otlp",
        RouteName = "example-route",
        Position = 2,
        Signals = ["logs", "traces", "metrics"],
        Buckets = ["model.io", "tool.activity"],
        CollectLogs = true,
        CollectTraces = false,
        CollectMetrics = true,
        ProfileName = "example-profile",
        Extends = "content",
        Detectors = ["pii", "credentials", "secrets"],
        FieldModes = [new("path", "hash"), new("content", "inherit")],
        ReplaceWith = "sensitive",
        Sources = ["sidecar"],
        Connectors = ["claudecode"],
        ProducerActions = ["block"],
        EventNames = ["guardrail.verdict"],
        MinSeverity = "HIGH",
        RouteAction = "send",
    };

    [Fact]
    public void Every_command_the_editor_can_build_is_a_command_of_the_pinned_runtime_with_options_it_has()
    {
        foreach (var info in RedactionOperations.All)
        {
            // Bucket set takes a profile or inherit, not both: check the two shapes.
            var shapes = info.Operation == RedactionOperation.BucketSet
                ? new[] { Everything(info.Operation) with { InheritProfile = false }, Everything(info.Operation) with { Profile = string.Empty } }
                : [Everything(info.Operation)];

            foreach (var inputs in shapes)
            {
                var argvs = info.IsMutation
                    ? new[] { RedactionArgv.Preview(info.Operation, inputs), RedactionArgv.Apply(info.Operation, inputs, restart: true), RedactionArgv.Apply(info.Operation, inputs, restart: false) }
                    : [RedactionArgv.Read(info.Operation, inputs)];

                foreach (var argv in argvs)
                {
                    Assert.Null(Check(argv));
                }
            }
        }
    }

    [Fact]
    public void The_checker_notices_a_renamed_flag_and_a_missing_value()
    {
        Assert.Equal("'setup redaction apply' has no option --scopes", Check(["setup", "redaction", "apply", "--scopes", "defaults"]));
        Assert.Equal("--profile needs a value", Check(["setup", "redaction", "apply", "--profile"]));
        Assert.StartsWith("no such command", Check(["migrations", "status"]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_of_each_read_says_it_only_reads_and_each_change_has_a_preview_and_a_restart_choice()
    {
        foreach (var info in RedactionOperations.All.DistinctBy(static o => string.Join(' ', o.Path)))
        {
            var command = Tree.Single(c => c.Path == "setup redaction " + string.Join(' ', info.Path));
            var names = command.Options.SelectMany(static o => o.Names).ToHashSet(StringComparer.Ordinal);

            if (info.IsMutation)
            {
                Assert.Contains("--dry-run", names);
                Assert.Contains("--json", names);
                Assert.Contains("--yes", names);
                Assert.Contains("--restart", names);
                Assert.Contains("--no-restart", names);
            }
            else
            {
                Assert.DoesNotContain("--dry-run", names);
                Assert.DoesNotContain("--yes", names);
                Assert.DoesNotContain("--restart", names);
                Assert.True(command.Help.StartsWith("Show ", StringComparison.Ordinal) || command.Help.StartsWith("List ", StringComparison.Ordinal), command.Help);
            }
        }
    }

    // ------------------------------------------------------------------ what the editor refuses to build

    [Theory]
    [InlineData(RedactionOperation.ApplyEverywhere, "", "Choose a profile.")]
    [InlineData(RedactionOperation.ApplyDefaults, "--evil", "That profile's name cannot be passed to the CLI safely: use lower-case letters, digits, - and _.")]
    [InlineData(RedactionOperation.DefaultsSet, "", "Select at least one setting to change.")]
    [InlineData(RedactionOperation.BucketSet, "", "Choose a bucket.")]
    [InlineData(RedactionOperation.BucketReset, "", "Choose a bucket.")]
    [InlineData(RedactionOperation.ProfileShow, "", "Choose a profile.")]
    [InlineData(RedactionOperation.ProfileSet, "", "Enter a profile name.")]
    [InlineData(RedactionOperation.ProfileRemove, "", "Choose the custom profile to remove.")]
    [InlineData(RedactionOperation.DestinationShow, "", "Choose a destination.")]
    [InlineData(RedactionOperation.DestinationSend, "", "Choose a destination.")]
    [InlineData(RedactionOperation.RouteAdd, "", "Choose a destination.")]
    [InlineData(RedactionOperation.RouteMove, "", "Choose a destination.")]
    public void An_empty_form_says_what_is_missing_first(RedactionOperation op, string profile, string expected)
    {
        var inputs = new RedactionInputs { Profile = profile };
        var problems = RedactionArgv.Problems(op, inputs);

        Assert.Equal(expected, problems[0]);

        // and no command line is built from it, whichever shape the operation has
        if (RedactionOperations.Info(op).IsMutation)
        {
            Assert.Throws<ArgumentException>(() => RedactionArgv.Preview(op, inputs));
            Assert.Throws<ArgumentException>(() => RedactionArgv.Apply(op, inputs, restart: false));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => RedactionArgv.Read(op, inputs));
        }
    }

    [Fact]
    public void The_mac_s_messages_are_the_ones_the_form_gives()
    {
        var send = RedactionArgv.Problems(RedactionOperation.DestinationSend, new() { Destination = "example-otlp" });
        Assert.Contains("Select at least one signal.", send);
        Assert.Contains("Select at least one bucket or *.", send);

        Assert.Contains("Enter a route name.", RedactionArgv.Problems(RedactionOperation.RouteAdd, new() { Destination = "example-otlp", Signals = ["logs"] }));
        Assert.Contains("Position must be a positive integer.", RedactionArgv.Problems(RedactionOperation.RouteMove, new() { Destination = "example-otlp", RouteName = "r" }));
        Assert.Contains("Position must be a positive integer.", RedactionArgv.Problems(RedactionOperation.RouteMove, new() { Destination = "example-otlp", RouteName = "r", Position = 0 }));
        Assert.Contains("Position must be a positive integer.", RedactionArgv.Problems(RedactionOperation.RouteAdd, new() { Destination = "example-otlp", RouteName = "r", Signals = ["logs"], Position = -3 }));
        Assert.Empty(RedactionArgv.Problems(RedactionOperation.RouteAdd, new() { Destination = "example-otlp", RouteName = "r", Signals = ["logs"] })); // last is fine
    }

    [Fact]
    public void The_built_in_destinations_are_read_only_for_send_inherit_and_routes_but_can_be_shown()
    {
        foreach (var generated in new[] { "local-sqlite", "managed-enterprise-ai-defense" })
        {
            Assert.True(RedactionVocabulary.IsGeneratedDestination(generated));
            Assert.Empty(RedactionArgv.Problems(RedactionOperation.DestinationShow, new() { Destination = generated }));

            foreach (var op in new[] { RedactionOperation.DestinationSend, RedactionOperation.DestinationInherit, RedactionOperation.RouteList, RedactionOperation.RouteAdd, RedactionOperation.RouteSet, RedactionOperation.RouteMove, RedactionOperation.RouteRemove })
            {
                var inputs = new RedactionInputs { Destination = generated, RouteName = "r", Position = 1, Signals = ["logs"], Buckets = ["*"] };
                var problems = RedactionArgv.Problems(op, inputs);
                Assert.Contains(problems, p => p.Contains("generated and read-only", StringComparison.Ordinal) && p.Contains(generated, StringComparison.Ordinal));
            }
        }

        Assert.True(RedactionOperations.Info(RedactionOperation.RouteList).NeedsConfigurableDestination);
        Assert.False(RedactionOperations.Info(RedactionOperation.DestinationShow).NeedsConfigurableDestination);
        Assert.False(RedactionOperations.Info(RedactionOperation.BucketSet).NeedsConfigurableDestination);
    }

    [Fact]
    public void Buckets_signals_severities_and_profile_edits_are_held_to_the_vocabulary()
    {
        Assert.Contains("A bucket is one of the catalog's fourteen, or * for all of them.", RedactionArgv.Problems(RedactionOperation.DestinationSend, new() { Destination = "d", Signals = ["logs"], Buckets = ["not.a.bucket"] }));
        Assert.Contains("All buckets (*) cannot be combined with a named bucket.", RedactionArgv.Problems(RedactionOperation.DestinationSend, new() { Destination = "d", Signals = ["logs"], Buckets = ["*", "model.io"] }));
        Assert.Empty(RedactionArgv.Problems(RedactionOperation.DestinationSend, new() { Destination = "d", Signals = ["logs"], Buckets = ["*"] }));
        Assert.Contains("A signal is logs, traces or metrics.", RedactionArgv.Problems(RedactionOperation.DestinationSend, new() { Destination = "d", Signals = ["events"], Buckets = ["*"] }));
        Assert.Contains("The minimum severity is INFO, LOW, MEDIUM, HIGH or CRITICAL.", RedactionArgv.Problems(RedactionOperation.RouteSet, new() { Destination = "d", RouteName = "r", Signals = ["logs"], MinSeverity = "high" }));
        Assert.Contains("A route sends or drops what it matches.", RedactionArgv.Problems(RedactionOperation.RouteSet, new() { Destination = "d", RouteName = "r", Signals = ["logs"], RouteAction = "keep" }));

        // bucket set: a profile or the default back, never both, and something to change
        Assert.Contains("Choose a profile or give the default back, not both.", RedactionArgv.Problems(RedactionOperation.BucketSet, new() { Bucket = "model.io", Profile = "strict", InheritProfile = true }));
        Assert.Contains("Select at least one setting to change.", RedactionArgv.Problems(RedactionOperation.BucketSet, new() { Bucket = "model.io" }));
        Assert.Empty(RedactionArgv.Problems(RedactionOperation.BucketSet, new() { Bucket = "model.io", InheritProfile = true }));
        Assert.Empty(RedactionArgv.Problems(RedactionOperation.BucketSet, new() { Bucket = "model.io", CollectTraces = false }));

        // profile set: built-ins are not editable, a new one needs a base, the vocabulary is closed
        Assert.Contains("A built-in profile cannot be edited: give the custom profile a name of its own.", RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "strict", Extends = "content" }));
        Assert.Contains("Choose the built-in profile the new profile starts from.", RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "mine", IsNewProfile = true, Detectors = ["pii"] }));
        Assert.Empty(RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "mine", IsNewProfile = false, Detectors = ["pii"] })); // an existing profile keeps its base
        Assert.Contains("A custom profile starts from sensitive, content or strict.", RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "mine", Extends = "none" }));
        Assert.Contains("A detector group is pii, credentials or secrets.", RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "mine", Extends = "strict", Detectors = ["names"] }));
        Assert.Contains("'secret' is not a field class.", RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "mine", Extends = "strict", FieldModes = [new("secret", "hash")] }));
        Assert.Contains("'mask' is not a mode for the path field.", RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "mine", Extends = "strict", FieldModes = [new("path", "mask")] }));
        Assert.Contains("Choose what to set: the profile it starts from, detector groups or a field mode.", RedactionArgv.Problems(RedactionOperation.ProfileSet, new() { ProfileName = "mine" }));

        // profile remove: a custom one, replaced by a different one
        Assert.Contains("A built-in profile cannot be removed.", RedactionArgv.Problems(RedactionOperation.ProfileRemove, new() { ProfileName = "strict" }));
        Assert.Contains("The replacement must be a different profile.", RedactionArgv.Problems(RedactionOperation.ProfileRemove, new() { ProfileName = "mine", ReplaceWith = "mine" }));
        Assert.Empty(RedactionArgv.Problems(RedactionOperation.ProfileRemove, new() { ProfileName = "mine", ReplaceWith = "strict" }));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("example-otlp", true)]
    [InlineData("a_b-9", true)]
    [InlineData("0lead", true)]
    [InlineData("", false)]
    [InlineData("-leading-dash", false)]
    [InlineData("Upper", false)]
    [InlineData("has space", false)]
    [InlineData("two\nlines", false)]
    [InlineData("trailing\n", false)]
    [InlineData("--yes", false)]
    [InlineData("a*b", false)]
    [InlineData("%PATH%", false)]
    [InlineData("é", false)]
    public void A_name_that_goes_on_the_command_line_is_a_plain_lower_case_word(string name, bool safe)
    {
        Assert.Equal(safe, RedactionVocabulary.IsStableName(name));
        Assert.False(RedactionVocabulary.IsStableName(new string('a', 65)));
        Assert.True(RedactionVocabulary.IsStableName(new string('a', 64)));
    }

    [Theory]
    [InlineData("claudecode", true)]
    [InlineData("guardrail.verdict", true)]
    [InlineData("tool_call:post", true)]
    [InlineData("-x", false)]
    [InlineData("has space", false)]
    [InlineData("a*", false)]
    [InlineData("a?", false)]
    [InlineData("%USERNAME%", false)]
    [InlineData("$HOME", false)]
    [InlineData("~root", false)]
    [InlineData("tab\there", false)]
    [InlineData("", false)]
    public void A_selector_value_cannot_read_as_an_option_or_be_rewritten_by_the_cli(string value, bool safe) =>
        Assert.Equal(safe, RedactionVocabulary.IsSelectorValue(value));

    [Fact]
    public void Choosing_the_profile_none_is_recognised_wherever_it_can_be_chosen()
    {
        Assert.True(RedactionArgv.SetsNoRedaction(RedactionOperation.RemoveAll, new()));

        foreach (var op in new[] { RedactionOperation.ApplyEverywhere, RedactionOperation.ApplyDefaults, RedactionOperation.DefaultsSet, RedactionOperation.BucketSet, RedactionOperation.DestinationSend })
        {
            Assert.True(RedactionArgv.SetsNoRedaction(op, new() { Profile = "none" }), op.ToString());
            Assert.False(RedactionArgv.SetsNoRedaction(op, new() { Profile = "strict" }), op.ToString());
            Assert.False(RedactionArgv.SetsNoRedaction(op, new()), op.ToString());
        }

        // a route means something only when it sends logs or traces
        var route = new RedactionInputs { Profile = "none", Signals = ["logs"], RouteAction = "send" };
        foreach (var op in new[] { RedactionOperation.RouteAdd, RedactionOperation.RouteSet })
        {
            Assert.True(RedactionArgv.SetsNoRedaction(op, route), op.ToString());
            Assert.False(RedactionArgv.SetsNoRedaction(op, route with { RouteAction = "drop" }), op.ToString());
            Assert.False(RedactionArgv.SetsNoRedaction(op, route with { Signals = ["metrics"] }), op.ToString());
        }

        // the rest never name a profile to set
        foreach (var op in new[]
                 {
                     RedactionOperation.Status, RedactionOperation.DefaultsReset, RedactionOperation.BucketReset, RedactionOperation.ProfileSet,
                     RedactionOperation.ProfileRemove, RedactionOperation.DestinationInherit, RedactionOperation.RouteMove, RedactionOperation.RouteRemove,
                 })
        {
            Assert.False(RedactionArgv.SetsNoRedaction(op, new() { Profile = "none" }), op.ToString());
        }
    }

    [Fact]
    public void A_route_profile_is_sent_only_with_a_route_that_sends_logs_or_traces()
    {
        var send = new RedactionInputs { Destination = "d", RouteName = "r", Signals = ["logs"], Profile = "strict", RouteAction = "send" };
        Assert.Contains("--profile", RedactionArgv.Preview(RedactionOperation.RouteAdd, send));
        Assert.DoesNotContain("--profile", RedactionArgv.Preview(RedactionOperation.RouteAdd, send with { RouteAction = "drop" }));
        Assert.DoesNotContain("--profile", RedactionArgv.Preview(RedactionOperation.RouteAdd, send with { Signals = ["metrics"] }));
    }

    [Fact]
    public void Repeated_options_keep_their_order_and_drop_duplicates()
    {
        var argv = RedactionArgv.Preview(
            RedactionOperation.DestinationSend,
            new() { Destination = "d", Signals = ["traces", "logs", "traces"], Buckets = ["model.io", "tool.activity"], Profile = "content" });

        Assert.Equal(
            ["setup", "redaction", "destination", "send", "d", "--signal", "traces", "--signal", "logs", "--bucket", "model.io", "--bucket", "tool.activity", "--profile", "content", "--json", "--dry-run"],
            argv);
    }
}
