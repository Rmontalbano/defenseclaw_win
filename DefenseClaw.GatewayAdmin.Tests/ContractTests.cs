using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin.Tests;

public class VerbTests
{
    [Theory]
    [InlineData("install-copy", AdminVerb.InstallCopy)]
    [InlineData("start", AdminVerb.Start)]
    [InlineData("restart", AdminVerb.Restart)]
    [InlineData("stop", AdminVerb.Stop)]
    [InlineData("repair-ownership", AdminVerb.RepairOwnership)]
    [InlineData("status", AdminVerb.Status)]
    public void Each_known_verb_alone_is_accepted(string word, AdminVerb expected)
    {
        Assert.True(AdminVerbs.TryParse(new[] { word }, out var verb, out var problem));
        Assert.Equal(expected, verb);
        Assert.Equal(string.Empty, problem);
        Assert.Equal(word, AdminVerbs.Name(verb));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Start")]
    [InlineData("START")]
    [InlineData("start ")]
    [InlineData(" start")]
    [InlineData("/start")]
    [InlineData("-start")]
    [InlineData("--help")]
    [InlineData("/?")]
    [InlineData("start;whoami")]
    [InlineData("start --path C:\\x")]
    [InlineData("install_copy")]
    [InlineData("install")]
    [InlineData("shell")]
    [InlineData("run")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    public void Anything_else_is_refused(string word)
    {
        Assert.False(AdminVerbs.TryParse(new[] { word }, out _, out var problem));
        Assert.Contains("Unknown verb", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void No_argument_is_refused()
    {
        Assert.False(AdminVerbs.TryParse(Array.Empty<string>(), out _, out var problem));
        Assert.Contains("required", problem, StringComparison.Ordinal);
        Assert.False(AdminVerbs.TryParse(null, out _, out _));
    }

    [Theory]
    [InlineData("start", "extra")]
    [InlineData("start", "--path")]
    [InlineData("install-copy", "C:\\evil.exe")]
    [InlineData("status", "status")]
    [InlineData("repair-ownership", "C:\\Users\\other")]
    public void An_extra_argument_after_a_known_verb_is_refused(string verb, string extra)
    {
        Assert.False(AdminVerbs.TryParse(new[] { verb, extra }, out _, out var problem));
        Assert.Contains("no other argument", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_usage_line_lists_exactly_the_six_verbs()
    {
        Assert.Equal("install-copy | start | restart | stop | repair-ownership | status", AdminVerbs.Usage());
        Assert.Equal(6, AdminVerbs.All.Count);
        Assert.False(AdminVerbs.Changes(AdminVerb.Status));
        Assert.All(AdminVerbs.All.Where(v => v != AdminVerb.Status), v => Assert.True(AdminVerbs.Changes(v)));
    }

    [Fact]
    public void The_exit_codes_are_the_documented_ones()
    {
        Assert.Equal(64, AdminExit.Usage);
        Assert.Equal(77, AdminExit.Refused);
        Assert.Equal(1223, AdminExit.UacCancelled);
    }
}

public class LayoutTests
{
    private const string Sid = Rig.UserSid;

    [Fact]
    public void Every_path_is_derived_from_the_three_inputs()
    {
        var layout = AdminLayout.Create(@"C:\Program Files", Sid, @"C:\Users\tester");

        Assert.Equal(@"C:\Program Files\DefenseClaw Companion", layout.ProductRoot);
        Assert.Equal(@"C:\Program Files\DefenseClaw Companion\GatewayAdmin\DefenseClaw.GatewayAdmin.exe", layout.HelperExe);
        Assert.Equal(@"C:\Program Files\DefenseClaw Companion\AdminGateway\" + Sid + @"\defenseclaw-gateway.exe", layout.GatewayCopyPath);
        Assert.Equal(@"C:\Program Files\DefenseClaw Companion\AdminGateway\" + Sid + @"\copy.json", layout.CopyRecordPath);
        Assert.Equal(@"C:\Users\tester\.local\bin\defenseclaw-gateway.exe", layout.InstalledGatewayPath);
        Assert.Equal(@"C:\Users\tester\.defenseclaw", layout.DataDirectory);
        Assert.Equal(@"C:\Users\tester\AppData\Local\DefenseClaw.App\admin-gateway\manifest.json", layout.ManifestPath);
        Assert.Equal(new[] { layout.ProgramFiles, layout.ProductRoot, layout.AdminGatewayRoot, layout.UserDirectory }, layout.CopyChain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tester")]
    [InlineData(@"..\..\evil")]
    [InlineData(@"S-1-5-21-1\..\..\x")]
    [InlineData("S-1-5-21-1/2")]
    [InlineData("S-1-")]
    [InlineData("s-1-5-21")]
    [InlineData("S-1-5-21-1111 ")]
    public void A_user_that_is_not_a_sid_cannot_be_a_folder_name(string sid)
    {
        Assert.False(AdminLayout.IsValidSid(sid));
        Assert.Throws<ArgumentException>(() => AdminLayout.Create(@"C:\PF", sid, @"C:\Users\tester"));
    }

    [Theory]
    [InlineData(@"relative\path")]
    [InlineData(@"C:\Users\..\Windows")]
    [InlineData(@"\\server\share\tester")]
    [InlineData("")]
    public void A_profile_must_be_absolute_and_already_normalised(string profile)
    {
        Assert.Throws<ArgumentException>(() => AdminLayout.Create(@"C:\PF", Sid, profile));
    }

    [Fact]
    public void A_profile_inside_program_files_is_refused()
    {
        Assert.Throws<ArgumentException>(() => AdminLayout.Create(@"C:\PF", Sid, @"C:\PF\tester"));
        Assert.Throws<ArgumentException>(() => AdminLayout.Create(@"C:\Users\x\PF", Sid, @"C:\Users\x"));
    }

    [Fact]
    public void The_copy_is_recognised_by_its_normalised_path_only()
    {
        var layout = AdminLayout.Create(@"C:\PF", Sid, @"C:\Users\tester");
        Assert.True(layout.IsGatewayCopy(layout.GatewayCopyPath.ToUpperInvariant()));
        Assert.True(layout.IsGatewayCopy(layout.UserDirectory + @"\.\defenseclaw-gateway.exe"));
        Assert.False(layout.IsGatewayCopy(layout.InstalledGatewayPath));
        Assert.False(layout.IsGatewayCopy(layout.UserDirectory + @"\sub\defenseclaw-gateway.exe"));
        Assert.False(layout.IsGatewayCopy(null));
        Assert.False(layout.IsGatewayCopy(""));
    }
}

public class AclPolicyTests
{
    private static NodeSecurity Node(string owner, params AclEntry[] entries) => new(owner, true, entries);

    [Fact]
    public void An_admin_only_file_passes()
    {
        Assert.Empty(AdminAcl.Problems(Node(AdminAcl.Administrators, FakeFs.AdminOnly().ToArray()), "file"));
        Assert.Empty(AdminAcl.Problems(Node(AdminAcl.TrustedInstaller, FakeFs.AdminOnly().ToArray()), "file"));
        Assert.Empty(AdminAcl.Problems(Node(AdminAcl.System, FakeFs.AdminOnly().ToArray()), "file"));
    }

    [Theory]
    [InlineData(AdminAcl.Users, 0x2u)]          // write data
    [InlineData(AdminAcl.Users, 0x4u)]          // append
    [InlineData(AdminAcl.AuthenticatedUsers, 0x40u)] // delete children
    [InlineData(AdminAcl.Everyone, 0x10000u)]   // delete
    [InlineData(AdminAcl.Users, 0x40000u)]      // change permissions
    [InlineData(AdminAcl.Users, 0x80000u)]      // take ownership
    [InlineData(AdminAcl.Users, 0x100u)]        // write attributes
    [InlineData(AdminAcl.Users, 0x10u)]         // write extended attributes
    [InlineData(AdminAcl.Users, 0x1F01FFu)]     // full control
    [InlineData(AdminAcl.Users, 0x10000000u)]   // GENERIC_ALL, unmapped
    [InlineData(AdminAcl.Users, 0x40000000u)]   // GENERIC_WRITE, unmapped
    [InlineData(Rig.UserSid, 0x2u)]             // the user themself
    public void Any_other_principal_that_may_write_in_any_way_fails(string sid, uint mask)
    {
        var entries = FakeFs.AdminOnly();
        entries.Add(new AclEntry(sid, true, mask));

        var problems = AdminAcl.Problems(Node(AdminAcl.Administrators, entries.ToArray()), "copy");

        var problem = Assert.Single(problems);
        Assert.Contains(sid, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_and_execute_for_anyone_is_fine()
    {
        var entries = FakeFs.AdminOnly();
        entries.Add(new AclEntry(AdminAcl.Everyone, true, AdminAcl.ReadExecuteMask));
        entries.Add(new AclEntry(Rig.UserSid, true, AdminAcl.ReadExecuteMask));

        Assert.Empty(AdminAcl.Problems(Node(AdminAcl.Administrators, entries.ToArray()), "copy"));
    }

    [Fact]
    public void Deny_entries_and_inherit_only_entries_do_not_count_for_the_object_itself()
    {
        var entries = FakeFs.AdminOnly();
        entries.Add(new AclEntry(AdminAcl.Users, false, 0x1F01FF));
        entries.Add(new AclEntry(AdminAcl.Users, true, 0x1F01FF, InheritOnly: true));

        Assert.Empty(AdminAcl.Problems(Node(AdminAcl.Administrators, entries.ToArray()), "folder"));
    }

    [Fact]
    public void Creator_owner_may_write_because_it_means_the_administrator_that_created_it()
    {
        var entries = FakeFs.AdminOnly();
        entries.Add(new AclEntry(AdminAcl.CreatorOwner, true, 0x1F01FF));

        Assert.Empty(AdminAcl.Problems(Node(AdminAcl.Administrators, entries.ToArray()), "folder"));
    }

    [Theory]
    [InlineData(Rig.UserSid)]
    [InlineData(AdminAcl.Users)]
    [InlineData(null)]
    public void An_owner_who_is_not_an_administrator_fails(string? owner)
    {
        var problem = Assert.Single(AdminAcl.Problems(new NodeSecurity(owner, true, FakeFs.AdminOnly().ToArray()), "file"));
        Assert.Contains("not an administrator", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_access_list_means_everyone_may_change_it()
    {
        var problems = AdminAcl.Problems(new NodeSecurity(AdminAcl.Administrators, false, Array.Empty<AclEntry>()), "file");

        Assert.Contains(problems, p => p.Contains("no access list", StringComparison.Ordinal));
    }
}

public class InspectorTests
{
    [Fact]
    public void Nothing_installed_is_not_a_fault()
    {
        var rig = new Rig(atHome: false);
        var inspection = new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout);

        Assert.False(inspection.Exists);
        Assert.False(inspection.Trusted);
        Assert.Empty(inspection.Problems);
    }

    [Fact]
    public void An_installed_recorded_admin_only_copy_is_trusted()
    {
        var rig = new Rig(withCopy: true);
        var inspection = new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout);

        Assert.True(inspection.Exists);
        Assert.True(inspection.Secure);
        Assert.True(inspection.MatchesRecord);
        Assert.True(inspection.Trusted);
        Assert.Equal(Rig.Sha(Rig.GatewayBytes), inspection.Sha256);
        Assert.Equal("1.0.0", inspection.Record?.Version);
    }

    [Fact]
    public void Changed_bytes_no_longer_match_the_record()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.GatewayCopyPath].Data = Rig.GatewayBytes.Select(b => (byte)(b ^ 1)).ToArray();

        var inspection = new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout);

        Assert.True(inspection.Secure);
        Assert.False(inspection.MatchesRecord);
        Assert.False(inspection.Trusted);
    }

    [Fact]
    public void A_file_a_user_can_write_is_not_trusted_even_with_the_right_bytes()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.GatewayCopyPath].Acl.Add(new AclEntry(Rig.UserSid, true, 0x2));

        var inspection = new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout);

        Assert.True(inspection.MatchesRecord);
        Assert.False(inspection.Secure);
        Assert.False(inspection.Trusted);
        Assert.Contains(inspection.Problems, p => p.Contains(Rig.UserSid, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Folders))]
    public void A_loose_folder_anywhere_on_the_way_down_is_not_trusted(Func<AdminLayout, string> folder)
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[folder(rig.Layout)].Acl.Add(new AclEntry(AdminAcl.Users, true, 0x2));

        Assert.False(new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout).Trusted);
    }

    [Theory]
    [MemberData(nameof(Folders))]
    public void A_reparse_point_anywhere_on_the_way_down_is_not_trusted_and_the_file_is_not_read(Func<AdminLayout, string> folder)
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[folder(rig.Layout)].IsReparse = true;
        var inspector = new AdminCopyInspector(rig.Fs);

        var inspection = inspector.InspectGatewayCopy(rig.Layout);

        Assert.False(inspection.Trusted);
        Assert.Contains(inspection.Problems, p => p.Contains("reparse point", StringComparison.Ordinal));
        Assert.Equal(0, inspector.HashesComputed);
        Assert.Null(inspection.Sha256);
    }

    public static TheoryData<Func<AdminLayout, string>> Folders() => new()
    {
        l => l.ProgramFiles,
        l => l.ProductRoot,
        l => l.AdminGatewayRoot,
        l => l.UserDirectory,
        l => l.GatewayCopyPath,
        l => l.CopyRecordPath,
    };

    [Fact]
    public void The_copy_owned_by_the_user_is_not_trusted()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.GatewayCopyPath].Owner = Rig.UserSid;

        Assert.False(new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout).Trusted);
    }

