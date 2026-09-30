using System.Reflection;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// What <c>ConfigEditorWindow.CloseForExitAsync</c> tells the tray Exit. The window is built but never shown, and the
/// two private pieces of state the answer depends on (the open editor and "a close is already in flight") are set by
/// reflection, so nothing flashes on the operator's screen and no dialog is asked.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ConfigEditorExitTests : IDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task With_no_editor_open_the_exit_is_cleared()
    {
        var result = await UiThread.Run(() => ConfigEditorWindow.CloseForExitAsync());

        Assert.Equal(ConfigEditorExitResult.Closed, result);
    }

    [Fact]
    public async Task An_editor_whose_close_is_already_in_flight_answers_busy_rather_than_a_silent_no()
    {
        using var services = TestServices.Create(_temp);

        var result = await UiThread.Run(async () =>
        {
            var window = new ConfigEditorWindow(services);
            var currentField = typeof(ConfigEditorWindow).GetField("_current", Private)!;
            var flowField = typeof(ConfigEditorWindow).GetField("_closeFlowRunning", Private)!;
            try
            {
                currentField.SetValue(null, window);
                flowField.SetValue(window, true);

                return await ConfigEditorWindow.CloseForExitAsync();
            }
            finally
            {
                flowField.SetValue(window, false);
                currentField.SetValue(null, null);
                window.Close();
            }
        });

        Assert.Equal(ConfigEditorExitResult.Busy, result);
    }

    [Fact]
    public async Task A_clean_editor_that_is_not_busy_is_closed_and_the_exit_cleared()
    {
        using var services = TestServices.Create(_temp);

        var result = await UiThread.Run(async () =>
        {
            var window = new ConfigEditorWindow(services);
            var currentField = typeof(ConfigEditorWindow).GetField("_current", Private)!;
            try
            {
                currentField.SetValue(null, window);

                return await ConfigEditorWindow.CloseForExitAsync();
            }
            finally
            {
                currentField.SetValue(null, null);
            }
        });

        Assert.Equal(ConfigEditorExitResult.Closed, result);
    }
}
