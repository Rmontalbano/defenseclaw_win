using System.Security.AccessControl;
using System.Security.Principal;
using DefenseClaw.App.Services;
using Microsoft.Win32;

namespace DefenseClaw.App.Tests.Startup;

/// <summary>
/// Start-with-Windows against a scratch registry hive under <c>HKCU\Software\DefenseClaw.App.Tests</c>: the manager's
/// root is injectable, so no test ever writes the real Run key. A locked-down key is simulated with a read-only handle
/// (and, for the read path, a deny ACE) - what a policy-managed Run key does to the tray menu's click.
/// </summary>
public sealed class AutostartManagerTests : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "DefenseClawWin";

    private readonly string _rootPath = @"Software\DefenseClaw.App.Tests\Autostart\" + Guid.NewGuid().ToString("n");
    private readonly RegistryKey _root;

    public AutostartManagerTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
    }

    public void Dispose()
    {
        _root.Dispose();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scratch key left behind is untidy, not harmful.
        }
    }

    private RegistryKey ReadOnlyRoot() => Registry.CurrentUser.OpenSubKey(_rootPath, writable: false)!;

    private string? StoredCommand()
    {
        using var key = _root.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    [Fact]
    public void Toggling_writes_the_run_value_and_toggling_again_removes_it()
    {
        Assert.False(AutostartManager.IsEnabledIn(_root));

        var on = AutostartManager.Toggle(_root);

        Assert.True(on.Succeeded);
        Assert.True(on.Enabled);
        Assert.Null(on.FailureMessage);
        Assert.EndsWith("--minimized", StoredCommand(), StringComparison.Ordinal);
        Assert.True(AutostartManager.IsEnabledIn(_root));

        var off = AutostartManager.Toggle(_root);

        Assert.True(off.Succeeded);
        Assert.False(off.Enabled);
        Assert.Null(StoredCommand());
        Assert.False(AutostartManager.IsEnabledIn(_root));
    }

    [Fact]
    public void A_run_value_that_task_manager_disabled_reads_as_off_and_toggling_turns_it_on_and_clears_the_flag()
    {
        using (var run = _root.CreateSubKey(RunKey))
        {
            run.SetValue(ValueName, "\"C:\\somewhere\\DefenseClaw.App.exe\" --minimized");
        }

        using (var approved = _root.CreateSubKey(ApprovedKey))
        {
            approved.SetValue(ValueName, new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.False(AutostartManager.IsEnabledIn(_root));

        var result = AutostartManager.Toggle(_root);

        Assert.True(result.Succeeded);
        Assert.True(result.Enabled);
        Assert.True(AutostartManager.IsEnabledIn(_root));
        using var flag = _root.OpenSubKey(ApprovedKey);
        Assert.Null(flag?.GetValue(ValueName));
    }

    [Fact]
    public void A_key_that_refuses_writes_reports_the_reason_and_the_state_it_was_in_instead_of_throwing()
    {
        // The read-only handle makes CreateSubKey throw UnauthorizedAccessException, as a policy-locked Run key does.
        using var locked = ReadOnlyRoot();

        var result = AutostartManager.Toggle(locked);

        Assert.False(result.Succeeded);
        Assert.False(result.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.StartsWith("Could not change Start with Windows: ", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(result.Error!, result.FailureMessage, StringComparison.Ordinal);
        Assert.Null(StoredCommand());
    }

    [Fact]
    public void A_refused_turn_off_leaves_the_value_in_place_and_says_it_is_still_on()
    {
        _ = AutostartManager.Toggle(_root);
        Assert.True(AutostartManager.IsEnabledIn(_root));
        using var locked = ReadOnlyRoot();

        var result = AutostartManager.Toggle(locked);

        Assert.False(result.Succeeded);
        Assert.True(result.Enabled);
        Assert.NotNull(StoredCommand());
        Assert.True(AutostartManager.IsEnabledIn(_root));
    }

    [Fact]
    public void Reading_a_key_that_denies_reads_is_off_and_does_not_throw()
    {
        using (var run = _root.CreateSubKey(RunKey))
        {
            run.SetValue(ValueName, "\"C:\\somewhere\\DefenseClaw.App.exe\" --minimized");
        }

        var me = WindowsIdentity.GetCurrent().User!;
        var runPath = _rootPath + "\\" + RunKey;

        void Deny(bool deny)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                runPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree,
                RegistryRights.ChangePermissions | RegistryRights.ReadPermissions)!;
            var security = key.GetAccessControl();
            var rule = new RegistryAccessRule(me, RegistryRights.QueryValues, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
            if (deny)
            {
                security.AddAccessRule(rule);
            }
            else
            {
                security.RemoveAccessRule(rule);
            }

            key.SetAccessControl(security);
        }

        Deny(deny: true);
        try
        {
            Assert.False(AutostartManager.IsEnabledIn(_root));
        }
        finally
        {
            Deny(deny: false);
        }

        Assert.True(AutostartManager.IsEnabledIn(_root));
    }

    [Fact]
    public void The_result_of_a_change_carries_no_failure_message()
    {
        Assert.Null(new AutostartToggleResult(true, null).FailureMessage);
        Assert.Equal(
            "Could not change Start with Windows: Access to the registry key is denied.",
            new AutostartToggleResult(false, "Access to the registry key is denied.").FailureMessage);
    }
}
