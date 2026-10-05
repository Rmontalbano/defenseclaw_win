using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Shell;

/// <summary>
/// Skill, plugin, MCP, registry and tool names are attacker-chosen text that lands in the review heading, the command box and Activity.
/// A right-to-left override, a zero-width character or a newline in one can make a confirmation read as a different item, so those
/// characters are spelled out where the name is shown (<c>\u202E</c>, <c>\n</c>), the review says when a name holds anything unusual,
/// and the argv that runs - after <c>--</c> - is not touched.
/// </summary>
public sealed class NameDisplayTests : IDisposable
{
    private const string Rlo = "\u202E";

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public NameDisplayTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static CommandReview ReviewOf(string title, params string[] argv) =>
        new() { Title = title, Steps = new[] { new CommandReviewStep(argv, floor: CommandTier.StateChanging) } };

    private static CommandReviewWarning? UnusualWarning(CommandReview review) =>
        review.Warnings.FirstOrDefault(w => w.Title == CommandReviewWarning.UnusualCharactersTitle);

    // ------------------------------------------------------------------ the command box

    [Fact]
    public void The_command_box_spells_out_the_characters_of_a_hostile_name_on_one_line()
    {
        var step = new CommandReviewStep(new[] { "skill", "block", "--", "pdf" + Rlo + "tools\nrm -rf" });

        Assert.Equal(@"defenseclaw skill block -- ""pdf\u202Etools\nrm -rf""", step.CommandText);
        Assert.DoesNotContain('\n', step.CommandText);
        Assert.DoesNotContain(Rlo[0], step.CommandText);
    }

    [Fact]
    public void The_argv_and_the_text_the_copy_button_pastes_are_the_real_name()
    {
        var name = "pdf" + Rlo + "tools\nrm";
        var step = new CommandReviewStep(new[] { "skill", "block", "--", name });

        Assert.Equal(name, step.Argv[^1]);
        Assert.Contains(Rlo[0], step.ClipboardText);
        Assert.Contains('\n', step.ClipboardText);
    }

    [Fact]
    public void A_name_with_a_space_is_still_quoted_and_one_with_only_a_zero_width_character_is_not_empty()
    {
        Assert.Equal("defenseclaw skill block -- \"my skill\"", new CommandReviewStep(new[] { "skill", "block", "--", "my skill" }).CommandText);
        Assert.Equal("defenseclaw skill block -- \\u200B", new CommandReviewStep(new[] { "skill", "block", "--", "\u200B" }).CommandText);
        Assert.Equal("\"\"", CommandReview.Quote(string.Empty));
    }

    [Fact]
    public void Every_line_of_the_review_text_is_one_line_per_step()
    {
        var review = new CommandReview
        {
            Title = "Run two?",
            Steps = new[]
            {
                new CommandReviewStep(new[] { "skill", "block", "--", "a\nb" }, number: 1),
                new CommandReviewStep(new[] { "skill", "allow", "--", "c\r\nd" }, number: 2),
            },
        };

        Assert.Equal(new[] { @"defenseclaw skill block -- a\nb", @"defenseclaw skill allow -- c\r\nd" }, review.CommandText.Split(Environment.NewLine));
    }

    // ------------------------------------------------------------------ the heading

    [Fact]
    public void The_heading_spells_out_a_name_and_stays_on_one_line()
    {
        var review = ReviewOf("Block skill “pdf" + Rlo + "tools\nrm”?", "skill", "block", "--", "x");

        Assert.Equal(@"Block skill “pdf\u202Etools\nrm”?", review.Title);
        Assert.Equal("Review command: " + review.Title, review.AutomationName);
    }

    [Fact]
    public void An_ordinary_heading_is_unchanged_and_a_review_copy_keeps_the_heading()
    {
        var review = ReviewOf("Block skill “pdf-tools” for connector “claudecode”?", "skill", "block", "--", "pdf-tools");

        Assert.Equal("Block skill “pdf-tools” for connector “claudecode”?", review.Title);
        Assert.Equal(review.Title, (review with { ConfirmLabel = "Block" }).Title);
    }

    [Fact]
    public void A_step_purpose_that_names_an_item_is_spelled_out_too()
    {
        var step = new CommandReviewStep(new[] { "registry", "approve", "src", "x" }, "Approve a" + Rlo + "b\nfrom src.");

        Assert.Equal(@"Approve a\u202Eb\nfrom src.", step.Purpose);
    }

