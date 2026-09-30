using System.Diagnostics;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Wizards;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// "resolves to …" under the review's argv is bound on the UI thread. It used to be a PATH lookup on every read;
/// with a dead network entry on PATH that froze the window for ~40 s.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class WizardExecutablePathTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _warmTemp = new();
    private readonly DeadPathProbe _probe = new();

    public void Dispose()
    {
        _probe.Dispose();
        _temp.Dispose();
        _warmTemp.Dispose();
    }

    [Fact]
    public void Opening_a_wizard_does_not_wait_for_the_PATH_lookup_and_shows_the_answer_when_it_lands()
    {
        var bin = _temp.File("bin");
        var cli = Path.Combine(bin, "defenseclaw.exe");
        _ = _probe.Install(cli);

        using var services = AppServices.CreateIsolated(
            _probe.PathsFor(_temp, bin),
            claudeSettingsPath: _temp.File("claude-settings.json"));
        using var warmServices = TestServices.Create(_warmTemp);

        var vm = UiThread.Run(() =>
        {
            using (new WizardViewModel(warmServices, WizardSamples.Galileo()))
            {
                // Warm-up: JIT and first-use costs are not what is being measured.
            }

            var stopwatch = Stopwatch.StartNew();
            var wizard = new WizardViewModel(services, WizardSamples.Galileo());
            var text = wizard.ExecutablePath;
            stopwatch.Stop();

            Assert.True(
                stopwatch.ElapsedMilliseconds < 500,
                $"opening the wizard took {stopwatch.ElapsedMilliseconds} ms with a dead entry on PATH");
            Assert.Contains("looking it up", text, StringComparison.Ordinal);
            return wizard;
        });

        try
        {
            _probe.WaitUntilBlocked();
            _probe.Release();

            UiThread.WaitFor(() => vm.ExecutablePath == cli, "the wizard's executable path to resolve");
        }
        finally
        {
            UiThread.Run(vm.Dispose);
        }
    }

    [Fact]
    public void A_cli_that_is_not_installed_is_reported_once_the_lookup_has_finished()
    {
        _probe.Release();

        using var services = AppServices.CreateIsolated(
            _probe.PathsFor(_temp, _temp.File("bin")),
            claudeSettingsPath: _temp.File("claude-settings.json"));

        var vm = UiThread.Run(() => new WizardViewModel(services, WizardSamples.Galileo()));
        try
        {
            UiThread.WaitFor(() => vm.ExecutablePath == "defenseclaw (not found on PATH)", "the not-found text");
        }
        finally
        {
            UiThread.Run(vm.Dispose);
        }
    }
}
