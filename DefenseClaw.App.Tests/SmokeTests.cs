using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests;

public class SmokeTests
{
    [Fact]
    public void App_assembly_is_reachable_and_internals_are_visible()
    {
        Assert.Equal("DefenseClaw.App", typeof(DefenseClaw.App.Services.AppServices).Assembly.GetName().Name);
        Assert.Equal("Ctrl+1", DefenseClaw.App.Services.ShellShortcuts.PanelChordText(0));
    }

    [Fact]
    public void Isolated_services_use_the_scratch_directory_and_leave_the_singleton_alone()
    {
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp);

        Assert.Equal(temp.Path, services.Paths.DataDirectory);
        Assert.Throws<InvalidOperationException>(() => DefenseClaw.App.Services.AppServices.Current);
    }
}
