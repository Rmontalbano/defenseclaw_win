using DefenseClaw.GatewayAdmin.Contract;

namespace DefenseClaw.GatewayAdmin.Tests;

public class HelperPreflightTests
{
    [Fact]
    public void Status_needs_no_elevation_and_changes_nothing()
    {
        var rig = new Rig(withCopy: true);
        rig.Identity.Value = rig.Caller(elevated: false, split: false, parent: null);

        var result = rig.Run(AdminVerb.Status);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        var report = AdminStatusReport.TryParse(string.Join('\n', result.Lines))!;
        Assert.True(report.CopyTrusted);
        Assert.Empty(rig.Fs.Changes);
        Assert.Empty(rig.Runner.Requests);
    }

    [Theory]
    [InlineData(AdminVerb.InstallCopy)]
    [InlineData(AdminVerb.Start)]
    [InlineData(AdminVerb.Restart)]
    [InlineData(AdminVerb.Stop)]
    [InlineData(AdminVerb.RepairOwnership)]
    public void A_changing_verb_without_an_elevated_token_changes_nothing(AdminVerb verb)
    {
        var rig = new Rig(withCopy: true);
        rig.Identity.Value = rig.Caller(elevated: false);

        var result = rig.Run(verb);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("elevated", rig.AllLines(result), StringComparison.Ordinal);
        AssertUntouched(rig);
    }

    [Theory]
    [InlineData(AdminVerb.InstallCopy)]
    [InlineData(AdminVerb.Start)]
    [InlineData(AdminVerb.Restart)]
    [InlineData(AdminVerb.Stop)]
    [InlineData(AdminVerb.RepairOwnership)]
    public void An_elevation_that_is_not_the_users_own_split_token_is_refused(AdminVerb verb)
    {
        var rig = new Rig(withCopy: true);
        rig.Identity.Value = rig.Caller(split: false);

        var result = rig.Run(verb);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("own administrator token", rig.AllLines(result), StringComparison.Ordinal);
        AssertUntouched(rig);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Rig.OtherSid)]
    public void A_parent_that_is_not_the_same_account_or_is_gone_is_refused(string? parent)
    {
        var rig = new Rig(withCopy: true);
        rig.Identity.Value = rig.Caller(parent: parent);

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("started the helper", rig.AllLines(result), StringComparison.Ordinal);
        AssertUntouched(rig);
    }

    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("CORECLR_PROFILER")]
    public void Loader_switches_in_the_environment_refuse_every_verb_including_status(string variable)
    {
        var rig = new Rig(withCopy: true);
        rig.Identity.Value = rig.Caller(environment: new[] { "PATH", variable });

        foreach (var verb in AdminVerbs.All)
        {
            var result = rig.Run(verb);
            Assert.Equal(AdminExit.Refused, result.ExitCode);
            Assert.Contains(variable, rig.AllLines(result), StringComparison.Ordinal);
        }

        AssertUntouched(rig);
    }

    [Fact]
    public void An_identity_that_cannot_be_established_is_a_refusal()
    {
        var rig = new Rig(withCopy: true);
        rig.Identity.Throws = new InvalidOperationException("The profile folder could not be confirmed from the profile list.");

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("profile folder", rig.AllLines(result), StringComparison.Ordinal);
        AssertUntouched(rig);
    }

    [Fact]
    public void A_helper_running_from_anywhere_but_its_place_may_only_install_itself()
    {
        var rig = new Rig(atHome: false, withCopy: true);
        rig.Identity.Value = rig.Caller(image: @"C:\Downloads\DefenseClaw.GatewayAdmin.exe");

        foreach (var verb in new[] { AdminVerb.Start, AdminVerb.Stop, AdminVerb.Restart, AdminVerb.RepairOwnership })
        {
            var result = rig.Run(verb);
            Assert.Equal(AdminExit.Refused, result.ExitCode);
            Assert.Contains("does not run from", rig.AllLines(result), StringComparison.Ordinal);
        }

        AssertUntouched(rig);
    }

    [Fact]
    public void A_helper_in_program_files_that_a_user_could_have_written_is_refused()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.HelperExe].Acl.Add(new AclEntry(Rig.UserSid, true, 0x2));

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains(Rig.UserSid, rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Runner.Requests);
    }

    [Fact]
    public void A_helper_whose_bytes_changed_since_it_was_recorded_is_refused()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.HelperExe].Data = Rig.HelperBytes.Reverse().ToArray();

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Empty(rig.Runner.Requests);
    }

    private static void AssertUntouched(Rig rig)
    {
        Assert.Empty(rig.Fs.Changes);
        Assert.Empty(rig.Runner.Requests);
        Assert.Empty(rig.Consent.Asked);
        Assert.Empty(rig.Token.Owners);
        Assert.Empty(rig.Owners.Set);
    }
}

