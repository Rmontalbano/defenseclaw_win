using System.Text.Json;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The disk side of CUST-352 on synthetic files modelled on DefenseClaw 1.0's formats: the pinned Codex executable, CAS v2 receipt sets, the
/// quarantine that moves a stuck one aside and puts it back, and <c>.hookcfg</c>. Every file is made up in a scratch directory; no real data
/// directory or Codex folder is read or written.
/// </summary>
public sealed class CasDurabilityTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 21, 30, 0, TimeSpan.Zero);

    private const string StuckId = "068ff1aa22bb33cc44dd55ee";
    private const string OtherId = "aa00bb11cc22dd33ee44ff55";
    private const string DoneId = "1234567890abcdef12345678";

    private readonly TempDirectory _temp = new("dcw-durability");
    private readonly string _data;
    private readonly string _codex;

    public CasDurabilityTests()
    {
        _data = Path.Combine(_temp.Path, "dc");
        _codex = Path.Combine(_temp.Path, ".codex");
        _ = Directory.CreateDirectory(_data);
        _ = Directory.CreateDirectory(_codex);
    }

    public void Dispose() => _temp.Dispose();

    private string Receipt(string id, string phase, string target, string[]? stagingNames = null, DateTimeOffset? written = null)
    {
        var suffix = phase == "staging" ? string.Empty : "." + phase;
        var path = Path.Combine(_data, $".defenseclaw-cas-v2-{id}.intent{suffix}");
        var body = new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["phase"] = phase,
            ["transaction_id"] = new string('a', 48),
            ["logical_path"] = target,
            ["input_path"] = target,
            ["target_path"] = target,
            ["stage_final_name"] = stagingNames is { Length: > 0 } ? stagingNames[0] : null,
            ["stage"] = new Dictionary<string, object?> { ["name"] = stagingNames is { Length: > 1 } ? stagingNames[1] : "config.toml" },
            ["old"] = new Dictionary<string, object?> { ["name"] = "config.toml" },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(body));
        File.SetLastWriteTimeUtc(path, (written ?? Now.AddMinutes(-30)).UtcDateTime);
        return path;
    }

    private string Temp(string name, DateTimeOffset? written = null, string content = "staged")
    {
        var path = Path.Combine(_codex, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, (written ?? Now.AddMinutes(-30)).UtcDateTime);
        return path;
    }

    private string Target()
    {
        var path = Path.Combine(_codex, "config.toml");
        File.WriteAllText(path, "model = \"x\"\n");
        return path;
    }

    // ---- pending receipt sets -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_set_with_no_terminal_or_complete_receipt_is_pending_with_its_staging_files()
    {
        var target = Target();
        var staging = ".tmp-cas-v2-stage-" + new string('1', 32);
        var a = Receipt(StuckId, "staging", target, new[] { staging });
        var b = Receipt(StuckId, "allocation", target);
        var c = Receipt(StuckId, "prepared", target);
        var temp = Temp(staging);

        var pending = Assert.Single(CasReceipts.FindPending(_data, Now));

        Assert.Equal(StuckId, pending.Id);
        Assert.Equal(target, pending.TargetPath);
        Assert.True(pending.IsCodexConfig);
        Assert.Equal("staging+allocation+prepared", pending.Phases);
        Assert.Equal(new[] { a, b, c }.Order().ToArray(), pending.ReceiptFiles.Order().ToArray());
        Assert.Equal(new[] { temp }, pending.TempFiles);
    }

    [Fact]
    public void A_finished_set_is_not_pending_whichever_way_it_finished()
    {
        var target = Target();
        _ = Receipt(DoneId, "staging", target);
        _ = Receipt(DoneId, "complete", target);
        _ = Receipt(OtherId, "staging", target);
        _ = Receipt(OtherId, "terminal", target);

        Assert.Empty(CasReceipts.FindPending(_data, Now));
    }

    [Fact]
    public void Only_the_unfinished_one_of_several_sets_is_listed()
    {
        var target = Target();
        _ = Receipt(DoneId, "staging", target);
        _ = Receipt(DoneId, "complete", target);
        _ = Receipt(StuckId, "staging", target);

        var pending = Assert.Single(CasReceipts.FindPending(_data, Now));
        Assert.Equal(StuckId, pending.Id);
    }

    [Fact]
    public void A_write_in_flight_is_not_stuck_until_it_is_two_minutes_old()
    {
        var target = Target();
        _ = Receipt(StuckId, "staging", target, written: Now.AddSeconds(-90));

        Assert.Empty(CasReceipts.FindPending(_data, Now));
        Assert.Single(CasReceipts.FindPending(_data, Now.AddSeconds(31)));
    }

    [Fact]
    public void A_retired_set_and_unrelated_files_are_never_listed()
    {
        var gc = Path.Combine(_data, $".defenseclaw-cas-v2-{StuckId}.intent.gc-{new string('b', 48)}");
        File.WriteAllText(gc, "{}");
        File.SetLastWriteTimeUtc(gc, Now.AddHours(-1).UtcDateTime);
        File.WriteAllText(Path.Combine(_data, ".defenseclaw-cas-v2-notahexdigest.intent"), "{}");
        File.WriteAllText(Path.Combine(_data, "config.yaml"), "x: 1");

        Assert.Empty(CasReceipts.FindPending(_data, Now));
    }

    [Fact]
    public void An_unreadable_receipt_is_still_a_pending_set_with_no_target_and_no_staging_files()
    {
        var path = Path.Combine(_data, $".defenseclaw-cas-v2-{StuckId}.intent");
        File.WriteAllText(path, "{ not json");
        File.SetLastWriteTimeUtc(path, Now.AddHours(-1).UtcDateTime);

        var pending = Assert.Single(CasReceipts.FindPending(_data, Now));
        Assert.Null(pending.TargetPath);
        Assert.Empty(pending.TempFiles);
    }

    [Fact]
    public void An_orphan_staging_file_beside_the_target_goes_with_the_set_but_a_young_or_owned_one_does_not()
    {
        var target = Target();
        var owned = ".tmp-cas-v2-stage-" + new string('2', 32);
        _ = Receipt(StuckId, "staging", target, new[] { owned });
        var ownedPath = Temp(owned);
        var orphan = Temp(".tmp-cas-v2-marker-" + new string('3', 32));
        _ = Temp(".tmp-cas-v2-young-" + new string('4', 32), written: Now.AddSeconds(-5));
        _ = Temp("notes.txt");

        var pending = Assert.Single(CasReceipts.FindPending(_data, Now));

        Assert.Equal(new[] { ownedPath, orphan }.Order().ToArray(), pending.TempFiles.Order().ToArray());
    }

    [Fact]
    public void A_receipt_cannot_make_the_app_name_a_file_that_is_not_a_staging_file()
    {
        var target = Target();
        _ = Receipt(StuckId, "staging", target, new[] { @"..\config.toml", ".tmp-cas-v2-..\\x" });

        var pending = Assert.Single(CasReceipts.FindPending(_data, Now));

        Assert.DoesNotContain(pending.TempFiles, f => string.Equals(Path.GetFileName(f), "config.toml", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the quarantine --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Moving_a_stuck_set_aside_moves_it_with_a_manifest_and_touches_nothing_else()
    {
        var target = Target();
        var staging = ".tmp-cas-v2-stage-" + new string('5', 32);
        var stuckFiles = new[]
        {
            Receipt(StuckId, "staging", target, new[] { staging }),
            Receipt(StuckId, "allocation", target),
            Receipt(StuckId, "prepared", target),
            Temp(staging, content: "new config body"),
        };
        var doneFiles = new[] { Receipt(DoneId, "staging", target), Receipt(DoneId, "complete", target) };
        var otherFile = Receipt(OtherId, "staging", target, written: Now.AddSeconds(-10));
        var root = Path.Combine(_temp.Path, "quarantine");

        var result = CasQuarantine.MoveAside(_data, new[] { StuckId }, root, Now, gatewayRunning: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(4, result.Files);
        Assert.All(stuckFiles, f => Assert.False(File.Exists(f), f));
        Assert.All(doneFiles, f => Assert.True(File.Exists(f), f));
        Assert.True(File.Exists(otherFile));
        Assert.True(File.Exists(target));
        Assert.Equal("model = \"x\"\n", File.ReadAllText(target));

        var manifestPath = Path.Combine(result.Directory!, CasQuarantine.ManifestName);
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var entries = manifest.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(4, entries.Length);
        Assert.Contains(entries, e => e.GetProperty("original").GetString() == stuckFiles[3] && e.GetProperty("size").GetInt64() == "new config body".Length);
        Assert.All(entries, e =>
        {
            Assert.Equal(64, e.GetProperty("sha256").GetString()!.Length);
            Assert.True(e.GetProperty("moved").GetBoolean());
            Assert.True(File.Exists(Path.Combine(result.Directory!, e.GetProperty("stored").GetString()!)));
        });
        Assert.DoesNotContain(CasReceipts.FindPending(_data, Now), p => p.Id == StuckId);
    }

    [Fact]
    public void Moving_aside_is_refused_while_the_gateway_runs_and_moves_nothing()
    {
        var target = Target();
        var receipt = Receipt(StuckId, "staging", target);
        var root = Path.Combine(_temp.Path, "quarantine");

        var result = CasQuarantine.MoveAside(_data, new[] { StuckId }, root, Now, gatewayRunning: true);

        Assert.False(result.Succeeded);
        Assert.Contains("running", result.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(receipt));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_set_that_finished_since_the_review_is_left_alone()
    {
        var target = Target();
        var a = Receipt(StuckId, "staging", target);
        var b = Receipt(StuckId, "complete", target);

        var result = CasQuarantine.MoveAside(_data, new[] { StuckId }, Path.Combine(_temp.Path, "quarantine"), Now, gatewayRunning: false);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void An_id_that_was_not_asked_for_is_never_moved()
    {
        var target = Target();
        _ = Receipt(StuckId, "staging", target);
        var other = Receipt(OtherId, "staging", target);

        var result = CasQuarantine.MoveAside(_data, new[] { StuckId }, Path.Combine(_temp.Path, "quarantine"), Now, gatewayRunning: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Put_back_restores_exactly_what_was_moved_and_is_refused_while_the_gateway_runs()
    {
        var target = Target();
        var staging = ".tmp-cas-v2-stage-" + new string('6', 32);
        var files = new[] { Receipt(StuckId, "staging", target, new[] { staging }), Receipt(StuckId, "prepared", target), Temp(staging, content: "body") };
        var contents = files.Select(File.ReadAllText).ToArray();
        var root = Path.Combine(_temp.Path, "quarantine");
        var moved = CasQuarantine.MoveAside(_data, new[] { StuckId }, root, Now, gatewayRunning: false);
        Assert.True(moved.Succeeded, moved.Message);

        var refused = CasQuarantine.PutBack(moved.Directory!, gatewayRunning: true);
        Assert.False(refused.Succeeded);
        Assert.All(files, f => Assert.False(File.Exists(f)));

        var back = CasQuarantine.PutBack(moved.Directory!, gatewayRunning: false);

        Assert.True(back.Succeeded, back.Message);
        Assert.Equal(3, back.Files);
        Assert.Equal(contents, files.Select(File.ReadAllText).ToArray());
        Assert.Null(CasQuarantine.LatestUnrestored(root));
        Assert.Single(CasReceipts.FindPending(_data, Now));
    }

    [Fact]
    public void Put_back_leaves_a_changed_file_and_an_occupied_place_alone_and_says_so()
    {
        var target = Target();
        var a = Receipt(StuckId, "staging", target);
        var b = Receipt(StuckId, "prepared", target);
        var root = Path.Combine(_temp.Path, "quarantine");
        var moved = CasQuarantine.MoveAside(_data, new[] { StuckId }, root, Now, gatewayRunning: false);

        File.WriteAllText(a, "someone wrote a new one");
        File.WriteAllText(Path.Combine(moved.Directory!, Path.GetFileName(b)), "tampered");

        var back = CasQuarantine.PutBack(moved.Directory!, gatewayRunning: false);

        Assert.False(back.Succeeded);
        Assert.Equal("someone wrote a new one", File.ReadAllText(a));
        Assert.False(File.Exists(b));
        Assert.Contains("changed", back.Message, StringComparison.Ordinal);
        Assert.NotNull(CasQuarantine.LatestUnrestored(root));
    }

    [Fact]
    public void A_doctored_manifest_cannot_send_a_file_anywhere_the_app_did_not_take_it_from()
    {
        var target = Target();
        _ = Receipt(StuckId, "staging", target);
        var root = Path.Combine(_temp.Path, "quarantine");
        var moved = CasQuarantine.MoveAside(_data, new[] { StuckId }, root, Now, gatewayRunning: false);
        var manifestPath = Path.Combine(moved.Directory!, CasQuarantine.ManifestName);
        var elsewhere = Path.Combine(_temp.Path, "elsewhere.txt");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(JsonSerializer.Serialize(Path.Combine(_data, $".defenseclaw-cas-v2-{StuckId}.intent")).Trim('"'), JsonSerializer.Serialize(elsewhere).Trim('"'), StringComparison.Ordinal));

        var back = CasQuarantine.PutBack(moved.Directory!, gatewayRunning: false);

        Assert.False(File.Exists(elsewhere));
        Assert.Contains("not a file this app moved", back.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_latest_unrestored_quarantine_is_the_newest_one_with_files_left()
    {
        var target = Target();
        var root = Path.Combine(_temp.Path, "quarantine");
        _ = Receipt(StuckId, "staging", target);
        var first = CasQuarantine.MoveAside(_data, new[] { StuckId }, root, Now, gatewayRunning: false);
        _ = Receipt(OtherId, "staging", target);
        var second = CasQuarantine.MoveAside(_data, new[] { OtherId }, root, Now.AddMinutes(5), gatewayRunning: false);

        Assert.Equal(second.Directory, CasQuarantine.LatestUnrestored(root)!.Directory);
        _ = CasQuarantine.PutBack(second.Directory!, gatewayRunning: false);
        Assert.Equal(first.Directory, CasQuarantine.LatestUnrestored(root)!.Directory);
        Assert.Null(CasQuarantine.LatestUnrestored(Path.Combine(_temp.Path, "no-such-folder")));
    }

    // ---- the pinned Codex executable ---------------------------------------------------------------------------------------------

    private const string OldExe = @"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\0123456789abcdef\codex.exe";
    private const string NewExe = @"C:\Users\tester\AppData\Local\OpenAI\Codex\bin\fedcba9876543210\codex.exe";

    private void WriteLock(string executable) =>
        _temp.Write("dc/hook_contract_lock.json", JsonSerializer.Serialize(new { version = 2, connectors = new { codex = new { connector = "codex", agent_executable = executable }, claudecode = new { connector = "claudecode", agent_executable = "C:\\x\\claude.exe" } } }));

    [Fact]
    public void A_pinned_executable_that_is_gone_is_missing_and_one_that_is_there_is_not()
    {
        WriteLock(OldExe);

        var gone = AgentExecutablePin.Read(_data, "codex", Now, exists: p => p == NewExe);
        var there = AgentExecutablePin.Read(_data, "codex", Now, exists: p => p == OldExe);

        Assert.True(gone.IsMissing);
        Assert.Equal(OldExe, gone.Executable);
        Assert.Equal(AgentExecutablePin.LockFileName, gone.Source);
        Assert.False(there.IsMissing);
        Assert.True(there.Exists);
    }

    [Fact]
    public void An_unexpired_selection_receipt_is_used_when_the_lock_names_nothing_and_an_expired_one_is_ignored()
    {
        string Selection(DateTimeOffset expires) => JsonSerializer.Serialize(new
        {
            schema_version = 1,
            updated_at = Now.ToString("O"),
            selections = new { codex = new { connector = "codex", executable = OldExe, expires_at = expires.ToString("O") } },
        });

        _ = _temp.Write("dc/agent_selection.json", Selection(Now.AddMinutes(10)));
        var live = AgentExecutablePin.Read(_data, "codex", Now, exists: _ => false);
        Assert.True(live.IsMissing);
        Assert.Equal(AgentExecutablePin.SelectionFileName, live.Source);

        _ = _temp.Write("dc/agent_selection.json", Selection(Now.AddDays(-2)));
        var expired = AgentExecutablePin.Read(_data, "codex", Now, exists: _ => false);
        Assert.Null(expired.Executable);
        Assert.False(expired.IsMissing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("codex.exe")]
    [InlineData("{ not json")]
    public void Nothing_odd_in_the_lock_is_called_missing(string executableOrJunk)
    {
        _ = _temp.Write("dc/hook_contract_lock.json", executableOrJunk.StartsWith('{') ? executableOrJunk : JsonSerializer.Serialize(new { version = 2, connectors = new { codex = new { agent_executable = executableOrJunk } } }));

        Assert.False(AgentExecutablePin.Read(_data, "codex", Now, exists: _ => false).IsMissing);
    }

    [Fact]
    public void A_missing_lock_file_pins_nothing_and_a_path_that_cannot_be_checked_is_not_called_missing()
    {
        Assert.Null(AgentExecutablePin.Read(_data, "codex", Now).Executable);

        WriteLock(OldExe);
        var unknown = AgentExecutablePin.Read(_data, "codex", Now, exists: _ => throw new IOException("share did not answer"));
        Assert.False(unknown.IsMissing);
    }

    // ---- .hookcfg and the facts ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Fail_modes_come_from_hookcfg_and_nothing_else_in_it_is_read()
    {
        var path = _temp.Write("hookcfg.json", "{\"gateway_token\":\"do-not-read\",\"fail_modes\":{\"Codex\":\"closed\",\"claudecode\":\"open\",\"bad\":\"maybe\",\"n\":3}}");

        var modes = DurabilityReader.ReadFailModes(path);

        Assert.Equal(new[] { new ConnectorFailMode("claudecode", "open"), new ConnectorFailMode("codex", "closed") }, modes);
        Assert.Empty(DurabilityReader.ReadFailModes(_temp.File("missing")));
        Assert.Empty(DurabilityReader.ReadFailModes(_temp.Write("junk.json", "[]")));
    }

    [Fact]
    public void The_facts_read_everything_and_compare_by_value()
    {
        var hooks = Path.Combine(_data, "hooks");
        _ = Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, ".hookcfg"), "{\"fail_modes\":{\"codex\":\"closed\"}}");
        WriteLock(OldExe);
        _ = Receipt(StuckId, "staging", Target());
        var root = Path.Combine(_temp.Path, "quarantine");

        var one = DurabilityReader.Read(_data, hooks, root, Now, _ => false);
        var two = DurabilityReader.Read(_data, hooks, root, Now, _ => false);

        Assert.Equal(one, two);
        Assert.True(one.CodexExecutableMissing);
        Assert.Equal(new[] { "codex" }, one.FailClosedConnectors);
        Assert.Equal("closed", one.FailModeOf("CODEX"));
        Assert.Null(one.FailModeOf("claudecode"));
        Assert.Single(one.PendingWrites);
        Assert.NotEqual(one, DurabilityReader.Read(_data, hooks, root, Now, _ => true));
    }
}
