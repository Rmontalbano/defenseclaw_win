using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Audit;
using Microsoft.Data.Sqlite;

namespace DefenseClaw.App.Tests.Audit;

/// <summary>
/// The composition root hands one <see cref="AuditChangeProbe"/> over the live audit.db to every reader of it, owns its lifetime, and
/// starts no timer of its own (the probe does something only when asked). Synthetic database from the real DDL.
/// </summary>
public sealed class AuditChangeWiringTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private AppServices Services()
    {
        AuditTestDatabase.Create(Path.Combine(_temp.Path, "audit.db"), 12);
        return TestServices.Create(_temp);
    }

    [Fact]
    public async Task One_kept_connection_serves_the_audit_reader_the_queue_and_the_readers_the_panels_build()
    {
        using var services = Services();
        var probe = services.AuditChanges;
        Assert.False(probe.IsOpen);
        Assert.Equal(0, probe.SampleCount);

        _ = await services.Audit.QueryAsync(new AuditQuery());
        _ = await services.AlertQueue.ReadAsync();
        _ = await new MutationReader(services.Paths.AuditDatabasePath, services.AuditChanges).ReadAsync();
        _ = await new EventStreamReader(services.Paths.AuditDatabasePath, probe: services.AuditChanges).ReadAsync(EventStreamKind.Events);
        _ = await new NetworkEgressReader(services.Paths.AuditDatabasePath, services.AuditChanges).ReadRecentAsync();

        Assert.Equal(5, probe.SampleCount);
        Assert.Equal(1, probe.OpenCount);
        Assert.True(probe.IsOpen);

        // The second round is five probes and nothing else.
        _ = await services.Audit.QueryAsync(new AuditQuery());
        _ = await services.AlertQueue.ReadAsync();
        Assert.Equal(1, probe.OpenCount);
        Assert.Equal(1, services.Audit.UnchangedReads);
        Assert.Equal(1, services.AlertQueue.UnchangedReads);
    }

    [Fact]
    public async Task The_panels_readers_are_built_over_the_shared_probe()
    {
        using var services = Services();

        var activity = new ActivityPanelViewModel(services);
        await activity.Mutations.LoadAsync();
        await activity.Mutations.LoadAsync();

        Assert.True(services.AuditChanges.SampleCount >= 2);
        Assert.Equal(1, services.AuditChanges.OpenCount);
    }

    [Fact]
    public void Disposing_the_services_gives_the_audit_file_back_and_a_later_sample_is_unknown()
    {
        var services = Services();
        var path = services.Paths.AuditDatabasePath;
        Assert.True(services.AuditChanges.Sample().IsKnown);
        Assert.True(services.AuditChanges.IsOpen);

        services.Dispose();
        SqliteConnection.ClearAllPools();

        Assert.False(services.AuditChanges.IsOpen);
        Assert.False(services.AuditChanges.Sample().IsKnown);

        // A handle without share-delete would refuse the rename.
        File.Move(path, path + ".moved");
        File.Move(path + ".moved", path);
    }

    [Fact]
    public async Task A_missing_database_is_not_opened_or_created_by_the_probe_or_any_reader()
    {
        using var services = TestServices.Create(_temp);
        var path = services.Paths.AuditDatabasePath;

        _ = await services.AlertQueue.ReadAsync();
        _ = await new MutationReader(path, services.AuditChanges).ReadAsync();
        Assert.False(services.AuditChanges.Sample().IsKnown);

        Assert.False(File.Exists(path));
        Assert.False(services.AuditChanges.IsOpen);
        Assert.Equal(0, services.AuditChanges.OpenCount);
    }
}
