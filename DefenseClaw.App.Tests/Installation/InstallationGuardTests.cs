using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.Installation;

/// <summary>
/// The app's one answer to "may this installation be changed?" (CUST-308): <c>Services.Installation</c>. Every surface asks it instead of working
/// the rule out again, so what is pinned here is what every disabled control, review and banner says: the verdict, the one sentence, which argv
/// are reads, when the runtime cannot be upgraded, and how a verdict that moves while the app runs (config.yaml edited to managed, or fixed) reaches
/// the panels. All contexts are synthetic (<see cref="TestInstallations"/>); nothing here reads or writes a real folder.
/// </summary>
public sealed class InstallationGuardTests : IDisposable
{
    private static readonly string[] ReadArgv = { "skill", "list" };
    private static readonly string[] ChangeArgv = { "skill", "block", "--connector", "claudecode", "--", "pdf-tools" };
    private static readonly string[] DestroyArgv = { "skill", "remove", "--connector", "claudecode", "--", "pdf-tools" };

    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    private AppServices Create(InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _services.Add(services);
        return services;
    }

    // ------------------------------------------------------------------ the usual installation

    [Fact]
    public void A_fresh_composition_drives_the_users_own_installation_and_nothing_is_off()
    {
        var guard = Create().Installation;

        Assert.True(guard.IsMutable);
        Assert.Equal(InstallationAccess.UnmanagedMutable, guard.Context.Access);
        Assert.Null(guard.BlockedReason);
        Assert.Null(guard.BannerText);
        Assert.Null(guard.UpgradeBlockedReason);
        Assert.True(guard.CanUpgrade);
        Assert.Null(guard.ReasonFor(ReadArgv));
        Assert.Null(guard.ReasonFor(ChangeArgv));
        Assert.Null(guard.ReasonFor(DestroyArgv));
        Assert.Null(guard.ReasonFor(CommandTier.Destructive));
    }

    [Fact]
    public void The_paths_of_a_composition_carry_the_installation_the_guard_starts_with()
    {
        var managed = TestInstallations.Managed(_temp.Path);
        var services = Create(managed);

        Assert.Same(managed, services.Paths.Installation);
        Assert.Same(managed, services.Installation.Context);
    }

    // ------------------------------------------------------------------ a read-only installation

    public static IEnumerable<object[]> ReadOnlyInstallations() =>
        new[]
        {
            new object[] { "managed layout" },
            new object[] { "managed by config.yaml" },
            new object[] { "invalid selection" },
        };

    private InstallationContext ReadOnly(string which) => which switch
    {
        "managed layout" => TestInstallations.Managed(_temp.Path),
        "managed by config.yaml" => TestInstallations.ManagedAt(_temp.Path),
        _ => TestInstallations.Invalid(),
    };