    [Fact]
    public void A_copy_without_a_record_is_present_but_not_trusted()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items.Remove(rig.Layout.CopyRecordPath);

        var inspection = new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout);

        Assert.True(inspection.Exists);
        Assert.False(inspection.Trusted);
        Assert.Contains(inspection.Problems, p => p.Contains("no record", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_access_list_is_a_fault_not_a_pass()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.GatewayCopyPath].SecurityUnreadable = true;

        var inspection = new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout);

        Assert.False(inspection.Trusted);
        Assert.Contains(inspection.Problems, p => p.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void The_hash_is_cached_by_file_identity_and_recomputed_when_the_file_changes()
    {
        var rig = new Rig(withCopy: true);
        var inspector = new AdminCopyInspector(rig.Fs);

        _ = inspector.InspectGatewayCopy(rig.Layout);
        _ = inspector.InspectGatewayCopy(rig.Layout);
        Assert.Equal(1, inspector.HashesComputed);

        rig.Fs.Items[rig.Layout.GatewayCopyPath].LastWrite = rig.Fs.Items[rig.Layout.GatewayCopyPath].LastWrite.AddMinutes(1);
        _ = inspector.InspectGatewayCopy(rig.Layout);
        Assert.Equal(2, inspector.HashesComputed);
    }

    [Fact]
    public void The_access_lists_are_read_every_time_even_when_the_hash_is_cached()
    {
        var rig = new Rig(withCopy: true);
        var inspector = new AdminCopyInspector(rig.Fs);
        Assert.True(inspector.InspectGatewayCopy(rig.Layout).Trusted);

        rig.Fs.Items[rig.Layout.UserDirectory].Acl.Add(new AclEntry(Rig.UserSid, true, 0x2));

        Assert.False(inspector.InspectGatewayCopy(rig.Layout).Trusted);
    }

    [Fact]
    public void The_installed_gateway_is_hashed_and_a_link_on_its_path_is_refused()
    {
        var rig = new Rig();
        var inspector = new AdminCopyInspector(rig.Fs);
        var (sha, problem) = inspector.HashInstalledGateway(rig.Layout);
        Assert.Equal(Rig.Sha(Rig.GatewayBytes), sha);
        Assert.Null(problem);

        rig.Fs.Items[Path.Combine(Rig.Profile, ".local", "bin")].IsReparse = true;
        (sha, problem) = inspector.HashInstalledGateway(rig.Layout);
        Assert.Null(sha);
        Assert.Contains("link or junction", problem, StringComparison.Ordinal);

        rig.Fs.Items[Path.Combine(Rig.Profile, ".local", "bin")].IsReparse = false;
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].IsReparse = true;
        Assert.Contains("link", inspector.HashInstalledGateway(rig.Layout).Problem, StringComparison.Ordinal);

        rig.Fs.Items.Remove(rig.Layout.InstalledGatewayPath);
        Assert.Contains("not where the installer puts it", inspector.HashInstalledGateway(rig.Layout).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_installed_gateway_is_refused()
    {
        var rig = new Rig();
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = Array.Empty<byte>();

        Assert.Contains("implausible", new AdminCopyInspector(rig.Fs).HashInstalledGateway(rig.Layout).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_says_whether_the_copy_is_the_installed_gateway()
    {
        var rig = new Rig(withCopy: true);
        var inspector = new AdminCopyInspector(rig.Fs);

        var same = inspector.Report(rig.Layout);
        Assert.True(same.HelperTrusted);
        Assert.True(same.CopyTrusted);
        Assert.True(same.CopyMatchesInstalled);

        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = Rig.GatewayBytes.Reverse().ToArray();
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].LastWrite = rig.Fs.Items[rig.Layout.InstalledGatewayPath].LastWrite.AddMinutes(1);
        var differs = inspector.Report(rig.Layout);
        Assert.False(differs.CopyMatchesInstalled);
        Assert.Equal("1.0.0", differs.CopyVersion);
        Assert.Equal(differs.ToJson(), AdminStatusReport.TryParse(differs.ToJson())!.ToJson());
    }
}

public class RecordTests
{
    [Fact]
    public void A_manifest_round_trips_and_is_checked_for_user_and_age()
    {
        var now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        var manifest = new ReleaseManifest(1, Rig.UserSid, "1.0.0", Rig.Sha(Rig.GatewayBytes), new string('b', 64), "Verified", now.ToString("O"));
        var parsed = ReleaseManifest.TryParse(manifest.ToJson())!;

        Assert.Null(parsed.Problem(Rig.UserSid, now.AddMinutes(1)));
        Assert.Contains("another user", parsed.Problem(Rig.OtherSid, now)!, StringComparison.Ordinal);
        Assert.Contains("older than 30 minutes", parsed.Problem(Rig.UserSid, now.AddMinutes(31))!, StringComparison.Ordinal);
        Assert.Contains("future", parsed.Problem(Rig.UserSid, now.AddMinutes(-10))!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"schema\":2}")]
    [InlineData("{\"schema\":1,\"userSid\":\"x\",\"gatewaySha256\":\"abc\",\"zipSha256\":\"abc\"}")]
    public void A_manifest_that_is_not_one_is_null(string json)
    {
        Assert.Null(ReleaseManifest.TryParse(json));
        Assert.Null(CopyRecord.TryParse(json));
        Assert.Null(HelperRecord.TryParse(json));
    }

    [Fact]
    public void Hex_shape_is_checked()
    {
        Assert.True(Hex.IsSha256(new string('a', 64)));
        Assert.False(Hex.IsSha256(new string('g', 64)));
        Assert.False(Hex.IsSha256(new string('a', 63)));
        Assert.Equal("(none)", Hex.Short(null));
    }
}

public class EnvironmentTests
{
    private static readonly AdminLayout Layout = AdminLayout.Create(Rig.ProgramFiles, Rig.UserSid, Rig.Profile);

    [Fact]
    public void The_environment_is_exactly_the_documented_set_and_nothing_inherited()
    {
        // The parent had secrets and loader switches; the builder takes no environment at all, so none can reach the child.
        Environment.SetEnvironmentVariable("DC_TEST_SECRET_TOKEN", "hunter2-not-real");
        try
        {
            var env = GatewayEnvironment.Build(Layout, @"C:\TestWin", "tester");

            Assert.Equal(GatewayEnvironment.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), env.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain(env.Values, v => v.Contains("hunter2", StringComparison.Ordinal));
            Assert.DoesNotContain(env.Keys, k => k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || k.Contains("KEY", StringComparison.OrdinalIgnoreCase) || k.Contains("SECRET", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DC_TEST_SECRET_TOKEN", null);
        }
    }

    [Fact]
    public void The_users_folders_are_the_invoking_users_and_the_data_folder_is_named()
    {
        var env = GatewayEnvironment.Build(Layout, @"C:\TestWin\", "tester");

        Assert.Equal(Rig.Profile, env["USERPROFILE"]);
        Assert.Equal(Rig.Profile, env["HOME"]);
        Assert.Equal(Rig.Profile + @"\AppData\Local", env["LOCALAPPDATA"]);
        Assert.Equal(Rig.Profile + @"\.defenseclaw", env["DEFENSECLAW_HOME"]);
        Assert.Equal("tester", env["USERNAME"]);
        Assert.Equal("1", env["NO_COLOR"]);
        Assert.Equal(@"C:\TestWin", env["SystemRoot"]);
    }

    [Fact]
    public void The_path_is_system_folders_first_and_the_users_local_bin_last()
    {
        var parts = GatewayEnvironment.Build(Layout, @"C:\TestWin", "tester")["PATH"].Split(';');

        Assert.Equal(@"C:\TestWin\System32", parts[0]);
        Assert.Equal(Rig.Profile + @"\.local\bin", parts[^1]);
        Assert.DoesNotContain(parts, p => p.Contains("Temp", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("dotnet_startup_hooks")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH")]
    [InlineData("COR_PROFILER")]
    [InlineData("COMPlus_ProfAPI")]
    [InlineData("DOTNET_ADDITIONAL_DEPS")]
    public void Loader_and_runtime_switches_are_dangerous(string name)
    {
        Assert.Contains(name, GatewayEnvironment.DangerousVariables(new[] { "PATH", name, "TEMP" }));
    }

    [Fact]
    public void Ordinary_variables_are_not_dangerous()
    {
        Assert.Empty(GatewayEnvironment.DangerousVariables(new[] { "PATH", "TEMP", "GITHUB_TOKEN", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_ROOT" }));
    }
}

public class SourceHygieneTests
{
    [Fact]
    public void The_helper_has_no_way_to_shell_out_to_the_tools_the_design_forbids()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "DefenseClaw.Win.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var forbidden = new System.Text.RegularExpressions.Regex(@"\bicacls\b|\btakeown\b|\bschtasks\b|\brunas\b|\bcmd\.exe\b|\bpowershell\.exe\b|ShellExecuteEx|UseShellExecute\s*=\s*true", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        foreach (var folder in new[] { "DefenseClaw.GatewayAdmin", "DefenseClaw.GatewayAdmin.Contract" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, folder), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(@"\obj\", StringComparison.Ordinal) || file.Contains(@"\bin\", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var (line, index) in File.ReadAllLines(file).Select((l, i) => (l.Trim(), i)))
                {
                    if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (forbidden.IsMatch(line) && !line.Contains("\"cmd.exe\"", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{index + 1}: {line}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }
}