    // ------------------------------------------------------------------ the warning

    [Fact]
    public void A_target_with_a_bidirectional_override_adds_the_warning_with_what_is_in_it()
    {
        var review = ReviewOf("Block skill?", "skill", "block", "--", "pdf" + Rlo + "tools");

        var warning = Assert.IsType<CommandReviewWarning>(UnusualWarning(review));
        Assert.StartsWith("The name contains unusual characters (U+202E bidirectional control).", warning.Message, StringComparison.Ordinal);
        Assert.Contains("spelled out", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pdf\u200Btools", "U+200B zero-width or invisible")]
    [InlineData("a\nb", "U+000A line break")]
    [InlineData("p\u0430f", "U+0430 non-ASCII character")]
    [InlineData("pdf\u2011tools", "U+2011 non-ASCII character")]
    [InlineData("a\u00A0b", "U+00A0 space that is not U+0020")]
    public void Zero_width_characters_newlines_and_homoglyphs_each_raise_it(string name, string described)
    {
        var review = ReviewOf("Block skill?", "skill", "block", "--", name);

        var warning = Assert.IsType<CommandReviewWarning>(UnusualWarning(review));
        Assert.Contains($"The name contains unusual characters ({described})", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pdf-tools")]
    [InlineData("my skill")]
    [InlineData("--dangerous")]
    [InlineData("C:\\Users\\operator\\skills\\pdf")]
    public void An_ordinary_name_raises_nothing(string name)
    {
        var review = ReviewOf("Block skill?", "skill", "block", "--", name);

        Assert.Null(UnusualWarning(review));
        Assert.Empty(review.Warnings);
    }

    [Fact]
    public void Only_the_targets_are_judged_not_the_words_of_the_command_or_its_option_values()
    {
        // A reason typed by the operator, a connector, a path: none of them is a name from outside, so a reason with an accent does not warn.
        var review = ReviewOf("Block skill?", "skill", "block", "--reason", "pas vérifié", "--connector", "claudecode", "--", "pdf-tools");

        Assert.Null(UnusualWarning(review));
    }

    [Fact]
    public void A_name_listed_by_the_surface_is_judged_like_a_target_and_not_twice()
    {
        var review = new CommandReview
        {
            Title = "Approve entry?",
            Steps = new[] { new CommandReviewStep(new[] { "registry", "approve", "src", "x" + Rlo + "y", "--type", "skill" }, floor: CommandTier.StateChanging) },
            Names = new[] { "src", "x" + Rlo + "y" },
        };

        var warning = Assert.Single(review.Warnings, w => w.Title == CommandReviewWarning.UnusualCharactersTitle);
        Assert.StartsWith("The name contains unusual characters (U+202E bidirectional control).", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_hostile_names_make_one_warning_that_describes_both()
    {
        var review = new CommandReview
        {
            Title = "Approve entry?",
            Steps = new[] { new CommandReviewStep(new[] { "registry", "approve", "s\u200Bx", "y" + Rlo + "z" }, floor: CommandTier.StateChanging) },
            Names = new[] { "s\u200Bx", "y" + Rlo + "z" },
        };

        var warning = Assert.Single(review.Warnings, w => w.Title == CommandReviewWarning.UnusualCharactersTitle);
        Assert.StartsWith("2 names contain unusual characters (U+200B zero-width or invisible; U+202E bidirectional control).", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_warning_the_surface_supplied_itself_is_kept_and_not_repeated()
    {
        var supplied = new CommandReviewWarning(CommandReviewWarning.UnusualCharactersTitle, "Custom words.");
        var review = new CommandReview
        {
            Title = "Block skill?",
            Steps = new[] { new CommandReviewStep(new[] { "skill", "block", "--", "a" + Rlo + "b" }, floor: CommandTier.StateChanging) },
            Warnings = new[] { supplied },
        };

        Assert.Equal(new[] { supplied }, review.Warnings);
    }

    [Fact]
    public void The_warning_sits_beside_the_other_derived_warnings_and_the_tier_is_not_lowered()
    {
        var review = ReviewOf("Block skill?", "config", "show", "--reveal", "--", "a" + Rlo + "b");

        Assert.Contains(review.Warnings, w => w.Title == CommandReviewWarning.SecretOutputTitle);
        Assert.Contains(review.Warnings, w => w.Title == CommandReviewWarning.UnusualCharactersTitle);
        Assert.NotEqual(CommandTier.ReadOnly, review.Tier);
    }

    // ------------------------------------------------------------------ the Govern panels

    private static void Act(GovernPanelViewModelBase vm, GovernRow row, GovernVerbs verb) =>
        ((IGovernRowHost)vm).OnRowAction(row, verb);

    private static GovernRow Skill(SkillsPanelViewModel vm, string name) =>
        Assert.Single(vm.ParseRows(System.Text.Json.JsonSerializer.Serialize(new[] { new { name } })));

    [Fact]
    public void A_skill_with_a_hostile_name_is_reviewed_with_the_name_spelled_out_the_warning_and_the_real_argv()
    {
        var vm = new SkillsPanelViewModel(_services);
        var name = "pdf" + Rlo + "tools\nrm";

        Act(vm, Skill(vm, name), GovernVerbs.Quarantine);

        Assert.True(vm.IsConfirmOpen);
        Assert.StartsWith(@"Quarantine skill “pdf\u202Etools\nrm” on ", vm.ConfirmHeading, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', vm.ConfirmHeading);
        Assert.Equal(@"defenseclaw skill quarantine -- pdf\u202Etools\nrm", vm.ConfirmCommandText);

        var review = Assert.IsType<CommandReview>(vm.ConfirmReview);
        Assert.Equal(name, Assert.Single(review.Steps).Argv[^1]);
        Assert.Contains("U+202E bidirectional control", UnusualWarning(review)!.Message, StringComparison.Ordinal);
        Assert.Contains("U+000A line break", UnusualWarning(review)!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_skill_with_an_ordinary_name_gets_no_such_warning()
    {
        var vm = new SkillsPanelViewModel(_services);

        Act(vm, Skill(vm, "pdf-tools"), GovernVerbs.Quarantine);

        Assert.Null(UnusualWarning(Assert.IsType<CommandReview>(vm.ConfirmReview)));
    }

    // ------------------------------------------------------------------ the Discover review (registries)

    [Fact]
    public void The_discover_review_judges_the_names_its_surface_lists_even_when_the_argv_has_no_double_dash()
    {
        var review = new DiscoverActionReview(_services);
        var name = "x" + Rlo + "y";

        review.Open(
            "Approve skill “" + name + "”?",
            "Marks the entry approved.",
            new[] { new DiscoverStep(new[] { "registry", "approve", "src", name, "--type", "skill", "--json" }, "Approve " + name + " from src.") },
            names: new[] { "src", name });

        var shown = Assert.IsType<CommandReview>(review.CommandReview);
        Assert.Equal(@"Approve skill “x\u202Ey”?", shown.Title);
        Assert.Equal(@"Approve x\u202Ey from src.", Assert.Single(shown.Steps).Purpose);
        Assert.Equal(@"defenseclaw registry approve src x\u202Ey --type skill --json", Assert.Single(shown.Steps).CommandText);
        Assert.NotNull(UnusualWarning(shown));
    }

    [Fact]
    public void The_discover_review_with_no_names_and_no_double_dash_has_nothing_to_flag()
    {
        var review = new DiscoverActionReview(_services);

        review.Open(
            "Sync “src”?",
            "Fetches the manifest.",
            new[] { new DiscoverStep(new[] { "registry", "sync", "src", "--json" }, "Fetch from src.") });

        Assert.Null(UnusualWarning(Assert.IsType<CommandReview>(review.CommandReview)));
    }

    // ------------------------------------------------------------------ Activity

    [Fact]
    public void An_activity_card_shows_the_name_spelled_out_on_one_line_and_pastes_the_real_one()
    {
        var name = "pdf" + Rlo + "tools\nrm";
        var copied = new List<string>();
        var row = new ActivityRow(InvocationFactory.Create(false, "skill", "block", "--", name), runner: null, notify: _ => { })
        {
            ClipboardWriter = copied.Add,
        };

        Assert.Equal(@"C:\test\defenseclaw.exe skill block -- pdf\u202Etools\nrm", row.CommandLine);
        Assert.Equal(@"defenseclaw skill block -- pdf\u202Etools\nrm", row.ShortCommand);
        Assert.DoesNotContain('\n', row.CommandLine);
        Assert.DoesNotContain('\n', row.ShortCommand);

        row.CopyCommand.Execute(null);
        Assert.Contains(name, Assert.Single(copied), StringComparison.Ordinal);
    }
}
