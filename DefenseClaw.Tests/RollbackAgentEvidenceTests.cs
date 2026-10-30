using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// CUST-399: the check install.ps1 1.0.5 makes before a rollback changes anything (<c>Get-StaleAgentEvidence</c> and <c>Test-FreshAgentSelection</c>,
/// scripts/install.ps1:5100-5149 at tag 1.0.5), read from synthetic <c>hook_contract_lock.json</c> and <c>agent_selection.json</c> files and a synthetic
/// "codex.exe" in a scratch directory. No real Codex or data directory is read.
/// </summary>
public sealed class RollbackAgentEvidenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new("dcw-rollback-agent");
    private readonly string _data;
    private readonly string _exe;

    public RollbackAgentEvidenceTests()
    {
        _data = Directory.CreateDirectory(Path.Combine(_temp.Path, "previous", "data")).FullName;
        _exe = Path.Combine(Directory.CreateDirectory(Path.Combine(_temp.Path, "codex", "bin")).FullName, "codex.exe");
        File.WriteAllText(_exe, "synthetic codex 0.47.0");
    }

    public void Dispose() => _temp.Dispose();

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private void Lock(string sha256, string source = "setup-selected", string? exe = null, string version = "codex-cli 0.47.0") =>
        File.WriteAllText(Path.Combine(_data, AgentExecutablePin.LockFileName), JsonSerializer.Serialize(new
        {
            connectors = new Dictionary<string, object>
            {
                ["codex"] = new Dictionary<string, object>
                {
                    ["agent_executable"] = exe ?? _exe,
                    ["agent_executable_sha256"] = sha256,
                    ["agent_executable_source"] = source,
                    ["raw_agent_version"] = version,
                },
            },
        }));

    private void Selection(string sha256, DateTimeOffset expires) =>
        File.WriteAllText(Path.Combine(_data, AgentExecutablePin.SelectionFileName), JsonSerializer.Serialize(new
        {
            selections = new Dictionary<string, object>
            {
                ["codex"] = new Dictionary<string, object>
                {
                    ["source"] = "setup-selected",
                    ["executable"] = _exe,
                    ["sha256"] = sha256,
                    ["expires_at"] = expires.ToString("O"),
                },
            },
        }));

    [Fact]
    public void An_unchanged_codex_is_not_stale()
    {
        Lock(Sha("synthetic codex 0.47.0"));

        Assert.Empty(RollbackAgentEvidence.Find(_data, Now));
    }

    [Fact]
    public void A_codex_that_updated_itself_since_setup_is_stale_and_named_with_its_version()
    {
        Lock(Sha("synthetic codex 0.46.0"));

        var stale = Assert.Single(RollbackAgentEvidence.Find(_data, Now));

        Assert.Equal("codex", stale.Connector);
        Assert.Equal("Codex 0.47.0", stale.Label);
        Assert.False(stale.Missing);
        Assert.Equal(_exe, stale.Executable);
    }

    [Fact]
    public void A_sealed_path_that_is_gone_is_stale_as_missing()
    {
        Lock(Sha("x"), exe: Path.Combine(_temp.Path, "codex", "old", "codex.exe"), version: string.Empty);

        var stale = Assert.Single(RollbackAgentEvidence.Find(_data, Now));

        Assert.True(stale.Missing);
        Assert.Equal("Codex", stale.Label);
    }

    [Fact]
    public void A_fresh_setup_selection_of_the_program_on_disk_is_not_stale_but_an_expiring_or_mismatched_one_is()
    {
        Lock(Sha("synthetic codex 0.46.0"));

        Selection(Sha("synthetic codex 0.47.0"), Now.AddMinutes(10));
        Assert.Empty(RollbackAgentEvidence.Find(_data, Now));

        // Five minutes or less to go is not fresh (the installer's AddMinutes(5)).
        Selection(Sha("synthetic codex 0.47.0"), Now.AddMinutes(4));
        Assert.Single(RollbackAgentEvidence.Find(_data, Now));

        Selection(Sha("another program"), Now.AddMinutes(10));
        Assert.Single(RollbackAgentEvidence.Find(_data, Now));
    }

    [Fact]
    public void Only_a_setup_selected_codex_with_a_well_formed_hash_is_judged_and_odd_files_say_nothing()
    {
        Lock(Sha("other"), source: "path");
        Assert.Empty(RollbackAgentEvidence.Find(_data, Now));

        Lock("not-a-hash");
        Assert.Empty(RollbackAgentEvidence.Find(_data, Now));

        File.WriteAllText(Path.Combine(_data, AgentExecutablePin.LockFileName), "{ not json");
        Assert.Empty(RollbackAgentEvidence.Find(_data, Now));

        Assert.Empty(RollbackAgentEvidence.Find(Path.Combine(_temp.Path, "no-such-folder"), Now));
    }

    [Fact]
    public void A_program_that_cannot_be_hashed_is_skipped_as_the_installer_skips_it()
    {
        Lock(Sha("synthetic codex 0.46.0"));

        Assert.Empty(RollbackAgentEvidence.Find(_data, Now, sha256: _ => null));
    }
}
