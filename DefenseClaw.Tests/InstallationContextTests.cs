using DefenseClaw.Core.Paths;
using DefenseClaw.Core.Runtime;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// <see cref="InstallationContext.Resolve"/> over synthetic PCs (<see cref="InstallationMachine"/>): the precedence table, the classification,
/// the Windows managed layout, and every way a selection can be invalid. No test touches the real disk, the real environment, Program Files or
/// ProgramData, so a developer machine with <c>DEFENSECLAW_HOME</c> set, or a real managed install, cannot change a result.
/// </summary>
public sealed class InstallationContextTests
{
    private const string Home = @"D:\dc\home";
    private const string UserConfig = @"D:\cfg\config.yaml";
    private const string DevHome = @"D:\dev\home";
    private const string DevCli = @"D:\dev\bin\defenseclaw.exe";
    private static readonly string SecureConfig = InstallationMachine.SecureClientState + @"\etc\config.yaml";
    private static readonly string StandaloneConfig = InstallationMachine.StandaloneState + @"\etc\config.yaml";

    private const string ManagedReason = "This installation is administrator managed. Use enterprise deployment tooling to change it.";

    private static InstallationMachine UserMachine() => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "user-config.yaml");

    // ------------------------------------------------------------------------------------------------------------ the precedence table

    /// <summary>One row of the table: a PC, and what the resolution must say about it.</summary>
    private sealed record Row(
        Func<InstallationMachine> Machine,
        InstallationSource Source,
        InstallationAccess Access,
        string Home,
        string Config,
        string? PinnedHome = null,
        string? PinnedConfig = null,
        bool PinsMode = false,
        string? Reason = null);

    private static readonly Dictionary<string, Row> Table = new(StringComparer.Ordinal)
    {
        ["nothing selects anything: the user default"] = new(
            UserMachine,
            InstallationSource.UserDefault, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml"),

        ["a fresh PC with no config yet is the user default and writable"] = new(
            () => new InstallationMachine(),
            InstallationSource.UserDefault, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml"),

        ["DEFENSECLAW_HOME selects the home and pins it"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", Home).WithFixture(Home + @"\config.yaml", "user-config.yaml"),
            InstallationSource.EnvironmentHome, InstallationAccess.UnmanagedMutable,
            Home, Home + @"\config.yaml", PinnedHome: Home),

        ["DEFENSECLAW_HOME with a trailing separator is the same folder"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", Home + @"\"),
            InstallationSource.EnvironmentHome, InstallationAccess.UnmanagedMutable,
            Home, Home + @"\config.yaml", PinnedHome: Home),

        ["DEFENSECLAW_CONFIG alone keeps the default home and pins only the config"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_CONFIG", UserConfig).WithFixture(UserConfig, "user-config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, UserConfig, PinnedConfig: UserConfig),

        ["DEFENSECLAW_CONFIG outranks DEFENSECLAW_HOME, which still supplies the home"] = new(
            () => new InstallationMachine()
                .WithEnvironment("DEFENSECLAW_CONFIG", UserConfig)
                .WithEnvironment("DEFENSECLAW_HOME", Home)
                .WithFixture(UserConfig, "user-config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.UnmanagedMutable,
            Home, UserConfig, PinnedHome: Home, PinnedConfig: UserConfig),

        ["the developer selector is the app override and carries its own home"] = new(
            () => new InstallationMachine { Runtime = RuntimeSelection.ForCli(DevCli, DevHome, null) },
            InstallationSource.AppOverride, InstallationAccess.UnmanagedMutable,
            DevHome, DevHome + @"\config.yaml", PinnedHome: DevHome),

        ["the app override outranks DEFENSECLAW_HOME"] = new(
            () => new InstallationMachine { Runtime = RuntimeSelection.ForCli(DevCli, DevHome, null) }.WithEnvironment("DEFENSECLAW_HOME", Home),
            InstallationSource.AppOverride, InstallationAccess.UnmanagedMutable,
            DevHome, DevHome + @"\config.yaml", PinnedHome: DevHome),

        ["a container's host copy is the app override too"] = new(
            () => new InstallationMachine { Runtime = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", @"D:\dc\data") },
            InstallationSource.AppOverride, InstallationAccess.UnmanagedMutable,
            @"D:\dc\data", @"D:\dc\data\config.yaml", PinnedHome: @"D:\dc\data"),

        ["DEFENSECLAW_CONFIG and a selector that name the same folder agree"] = new(
            () => new InstallationMachine { Runtime = RuntimeSelection.ForCli(DevCli, DevHome, null) }
                .WithEnvironment("DEFENSECLAW_CONFIG", DevHome + @"\config.yaml")
                .WithEnvironment("DEFENSECLAW_HOME", DevHome)
                .WithFixture(DevHome + @"\config.yaml", "user-config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.UnmanagedMutable,
            DevHome, DevHome + @"\config.yaml", PinnedHome: DevHome, PinnedConfig: DevHome + @"\config.yaml"),

        ["DEFENSECLAW_CONFIG and a selector that name different folders are invalid"] = new(
            () => new InstallationMachine { Runtime = RuntimeSelection.ForCli(DevCli, DevHome, null) }
                .WithEnvironment("DEFENSECLAW_CONFIG", UserConfig)
                .WithFixture(UserConfig, "user-config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, UserConfig, PinnedConfig: UserConfig,
            Reason: "DEFENSECLAW_CONFIG is set and the developer runtime selector names a different folder; remove one of them."),

        ["a Cisco Secure Client managed layout is the managed installation"] = new(
            () => new InstallationMachine().WithSecureClientLayout(),
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: ManagedReason),

        ["a standalone-profile managed layout is the managed installation"] = new(
            () => new InstallationMachine().WithStandaloneLayout(),
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.StandaloneState + @"\runtime", StandaloneConfig,
            PinnedHome: InstallationMachine.StandaloneState + @"\runtime", PinnedConfig: StandaloneConfig, PinsMode: true,
            Reason: ManagedReason),

        ["the managed layout outranks the user default even when the user has a config"] = new(
            () => UserMachine().WithSecureClientLayout(),
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: ManagedReason),

        ["a managed layout and a non-managed DEFENSECLAW_HOME conflict"] = new(
            () => new InstallationMachine().WithSecureClientLayout().WithEnvironment("DEFENSECLAW_HOME", Home).WithFixture(Home + @"\config.yaml", "user-config.yaml"),
            InstallationSource.EnvironmentHome, InstallationAccess.InvalidReadOnly,
            Home, Home + @"\config.yaml", PinnedHome: Home,
            Reason: "A managed (enterprise) DefenseClaw installation is present on this PC and conflicts with the selected non-managed installation."),

        ["a managed layout and a non-managed DEFENSECLAW_CONFIG conflict"] = new(
            () => new InstallationMachine().WithSecureClientLayout().WithEnvironment("DEFENSECLAW_CONFIG", UserConfig).WithFixture(UserConfig, "user-config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, UserConfig, PinnedConfig: UserConfig,
            Reason: "A managed (enterprise) DefenseClaw installation is present on this PC and conflicts with the selected non-managed installation."),

        ["a managed layout and a developer selector conflict"] = new(
            () => new InstallationMachine { Runtime = RuntimeSelection.ForCli(DevCli, DevHome, null) }.WithSecureClientLayout(),
            InstallationSource.AppOverride, InstallationAccess.InvalidReadOnly,
            DevHome, DevHome + @"\config.yaml", PinnedHome: DevHome,
            Reason: "A managed (enterprise) DefenseClaw installation is present on this PC and conflicts with the selected non-managed installation."),

        ["DEFENSECLAW_CONFIG naming the managed config selects the managed installation and its runtime home"] = new(
            () => new InstallationMachine().WithSecureClientLayout().WithEnvironment("DEFENSECLAW_CONFIG", SecureConfig),
            InstallationSource.EnvironmentConfig, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: ManagedReason),

        ["a user folder whose config says managed is managed, not invalid, next to a managed layout"] = new(
            () => new InstallationMachine().WithSecureClientLayout().WithEnvironment("DEFENSECLAW_HOME", Home).WithFixture(Home + @"\config.yaml", "managed-secureclient-config.yaml"),
            InstallationSource.EnvironmentHome, InstallationAccess.ManagedReadOnly,
            Home, Home + @"\config.yaml", PinnedHome: Home, PinsMode: true,
            Reason: ManagedReason),

        ["both profiles at once are invalid"] = new(
            () => new InstallationMachine().WithSecureClientLayout().WithStandaloneLayout(),
            InstallationSource.ManagedLayout, InstallationAccess.InvalidReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig,
            Reason: "Both a Cisco Secure Client and a standalone managed DefenseClaw installation are present; DefenseClaw supports only one at a time."),

        ["the install root alone is evidence, and a config nobody can read says so"] = new(
            () => new InstallationMachine().WithFile(InstallationMachine.SecureClientInstall + @"\bin\defenseclaw.exe", "MZ"),
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: "The administrator-owned managed config could not be read, so state-changing actions stay off."),

        ["a managed config a standard user cannot read stays managed"] = new(
            () =>
            {
                var machine = new InstallationMachine();
                machine.Directories.Add(InstallationMachine.SecureClientState);
                machine.Unreadable.Add(SecureConfig);
                return machine;
            },
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: "The administrator-owned managed config could not be read, so state-changing actions stay off."),

        ["an uninstall's tombstone is not a managed layout"] = new(
            () => UserMachine().WithSecureClientLayout().WithFixture(InstallationMachine.SecureClientState + @"\install\deployment.json", "deployment-tombstone.json"),
            InstallationSource.UserDefault, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml"),

        ["an installed record keeps the layout"] = new(
            () => new InstallationMachine().WithSecureClientLayout().WithFixture(InstallationMachine.SecureClientState + @"\install\deployment.json", "deployment-installed.json"),
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: ManagedReason),

        ["a record from before tombstones means installed"] = new(
            () => new InstallationMachine().WithSecureClientLayout().WithFixture(InstallationMachine.SecureClientState + @"\install\deployment.json", "deployment-legacy.json"),
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: ManagedReason),

        ["a record nobody can read still counts as installed"] = new(
            () =>
            {
                var machine = new InstallationMachine().WithSecureClientLayout();
                machine.Unreadable.Add(InstallationMachine.SecureClientState + @"\install\deployment.json");
                return machine;
            },
            InstallationSource.ManagedLayout, InstallationAccess.ManagedReadOnly,
            InstallationMachine.SecureClientState + @"\runtime", SecureConfig,
            PinnedHome: InstallationMachine.SecureClientState + @"\runtime", PinnedConfig: SecureConfig, PinsMode: true,
            Reason: ManagedReason),

        ["a user config that says managed_enterprise is managed"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "managed-secureclient-config.yaml"),
            InstallationSource.UserDefault, InstallationAccess.ManagedReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml", PinsMode: true,
            Reason: ManagedReason),

        ["the legacy word managed means managed_enterprise"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "user-config-managed-alias.yaml"),
            InstallationSource.UserDefault, InstallationAccess.ManagedReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml", PinsMode: true,
            Reason: ManagedReason),

        ["the legacy word standalone is unmanaged_byod and writable"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "user-config-byod.yaml"),
            InstallationSource.UserDefault, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml"),

        ["DEFENSECLAW_DEPLOYMENT_MODE pinned to managed_enterprise makes a plain config managed"] = new(
            () => UserMachine().WithEnvironment("DEFENSECLAW_DEPLOYMENT_MODE", "managed_enterprise"),
            InstallationSource.UserDefault, InstallationAccess.ManagedReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml", PinsMode: true,
            Reason: ManagedReason),

        ["a pin that disagrees with the config is invalid"] = new(
            () => new InstallationMachine()
                .WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "managed-secureclient-config.yaml")
                .WithEnvironment("DEFENSECLAW_DEPLOYMENT_MODE", "unmanaged_byod"),
            InstallationSource.UserDefault, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml",
            Reason: "DEFENSECLAW_DEPLOYMENT_MODE conflicts with the deployment_mode in config.yaml."),

        ["a pin the CLI would refuse is invalid"] = new(
            () => UserMachine().WithEnvironment("DEFENSECLAW_DEPLOYMENT_MODE", "roaming_laptop"),
            InstallationSource.UserDefault, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml",
            Reason: "DEFENSECLAW_DEPLOYMENT_MODE has an unsupported value: roaming_laptop."),

        ["a deployment_mode the CLI would refuse is invalid"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "invalid-mode-config.yaml"),
            InstallationSource.UserDefault, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml",
            Reason: "config.yaml deployment_mode has an unsupported value: roaming_laptop."),

        ["a deployment_mode that is not a string is invalid"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "mode-list-config.yaml"),
            InstallationSource.UserDefault, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml",
            Reason: "config.yaml deployment_mode must be a string."),

        ["a relative DEFENSECLAW_CONFIG is invalid"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_CONFIG", @"sandbox\config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, Path.GetFullPath(@"sandbox\config.yaml"), PinnedConfig: Path.GetFullPath(@"sandbox\config.yaml"),
            Reason: "DEFENSECLAW_CONFIG must be an absolute path (a drive letter or a network share)."),

        ["a drive-relative DEFENSECLAW_HOME is invalid"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", "D:dc"),
            InstallationSource.EnvironmentHome, InstallationAccess.InvalidReadOnly,
            Path.GetFullPath("D:dc"), Path.Combine(Path.GetFullPath("D:dc"), "config.yaml"), PinnedHome: Path.GetFullPath("D:dc"),
            Reason: "DEFENSECLAW_HOME must be an absolute path (a drive letter or a network share)."),

        ["a root-relative DEFENSECLAW_HOME is invalid"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", @"\dc"),
            InstallationSource.EnvironmentHome, InstallationAccess.InvalidReadOnly,
            Path.GetFullPath(@"\dc"), Path.Combine(Path.GetFullPath(@"\dc"), "config.yaml"), PinnedHome: Path.GetFullPath(@"\dc"),
            Reason: "DEFENSECLAW_HOME must be an absolute path (a drive letter or a network share)."),

        ["a network share is an absolute path"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_CONFIG", @"\\fileserver\dc\config.yaml").WithFixture(@"\\fileserver\dc\config.yaml", "user-config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, @"\\fileserver\dc\config.yaml", PinnedConfig: @"\\fileserver\dc\config.yaml"),

        ["a DEFENSECLAW_CONFIG that names a missing file is invalid"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_CONFIG", @"D:\missing\config.yaml"),
            InstallationSource.EnvironmentConfig, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, @"D:\missing\config.yaml", PinnedConfig: @"D:\missing\config.yaml",
            Reason: "The config.yaml that DEFENSECLAW_CONFIG names does not exist."),

        ["a missing config under DEFENSECLAW_HOME is just a PC that has not run init"] = new(
            () => new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", Home),
            InstallationSource.EnvironmentHome, InstallationAccess.UnmanagedMutable,
            Home, Home + @"\config.yaml", PinnedHome: Home),

        ["a user config that exists but cannot be read is unknown, so read-only"] = new(
            () =>
            {
                var machine = new InstallationMachine();
                machine.Unreadable.Add(InstallationMachine.DefaultHome + @"\config.yaml");
                return machine;
            },
            InstallationSource.UserDefault, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml",
            Reason: "config.yaml exists but could not be read, so what it says about this installation is unknown."),

        ["a config that is not a mapping is invalid"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "not-a-mapping-config.yaml"),
            InstallationSource.UserDefault, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml",
            Reason: "config.yaml must contain a top-level mapping."),

        ["a data_dir that depends on the working directory is invalid"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "relative-data-dir-config.yaml"),
            InstallationSource.UserDefault, InstallationAccess.InvalidReadOnly,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml",
            Reason: "config.yaml data_dir must be an absolute path."),

        ["a ~ in data_dir and observability.local.path is the user's profile and fine"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "tilde-data-dir-config.yaml"),
            InstallationSource.UserDefault, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml"),

        ["a config that is only a comment is an empty mapping and fine"] = new(
            () => new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "comments-only-config.yaml"),
            InstallationSource.UserDefault, InstallationAccess.UnmanagedMutable,
            InstallationMachine.DefaultHome, InstallationMachine.DefaultHome + @"\config.yaml"),
    };

    public static TheoryData<string> RowNames => new(Table.Keys);

    [Theory]
    [MemberData(nameof(RowNames))]
    public void The_precedence_table_resolves_every_row(string name)
    {
        var row = Table[name];

        var context = row.Machine().Resolve();

        Assert.Equal(row.Source, context.Source);
        Assert.Equal(row.Access, context.Access);
        Assert.Equal(row.Home, context.HomeRoot);
        Assert.Equal(row.Config, context.ConfigPath);
        Assert.Equal(row.PinnedHome, context.PinnedHome);
        Assert.Equal(row.PinnedConfig, context.PinnedConfig);
        Assert.Equal(row.Access == InstallationAccess.UnmanagedMutable, context.IsMutable);

        // Every read-only row carries a sentence; the writable ones carry none.
        if (row.Reason is not null)
        {
            Assert.Equal(row.Reason, context.Reason);
            Assert.Equal(row.Reason, context.BlockedReason);
        }
        else if (context.IsMutable)
        {
            Assert.Null(context.Reason);
            Assert.Null(context.BlockedReason);
        }

        // The environment the children get: the home and the config as pinned, the mode only for a managed installation.
        var protectedVariables = context.ProtectedEnvironment().ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(row.PinnedHome, protectedVariables["DEFENSECLAW_HOME"]);
        Assert.Equal(row.PinnedConfig, protectedVariables["DEFENSECLAW_CONFIG"]);
        Assert.Equal(row.PinsMode ? "managed_enterprise" : null, protectedVariables["DEFENSECLAW_DEPLOYMENT_MODE"]);
    }

    [Fact]
    public void Every_read_only_row_has_one_sentence_and_no_row_loses_its_reason()
    {
        foreach (var (name, row) in Table.Where(r => r.Value.Access != InstallationAccess.UnmanagedMutable))
        {
            var context = row.Machine().Resolve();

            Assert.False(string.IsNullOrWhiteSpace(context.Reason), name);
            Assert.EndsWith(".", context.Reason!, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', context.Reason!);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ labels and what is shown

    [Fact]
    public void The_labels_name_the_source_and_the_access()
    {
        Assert.Equal("User default", UserMachine().Resolve().SourceLabel);
        Assert.Equal("Unmanaged — setup changes allowed", UserMachine().Resolve().AccessLabel);
        Assert.Equal("DEFENSECLAW_HOME", new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", Home).Resolve().SourceLabel);
        Assert.Equal("DEFENSECLAW_CONFIG", new InstallationMachine().WithEnvironment("DEFENSECLAW_CONFIG", UserConfig).WithFixture(UserConfig, "user-config.yaml").Resolve().SourceLabel);
        Assert.StartsWith("Developer runtime selector", new InstallationMachine { Runtime = RuntimeSelection.ForCli(DevCli, DevHome, null) }.Resolve().SourceLabel, StringComparison.Ordinal);

        var secure = new InstallationMachine().WithSecureClientLayout().Resolve();
        Assert.Equal("Managed installation (Cisco Secure Client)", secure.SourceLabel);
        Assert.Equal("Managed enterprise — read only", secure.AccessLabel);
        Assert.Equal("Managed installation (standalone)", new InstallationMachine().WithStandaloneLayout().Resolve().SourceLabel);

        var invalid = new InstallationMachine().WithEnvironment("DEFENSECLAW_HOME", "relative").Resolve();
        Assert.Equal("Invalid installation selection — read only", invalid.AccessLabel);
        Assert.Equal("DEFENSECLAW_HOME · Invalid installation selection — read only", invalid.Describe());
    }

    [Fact]
    public void The_managed_layout_is_remembered_with_its_profile_and_paths()
    {
        var context = new InstallationMachine().WithStandaloneLayout().Resolve();

        var layout = Assert.IsType<ManagedLayout>(context.Layout);
        Assert.Equal(ManagedProfiles.Standalone, layout.Profile);
        Assert.Equal(InstallationMachine.StandaloneInstall, layout.InstallRoot);
        Assert.Equal(InstallationMachine.StandaloneState, layout.StateRoot);
        Assert.Equal(InstallationMachine.StandaloneInstall + @"\bin\defenseclaw.exe", layout.CliPath);
        Assert.Equal(InstallationMachine.StandaloneState + @"\install\deployment.json", layout.MetadataPath);
        Assert.Equal("managed_enterprise", context.DeploymentMode);

        var secure = ManagedLayout.For(ManagedProfiles.SecureClient, @"C:\PF", @"C:\PD");
        Assert.Equal(@"C:\PF\Cisco\Cisco Secure Client\DefenseClaw", secure.InstallRoot);
        Assert.Equal(@"C:\PD\Cisco\Cisco Secure Client\DefenseClaw", secure.StateRoot);
        Assert.Equal(@"C:\PD\Cisco\Cisco Secure Client\DefenseClaw\runtime", secure.RuntimeDirectory);
    }

    [Fact]
    public void A_writable_installation_pins_nothing_so_a_default_run_changes_nothing()
    {
        var context = UserMachine().Resolve();

        Assert.Null(context.PinnedHome);
        Assert.Null(context.PinnedConfig);
        Assert.All(context.ProtectedEnvironment(), pin => Assert.Null(pin.Value));
        Assert.Equal(new[] { "DEFENSECLAW_HOME", "DEFENSECLAW_CONFIG", "DEFENSECLAW_DEPLOYMENT_MODE" }, context.ProtectedEnvironment().Select(p => p.Key));
        Assert.Equal(InstallationContext.ProtectedVariables, context.ProtectedEnvironment().Select(p => p.Key));
    }

    [Fact]
    public void The_developer_override_is_recorded_for_the_installation_block()
    {
        var cli = new InstallationMachine { Runtime = RuntimeSelection.ForCli(DevCli, DevHome, null) }.Resolve();
        var container = new InstallationMachine { Runtime = RuntimeSelection.ForContainer("dc-next-1", "http://127.0.0.1:18971", @"D:\dc\data") }.Resolve();
        var plain = UserMachine().Resolve();

        Assert.Equal(DevHome, cli.OverridePath);
        Assert.True(cli.UsesDeveloperRuntime);
        Assert.Equal(@"D:\dc\data", container.OverridePath);
        Assert.True(container.UsesDeveloperRuntime);
        Assert.Null(plain.OverridePath);
        Assert.False(plain.UsesDeveloperRuntime);
    }

    // ------------------------------------------------------------------------------------------------------------ the bits

    [Theory]
    [InlineData("managed", "managed_enterprise")]
    [InlineData("managed_enterprise", "managed_enterprise")]
    [InlineData("  Managed_Enterprise ", "managed_enterprise")]
    [InlineData("standalone", "unmanaged_byod")]
    [InlineData("unmanaged_byod", "unmanaged_byod")]
    [InlineData("ci", "ci_cd")]
    [InlineData("ci_cd", "ci_cd")]
    [InlineData("edge", "server")]
    [InlineData("server", "server")]
    [InlineData("sandboxed", "sandboxed")]
    [InlineData("saas", "saas")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("managed-enterprise", null)]
    [InlineData("roaming_laptop", null)]
    public void The_deployment_mode_is_normalised_as_the_cli_reads_it(string? raw, string? expected) =>
        Assert.Equal(expected, InstallationContext.NormalizeDeploymentMode(raw));

    [Theory]
    [InlineData(@"C:\dc", true)]
    [InlineData(@"c:/dc/config.yaml", true)]
    [InlineData(@"C:\", true)]
    [InlineData(@"\\fileserver\share\dc", true)]
    [InlineData(@"\\?\C:\dc", true)]
    [InlineData(@"dc", false)]
    [InlineData(@".\dc", false)]
    [InlineData(@"..\dc", false)]
    [InlineData(@"C:dc", false)]
    [InlineData(@"\dc", false)]
    [InlineData("/dc", false)]
    [InlineData("~", false)]
    [InlineData(@"~\dc", false)]
    [InlineData(@"%USERPROFILE%\dc", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void An_absolute_path_is_a_drive_letter_or_a_share_and_nothing_that_depends_on_a_working_directory(string? path, bool expected) =>
        Assert.Equal(expected, InstallationContext.IsAbsoluteWindowsPath(path));

    [Fact]
    public void A_value_in_a_reason_cannot_carry_a_line_break_or_a_control_character()
    {
        var context = new InstallationMachine()
            .WithEnvironment("DEFENSECLAW_DEPLOYMENT_MODE", "bad\r\nmode\u202E")
            .Resolve();

        Assert.True(context.IsInvalid);
        Assert.DoesNotContain('\n', context.Reason!);
        Assert.DoesNotContain('\r', context.Reason!);
        Assert.DoesNotContain('\u202E', context.Reason!);
    }

    [Fact]
    public void A_broken_yaml_file_is_invalid_with_the_parsers_own_first_line_and_a_repaired_one_is_writable_again()
    {
        var broken = new InstallationMachine().WithFixture(InstallationMachine.DefaultHome + @"\config.yaml", "broken-config.yaml").Resolve();
        var repaired = UserMachine().Resolve();

        Assert.True(broken.IsInvalid);
        Assert.StartsWith("config.yaml is not valid YAML: ", broken.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', broken.Reason!);
        Assert.True(repaired.IsMutable);
    }

    // ------------------------------------------------------------------------------------------------------------ the real reader

    [Fact]
    public void The_real_reader_returns_the_text_a_missing_file_as_null_and_never_a_file_past_the_cap()
    {
        using var temp = new TempDirectory();
        var present = temp.Write("config.yaml", "deployment_mode: managed\n");
        var big = temp.File("big.yaml");
        File.WriteAllBytes(big, new byte[InstallationContext.MaxConfigBytes + 1]);

        Assert.Equal("deployment_mode: managed\n", InstallationContext.ReadConfigText(present));
        Assert.Null(InstallationContext.ReadConfigText(temp.File("missing.yaml")));
        Assert.Null(InstallationContext.ReadConfigText(big));
        Assert.Null(InstallationContext.ReadConfigText(temp.Path));
    }

    [Fact]
    public void The_real_reader_reads_a_file_another_process_holds_open_for_writing()
    {
        using var temp = new TempDirectory();
        var path = temp.File("config.yaml");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("gateway:\n  api_port: 18970\n"u8);
        writer.Flush();

        Assert.Equal("gateway:\n  api_port: 18970\n", InstallationContext.ReadConfigText(path));
    }
}