    [Theory]
    [MemberData(nameof(ReadOnlyInstallations))]
    public void A_read_only_installation_gives_one_sentence_and_the_banner_is_that_sentence_behind_a_fixed_prefix(string which)
    {
        var context = ReadOnly(which);
        var guard = Create(context).Installation;

        Assert.False(context.IsMutable);
        Assert.False(guard.IsMutable);
        Assert.False(string.IsNullOrWhiteSpace(guard.BlockedReason));
        Assert.Equal(context.BlockedReason, guard.BlockedReason);
        Assert.Equal("State-changing actions disabled: " + guard.BlockedReason, guard.BannerText);
        Assert.DoesNotContain("\n", guard.BlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_managed_installation_names_the_administrator_not_the_app()
    {
        var guard = Create(TestInstallations.Managed(_temp.Path)).Installation;

        Assert.Equal(TestInstallations.ManagedReason, guard.BlockedReason);
        Assert.Equal(InstallationAccess.ManagedReadOnly, guard.Context.Access);
    }

    [Theory]
    [MemberData(nameof(ReadOnlyInstallations))]
    public void A_read_still_runs_and_everything_else_gets_the_installations_sentence(string which)
    {
        var guard = Create(ReadOnly(which)).Installation;

        Assert.Null(guard.ReasonFor(ReadArgv));
        Assert.Null(guard.ReasonFor(new[] { "status" }));
        Assert.Equal(guard.BlockedReason, guard.ReasonFor(ChangeArgv));
        Assert.Equal(guard.BlockedReason, guard.ReasonFor(DestroyArgv));

        // The gateway program is classified the same way, and any other program is a change: unknown is never a read.
        Assert.Null(guard.ReasonFor("defenseclaw-gateway", new[] { "status" }));
        Assert.Equal(guard.BlockedReason, guard.ReasonFor("defenseclaw-gateway", new[] { "start" }));
        Assert.Equal(guard.BlockedReason, guard.ReasonFor("powershell", new[] { "-Command", "Get-Date" }));
    }

    [Fact]
    public void A_dry_run_preview_is_a_read_on_a_read_only_installation()
    {
        var guard = Create(TestInstallations.Managed(_temp.Path)).Installation;

        Assert.Null(guard.ReasonFor(new[] { "alerts", "acknowledge", "--dry-run" }));
    }

    [Fact]
    public void A_review_is_blocked_by_the_first_of_its_steps_that_changes_something()
    {
        var guard = Create(TestInstallations.Managed(_temp.Path)).Installation;
        var reads = new[] { new CommandReviewStep(ReadArgv), new CommandReviewStep(new[] { "mcp", "list" }) };
        var mixed = new[] { new CommandReviewStep(ReadArgv), new CommandReviewStep(ChangeArgv), new CommandReviewStep(DestroyArgv) };

        Assert.Null(guard.ReasonFor(reads));
        Assert.Equal(TestInstallations.ManagedReason, guard.ReasonFor(mixed));
        Assert.Null(guard.ReasonFor(Array.Empty<CommandReviewStep>()));
    }

    [Fact]
    public void A_tier_is_blocked_unless_it_is_a_read()
    {
        var guard = Create(TestInstallations.Managed(_temp.Path)).Installation;

        Assert.Null(guard.ReasonFor(CommandTier.ReadOnly));
        Assert.Equal(TestInstallations.ManagedReason, guard.ReasonFor(CommandTier.StateChanging));
        Assert.Equal(TestInstallations.ManagedReason, guard.ReasonFor(CommandTier.Destructive));
    }

    // ------------------------------------------------------------------ the runtime upgrade

    [Fact]
    public void The_runtime_is_upgraded_from_here_only_when_it_is_the_one_setup_installed()
    {
        Assert.Null(Create().Installation.UpgradeBlockedReason);
        Assert.True(Create().Installation.CanUpgrade);
    }

    [Theory]
    [MemberData(nameof(ReadOnlyInstallations))]
    public void A_managed_or_invalid_installation_is_not_upgraded_from_here_and_says_why(string which)
    {
        var guard = Create(ReadOnly(which)).Installation;

        Assert.False(guard.CanUpgrade);
        Assert.StartsWith("The runtime is not upgraded from here. ", guard.UpgradeBlockedReason, StringComparison.Ordinal);
        Assert.EndsWith(guard.BlockedReason!, guard.UpgradeBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_developer_runtime_is_writable_but_is_not_upgraded_by_setup()
    {
        var context = TestInstallations.DeveloperRuntime();
        var guard = new InstallationGuard(context, TestInstallations.DeveloperSelection);

        Assert.True(guard.IsMutable);
        Assert.Null(guard.BlockedReason);
        Assert.False(guard.CanUpgrade);
        Assert.Contains("developer runtime", guard.UpgradeBlockedReason, StringComparison.Ordinal);
        Assert.Contains("Settings", guard.UpgradeBlockedReason, StringComparison.Ordinal);
        Assert.Equal(InstallationSource.AppOverride, context.Source);
        Assert.Equal(TestInstallations.DeveloperHome, context.OverridePath);
    }

    // ------------------------------------------------------------------ a verdict that moves while the app runs

    private sealed class Resolver
    {
        public InstallationContext Next { get; set; } = TestInstallations.UserDefault();

        public bool Throw { get; set; }

        public int Calls { get; private set; }

        public InstallationContext Resolve()
        {
            Calls++;
            return Throw ? throw new IOException("synthetic: the disk went away") : Next;
        }
    }

    [Fact]
    public void A_guard_without_a_resolver_never_changes()
    {
        var guard = new InstallationGuard(TestInstallations.UserDefault());
        var raised = 0;
        guard.Changed += (_, _) => raised++;

        Assert.False(guard.Refresh());
        guard.PublishPendingChange();

        Assert.Equal(0, raised);
        Assert.True(guard.IsMutable);
    }

    [Fact]
    public void A_refresh_that_finds_the_same_answer_changes_nothing_and_says_nothing()
    {
        var resolver = new Resolver { Next = TestInstallations.UserDefault() };
        var guard = new InstallationGuard(TestInstallations.UserDefault(), resolve: resolver.Resolve);
        var raised = 0;
        guard.Changed += (_, _) => raised++;

        Assert.False(guard.Refresh());
        guard.PublishPendingChange();

        Assert.Equal(1, resolver.Calls);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_config_edited_to_managed_turns_the_guard_read_only_and_is_announced_once_when_published()
    {
        var resolver = new Resolver { Next = TestInstallations.ManagedByConfigMode() };
        var guard = new InstallationGuard(TestInstallations.UserDefault(), resolve: resolver.Resolve);
        var raised = 0;
        guard.Changed += (_, _) => raised++;

        // The off-thread half: the verdict is in force at once (the runner reads it), but nobody is told yet.
        Assert.True(guard.Refresh());
        Assert.False(guard.IsMutable);
        Assert.Equal(TestInstallations.ManagedReason, guard.BlockedReason);
        Assert.Equal(0, raised);

        // The UI-thread half announces it, once.
        guard.PublishPendingChange();
        guard.PublishPendingChange();
        Assert.Equal(1, raised);

        // A refresh that now finds the same managed answer is not news.
        Assert.False(guard.Refresh());
        guard.PublishPendingChange();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_config_that_is_fixed_turns_the_guard_writable_again()
    {
        var resolver = new Resolver { Next = TestInstallations.Invalid() };
        var guard = new InstallationGuard(TestInstallations.UserDefault(), resolve: resolver.Resolve);
        var raised = 0;
        guard.Changed += (_, _) => raised++;

        Assert.True(guard.Refresh());
        guard.PublishPendingChange();
        Assert.False(guard.IsMutable);

        resolver.Next = TestInstallations.UserDefault();
        Assert.True(guard.Refresh());
        guard.PublishPendingChange();

        Assert.True(guard.IsMutable);
        Assert.Null(guard.BlockedReason);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_resolution_that_throws_keeps_the_answer_in_force_instead_of_taking_the_caller_down()
    {
        var resolver = new Resolver { Throw = true };
        var guard = new InstallationGuard(TestInstallations.Managed(), resolve: resolver.Resolve);
        var raised = 0;
        guard.Changed += (_, _) => raised++;

        Assert.False(guard.Refresh());
        guard.PublishPendingChange();

        Assert.False(guard.IsMutable);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Replace_swaps_the_context_and_announces_it_at_once_for_a_harness_that_stands_in_for_a_changed_config()
    {
        var guard = Create().Installation;
        var seen = new List<bool>();
        guard.Changed += (_, _) => seen.Add(guard.IsMutable);

        guard.Replace(TestInstallations.Managed());
        guard.Replace(TestInstallations.UserDefault());

        Assert.Equal(new[] { false, true }, seen);
    }

    // ------------------------------------------------------------------ one enforcement point, reached through the composition

    /// <summary>A refused run is the Activity entry every refusal is (<see cref="CliRunner.RecordRefusal"/>): no exit code, "refused - reason", nothing started.</summary>
    private static void AssertRefused(CliInvocation invocation)
    {
        Assert.False(invocation.IsRunning);
        Assert.Null(invocation.ExitCode);
        Assert.StartsWith(CliRunner.RefusedPrefix + " — ", invocation.FailureReason, StringComparison.Ordinal);
        Assert.Contains(TestInstallations.ManagedReason, invocation.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_composition_runner_refuses_a_change_on_a_managed_installation_and_still_reaches_a_read()
    {
        var services = Create(TestInstallations.Managed(_temp.Path));

        var refused = await services.Cli.RunAsync(ChangeArgv);

        AssertRefused(refused);
        Assert.Contains(refused.OutputLines, line => line.Text.Contains(TestInstallations.ManagedReason, StringComparison.Ordinal));

        // A read is not refused: it goes on to look for the CLI, which the isolated composition does not have.
        _ = await Assert.ThrowsAsync<CliNotFoundException>(() => services.Cli.RunAsync(ReadArgv));
    }

    [Fact]
    public async Task The_composition_runner_follows_the_guard_when_the_verdict_moves()
    {
        var services = Create();

        // Writable: a change goes on to look for the CLI.
        _ = await Assert.ThrowsAsync<CliNotFoundException>(() => services.Cli.RunAsync(ChangeArgv));

        services.Installation.Replace(TestInstallations.ManagedByConfigMode());
        var refused = await services.Cli.RunAsync(ChangeArgv);
        AssertRefused(refused);

        services.Installation.Replace(TestInstallations.UserDefault());
        _ = await Assert.ThrowsAsync<CliNotFoundException>(() => services.Cli.RunAsync(ChangeArgv));
    }
}
