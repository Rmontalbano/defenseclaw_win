using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// A plugin install from a plain <c>http://</c> address or a network (UNC) share puts a warning bar in its command review: the
/// download is not encrypted or checked, or whoever controls the share chooses what is installed. Other sources get no such bar.
/// Nothing runs here: the review is built and the confirm step is never taken.
/// </summary>
public sealed class PluginInstallSourceWarningTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public PluginInstallSourceWarningTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private PluginsPanelViewModel Plugin(string target)
    {
        var vm = new PluginsPanelViewModel(_services) { InstallNameOrPath = target };
        vm.SubmitInstallFormCommand.Execute(null);
        return vm;
    }

    private static IReadOnlyList<CommandReviewWarning> SourceBars(PluginsPanelViewModel vm) =>
        vm.ConfirmReview?.Warnings.Where(w => w.Title == PluginsPanelViewModel.InstallSourceTitle).ToArray()
        ?? Array.Empty<CommandReviewWarning>();

    [Theory]
    [InlineData("http://example.test/plugin.tgz")]
    [InlineData("http://example.test/plugin.tgz?version=2")]
    public void A_plain_http_source_shows_the_unencrypted_warning_in_the_review(string target)
    {
        var vm = Plugin(target);

        Assert.True(vm.IsConfirmOpen, vm.InstallFormError);
        var bar = Assert.Single(SourceBars(vm));
        Assert.Contains("plain http:// address", bar.Message, StringComparison.Ordinal);
        Assert.Contains("not encrypted", bar.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\\\\server\\share\\plugins\\my-plugin")]
    [InlineData("//server/share/plugins/my-plugin")]
    public void A_network_share_source_shows_the_UNC_warning_in_the_review(string target)
    {
        var vm = Plugin(target);

        Assert.True(vm.IsConfirmOpen, vm.InstallFormError);
        var bar = Assert.Single(SourceBars(vm));
        Assert.Contains("network share (UNC path)", bar.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.test/plugin.tgz")]
    [InlineData("clawhub://example/pkg")]
    [InlineData("voice-call@1.2.0")]
    [InlineData("C:\\plugins\\my-plugin")]
    public void Other_sources_show_no_source_warning(string target)
    {
        var vm = Plugin(target);

        Assert.True(vm.IsConfirmOpen, vm.InstallFormError);
        Assert.Empty(SourceBars(vm));
    }

    [Fact]
    public void The_warning_helper_is_null_for_a_local_folder_and_names_both_risky_sources()
    {
        Assert.Null(PluginsPanelViewModel.InstallSourceWarning("C:\\plugins\\my-plugin"));
        Assert.NotNull(PluginsPanelViewModel.InstallSourceWarning("http://example.test/a.tgz"));
        Assert.NotNull(PluginsPanelViewModel.InstallSourceWarning("\\\\server\\share\\a"));
    }
}
