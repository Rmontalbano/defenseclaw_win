using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.SetupResources;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.SetupResources;

/// <summary>
/// Builds a Setup list editor over a scratch composition and the fake CLI (<see cref="FakeSetupCli"/>), so a test feeds exact output and sees
/// the exact command of every read and every confirmed change. Nothing here starts a process or touches the real install.
/// </summary>
internal sealed class SetupEditorHarness : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<AppServices> _services = new();

    public string DataDirectory => _temp.Path;

    public void Dispose()
    {
        foreach (var services in _services)
        {
            services.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>A scratch composition; <paramref name="installation"/> null is the usual writable one.</summary>
    public AppServices Services(InstallationContext? installation = null)
    {
        var services = TestServices.Create(_temp, installation: installation);
        _services.Add(services);
        return services;
    }

    /// <summary>The editor of <paramref name="resource"/> over <paramref name="services"/>, wired to a fake CLI (<paramref name="script"/> sets what it answers).</summary>
    public (SetupResourceViewModel Vm, FakeSetupCli Cli) Editor(
        Core.Setup.SetupResource resource,
        Action<FakeSetupCli>? script = null,
        AppServices? services = null,
        string prefill = "",
        string context = "")
    {
        services ??= Services();
        var cli = new FakeSetupCli();
        script?.Invoke(cli);

        var vm = SetupResourceViewModel.Create(services, resource, prefill, context);
        vm.RunCli = cli.Run;
        vm.Review.RunStep = cli.Step;
        return (vm, cli);
    }

    /// <summary>The editor, read once.</summary>
    public async Task<(SetupResourceViewModel Vm, FakeSetupCli Cli)> OpenAsync(
        Core.Setup.SetupResource resource,
        Action<FakeSetupCli>? script = null,
        AppServices? services = null,
        string prefill = "",
        string context = "")
    {
        var (vm, cli) = Editor(resource, script, services, prefill, context);
        await vm.ReadAsync();
        return (vm, cli);
    }

    /// <summary>Selects the row named <paramref name="key"/>.</summary>
    public static SetupResourceRow Select(SetupResourceViewModel vm, string key)
    {
        var row = vm.Rows.Single(r => r.Key == key);
        vm.SelectedRow = row;
        return row;
    }
}
