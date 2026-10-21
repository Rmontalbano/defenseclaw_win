using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Gateway.Durability;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>CUST-360: hash-based classification of a pending CAS write, on synthetic files in a scratch directory.</summary>
public sealed class CasClassifierTests : IDisposable
{
    private const string Id = "068ff1aa22bb33cc44dd55ee";
    private const string Old = "old content";
    private const string New = "new content";

    private readonly TempDirectory _temp = new("dcw-classify");
    private readonly string _data;
    private readonly string _agent;
    private readonly string _target;

    public CasClassifierTests()
    {
        _data = Path.Combine(_temp.Path, "dc");
        _agent = Path.Combine(_temp.Path, ".claude");
        _ = Directory.CreateDirectory(_data);
        _ = Directory.CreateDirectory(_agent);
        _target = Path.Combine(_agent, "settings.json");
    }

    public void Dispose() => _temp.Dispose();

    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private string Intent(string suffix, object? body = null, string? raw = null)
    {
        var path = Path.Combine(_data, $".defenseclaw-cas-v2-{Id}.intent{suffix}");
        File.WriteAllText(path, raw ?? JsonSerializer.Serialize(body ?? Body()));
        return path;
    }

    private Dictionary<string, object?> Body(bool oldExists = true) => new()
    {
        ["version"] = 2,
        ["target_path"] = _target,
        ["old_exists"] = oldExists,
        ["remove"] = false,
        ["old"] = new Dictionary<string, object?> { ["name"] = "settings.json", ["sha256"] = Sha(Old) },
        ["stage"] = new Dictionary<string, object?> { ["name"] = ".tmp-cas-v2-stage-aa", ["sha256"] = Sha(New) },
        ["stage_final_name"] = ".tmp-cas-v2-ready-aa",
        ["stage_marker"] = new Dictionary<string, object?> { ["name"] = ".tmp-cas-v2-marker-stage-aa", ["sha256"] = Sha("marker") },
        ["tombstone_name"] = ".tmp-cas-v2-old-aa",
    };

    private CasPendingWrite Write(params string[] receipts) =>
        new(Id, _data, receipts, Array.Empty<string>(), _target, "staging", DateTimeOffset.UtcNow);

    private void Put(string name, string content) => File.WriteAllText(Path.Combine(_agent, name), content);

    [Fact]
    public void Target_with_old_content_is_never_applied()
    {
        File.WriteAllText(_target, Old);
        var result = CasReceipts.Classify(Write(Intent(string.Empty)));
        Assert.Equal(CasWriteState.NeverApplied, result.State);
    }

    [Fact]
    public void Target_with_staged_content_is_committed()
    {
        File.WriteAllText(_target, New);
        Assert.Equal(CasWriteState.Committed, CasReceipts.Classify(Write(Intent(string.Empty))).State);
    }

    [Fact]
    public void Target_matching_neither_is_superseded()
    {
        File.WriteAllText(_target, "someone else");
        Assert.Equal(CasWriteState.Superseded, CasReceipts.Classify(Write(Intent(string.Empty))).State);
    }

    [Fact]
    public void Absent_target_that_never_existed_is_never_applied()
    {
        var result = CasReceipts.Classify(Write(Intent(string.Empty, Body(oldExists: false))));
        Assert.Equal(CasWriteState.NeverApplied, result.State);
    }

    [Fact]
    public void Absent_target_that_used_to_exist_is_unknown()
    {
        Assert.Equal(CasWriteState.Unknown, CasReceipts.Classify(Write(Intent(string.Empty))).State);
    }

    [Fact]
    public void Prepared_receipt_wins_and_missing_prepared_falls_back_to_intent()
    {
        File.WriteAllText(_target, New);
        var plain = Intent(string.Empty);
        Assert.Equal(CasWriteState.Committed, CasReceipts.Classify(Write(plain)).State);

        // A prepared receipt with a different stage hash is the one read when present.
        var prepared = Body();
        prepared["stage"] = new Dictionary<string, object?> { ["name"] = ".tmp-cas-v2-stage-aa", ["sha256"] = Sha("other") };
        var preparedPath = Intent(".prepared", prepared);
        Assert.Equal(CasWriteState.Superseded, CasReceipts.Classify(Write(plain, preparedPath)).State);
    }

    [Fact]
    public void Malformed_receipt_is_unknown()
    {
        File.WriteAllText(_target, Old);
        var result = CasReceipts.Classify(Write(Intent(string.Empty, raw: "{ not json")));
        Assert.Equal(CasWriteState.Unknown, result.State);
    }

    [Fact]
    public void Artifact_hash_mismatch_is_unknown_and_a_matching_one_is_fine()
    {
        File.WriteAllText(_target, Old);
        var write = Write(Intent(string.Empty));
        Put(".tmp-cas-v2-marker-stage-aa", "marker");
        Assert.Equal(CasWriteState.NeverApplied, CasReceipts.Classify(write).State);

        Put(".tmp-cas-v2-marker-stage-aa", "tampered");
        Assert.Equal(CasWriteState.Unknown, CasReceipts.Classify(write).State);
    }

    [Fact]
    public void Tombstone_is_checked_against_the_old_hash()
    {
        File.WriteAllText(_target, New);
        var write = Write(Intent(string.Empty));
        Put(".tmp-cas-v2-old-aa", Old);
        Assert.Equal(CasWriteState.Committed, CasReceipts.Classify(write).State);
        Put(".tmp-cas-v2-old-aa", "not the old file");
        Assert.Equal(CasWriteState.Unknown, CasReceipts.Classify(write).State);
    }

    [Fact]
    public void Oversized_target_is_unknown()
    {
        using (var stream = new FileStream(_target, FileMode.Create))
        {
            stream.SetLength(CasReceipts.MaxHashBytes + 1);
        }

        var result = CasReceipts.Classify(Write(Intent(string.Empty)));
        Assert.Equal(CasWriteState.Unknown, result.State);
    }

    [Fact]
    public void Hasher_failure_is_unknown_and_never_throws()
    {
        File.WriteAllText(_target, Old);
        Assert.Equal(CasWriteState.Unknown, CasReceipts.Classify(Write(Intent(string.Empty)), _ => null).State);
        Assert.Equal(CasWriteState.Unknown, CasReceipts.Classify(Write(Intent(string.Empty)), _ => throw new InvalidOperationException()).State);
    }

    [Fact]
    public void Missing_receipts_are_unknown()
    {
        Assert.Equal(CasWriteState.Unknown, CasReceipts.Classify(Write()).State);
    }
}