public class HelperInstallCopyTests
{
    [Fact]
    public void A_manifest_matching_gateway_is_copied_after_the_operator_confirms_and_is_then_trusted()
    {
        var rig = new Rig();

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        var asked = Assert.Single(rig.Consent.Asked);
        Assert.Contains(Rig.Sha(Rig.GatewayBytes), asked.Text, StringComparison.Ordinal);
        Assert.Contains(rig.Layout.GatewayCopyPath, asked.Text, StringComparison.Ordinal);
        Assert.Equal(Rig.GatewayBytes, rig.Fs.Items[rig.Layout.GatewayCopyPath].Data);
        Assert.True(new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout).Trusted);
        Assert.Equal("1.0.0", CopyRecord.TryParse(rig.Fs.Text(rig.Layout.CopyRecordPath))!.Version);
    }

    [Fact]
    public void Everything_the_helper_writes_is_inside_its_own_program_files_folder_and_never_the_users()
    {
        var rig = new Rig();

        _ = rig.Run(AdminVerb.InstallCopy);

        Assert.NotEmpty(rig.Fs.Changes);
        Assert.All(rig.Fs.Changes, change =>
        {
            var path = change[(change.IndexOf(':', StringComparison.Ordinal) + 1)..];
            Assert.True(AdminLayout.IsInside(rig.Layout.ProductRoot, path), change);
        });
        Assert.Empty(rig.Token.Owners);
    }

    [Fact]
    public void The_copy_is_written_to_a_temporary_file_and_moved_into_place_only_after_it_was_verified()
    {
        var rig = new Rig();

        _ = rig.Run(AdminVerb.InstallCopy);

        var temp = rig.Fs.Changes.FindIndex(c => c.StartsWith("temp:", StringComparison.Ordinal));
        var replace = rig.Fs.Changes.FindIndex(c => c.StartsWith("replace:", StringComparison.Ordinal));
        var record = rig.Fs.Changes.FindIndex(c => c.EndsWith("copy.json", StringComparison.Ordinal));
        Assert.InRange(temp, 0, replace - 1);
        Assert.InRange(replace, 0, record - 1);
    }

    [Fact]
    public void Declining_the_operators_question_copies_nothing()
    {
        var rig = new Rig();
        rig.Consent.Answer = false;

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Declined, result.ExitCode);
        Assert.DoesNotContain(rig.Fs.Items.Keys, k => k.Contains("defenseclaw-gateway.exe", StringComparison.OrdinalIgnoreCase) && AdminLayout.IsInside(rig.Layout.ProductRoot, k));
    }

    [Fact]
    public void Without_a_manifest_nothing_is_copied_and_the_operator_is_not_asked()
    {
        var rig = new Rig();
        rig.Fs.Items.Remove(rig.Layout.ManifestPath);

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("Prepare administrator copy", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Consent.Asked);
        Assert.Empty(rig.MeaningfulChanges);
    }

    [Fact]
    public void A_stale_manifest_or_one_for_another_user_is_refused()
    {
        var stale = new Rig();
        stale.WriteManifest(created: stale.Clock.UtcNow.AddMinutes(-45));
        Assert.Equal(AdminExit.Refused, stale.Run(AdminVerb.InstallCopy).ExitCode);
        Assert.Empty(stale.MeaningfulChanges);

        var other = new Rig();
        other.WriteManifest(sid: Rig.OtherSid);
        var result = other.Run(AdminVerb.InstallCopy);
        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("another user", other.AllLines(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_gateway_that_is_not_the_one_the_manifest_names_is_refused_before_anything_is_asked()
    {
        var rig = new Rig();
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = Rig.GatewayBytes.Select(b => (byte)(b ^ 0x55)).ToArray();

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("is not the one the app checked", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Consent.Asked);
        Assert.Empty(rig.MeaningfulChanges);
    }

    [Fact]
    public void A_link_on_the_way_to_the_installed_gateway_is_refused()
    {
        var rig = new Rig();
        rig.Fs.Items[Path.Combine(Rig.Profile, ".local")].IsReparse = true;

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("link or junction", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.MeaningfulChanges);
    }

    [Fact]
    public void A_gateway_that_changes_while_it_is_being_copied_is_refused_and_the_temporary_file_removed()
    {
        var rig = new Rig();
        rig.Fs.AfterOpen = path =>
        {
            if (path == rig.Layout.InstalledGatewayPath)
            {
                // Someone touches the installed file after the helper opened it: its identity no longer matches what was looked at.
                rig.Fs.Items[path].LastWrite = rig.Fs.Items[path].LastWrite.AddSeconds(5);
            }
        };

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("changed while it was being copied", rig.AllLines(result), StringComparison.Ordinal);
        Assert.False(rig.Fs.Items.ContainsKey(rig.Layout.GatewayCopyPath));
        Assert.DoesNotContain(rig.Fs.Items.Keys, k => k.Contains(".tmp-", StringComparison.Ordinal));
        Assert.False(rig.Fs.Items.ContainsKey(rig.Layout.CopyRecordPath));
    }

    [Fact]
    public void A_copy_that_does_not_hash_to_the_source_is_refused_and_removed()
    {
        var rig = new Rig();
        rig.Fs.CorruptTemps = true;

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.False(rig.Fs.Items.ContainsKey(rig.Layout.GatewayCopyPath));
        Assert.DoesNotContain(rig.Fs.Items.Keys, k => k.Contains(".tmp-", StringComparison.Ordinal));
    }

    [Fact]
    public void An_up_to_date_copy_is_left_alone_without_asking()
    {
        var rig = new Rig(withCopy: true);

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.Empty(rig.Consent.Asked);
        Assert.DoesNotContain(rig.Fs.Changes, c => c.StartsWith("replace:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_different_old_copy_is_replaced_after_asking()
    {
        var rig = new Rig();
        rig.InstallCopyFiles(Rig.GatewayBytes.Reverse().ToArray());

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.True(result.ExitCode == AdminExit.Ok, rig.AllLines(result));
        Assert.Single(rig.Consent.Asked);
        Assert.Equal(Rig.GatewayBytes, rig.Fs.Items[rig.Layout.GatewayCopyPath].Data);
    }

    [Fact]
    public void An_old_copy_that_cannot_be_replaced_because_it_is_running_says_so()
    {
        var rig = new Rig();
        rig.InstallCopyFiles(Rig.GatewayBytes.Reverse().ToArray());
        rig.Fs.ReplaceFails.Add(rig.Layout.GatewayCopyPath);

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("stop it first", rig.AllLines(result), StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_is_left_in_the_helpers_own_folder_for_the_app()
    {
        var rig = new Rig();

        _ = rig.Run(AdminVerb.InstallCopy);

        var result = AdminRunResult.TryParse(rig.Fs.Text(rig.Layout.ResultPath))!;
        Assert.Equal("install-copy", result.Verb);
        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.NotEmpty(result.Lines);
    }

    // ---- the first run: the helper puts itself in Program Files ---------------------------------------------

    [Fact]
    public void The_first_run_installs_the_helper_then_the_copy_after_two_questions()
    {
        var rig = new Rig(atHome: false);
        rig.Fs.AddFile(@"C:\Downloads\DefenseClaw.GatewayAdmin.exe", Rig.HelperBytes, Rig.UserSid, Rig.UserAcl());
        rig.Identity.Value = rig.Caller(image: @"C:\Downloads\DefenseClaw.GatewayAdmin.exe");

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.Equal(2, rig.Consent.Asked.Count);
        Assert.Contains(Rig.Sha(Rig.HelperBytes), rig.Consent.Asked[0].Text, StringComparison.Ordinal);
        Assert.Equal(Rig.HelperBytes, rig.Fs.Items[rig.Layout.HelperExe].Data);
        Assert.True(new AdminCopyInspector(rig.Fs).InspectHelper(rig.Layout).Trusted);
        Assert.True(new AdminCopyInspector(rig.Fs).InspectGatewayCopy(rig.Layout).Trusted);
    }

    [Fact]
    public void Declining_the_helper_install_installs_nothing_and_does_not_go_on()
    {
        var rig = new Rig(atHome: false);
        rig.Fs.AddFile(@"C:\Downloads\DefenseClaw.GatewayAdmin.exe", Rig.HelperBytes, Rig.UserSid, Rig.UserAcl());
        rig.Identity.Value = rig.Caller(image: @"C:\Downloads\DefenseClaw.GatewayAdmin.exe");
        rig.Consent.Answer = false;

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Declined, result.ExitCode);
        Assert.Single(rig.Consent.Asked);
        Assert.False(rig.Fs.Items.ContainsKey(rig.Layout.HelperExe));
        Assert.Empty(rig.Fs.Changes);
    }

    [Fact]
    public void A_helper_that_changes_while_it_is_being_copied_is_not_installed()
    {
        var rig = new Rig(atHome: false);
        rig.Fs.AddFile(@"C:\Downloads\DefenseClaw.GatewayAdmin.exe", Rig.HelperBytes, Rig.UserSid, Rig.UserAcl());
        rig.Identity.Value = rig.Caller(image: @"C:\Downloads\DefenseClaw.GatewayAdmin.exe");
        rig.Fs.CorruptTemps = true;

        var result = rig.Run(AdminVerb.InstallCopy);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.False(rig.Fs.Items.ContainsKey(rig.Layout.HelperExe));
    }
}

public class HelperStartStopTests
{
    [Fact]
    public void Start_runs_the_copy_with_the_one_verb_a_rebuilt_environment_and_the_users_token_owner()
    {
        var rig = new Rig(withCopy: true);

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        var request = Assert.Single(rig.Runner.Requests);
        Assert.Equal(rig.Layout.GatewayCopyPath, request.Executable);
        Assert.Equal(new[] { "start" }, request.Arguments);
        Assert.Equal(rig.Layout.UserDirectory, request.WorkingDirectory);
        Assert.Equal(GatewayEnvironment.Build(rig.Layout, @"C:\TestWin", "tester"), request.Environment);
        Assert.DoesNotContain("GITHUB_TOKEN", request.Environment.Keys);
        Assert.Equal(new[] { Rig.UserSid }, rig.Token.Owners);
        // The owner is set before the process starts and put back after it, not left set for the helper's own writes.
        Assert.Equal(new[] { "owner:set", "run:start", "owner:restored" }, rig.Events);
    }

    [Fact]
    public void Start_without_a_copy_is_refused_with_a_way_forward()
    {
        var rig = new Rig();

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("Prepare administrator copy", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Runner.Requests);
    }

    [Fact]
    public void Start_refuses_a_copy_that_is_not_the_file_the_helper_installed()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.GatewayCopyPath].Data = Rig.GatewayBytes.Reverse().ToArray();

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("not the file the helper installed", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Runner.Requests);
    }

    [Fact]
    public void Start_refuses_when_the_installed_gateway_differs_from_the_copy_with_the_macs_words()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items[rig.Layout.InstalledGatewayPath].Data = Rig.GatewayBytes.Concat(new byte[] { 1 }).ToArray();

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("The installed gateway differs from the administrator copy. Choose Restart Gateway as Administrator to use the installed version.", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Runner.Requests);
        Assert.Empty(rig.Token.Owners);
    }

    [Fact]
    public void Start_refuses_while_files_in_the_data_folder_are_owned_by_administrators()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.AddFile(Path.Combine(Rig.Profile, ".defenseclaw", "hook_contract_lock.json"), new byte[] { 1 }, AdminAcl.Administrators);

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("Run Repair ownership first", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Runner.Requests);
    }

    [Fact]
    public void Start_refuses_when_a_gateway_is_already_running()
    {
        var rig = new Rig(withCopy: true);
        rig.Running.Value = () => true;

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Refused, result.ExitCode);
        Assert.Contains("already running", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Empty(rig.Runner.Requests);
    }

    [Fact]
    public void A_failing_start_is_a_failure_with_the_gateways_own_words()
    {
        var rig = new Rig(withCopy: true);
        rig.Runner.Behaviour = _ => new ProcessOutcome(true, 3, false, new[] { "port in use" }, null);

        var result = rig.Run(AdminVerb.Start);

        Assert.Equal(AdminExit.Failed, result.ExitCode);
        Assert.Contains("port in use", rig.AllLines(result), StringComparison.Ordinal);
        Assert.Contains("exited with code 3", rig.AllLines(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_that_never_finishes_or_never_starts_is_a_failure()
    {
        var timeout = new Rig(withCopy: true);
        timeout.Runner.Behaviour = _ => new ProcessOutcome(true, null, true, Array.Empty<string>(), null);
        Assert.Equal(AdminExit.Failed, timeout.Run(AdminVerb.Start).ExitCode);

        var missing = new Rig(withCopy: true);
        missing.Runner.Behaviour = _ => new ProcessOutcome(false, null, false, Array.Empty<string>(), "access denied");
        Assert.Equal(AdminExit.Failed, missing.Run(AdminVerb.Start).ExitCode);
    }

    [Fact]
    public void Stop_runs_only_the_stop_verb_of_the_copy_and_needs_no_source()
    {
        var rig = new Rig(withCopy: true);
        rig.Fs.Items.Remove(rig.Layout.InstalledGatewayPath);

        var result = rig.Run(AdminVerb.Stop);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.Equal(new[] { "stop" }, Assert.Single(rig.Runner.Requests).Arguments);
    }

    [Fact]
    public void Stop_without_a_trusted_copy_is_refused()
    {
        var rig = new Rig();
        Assert.Equal(AdminExit.Refused, rig.Run(AdminVerb.Stop).ExitCode);

        var tampered = new Rig(withCopy: true);
        tampered.Fs.Items[tampered.Layout.GatewayCopyPath].Owner = Rig.UserSid;
        Assert.Equal(AdminExit.Refused, tampered.Run(AdminVerb.Stop).ExitCode);
        Assert.Empty(tampered.Runner.Requests);
    }
}

public class HelperRestartTests
{
    [Fact]
    public void Restart_stops_the_old_one_refreshes_the_copy_then_starts()
    {
        var rig = new Rig(withCopy: true);
        var oldGateway = Rig.GatewayBytes.Reverse().ToArray();
        // The copy is the old gateway; the installed one is newer (what an upgrade leaves).
        rig.InstallCopyFiles(oldGateway);
        var running = true;
        rig.Running.Value = () => running;
        rig.Runner.Behaviour = request =>
        {
            if (request.Arguments[0] == "stop")
            {
                running = false;
            }

            return new ProcessOutcome(true, 0, false, new[] { "ok" }, null);
        };

        var result = rig.Run(AdminVerb.Restart);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.Equal(new[] { "owner:set", "run:stop", "owner:restored", "owner:set", "run:start", "owner:restored" }, rig.Events);
        Assert.Single(rig.Consent.Asked);
        Assert.Equal(Rig.GatewayBytes, rig.Fs.Items[rig.Layout.GatewayCopyPath].Data);
    }

    [Fact]
    public void A_stop_that_fails_aborts_before_the_copy_is_touched()
    {
        var rig = new Rig();
        rig.InstallCopyFiles(Rig.GatewayBytes.Reverse().ToArray());
        rig.Running.Value = () => true;
        rig.Runner.Behaviour = _ => new ProcessOutcome(true, 1, false, new[] { "access denied" }, null);

        var result = rig.Run(AdminVerb.Restart);

        Assert.Equal(AdminExit.Failed, result.ExitCode);
        Assert.Single(rig.Runner.Requests);
        Assert.Empty(rig.Consent.Asked);
        Assert.Equal(Rig.GatewayBytes.Reverse().ToArray(), rig.Fs.Items[rig.Layout.GatewayCopyPath].Data);
    }

    [Fact]
    public void Restart_with_nothing_running_refreshes_and_starts_without_a_stop()
    {
        var rig = new Rig(withCopy: true);

        var result = rig.Run(AdminVerb.Restart);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.Equal(new[] { "run:start" }, rig.Runner.Events.Where(e => e.StartsWith("run:", StringComparison.Ordinal)).ToArray());
        Assert.Empty(rig.Consent.Asked);
    }

    [Fact]
    public void Restart_for_the_first_time_makes_the_copy_then_stops_the_running_gateway_with_it()
    {
        var rig = new Rig();
        var running = true;
        rig.Running.Value = () => running;
        rig.Runner.Behaviour = request =>
        {
            if (request.Arguments[0] == "stop")
            {
                running = false;
            }

            return new ProcessOutcome(true, 0, false, Array.Empty<string>(), null);
        };

        var result = rig.Run(AdminVerb.Restart);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.Equal(new[] { "run:stop", "run:start" }, rig.Runner.Events.Where(e => e.StartsWith("run:", StringComparison.Ordinal)).ToArray());
        Assert.Single(rig.Consent.Asked);
    }

    [Fact]
    public void Declining_the_refresh_during_a_restart_does_not_start_anything()
    {
        var rig = new Rig();
        rig.InstallCopyFiles(Rig.GatewayBytes.Reverse().ToArray());
        rig.Consent.Answer = false;

        var result = rig.Run(AdminVerb.Restart);

        Assert.Equal(AdminExit.Declined, result.ExitCode);
        Assert.Empty(rig.Runner.Requests);
    }
}

public class HelperRepairOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-gwadmin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AdminLayout LayoutOnTemp() => AdminLayout.Create(Rig.ProgramFiles, Rig.UserSid, _root);

    private string Make(AdminLayout layout, params string[] relative)
    {
        Directory.CreateDirectory(layout.DataDirectory);
        foreach (var rel in relative)
        {
            var path = Path.Combine(layout.DataDirectory, rel);
            if (rel.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }
        }

        return layout.DataDirectory;
    }

    [Fact]
    public void Administrator_owned_items_become_the_users_and_everything_else_is_left()
    {
        var layout = LayoutOnTemp();
        var data = Make(layout, "gateway.pid", "audit.db", "logs\\", "logs\\a.log", "mine.txt", "theirs.txt");
        var owners = new FakeOwners();
        owners.Owner[Path.Combine(data, "mine.txt")] = Rig.UserSid;
        owners.Owner[Path.Combine(data, "theirs.txt")] = Rig.OtherSid;
        owners.Owner[data] = Rig.UserSid;

        var report = OwnershipRepair.Run(layout, owners);

        Assert.True(report.Success);
        Assert.Equal(1, report.LeftForeign);
        Assert.Equal(2, report.AlreadyUser);
        Assert.Equal(4, report.Fixed);
        Assert.All(owners.Set, s => Assert.Equal(Rig.UserSid, s.Sid));
        Assert.DoesNotContain(owners.Set, s => s.Path.EndsWith("theirs.txt", StringComparison.Ordinal) || s.Path.EndsWith("mine.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_is_counted_and_never_changed_or_walked_into()
    {
        var layout = LayoutOnTemp();
        var data = Make(layout, "linked\\", "linked\\inside.txt", "plain.txt");
        var owners = new FakeOwners();
        owners.Links.Add(Path.Combine(data, "linked"));

        var report = OwnershipRepair.Run(layout, owners);

        Assert.Equal(1, report.SkippedLinks);
        Assert.DoesNotContain(owners.Set, s => s.Path.Contains("linked", StringComparison.Ordinal));
        Assert.Contains(owners.Set, s => s.Path.EndsWith("plain.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failure_is_counted_and_named_and_the_run_is_not_a_success()
    {
        var layout = LayoutOnTemp();
        var data = Make(layout, "a.txt", "b.txt");
        var owners = new FakeOwners();
        owners.Failing.Add(Path.Combine(data, "a.txt"));

        var report = OwnershipRepair.Run(layout, owners);

        Assert.False(report.Success);
        Assert.Equal(1, report.Failed);
        Assert.Contains("a.txt", Assert.Single(report.FirstFailures), StringComparison.Ordinal);
        Assert.Contains(owners.Set, s => s.Path.EndsWith("b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void The_walk_stays_inside_the_data_folder()
    {
        var layout = LayoutOnTemp();
        var data = Make(layout, "a.txt");
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "x");
        var owners = new FakeOwners();

        _ = OwnershipRepair.Run(layout, owners);

        Assert.All(owners.Set, s => Assert.True(AdminLayout.IsInside(data, s.Path), s.Path));
    }

    [Fact]
    public void The_verb_refuses_a_missing_folder_and_a_folder_that_is_a_link()
    {
        var rig = new Rig();
        rig.Owners.Links.Add(rig.Layout.DataDirectory);

        // The rig's data folder is not on disk; the fake reports it missing.
        var missing = rig.Run(AdminVerb.RepairOwnership);
        Assert.Equal(AdminExit.Refused, missing.ExitCode);
        Assert.Contains("no .defenseclaw folder", rig.AllLines(missing), StringComparison.Ordinal);
    }

    [Fact]
    public void The_verb_reports_what_it_did_and_is_run_through_the_checked_helper()
    {
        var layout = LayoutOnTemp();
        _ = Make(layout, "gateway.pid");
        var rig = new Rig(withCopy: true);
        var owners = new FakeOwners();
        var identity = new FakeIdentity { Value = new CallerIdentity(Rig.UserSid, "tester", _root, true, true, Rig.UserSid, rig.Layout.HelperExe, @"C:\TestWin", Rig.ProgramFiles, new[] { "PATH" }) };
        var fs = rig.Fs;
        var helper = new AdminHelper(identity, fs, rig.Consent, rig.Runner, rig.Token, rig.Running, owners, rig.Clock);

        var result = helper.Run(AdminVerb.RepairOwnership);

        Assert.Equal(AdminExit.Ok, result.ExitCode);
        Assert.Contains("given back to you", string.Join('\n', result.Lines), StringComparison.Ordinal);
        Assert.Empty(rig.Runner.Requests);
        Assert.Empty(rig.Consent.Asked);
    }
}

public class StateOwnershipTests
{
    [Fact]
    public void Files_owned_by_anyone_but_the_user_are_named()
    {
        var rig = new Rig();
        rig.Fs.AddFile(Path.Combine(Rig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators);
        rig.Fs.AddFile(Path.Combine(Rig.Profile, ".defenseclaw", "audit.db"), new byte[] { 1 }, Rig.UserSid);
        rig.Fs.AddFile(Path.Combine(Rig.Profile, ".defenseclaw", ".env"), new byte[] { 1 }, Rig.OtherSid);

        var found = StateOwnership.Foreign(rig.Layout, rig.Fs);

        Assert.Equal(new[] { "gateway.pid", ".env" }, found);
    }

    [Fact]
    public void Missing_files_links_and_files_the_user_owns_are_no_problem()
    {
        var rig = new Rig();
        rig.Fs.AddFile(Path.Combine(Rig.Profile, ".defenseclaw", "audit.db"), new byte[] { 1 }, Rig.UserSid);
        rig.Fs.AddFile(Path.Combine(Rig.Profile, ".defenseclaw", "gateway.pid"), new byte[] { 1 }, AdminAcl.Administrators).IsReparse = true;

        Assert.Empty(StateOwnership.Foreign(rig.Layout, rig.Fs));
    }

    [Fact]
    public void An_owner_that_cannot_be_read_is_reported()
    {
        var rig = new Rig();
        rig.Fs.AddFile(Path.Combine(Rig.Profile, ".defenseclaw", "audit.db"), new byte[] { 1 }, Rig.UserSid).SecurityUnreadable = true;

        Assert.Equal(new[] { "audit.db" }, StateOwnership.Foreign(rig.Layout, rig.Fs));
    }
}
