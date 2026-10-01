using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// CUST-220: the pre-save review (a line diff of config.yaml on disk against the edited RAW, secrets masked) and the
/// "restart the gateway to apply" follow-up after a save. Everything runs on temp copies through
/// <see cref="ConfigEditorHarness"/>; the restart is a seam that records the request, so nothing is started.
/// </summary>
public sealed class ConfigReviewTests
{
    // Assembled so no source scanner takes these test literals for credentials; none is real.
    private const string OldSecret = "sample-old-" + "secret-value-12345";
    private const string NewSecret = "sample-new-" + "secret-value-67890";

    private static string ShownText(ConfigDiffReview review) =>
        string.Join("\n", review.Lines.Select(l => l.Text));

    // ------------------------------------------------------------------ masking

    [Fact]
    public void A_changed_secret_shows_as_a_changed_line_but_never_as_its_value()
    {
        var oldText = "gateway:\n  host: 127.0.0.1\n  token: " + OldSecret + "\n";
        var newText = "gateway:\n  host: 127.0.0.1\n  token: " + NewSecret + "\n";

        var review = ConfigDiffReviewBuilder.Build(oldText, newText);

        Assert.Equal(1, review.Added);
        Assert.Equal(1, review.Removed);
        var shown = ShownText(review);
        Assert.DoesNotContain(OldSecret, shown, StringComparison.Ordinal);
        Assert.DoesNotContain(NewSecret, shown, StringComparison.Ordinal);
        Assert.Contains("token: " + ConfigDiffReviewBuilder.Hidden, shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_var_names_stay_visible_because_they_are_not_values()
    {
        var review = ConfigDiffReviewBuilder.Build("a:\n  token_env: OLD_VAR\n", "a:\n  token_env: NEW_VAR\n");

        var shown = ShownText(review);
        Assert.Contains("OLD_VAR", shown, StringComparison.Ordinal);
        Assert.Contains("NEW_VAR", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_and_maps_beneath_a_secret_or_header_key_are_hidden_whole()
    {
        var raw = LineEndings.Normalize(ConfigSamples.Raw);

        // The whole file, masked line for line (the review only shows the context around a change).
        var masked = string.Join("\n", ConfigDiffReviewBuilder.MaskLines(LineDiffLines(raw)));

        Assert.DoesNotContain("sample-not-a-real-token", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("sample-auth-value", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("key-one", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("key-two", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pw", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("key=abc", masked, StringComparison.Ordinal);
        Assert.Contains("X-Team", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("platform", masked, StringComparison.Ordinal);
        Assert.Contains("OPENAI_API_KEY", masked, StringComparison.Ordinal);
        Assert.Contains("gpt-4o", masked, StringComparison.Ordinal);
        Assert.Contains("ok.example.test", masked, StringComparison.Ordinal);
    }

    private static string[] LineDiffLines(string text) => DefenseClaw.Core.Text.LineDiff.SplitLines(text);

    [Fact]
    public void The_masked_text_has_the_same_number_of_lines_as_the_input()
    {
        var lines = LineDiffLines(LineEndings.Normalize(ConfigSamples.Raw));
        Assert.Equal(lines.Length, ConfigDiffReviewBuilder.MaskLines(lines).Length);
    }

    [Fact]
    public void A_secret_looking_value_under_an_innocent_key_is_hidden_too()
    {
        // Assembled from halves: a GitHub-token-shaped literal.
        var token = "ghp" + "_0123456789abcdefghijklmnopqrstuvwxyz";
        var review = ConfigDiffReviewBuilder.Build("a:\n  note: hello\n", "a:\n  note: " + token + "\n");

        Assert.DoesNotContain(token, ShownText(review), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the review

    [Fact]
    public void The_review_has_tinted_kinds_markers_line_numbers_and_folded_context()
    {
        var oldText = string.Join('\n', Enumerable.Range(1, 30).Select(i => "k" + i + ": v")) + "\n";
        var newText = oldText.Replace("k15: v", "k15: changed", StringComparison.Ordinal);

        var review = ConfigDiffReviewBuilder.Build(oldText, newText);

        Assert.Equal(new[] { "Fold", "Context", "Context", "Context", "Removed", "Added", "Context", "Context", "Context", "Fold" },
            review.Lines.Select(l => l.Kind).ToArray());
        var removed = review.Lines.Single(l => l.Kind == "Removed");
        var added = review.Lines.Single(l => l.Kind == "Added");
        Assert.Equal(15, removed.OldNumber);
        Assert.Equal("-", removed.Marker);
        Assert.Equal(15, added.NewNumber);
        Assert.Equal("+", added.Marker);
        Assert.Equal("11 unchanged lines", review.Lines[0].Text);
    }

    [Fact]
    public void A_line_ending_only_difference_says_so_instead_of_showing_nothing()
    {
        var review = ConfigDiffReviewBuilder.Build("a: 1\r\nb: 2\r\n", "a: 1\nb: 2\n");

        Assert.False(review.HasLineChanges);
        Assert.True(review.TextsDiffer);
        Assert.Contains("line endings", review.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Save_opens_the_review_without_writing_anything(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        vm.RawText = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);

        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewing);
        Assert.NotNull(vm.Review);
        Assert.Equal(1, vm.Review!.Added);
        Assert.Equal(1, vm.Review.Removed);
        Assert.Equal(harness.Raw, File.ReadAllText(harness.ConfigPath));
        Assert.False(vm.ShowSaveResultBanner);
    }

    [Fact]
    public async Task The_review_of_a_real_config_never_contains_its_secrets()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        vm.RawText = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal)
            .Replace("sample-not-a-real-token", NewSecret, StringComparison.Ordinal);

        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        var shown = ShownText(vm.Review!);
        Assert.DoesNotContain("sample-not-a-real-token", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(NewSecret, shown, StringComparison.Ordinal);
        Assert.Equal(2, vm.Review!.Added);
    }

    [Fact]
    public async Task Back_to_editing_leaves_the_buffer_and_the_file_alone()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var edited = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);
        vm.RawText = edited;
        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        vm.CancelReviewCommand.Execute(null);

        Assert.False(vm.IsReviewing);
        Assert.Null(vm.Review);
        Assert.Equal(edited, vm.RawText);
        Assert.True(vm.HasUnsavedChanges);
        Assert.Equal(harness.Raw, File.ReadAllText(harness.ConfigPath));
    }

    [Fact]
    public async Task Saving_from_the_review_runs_the_pipeline_and_writes_the_edit()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var edited = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);
        vm.RawText = edited;
        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        await vm.ConfirmReviewedSaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsReviewing);
        Assert.Equal(edited, File.ReadAllText(harness.ConfigPath));
        Assert.False(vm.HasUnsavedChanges);
        Assert.NotNull(vm.LastBackupPath);
        Assert.True(File.Exists(vm.LastBackupPath));
    }

    [Fact]
    public async Task Save_with_nothing_changed_has_nothing_to_review_and_saves_directly()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;

        await vm.ReviewAndSaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsReviewing);
        Assert.True(vm.ShowSaveResultBanner);
    }

    [Fact]
    public async Task The_review_cannot_open_while_a_load_has_failed()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        vm.RawText = harness.Raw + "x: 1\n";
        vm.LoadError = "config.yaml could not be read";

        Assert.False(vm.ReviewAndSaveCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ restart follow-up

    [Fact]
    public async Task A_validated_save_offers_the_restart_and_the_button_only_asks_for_the_review()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        var asked = 0;
        vm.RestartGatewayRequest = () =>
        {
            asked++;
            return Task.CompletedTask;
        };
        Assert.False(vm.ShowRestartPrompt);

        vm.RawText = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.ShowRestartPrompt);
        Assert.True(vm.CanRestartGateway);
        Assert.Equal(0, asked);

        await vm.RestartGatewayCommand.ExecuteAsync(null);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task Without_a_wired_restart_the_bar_has_no_button()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;

        vm.RawText = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.ShowRestartPrompt);
        Assert.False(vm.CanRestartGateway);
    }

    [Fact]
    public async Task A_rejected_save_does_not_offer_a_restart()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        harness.PlantUnrunnableCli();
        var vm = harness.ViewModel;

        vm.RawText = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.ShowRestartPrompt);
    }

    [Fact]
    public async Task Editing_again_or_dismissing_takes_the_restart_bar_down()
    {
        using var harness = await ConfigEditorHarness.LoadAsync();
        var vm = harness.ViewModel;
        vm.RawText = harness.Raw.Replace("mode: observe", "mode: enforce", StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.ShowRestartPrompt);

        vm.RawText += "extra: 1\n";
        Assert.False(vm.ShowRestartPrompt);

        vm.RawText = vm.RawText.Replace("extra: 1\n", string.Empty, StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.ShowRestartPrompt);

        vm.DismissRestartPromptCommand.Execute(null);
        Assert.False(vm.ShowRestartPrompt);
    }
}
