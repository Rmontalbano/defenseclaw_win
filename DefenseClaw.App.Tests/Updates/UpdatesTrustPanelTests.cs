using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Updates;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.Updates;

namespace DefenseClaw.App.Tests.Updates;

/// <summary>
/// The trust panel's signature row. Reading a release can tell which signature files it PUBLISHES and nothing about whether they are valid, so before
/// anything is verified the row says "published, not verified yet" - never "signed" - and afterwards it says what cosign found, with "verified" for a
/// verified signature only.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class UpdatesTrustPanelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public UpdatesTrustPanelTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static ReleaseAsset Asset(string name, long size = 4096) => new() { Name = name, Size = size };

    private static async Task<ProvenanceReport> InspectAsync(params string[] assetNames)
    {
        using var inspector = new ProvenanceInspector(new HttpClient(new StubHttpHandler()), ownsHttpClient: true);
        return await inspector.InspectAsync(assetNames.Select(n => Asset(n)).ToArray());
    }

    private T WithPanel<T>(Func<UpdatesWindowViewModel, T> body) =>
        UiThread.Run(() =>
        {
            using var vm = new UpdatesWindowViewModel(_services);
            return body(vm);
        });

    // ------------------------------------------------------------------ what the release publishes

    [Theory]
    [InlineData(new[] { "checksums.txt", "checksums.txt.bundle" }, true)]
    [InlineData(new[] { "checksums.txt", "checksums.txt.sig", "checksums.txt.pem" }, true)]
    [InlineData(new[] { "checksums.txt", "checksums.txt.bundle", "checksums.txt.sig", "checksums.txt.pem" }, true)]
    [InlineData(new[] { "checksums.txt", "checksums.txt.sig" }, false)]
    [InlineData(new[] { "checksums.txt", "checksums.txt.pem" }, false)]
    [InlineData(new[] { "checksums.txt" }, false)]
    [InlineData(new[] { "checksums.txt.bundle", "checksums.txt.sig", "checksums.txt.pem" }, false)]
    [InlineData(new string[0], false)]
    public async Task A_release_publishes_a_signature_when_it_lists_a_bundle_or_a_signature_with_its_certificate_next_to_checksums_txt(string[] assets, bool published)
    {
        var report = await InspectAsync(assets);

        Assert.Equal(published, report.SignatureFilesPublished);
    }

    [Fact]
    public async Task The_report_says_which_files_are_there_and_that_is_all_it_says()
    {
        var report = await InspectAsync("checksums.txt", "checksums.txt.bundle", "checksums.txt.sig", "checksums.txt.pem");

        Assert.True(report.ChecksumsAssetPresent);
        Assert.True(report.ChecksumsBundlePresent);
        Assert.True(report.ChecksumsSignaturePresent);
        Assert.True(report.ChecksumsCertificatePresent);
    }

    [Fact]
    public async Task A_stub_asset_warning_does_not_call_checksums_txt_signed()
    {
        using var inspector = new ProvenanceInspector(new HttpClient(new StubHttpHandler()), ownsHttpClient: true);

        var report = await inspector.InspectAsync(new[] { new ReleaseAsset { Name = "defenseclaw_0.8.10_linux_amd64.tar.gz", Size = 134 } });

        var warning = Assert.Single(report.StubWarnings);
        Assert.DoesNotContain("signed", warning.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a hash comparison alone would pass it", warning.Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the row before anything is verified

    [Fact]
    public async Task A_published_bundle_is_shown_as_published_and_not_verified_yet_never_as_signed()
    {
        var report = await InspectAsync("checksums.txt", "checksums.txt.bundle");

        var (label, badge, detail) = WithPanel(vm =>
        {
            vm.ApplyProvenance(report);
            return (vm.SigstoreLabel, vm.SigstoreBadgeKey, vm.SigstoreDetail);
        });

        Assert.Equal("signature files published, not verified yet", label);
        Assert.Equal("Neutral", badge);
        Assert.Contains("checksums.txt.bundle", detail, StringComparison.Ordinal);
        Assert.Contains("Nothing here has checked that signature", detail, StringComparison.Ordinal);
        Assert.Contains("verifies it with cosign when cosign is installed", detail, StringComparison.Ordinal);
        Assert.Contains("not that the project signed it", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("signed with sigstore", label + detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verified (cosign)", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_published_signature_with_its_certificate_names_those_files()
    {
        var report = await InspectAsync("checksums.txt", "checksums.txt.sig", "checksums.txt.pem");

        var detail = WithPanel(vm =>
        {
            vm.ApplyProvenance(report);
            return vm.SigstoreDetail;
        });

        Assert.Contains("(.sig and .pem)", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_whose_signature_is_incomplete_says_it_has_none_to_verify()
    {
        var report = await InspectAsync("checksums.txt", "checksums.txt.sig");

        var (label, badge, detail) = WithPanel(vm =>
        {
            vm.ApplyProvenance(report);
            return (vm.SigstoreLabel, vm.SigstoreBadgeKey, vm.SigstoreDetail);
        });

        Assert.Equal("checksums.txt present, no signature published", label);
        Assert.Equal("Warn", badge);
        Assert.Contains(".pem certificate", detail, StringComparison.Ordinal);
        Assert.Contains("no sigstore signature to verify", detail, StringComparison.Ordinal);
        Assert.Contains("integrity check only", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_release_with_no_checksums_file_is_still_bad_news()
    {
        var report = await InspectAsync("DefenseClawSetup-x64.exe");

        var (label, badge) = WithPanel(vm =>
        {
            vm.ApplyProvenance(report);
            return (vm.SigstoreLabel, vm.SigstoreBadgeKey);
        });

        Assert.Equal("No checksums.txt on this release", label);
        Assert.Equal("Bad", badge);
    }

    // ------------------------------------------------------------------ the row after cosign has answered

    [Theory]
    [InlineData(ChecksumsSignatureState.Verified, "checksums.txt signature verified (cosign)", "Ok")]
    [InlineData(ChecksumsSignatureState.NotVerifiedNoCosign, "signature present, not verified (cosign not installed)", "Warn")]
    [InlineData(ChecksumsSignatureState.NotVerifiedCosignUnusable, "signature present, not verified (cosign could not run)", "Warn")]
    [InlineData(ChecksumsSignatureState.NotVerifiedUnavailable, "signature not verified (signature files could not be fetched)", "Warn")]
    [InlineData(ChecksumsSignatureState.DidNotVerify, "signature did not verify", "Bad")]
    [InlineData(ChecksumsSignatureState.NotPublished, "no signature published for checksums.txt", "Warn")]
    public void The_row_shows_what_cosign_found_in_the_same_words_as_the_upgrade_card(ChecksumsSignatureState state, string label, string badge)
    {
        var result = new ChecksumsSignatureResult { State = state, Detail = "Detail for " + state };

        var (shownLabel, shownBadge, shownDetail) = WithPanel(vm =>
        {
            vm.ApplySignature(result);
            return (vm.SigstoreLabel, vm.SigstoreBadgeKey, vm.SigstoreDetail);
        });

        Assert.Equal(label, shownLabel);
        Assert.Equal(badge, shownBadge);
        Assert.Equal(result.Detail, shownDetail);
    }

    [Fact]
    public void The_upgrade_cards_signature_event_updates_the_row()
    {
        var shown = WithPanel(vm =>
        {
            vm.Upgrade.ReportSignature(new ChecksumsSignatureResult { State = ChecksumsSignatureState.DidNotVerify, Detail = "rejected" });
            return (vm.SigstoreLabel, vm.SigstoreBadgeKey);
        });

        Assert.Equal(("signature did not verify", "Bad"), shown);
    }

    [Fact]
    public void A_check_that_found_nothing_to_inspect_says_so_without_a_claim()
    {
        var (label, detail) = WithPanel(vm =>
        {
            vm.ApplyProvenance(new ProvenanceReport());
            return (vm.SigstoreLabel, vm.SigstoreDetail);
        });

        Assert.Equal("No checksums.txt on this release", label);
        Assert.DoesNotContain("signed", detail, StringComparison.OrdinalIgnoreCase);
    }
}
