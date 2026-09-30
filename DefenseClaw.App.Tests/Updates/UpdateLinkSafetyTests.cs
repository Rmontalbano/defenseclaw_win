using System.Diagnostics;
using System.Text;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Updates;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The links the Updates window works from come from GitHub's JSON or from a cache file on disk, and are then
/// opened with the shell, pasted into copyable PowerShell commands and fetched (CUST-197 / D3-14). Only an https link
/// into this repository may survive; a cache stamped in the future is not "fresh forever"; and a URL in a copyable
/// command is a single-quoted literal that PowerShell cannot expand.
/// </summary>
public class UpdateLinkSafetyTests
{
    private const string Repo = "https://github.com/cisco-ai-defense/defenseclaw/";

    // ------------------------------------------------------------------ TrustedRepoUrl

    [Theory]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw/releases/tag/v0.8.10")]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw/releases/download/v0.8.10/DefenseClawSetup.exe")]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw/releases/download/v0.8.10/defenseclaw-upgrade.ps1")]
    [InlineData("https://GitHub.com/cisco-ai-defense/defenseclaw/releases/tag/v1")]
    [InlineData("  https://github.com/cisco-ai-defense/defenseclaw/releases  ")]
    public void A_link_into_this_repository_over_https_is_trusted(string url)
    {
        var trusted = UpdateChecker.TrustedRepoUrl(url);

        Assert.NotNull(trusted);
        Assert.StartsWith(Repo, trusted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // other schemes: the ones the shell would happily launch
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("\\\\evil.example\\share\\payload.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://github.com/cisco-ai-defense/defenseclaw/releases/tag/v1")]
    [InlineData("ftp://github.com/cisco-ai-defense/defenseclaw/releases")]
    // other hosts, ports and user-info
    [InlineData("https://evil.example/cisco-ai-defense/defenseclaw/releases/tag/v1")]
    [InlineData("https://github.com.evil.example/cisco-ai-defense/defenseclaw/releases")]
    [InlineData("https://github.com@evil.example/cisco-ai-defense/defenseclaw/releases")]
    [InlineData("https://user@github.com/cisco-ai-defense/defenseclaw/releases")]
    [InlineData("https://github.com:8443/cisco-ai-defense/defenseclaw/releases")]
    [InlineData("https://raw.githubusercontent.com/cisco-ai-defense/defenseclaw/main/x.exe")]
    // other repositories, and paths that only look like this one
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw")]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw-evil/releases")]
    [InlineData("https://github.com/other-org/defenseclaw/releases")]
    [InlineData("https://github.com/cisco-ai-defense/other-repo/releases")]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw/../../evil/repo/x.exe")]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw/%2e%2e/%2e%2e/evil/repo/x.exe")]
    [InlineData("https://github.com/evil/repo/cisco-ai-defense/defenseclaw/x")]
    [InlineData("https://github.com/")]
    [InlineData("not a url at all")]
    public void Anything_else_is_not(string? url) => Assert.Null(UpdateChecker.TrustedRepoUrl(url));

    [Fact]
    public void A_trusted_link_comes_back_normalised_and_free_of_control_characters()
    {
        var trusted = UpdateChecker.TrustedRepoUrl(Repo + "releases/download/v1/a\r\nb c.exe");

        // Either refused or escaped, never carried through raw into a shell or a command.
        Assert.True(trusted is null || trusted.All(c => c > ' ' && c != '\u007f'), trusted);
    }

    // ------------------------------------------------------------------ PowerShellSingleQuoted, through a real PowerShell

    [Fact]
    public void A_single_quoted_literal_is_one_string_to_powershell_whatever_is_inside()
    {
        var cases = new[]
        {
            "https://github.com/cisco-ai-defense/defenseclaw/releases/download/v1/DefenseClawSetup.exe",
            "plain",
            "it's",
            "'; whoami; '",
            "$(whoami)",
            "$env:USERNAME",
            "`whoami`",
            "say \"hi\"",
            "a&b;c|d",
            "‘smart’ ‚low‛ ’ quotes",
            "trailing backslash\\",
        };

        // -EncodedCommand carries the script without any command-line quoting of its own.
        var script = string.Join("\n", cases.Select(c => "Write-Output " + UpdateChecker.PowerShellSingleQuoted(c)));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + script));

        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded })
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "powershell.exe did not finish");

        Assert.True(process.ExitCode == 0, stderr);
        Assert.Equal(cases, stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).SkipLast(1).ToArray());
    }

    [Fact]
    public void Quoting_wraps_in_single_quotes_and_doubles_every_kind_of_single_quote()
    {
        Assert.Equal("'abc'", UpdateChecker.PowerShellSingleQuoted("abc"));
        Assert.Equal("''", UpdateChecker.PowerShellSingleQuoted(string.Empty));
        Assert.Equal("'it''s'", UpdateChecker.PowerShellSingleQuoted("it's"));
        Assert.Equal("'‘‘’’‚‚‛‛'", UpdateChecker.PowerShellSingleQuoted("‘’‚‛"));
        Assert.Equal("'$(whoami)'", UpdateChecker.PowerShellSingleQuoted("$(whoami)"));
    }

    // ------------------------------------------------------------------ the copyable commands

    private static ReleaseAsset Asset(string name, string? url) => new() { Name = name, DownloadUrl = url };

    [Fact]
    public void The_setup_command_single_quotes_a_trusted_url()
    {
        var url = Repo + "releases/download/v0.8.10/DefenseClawSetup.exe";

        var text = UpdatesWindowViewModel.BuildSetupCommand(Asset("DefenseClawSetup.exe", url));

        Assert.StartsWith($"curl.exe -LO '{url}'\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('"', text);
        Assert.Contains("/quiet /norestart INSTALLSCOPE=user", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_with_powershell_syntax_in_it_stays_inside_the_single_quotes()
    {
        var url = Repo + "releases/download/v1/$(whoami).exe";

        var setup = UpdatesWindowViewModel.BuildSetupCommand(Asset("DefenseClawSetup.exe", url));
        var script = UpdatesWindowViewModel.BuildUpgradeScriptCommand(Asset("defenseclaw-upgrade.ps1", url));

        Assert.Contains("'" + Repo + "releases/download/v1/$(whoami).exe'", setup, StringComparison.Ordinal);
        Assert.Contains("-Uri '" + Repo + "releases/download/v1/$(whoami).exe' ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", setup + script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example/DefenseClawSetup.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("http://github.com/cisco-ai-defense/defenseclaw/releases/download/v1/DefenseClawSetup.exe")]
    [InlineData("\"; whoami; \"")]
    [InlineData(null)]
    [InlineData("")]
    public void An_untrusted_or_missing_url_is_never_offered_as_a_command(string? url)
    {
        var setup = UpdatesWindowViewModel.BuildSetupCommand(Asset("DefenseClawSetup.exe", url));
        var script = UpdatesWindowViewModel.BuildUpgradeScriptCommand(Asset("defenseclaw-upgrade.ps1", url));

        Assert.Equal("No Setup exe asset was found on this release.", setup);
        Assert.Equal("No defenseclaw-upgrade.ps1 asset was found on this release.", script);
    }

    [Fact]
    public void No_asset_at_all_is_the_same_message()
    {
        Assert.Equal("No Setup exe asset was found on this release.", UpdatesWindowViewModel.BuildSetupCommand(null));
        Assert.Equal("No defenseclaw-upgrade.ps1 asset was found on this release.", UpdatesWindowViewModel.BuildUpgradeScriptCommand(null));
    }

    // ------------------------------------------------------------------ the window's Open button

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("https://evil.example/release", false)]
    [InlineData(null, false)]
    [InlineData("https://github.com/cisco-ai-defense/defenseclaw/releases/tag/v0.8.10", true)]
    public void The_release_page_can_be_opened_only_when_it_is_in_this_repository(string? htmlUrl, bool canOpen)
    {
        using var temp = new TempDirectory();
        StaThread.Run(() =>
        {
            using var services = TestServices.Create(temp);
            using var vm = new UpdatesWindowViewModel(services);

            vm.HtmlUrl = htmlUrl;

            Assert.Equal(canOpen, vm.OpenReleasePageCommand.CanExecute(null));
        });
    }

    // ------------------------------------------------------------------ the cache and the live response

    private static string CacheJson(DateTimeOffset fetchedAt, string htmlUrl, string downloadUrl) =>
        $$"""
        {"tagName":"v9.9.9","name":"Nine","publishedAt":"2026-01-01T00:00:00+00:00","htmlUrl":"{{htmlUrl}}",
         "assets":[{"name":"DefenseClawSetup.exe","size":1,"downloadUrl":"{{downloadUrl}}","contentType":"application/octet-stream"}],
         "fetchedAt":"{{fetchedAt:O}}"}
        """;

    private static string LiveJson(string htmlUrl, string downloadUrl) =>
        $$"""
        {"tag_name":"v9.9.9","name":"Nine","published_at":"2026-01-01T00:00:00Z","html_url":"{{htmlUrl}}",
         "assets":[{"name":"DefenseClawSetup.exe","size":1,"browser_download_url":"{{downloadUrl}}","content_type":"application/octet-stream"}]}
        """;

    private static readonly string GoodHtml = Repo + "releases/tag/v9.9.9";
    private static readonly string GoodDownload = Repo + "releases/download/v9.9.9/DefenseClawSetup.exe";

    private static (UpdateChecker Checker, StubHttpHandler Handler, string CacheFile) Checker(
        AppServicesHolder holder, TempDirectory temp, string? cacheJson)
    {
        var cacheFile = temp.File("cache.json");
        if (cacheJson is not null)
        {
            File.WriteAllText(cacheFile, cacheJson);
        }

        var handler = new StubHttpHandler();
        var checker = new UpdateChecker(holder.Services, new HttpClient(handler), ownsHttpClient: true, cacheFilePath: cacheFile);
        return (checker, handler, cacheFile);
    }

    private sealed class AppServicesHolder : IDisposable
    {
        public AppServicesHolder(TempDirectory temp) => Services = TestServices.Create(temp);

        public DefenseClaw.App.Services.AppServices Services { get; }

        public void Dispose() => Services.Dispose();
    }

    [Fact]
    public async Task A_fresh_cache_is_served_without_touching_the_network()
    {
        using var temp = new TempDirectory();
        using var holder = new AppServicesHolder(temp);
        var (checker, handler, _) = Checker(holder, temp, CacheJson(DateTimeOffset.UtcNow.AddHours(-1), GoodHtml, GoodDownload));
        using var disposeChecker = checker;

        var result = await checker.CheckAsync();

        Assert.True(result.FromCache);
        Assert.Empty(handler.Requested);
        Assert.Equal(GoodHtml, result.HtmlUrl);
        Assert.Equal(GoodDownload, Assert.Single(result.Assets).DownloadUrl);
    }

    [Fact]
    public async Task A_cache_stamped_a_little_ahead_of_the_clock_is_still_fresh()
    {
        using var temp = new TempDirectory();
        using var holder = new AppServicesHolder(temp);
        var (checker, handler, _) = Checker(holder, temp, CacheJson(DateTimeOffset.UtcNow.AddMinutes(1), GoodHtml, GoodDownload));
        using var disposeChecker = checker;

        var result = await checker.CheckAsync();

        Assert.True(result.FromCache);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task A_cache_stamped_in_the_future_is_not_fresh_and_is_not_a_fallback_either()
    {
        using var temp = new TempDirectory();
        using var holder = new AppServicesHolder(temp);
        var (checker, handler, _) = Checker(holder, temp, CacheJson(DateTimeOffset.UtcNow.AddYears(50), GoodHtml, GoodDownload));
        using var disposeChecker = checker;

        // The live check is attempted (the stub answers 404 for it) ...
        var result = await checker.CheckAsync();

        Assert.Contains("latest", handler.Requested);

        // ... and, having failed, there is nothing to fall back on: the poisoned cache is ignored, not shown as "stale".
        Assert.False(result.FromCache);
        Assert.Equal(UpdateCheckState.CheckFailed, result.State);
        Assert.Null(result.HtmlUrl);
        Assert.Empty(result.Assets);
    }

    [Fact]
    public async Task A_cache_stamped_in_the_future_is_replaced_by_a_live_answer()
    {
        using var temp = new TempDirectory();
        using var holder = new AppServicesHolder(temp);
        var (checker, handler, cacheFile) = Checker(holder, temp, CacheJson(DateTimeOffset.UtcNow.AddYears(50), GoodHtml, GoodDownload));
        using var disposeChecker = checker;
        _ = handler.Serve("latest", LiveJson(GoodHtml, GoodDownload));

        var result = await checker.CheckAsync();

        Assert.False(result.FromCache);
        Assert.Equal("v9.9.9", result.LatestVersion);

        // The poisoned stamp is gone from the file: the live answer was cached in its place.
        using var cached = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cacheFile));
        var stamp = cached.RootElement.GetProperty("fetchedAt").GetDateTimeOffset();
        Assert.True(stamp < DateTimeOffset.UtcNow.AddMinutes(5), stamp.ToString("O"));
    }

    [Fact]
    public async Task Links_outside_this_repository_are_dropped_from_a_cache_and_the_detail_says_so()
    {
        using var temp = new TempDirectory();
        using var holder = new AppServicesHolder(temp);
        var (checker, _, _) = Checker(
            holder,
            temp,
            CacheJson(DateTimeOffset.UtcNow.AddHours(-1), "file:///C:/Windows/System32/calc.exe", "https://evil.example/DefenseClawSetup.exe"));
        using var disposeChecker = checker;

        var result = await checker.CheckAsync();

        Assert.True(result.FromCache);
        Assert.Null(result.HtmlUrl);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("DefenseClawSetup.exe", asset.Name);
        Assert.Null(asset.DownloadUrl);
        Assert.Contains("2 release links were not on", result.Detail, StringComparison.Ordinal);
        Assert.Contains("ignored", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_outside_this_repository_are_dropped_from_a_live_response_too()
    {
        using var temp = new TempDirectory();
        using var holder = new AppServicesHolder(temp);
        var (checker, handler, cacheFile) = Checker(holder, temp, cacheJson: null);
        using var disposeChecker = checker;
        _ = handler.Serve("latest", LiveJson("javascript:alert(1)", "https://evil.example/DefenseClawSetup.exe"));

        var result = await checker.CheckAsync(forceRefresh: true);

        Assert.False(result.FromCache);
        Assert.Null(result.HtmlUrl);
        Assert.Null(Assert.Single(result.Assets).DownloadUrl);
        Assert.Contains("ignored", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(cacheFile));
    }

    [Fact]
    public async Task Links_in_this_repository_pass_through_and_add_no_note()
    {
        using var temp = new TempDirectory();
        using var holder = new AppServicesHolder(temp);
        var (checker, handler, _) = Checker(holder, temp, cacheJson: null);
        using var disposeChecker = checker;
        _ = handler.Serve("latest", LiveJson(GoodHtml, GoodDownload));

        var result = await checker.CheckAsync(forceRefresh: true);

        Assert.Equal(GoodHtml, result.HtmlUrl);
        Assert.Equal(GoodDownload, Assert.Single(result.Assets).DownloadUrl);
        Assert.DoesNotContain("ignored", result.Detail, StringComparison.Ordinal);
    }
}
