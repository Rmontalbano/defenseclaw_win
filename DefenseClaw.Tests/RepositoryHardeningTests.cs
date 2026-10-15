using System.Text.RegularExpressions;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace DefenseClaw.Tests;

/// <summary>
/// The supply-chain settings of the repository itself, held in place by tests so that a later edit cannot quietly undo them:
/// how the workflows are pinned and scoped, that dependency auditing covers transitive packages and fails the build, and that
/// the secret-scanner exceptions name single files. They read the real files from the repository root (found by walking up
/// from the test output to the solution), so a change to any of them is judged here, on every build.
/// </summary>
public class RepositoryHardeningTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DefenseClaw.Win.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"DefenseClaw.Win.sln was not found above {AppContext.BaseDirectory}.");
    }

    private static string RepositoryFile(params string[] relative) => Path.Combine(new[] { Root }.Concat(relative).ToArray());

    private static IEnumerable<string> WorkflowPaths() =>
        Directory.EnumerateFiles(RepositoryFile(".github", "workflows"), "*.y*ml").OrderBy(p => p, StringComparer.Ordinal);

    private static YamlMappingNode Load(string path)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(path));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode? Child(YamlNode? node, string key) =>
        node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Text(YamlNode? node) => (node as YamlScalarNode)?.Value;

    private static List<(string Id, YamlMappingNode Job)> Jobs(YamlMappingNode workflow) =>
        Child(workflow, "jobs") is YamlMappingNode jobs
            ? jobs.Children.Select(job => (Id: Text(job.Key)!, Job: (YamlMappingNode)job.Value)).ToList()
            : new List<(string Id, YamlMappingNode Job)>();

    private static IEnumerable<string> Scalars(YamlNode node) => node switch
    {
        YamlScalarNode scalar => new[] { scalar.Value ?? string.Empty },
        YamlMappingNode map => map.Children.SelectMany(pair => Scalars(pair.Key).Concat(Scalars(pair.Value))),
        YamlSequenceNode sequence => sequence.Children.SelectMany(Scalars),
        _ => Array.Empty<string>(),
    };

    private static List<YamlMappingNode> Steps(YamlMappingNode job) =>
        Child(job, "steps") is YamlSequenceNode steps ? steps.Children.OfType<YamlMappingNode>().ToList() : new List<YamlMappingNode>();

    private static bool Uses(YamlMappingNode step, string action) =>
        Text(Child(step, "uses"))?.StartsWith(action + "@", StringComparison.Ordinal) == true;

    [Fact]
    public void The_workflows_are_found_and_are_valid_yaml()
    {
        var paths = WorkflowPaths().ToList();

        Assert.Contains(paths, p => Path.GetFileName(p) == "ci.yml");
        Assert.Contains(paths, p => Path.GetFileName(p) == "security.yml");
        foreach (var path in paths)
        {
            Assert.NotEmpty(Jobs(Load(path)));
        }
    }

    [Fact]
    public void Every_action_a_workflow_uses_is_pinned_to_a_full_commit_sha_with_its_release_in_a_comment()
    {
        var uses = new Regex(@"^\s*(?:-\s+)?uses:\s*(?<ref>[^\s#]+)(?:\s+#\s*(?<comment>.*?))?\s*$", RegexOptions.Compiled);
        var pinned = new Regex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$", RegexOptions.Compiled);
        var release = new Regex(@"^v\d+(\.\d+)*\b", RegexOptions.Compiled);
        var offenders = new List<string>();
        var found = 0;

        foreach (var path in WorkflowPaths())
        {
            foreach (var (line, number) in File.ReadAllLines(path).Select((line, index) => (line, index + 1)))
            {
                var match = uses.Match(line);
                if (!match.Success || match.Groups["ref"].Value.StartsWith("./", StringComparison.Ordinal))
                {
                    continue;
                }

                found++;
                if (!pinned.IsMatch(match.Groups["ref"].Value) || !release.IsMatch(match.Groups["comment"].Value))
                {
                    offenders.Add($"{Path.GetFileName(path)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.True(found >= 8, "The scan is expected to find the actions the workflows use.");
        Assert.False(
            offenders.Count > 0,
            "Pin each action to its full 40-character commit SHA and put the release it stands for after it (\"# v1.2.3\"):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_workflow_defaults_to_a_read_only_token_and_none_runs_privileged_triggers()
    {
        foreach (var path in WorkflowPaths())
        {
            var workflow = Load(path);
            var permissions = Child(workflow, "permissions") as YamlMappingNode;

            Assert.True(permissions is not null, $"{Path.GetFileName(path)} has no top-level permissions block.");
            var granted = permissions!.Children.Select(pair => $"{Text(pair.Key)}: {Text(pair.Value)}").ToList();
            Assert.Equal(new[] { "contents: read" }, granted);

            // pull_request_target and workflow_run run with a write token and secrets against a pull request's own data.
            var triggers = Scalars(Child(workflow, "on")!).ToList();
            Assert.DoesNotContain("pull_request_target", triggers);
            Assert.DoesNotContain("workflow_run", triggers);
        }
    }

    [Fact]
    public void Only_the_release_job_can_write_and_it_runs_only_for_a_version_tag_pushed_to_this_repository()
    {
        var writers = new List<string>();
        foreach (var path in WorkflowPaths())
        {
            foreach (var (id, job) in Jobs(Load(path)))
            {
                var grantsWrite = Child(job, "permissions") is YamlMappingNode map && map.Children.Any(pair => Text(pair.Value) == "write");
                if (!grantsWrite)
                {
                    continue;
                }

                writers.Add($"{Path.GetFileName(path)}:{id}");
                var condition = Text(Child(job, "if")) ?? string.Empty;
                Assert.Contains("github.event_name == 'push'", condition, StringComparison.Ordinal);
                Assert.Contains("refs/tags/v", condition, StringComparison.Ordinal);
                Assert.Contains("!github.event.repository.fork", condition, StringComparison.Ordinal);
                Assert.Equal("package", Text(Child(job, "needs")));
            }
        }

        Assert.Equal(new[] { "ci.yml:release" }, writers);
    }

    [Fact]
    public void No_job_outside_the_release_job_reads_a_repository_secret()
    {
        foreach (var path in WorkflowPaths())
        {
            foreach (var (id, job) in Jobs(Load(path)))
            {
                if (id == "release")
                {
                    continue;
                }

                Assert.DoesNotContain(Scalars(job), value => value.Contains("secrets.", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Checkout_never_leaves_the_token_in_the_workspace()
    {
        var checkouts = 0;
        foreach (var path in WorkflowPaths())
        {
            foreach (var (_, job) in Jobs(Load(path)))
            {
                foreach (var step in Steps(job).Where(s => Uses(s, "actions/checkout")))
                {
                    checkouts++;
                    Assert.Equal("false", Text(Child(Child(step, "with"), "persist-credentials")));
                }
            }
        }

        Assert.True(checkouts >= 3, "The workflows are expected to check the repository out in several jobs.");
    }

    [Fact]
    public void The_release_publishes_every_asset_with_its_checksums_and_a_signed_provenance_attestation()
    {
        var workflow = Load(RepositoryFile(".github", "workflows", "ci.yml"));
        var jobs = Jobs(workflow);
        var release = jobs.Single(j => j.Id == "release").Job;
        var package = jobs.Single(j => j.Id == "package").Job;
        var steps = Steps(release);

        var permissions = Child(release, "permissions");
        Assert.Equal("write", Text(Child(permissions, "contents")));
        Assert.Equal("write", Text(Child(permissions, "id-token")));
        Assert.Equal("write", Text(Child(permissions, "attestations")));

        // The package job builds with the read-only token and writes checksums.txt over every asset; the release job only attests
        // and publishes what it downloaded, after checking it again.
        var packageSteps = Steps(package);
        var checksums = packageSteps.Single(s => Text(Child(s, "name")) == "Checksums");
        Assert.Contains("dist/checksums.txt", Text(Child(checksums, "run")), StringComparison.Ordinal);
        Assert.Contains(packageSteps, s => Uses(s, "actions/upload-artifact"));

        var verify = steps.Single(s => Text(Child(s, "name")) == "Verify release assets");
        Assert.Contains("Test-ReleaseAssets.ps1", Text(Child(verify, "run")), StringComparison.Ordinal);

        var attest = steps.Single(s => Uses(s, "actions/attest-build-provenance"));
        Assert.Contains("dist/*", Text(Child(Child(attest, "with"), "subject-path")) ?? string.Empty, StringComparison.Ordinal);

        var publish = steps.Single(s => Uses(s, "softprops/action-gh-release"));
        Assert.Contains("dist/*", Text(Child(Child(publish, "with"), "files")) ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("release-notes.md", Text(Child(Child(publish, "with"), "body_path")));

        Assert.True(steps.IndexOf(verify) < steps.IndexOf(attest), "The artifact is re-checked before it is attested.");
        Assert.True(steps.IndexOf(attest) < steps.IndexOf(publish), "The attestation is made before the release is created.");
    }

    [Fact]
    public void Both_exes_are_published_with_the_version_of_the_tag_by_a_job_that_cannot_write()
    {
        var workflow = Load(RepositoryFile(".github", "workflows", "ci.yml"));
        var package = Jobs(workflow).Single(j => j.Id == "package").Job;
        Assert.Null(Child(package, "permissions"));

        var publishes = Steps(package).Select(s => Text(Child(s, "run")) ?? string.Empty)
            .Where(r => r.Contains("dotnet publish", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, publishes.Count);
        Assert.All(publishes, run =>
        {
            Assert.Contains("-p:Version=${{ steps.version.outputs.version }}", run, StringComparison.Ordinal);
            Assert.Contains("-p:InformationalVersion=", run, StringComparison.Ordinal);
        });

        // Untagged builds are the -dev default, so they never look newer than a release.
        Assert.Contains("0.0.0-dev", File.ReadAllText(RepositoryFile("Directory.Build.props")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_is_per_user_with_a_fixed_id_and_never_touches_the_cisco_uninstall_key()
    {
        var script = File.ReadAllText(RepositoryFile("installer", "DefenseClawWin.iss"));

        Assert.Contains("PrivilegesRequired=lowest", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivilegesRequired=admin", script, StringComparison.Ordinal);
        Assert.Contains("AppId={{290C7D59-D43A-4CF8-BE38-60332DD563EC}", script, StringComparison.Ordinal);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\DefenseClawWin", script, StringComparison.Ordinal);
        Assert.Contains("ValueName: \"{#RunValueName}\"", script, StringComparison.Ordinal);
        // The only Uninstall key the script may name is its own (by AppId, implicitly); Cisco's is ...\Uninstall\DefenseClaw.
        Assert.DoesNotContain(@"Uninstall\DefenseClaw", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_keeps_the_raw_app_version_out_of_the_numeric_version_resource_and_names_its_uninstall_event_the_way_inno_does()
    {
        var script = File.ReadAllText(RepositoryFile("installer", "DefenseClawWin.iss"));

        // ISCC cannot be run on every machine, so what it rejected once is held here. Untagged builds are 0.0.0-dev, and the numeric
        // VersionInfo fields take digits and dots only: the text version carries "-dev".
        foreach (var directive in new[] { "VersionInfoVersion", "VersionInfoProductVersion" })
        {
            Assert.Matches(new Regex("^" + directive + @"=\{#NumericVersion\}\.0\s*$", RegexOptions.Multiline), script);
        }

        Assert.Matches(new Regex(@"^VersionInfoProductTextVersion=\{#AppVersion\}\s*$", RegexOptions.Multiline), script);

        // Inno calls an uninstall event only by its own name; a procedure named anything else compiles and never runs.
        Assert.Contains("procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentUninstallStep", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_changelog_keeps_an_unreleased_section_and_the_release_template_has_its_sections()
    {
        var changelog = File.ReadAllText(RepositoryFile("CHANGELOG.md"));
        Assert.Contains("## [Unreleased]", changelog, StringComparison.Ordinal);

        var template = File.ReadAllText(RepositoryFile(".github", "release-notes-template.md"));
        foreach (var section in new[] { "## Highlights", "## Downloads", "## Verification", "## SHA-256" })
        {
            Assert.Contains(section, template, StringComparison.Ordinal);
        }

        Assert.Contains("not Authenticode-signed", template, StringComparison.Ordinal);
    }

    [Fact]
    public void NuGet_audit_covers_transitive_packages_and_fails_the_build_on_a_vulnerability()
    {
        var properties = XDocument.Load(RepositoryFile("Directory.Build.props")).Descendants("PropertyGroup").Elements()
            .ToDictionary(element => element.Name.LocalName, element => element.Value.Trim(), StringComparer.Ordinal);

        Assert.Equal("true", properties["NuGetAudit"]);
        Assert.Equal("all", properties["NuGetAuditMode"]);

        // DefenseClaw.App does not turn on TreatWarningsAsErrors, so the audit codes are made errors here for every project.
        var errors = properties["WarningsAsErrors"].Split(new[] { ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(errors, new HashSet<string> { "NU1901", "NU1902", "NU1903", "NU1904" });
    }

    [Fact]
    public void Scanner_exceptions_name_single_test_files_and_the_default_secret_rules_stay_on()
    {
        var gitleaks = File.ReadAllText(RepositoryFile(".gitleaks.toml"));
        Assert.Matches(new Regex(@"^useDefault\s*=\s*true\s*$", RegexOptions.Multiline), gitleaks);
        Assert.DoesNotContain("commits =", gitleaks, StringComparison.Ordinal);
        Assert.DoesNotContain("[allowlist]", gitleaks, StringComparison.Ordinal);

        // Every allowlisted path is one .cs file; a directory pattern would exempt real code from the scan.
        var gitleaksPaths = gitleaks.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("'''(^|", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(gitleaksPaths);
        Assert.All(gitleaksPaths, path => Assert.EndsWith(@"\.cs$''',", path, StringComparison.Ordinal));

        var trufflehog = File.ReadAllLines(RepositoryFile(".trufflehog-exclude.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.NotEmpty(trufflehog);
        Assert.All(trufflehog, pattern =>
        {
            Assert.StartsWith("^", pattern, StringComparison.Ordinal);
            Assert.EndsWith(@"\.cs$", pattern, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_security_policy_says_how_to_report_privately_and_where_the_runtime_is_reported()
    {
        var policy = File.ReadAllText(RepositoryFile("SECURITY.md"));

        Assert.Contains("security/advisories/new", policy, StringComparison.Ordinal);
        Assert.Contains("cisco-ai-defense/defenseclaw", policy, StringComparison.Ordinal);
        Assert.Contains("Supported versions", policy, StringComparison.Ordinal);
    }
}
