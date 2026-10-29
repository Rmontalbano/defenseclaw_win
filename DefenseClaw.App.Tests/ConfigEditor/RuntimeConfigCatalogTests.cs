using System.Text.Json;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-391, the runtime's own config catalog: the dump's shape (synthetic dumps built on 1.0.4's <c>build_setup_sections</c>), what is never let through
/// (a password's value, a secret-shaped key, a URL's path), which interpreter each Windows layout gives, what the interpreter is started with, the cache,
/// and every way the read fails quietly. No process is started: the runner is a fixture.
/// </summary>
public sealed class RuntimeConfigCatalogTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------ the dump

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public void The_dump_reads_as_the_runtimes_sections_in_its_order_with_kinds_choices_and_hints(string eol)
    {
        var catalog = RuntimeConfigCatalog.Parse(LineEndings.With(RuntimeCatalogFixtures.Dump104(), eol))!;

        Assert.Equal(RuntimeCatalogFixtures.SectionNames, catalog.Sections.Select(s => s.Name));
        var general = catalog.Sections[0];
        Assert.Equal("Global paths, environment label, and the shared LLM key fallback.", general.Summary);
        Assert.StartsWith("Config Version is read-only", general.Help, StringComparison.Ordinal);
        Assert.True(general.Fields[1].IsSeparator);
        Assert.Equal("Paths", general.Fields[1].GroupName);

        var provider = catalog.Field("llm.provider")!;
        Assert.Equal(RuntimeFieldKind.Choice, provider.Kind);
        Assert.Equal(["anthropic", "openai", "azure", "ollama"], provider.Options);
        Assert.Equal("LLM provider family.", provider.Hint);
        Assert.Equal(RuntimeFieldKind.Int, catalog.Field("llm.timeout")!.Kind);
        Assert.Equal(RuntimeFieldKind.Header, catalog.Field("config_version")!.Kind);

        var blockAt = catalog.Describe("scanners.skill_scanner.fail_on_severity")!;
        Assert.Equal("Block At", blockAt.Label);
        Assert.Equal(["", "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO"], blockAt.Options);
        Assert.Null(blockAt.ReadOnlyReason);

        // A row its own editor shows read-only says so, in its own words when it has some.
        Assert.Equal("Read-only legacy setting; Windows toast delivery is inactive.", catalog.Describe("notifications.enabled")!.ReadOnlyReason);
        Assert.NotNull(catalog.Describe("guardrail.hook_fail_mode")!.ReadOnlyReason);
        Assert.Null(catalog.Describe("llm.max_tokens"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):\nModuleNotFoundError: No module named 'defenseclaw.tui'\n")]
    [InlineData(RuntimeConfigCatalog.Sentinel + "{not json")]
    [InlineData(RuntimeConfigCatalog.Sentinel + "[]")]
    [InlineData(RuntimeConfigCatalog.Sentinel + "{\"sections\": []}")]
    [InlineData(RuntimeConfigCatalog.Sentinel + "[{\"summary\": \"no name\"}]")]
    public void Anything_but_a_catalog_is_no_catalog(string stdout) => Assert.Null(RuntimeConfigCatalog.Parse(stdout));

    [Fact]
    public void An_unknown_kind_stays_visible_as_text_and_a_row_without_a_label_is_skipped()
    {
        var stdout = RuntimeConfigCatalog.Sentinel + JsonSerializer.Serialize(new[]
        {
            new { name = "Future", summary = "", help = "", fields = new object[] { new { label = "Shape", key = "future.shape", kind = "polygon", value = "x" }, new { key = "future.none" }, 7 } },
        });

        var section = RuntimeConfigCatalog.Parse(stdout)!.Sections.Single();
        var row = Assert.Single(section.Fields);
        Assert.Equal(RuntimeFieldKind.String, row.Kind);
        Assert.Equal("x", row.Value);
    }

    [Fact]
    public void A_password_never_has_a_value_a_secret_shaped_key_is_hidden_and_a_url_keeps_only_its_host()
    {
        var catalog = RuntimeConfigCatalog.Parse(RuntimeCatalogFixtures.Dump104())!;

        foreach (var key in new[] { "llm.api_key", "gateway.token", "cisco_ai_defense.api_key" })
        {
            Assert.Equal(RuntimeFieldKind.Password, catalog.Field(key)!.Kind);
            Assert.Equal(string.Empty, catalog.Field(key)!.Value);
            Assert.True(catalog.Describe(key)!.IsSecret);
        }

        Assert.Equal("https://proxy.example.test/[REDACTED]", catalog.Field("llm.base_url")!.Value);
        Assert.Equal("[enabled] https://hooks.example.test/[REDACTED]", catalog.Field("webhooks.0")!.Value);
        Assert.Equal("https://aidefense.example.test", catalog.Field("cisco_ai_defense.endpoint")!.Value);

        var everything = string.Join("\n", catalog.Sections.SelectMany(s => s.Fields).Select(f => f.Value + f.Label + f.Hint));
        Assert.DoesNotContain(RuntimeCatalogFixtures.LeakedSecret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(RuntimeCatalogFixtures.WebhookSecretPath, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("operator:pw", everything, StringComparison.Ordinal);

        Assert.Equal("(hidden)", RuntimeConfigCatalog.DisplayValue("gateway.device_key_file", RuntimeFieldKind.Header, @"C:\keys\device.pem"));
        Assert.Equal(string.Empty, RuntimeConfigCatalog.DisplayValue("anything", RuntimeFieldKind.Password, "value"));
        Assert.Equal("OPENAI_API_KEY", RuntimeConfigCatalog.DisplayValue("llm.api_key_env", RuntimeFieldKind.String, "OPENAI_API_KEY"));
    }

    [Fact]
    public void The_dump_script_imports_the_catalog_reads_config_and_writes_nothing()
    {
        var script = RuntimeConfigCatalog.DumpScript;

        Assert.Contains("from defenseclaw.tui.panels.setup import build_setup_sections", script, StringComparison.Ordinal);
        Assert.Contains("cfg = dc_config.load()", script, StringComparison.Ordinal);
        Assert.Contains("'' if hidden else", script, StringComparison.Ordinal);
        Assert.Contains("kind == 'password'", script, StringComparison.Ordinal);
        Assert.Contains(RuntimeConfigCatalog.Sentinel, script, StringComparison.Ordinal);
        foreach (var write in new[] { ".save(", "open(", "write", "unlink", "remove(", "subprocess", "os.system" })
        {
            Assert.DoesNotContain(write, script, StringComparison.Ordinal);
        }

        Assert.Equal(["-I", "-B", "-c", script], RuntimeCatalogLoader.Arguments);
    }

    // ------------------------------------------------------------------ which interpreter, per layout

    private static DefenseClawPaths Paths(string data, string bin, IEnumerable<string>? fallbacks = null, RuntimeSelection? runtime = null) =>
        new(dataDirectory: data, binDirectory: bin, searchPath: Array.Empty<string>(), fileExists: _ => false, fallbackBinDirectories: fallbacks, runtime: runtime);

    [Fact]
    public void Each_windows_layout_names_the_interpreter_its_cli_runs_on()
    {
        var data = @"C:\Users\operator\.defenseclaw";
        var localBin = @"C:\Users\operator\.local\bin";
        var setupBin = @"C:\Users\operator\AppData\Local\Programs\DefenseClaw\bin";
        var paths = Paths(data, setupBin, new[] { localBin, data + @"\.venv\Scripts" });

        // The Setup exe of 0.8.7-0.8.10 (and any native launcher): ..\runtime\python\python.exe.
        Assert.Contains(@"C:\Users\operator\AppData\Local\Programs\DefenseClaw\runtime\python\python.exe", RuntimeCatalogLoader.PythonCandidates(setupBin + @"\defenseclaw.exe", paths));

        // install.ps1 of 1.0.x: the launcher copy in ~\.local\bin has no Python beside it; the data directory's venv is the one.
        var script = RuntimeCatalogLoader.PythonCandidates(localBin + @"\defenseclaw.exe", paths);
        Assert.Equal(data + @"\.venv\Scripts\python.exe", script[^1]);

        // A virtual environment's own Scripts folder: the python.exe beside the CLI comes first.
        var venv = RuntimeCatalogLoader.PythonCandidates(@"D:\dev\venv\Scripts\defenseclaw.exe", paths);
        Assert.Equal(@"D:\dev\venv\Scripts\python.exe", venv[0]);
        Assert.DoesNotContain(paths.VenvPythonPath, venv);

        // No CLI, or the developer selector's container: nothing to start.
        Assert.Empty(RuntimeCatalogLoader.PythonCandidates(null, paths));
        var container = Paths(data, setupBin, runtime: RuntimeSelection.ForContainer("dc-test", null, @"C:\Synthetic\host-copy"));
        Assert.Empty(RuntimeCatalogLoader.PythonCandidates(setupBin + @"\defenseclaw.exe", container));
    }

    [Fact]
    public void The_interpreter_gets_the_installations_identity_and_no_python_variables()
    {
        var plain = RuntimeCatalogLoader.EnvironmentFor(Paths(@"C:\Synthetic\home", @"C:\Synthetic\bin"), TestInstallations.UserDefault());
        Assert.Null(plain["PYTHONHOME"]);
        Assert.Null(plain["PYTHONPATH"]);
        Assert.False(plain.ContainsKey(InstallationContext.ConfigVariable));

        var managed = TestInstallations.Managed();
        var pinned = RuntimeCatalogLoader.EnvironmentFor(Paths(@"C:\Synthetic\home", @"C:\Synthetic\bin"), managed);
        Assert.Equal(managed.PinnedConfig, pinned[InstallationContext.ConfigVariable]);
        Assert.Equal(InstallationContext.ManagedEnterprise, pinned[InstallationContext.DeploymentModeVariable]);
        Assert.DoesNotContain(pinned.Keys, name => name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || name.Contains("KEY", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ the read, the cache and the quiet failures

    private (DefenseClawPaths Paths, string Python) PlantedLayout()
    {
        RuntimeCatalogFixtures.PlantCliAndPython(_temp.Path);
        _ = _temp.WriteFile("config.yaml", "config_version: 9\n");
        return (TestServices.IsolatedPaths(_temp.Path), Path.Combine(_temp.Path, "no-such-bin", "python.exe"));
    }

    private static RuntimeCatalogRequest Request(DefenseClawPaths paths, string? version = "1.0.4") =>
        new(paths, InstallationContext.Unmanaged(paths.DataDirectory), version, @"C:\Synthetic\neutral-cwd");

    [Fact]
    public async Task The_dump_is_one_isolated_run_outside_the_data_directory_and_is_remembered_until_something_it_reads_changes()
    {
        var (paths, python) = PlantedLayout();
        var (runner, launches) = RuntimeCatalogFixtures.Answering(RuntimeCatalogFixtures.Dump104());
        var loader = new RuntimeCatalogLoader(runner);

        var first = await loader.LoadAsync(Request(paths), force: false);
        Assert.NotNull(first.Catalog);
        Assert.Null(first.UnavailableReason);
        var launch = Assert.Single(launches);
        Assert.Equal(python, launch.PythonPath);
        Assert.Equal(RuntimeCatalogLoader.Arguments, launch.Arguments);
        Assert.Equal(@"C:\Synthetic\neutral-cwd", launch.WorkingDirectory);
        Assert.False(launch.WorkingDirectory.StartsWith(paths.DataDirectory, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RuntimeCatalogLoader.DumpTimeout, launch.Timeout);

        // Same runtime, same file: remembered.
        Assert.Same(first, await loader.LoadAsync(Request(paths), force: false));
        Assert.Equal(1, loader.RunCount);

        // config.yaml changed: read again. So does another runtime version, and the Reload button.
        File.WriteAllText(paths.ConfigFilePath, "config_version: 9\nagent:\n  id: operator-box\n");
        File.SetLastWriteTimeUtc(paths.ConfigFilePath, DateTime.UtcNow.AddMinutes(1));
        _ = await loader.LoadAsync(Request(paths), force: false);
        Assert.Equal(2, loader.RunCount);
        _ = await loader.LoadAsync(Request(paths, "1.0.5"), force: false);
        Assert.Equal(3, loader.RunCount);
        _ = await loader.LoadAsync(Request(paths, "1.0.5"), force: true);
        Assert.Equal(4, loader.RunCount);
    }

    public static IEnumerable<object[]> Failures() => new[]
    {
        new object[] { RuntimePythonOutput.Fail("timed out after 20 s"), "timed out" },
        new object[] { RuntimePythonOutput.Fail("the interpreter could not be started: not a valid application"), "could not be started" },
        new object[] { new RuntimePythonOutput(1, "Traceback (most recent call last):\nImportError: cannot import name 'build_setup_sections'\n", null), "exited 1" },
        new object[] { new RuntimePythonOutput(0, "no sentinel here\n", null), "no catalog" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_or_timed_out_dump_is_a_reason_never_an_exception_and_never_quotes_the_output(RuntimePythonOutput output, string reason)
    {
        var (paths, _) = PlantedLayout();
        var loader = new RuntimeCatalogLoader(RuntimeCatalogFixtures.Ending(output));

        var result = await loader.LoadAsync(Request(paths), force: false);

        Assert.Null(result.Catalog);
        Assert.Contains(reason, result.UnavailableReason, StringComparison.Ordinal);
        Assert.DoesNotContain("Traceback", result.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runner_that_throws_or_a_missing_interpreter_is_also_just_a_reason()
    {
        var (paths, _) = PlantedLayout();
        var throwing = new RuntimeCatalogLoader((_, _) => throw new InvalidOperationException("boom"));
        Assert.Contains("could not be asked", (await throwing.LoadAsync(Request(paths), force: false)).UnavailableReason, StringComparison.Ordinal);

        var started = false;
        var nothingThere = new RuntimeCatalogLoader((_, _) => { started = true; return Task.FromResult(RuntimePythonOutput.Fail("x")); }, fileExists: _ => false);
        var missing = await nothingThere.LoadAsync(Request(paths), force: false);
        Assert.StartsWith("no runtime Python was found", missing.UnavailableReason, StringComparison.Ordinal);
        Assert.False(started);

        using var empty = new TempDirectory();
        var noCli = await new RuntimeCatalogLoader((_, _) => { started = true; return Task.FromResult(RuntimePythonOutput.Fail("x")); })
            .LoadAsync(Request(TestServices.IsolatedPaths(empty.Path)), force: false);
        Assert.Equal("the DefenseClaw CLI was not found", noCli.UnavailableReason);
        Assert.False(started);
    }

    // ------------------------------------------------------------------ the built-in catalogue's 1.0.x scanner keys

    [Theory]
    [InlineData("scanners.skill_scanner.fail_on_severity", "Findings at or above this severity block (empty: HIGH).")]
    [InlineData("scanners.skill_scanner.review_queue_min", "Findings from here up to Block At go to review (empty: MEDIUM).")]
    public void Block_at_and_review_from_are_the_severity_choices_with_a_blank(string path, string hint)
    {
        var info = ConfigFieldCatalog.Resolve(path, ConfigChoiceProfile.Windows)!;
        Assert.Equal(hint, info.Hint);
        Assert.Equal(["", "CRITICAL", "HIGH", "MEDIUM", "LOW", "INFO"], info.Options);
    }

    [Fact]
    public void Judge_source_is_inherit_or_override_and_use_osv_is_a_switch()
    {
        var judge = ConfigFieldCatalog.Resolve("scanners.skill_scanner.judge_source", ConfigChoiceProfile.Windows)!;
        Assert.Equal(["", "inherit", "override"], judge.Options);
        Assert.StartsWith("inherit: the top-level llm block", judge.Hint, StringComparison.Ordinal);

        var osv = ConfigFieldCatalog.Resolve("scanners.skill_scanner.analyzers.osv.enabled", ConfigChoiceProfile.Windows)!;
        Assert.False(osv.IsChoice);
        Assert.Equal("Optional: check dependencies on OSV.dev.", osv.Hint);
    }
}
