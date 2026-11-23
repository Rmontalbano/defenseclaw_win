using System.Runtime.CompilerServices;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Wizards;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.Tests.Wizards;

/// <summary>
/// <c>setup openclaw --replace</c> removes every other connector (runtime 1.0.10, cmd_setup.py@1.0.10:15395-15400,15451-15452). Its Destructive tier and the
/// "Removes every other connector" bar hold on every review path: the wizard, the palette, the Discover/Overview review and the Activity rerun, and for every
/// spelling the CLI accepts (the setup group's own options may come first: cmd_setup.py@1.0.10:547-600). Nothing is started.
/// </summary>
public sealed class OpenClawReplaceEveryPathTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public OpenClawReplaceEveryPathTests() => _services = TestServices.Create(_temp);

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    // ------------------------------------------------------------------ the argv rule

    [Theory]
    [InlineData(true, "setup", "openclaw", "--replace", "--yes")]
    [InlineData(true, "setup", "openclaw", "--yes", "--replace")]
    [InlineData(true, "setup", "--yes", "openclaw", "--replace")]
    [InlineData(true, "setup", "-y", "openclaw", "--yes", "--replace")]
    [InlineData(true, "setup", "--mode", "action", "openclaw", "--replace")]
    [InlineData(true, "setup", "--mode=action", "openclaw", "--replace")]
    [InlineData(true, "setup", "--restart", "--mode", "observe", "openclaw", "--replace", "--yes")]
    [InlineData(true, "setup", "-c", "codex", "openclaw", "--replace")]
    [InlineData(true, "setup", "-ccodex", "openclaw", "--replace")]
    [InlineData(true, "setup", "-yc", "codex", "openclaw", "--replace")]
    [InlineData(true, "setup", "--", "openclaw", "--replace")]
    [InlineData(false, "setup", "openclaw", "--yes")]
    [InlineData(false, "setup", "openclaw", "--", "--replace")]
    [InlineData(false, "setup", "openclaw", "--yes", "--", "--replace")]
    [InlineData(false, "setup", "--yes", "openclaw")]
    [InlineData(false, "setup", "codex", "--replace", "--yes")]
    [InlineData(false, "setup", "--connector", "openclaw", "codex", "--replace")]
    [InlineData(false, "setup", "-c", "openclaw", "zeptoclaw", "--replace")]
    [InlineData(false, "setup", "--mode", "openclaw", "codex", "--replace")]
    [InlineData(false, "setup", "openclaw=x", "--replace")]
    [InlineData(false, "setup", "openclaw", "--replace=yes")]
    [InlineData(false, "setup", "OpenClaw", "--replace")]
    [InlineData(false, "openclaw", "--replace")]
    [InlineData(false, "setup")]
    public void Applies_reads_the_target_the_way_the_setup_group_does(bool expected, params string[] argv)
    {
        Assert.Equal(expected, OpenClawReplaceReview.Applies(argv));
    }

    [Theory]
    [InlineData("setup", "--yes", "openclaw", "--replace")]
    [InlineData("setup", "openclaw", "--yes", "--replace")]
    public void The_tier_is_destructive_for_every_spelling_and_the_plain_command_is_not(params string[] argv)
    {
        Assert.Equal(CommandTier.Destructive, CommandReview.ResolveTier(argv));
        Assert.Equal(CommandTier.Destructive, CommandReview.ResolveTier(argv, CommandTier.StateChanging));
        Assert.NotEqual(CommandTier.Destructive, CommandReview.ResolveTier(new[] { "setup", "openclaw", "--yes" }, CommandTier.StateChanging));
        Assert.NotEqual(CommandTier.Destructive, CommandReview.ResolveTier(new[] { "setup", "openclaw", "--", "--replace" }, CommandTier.StateChanging));
    }

    // ------------------------------------------------------------------ every review path

    [Fact]
    public void The_discover_review_is_destructive_and_carries_the_bar()
    {
        var review = new DiscoverActionReview(_services);
        var step = new DiscoverStep(new[] { "setup", "--yes", "openclaw", "--replace" }, "Switch to OpenClaw.");

        Assert.Equal(CommandTier.Destructive, DiscoverActionReview.EffectiveTier(step));
        review.Open("Switch to OpenClaw?", "Removes the others.", new[] { step });
        Assert.Contains(review.CommandReview!.Warnings, w => w.Title == OpenClawReplaceReview.Title);
        Assert.True(review.CommandReview.IsDestructive);
        review.DismissCommand.Execute(null);

        var plain = new DiscoverStep(new[] { "setup", "openclaw", "--yes" }, "Set up OpenClaw.");
        review.Open("Set up OpenClaw?", "Adds it.", new[] { plain });
        Assert.DoesNotContain(review.CommandReview!.Warnings, w => w.Title == OpenClawReplaceReview.Title);
    }

    [Fact]
    public void The_activity_rerun_of_a_replace_is_destructive_and_carries_the_bar()
    {
        var rerun = new CommandRerun(_services);
        var replace = InvocationFactory.CreateFor("defenseclaw", new[] { "setup", "openclaw", "--yes", "--replace" });
        InvocationFactory.Finish(replace, 0);
        var plain = InvocationFactory.CreateFor("defenseclaw", new[] { "setup", "openclaw", "--yes" });
        InvocationFactory.Finish(plain, 0);

        Assert.Equal(CommandTier.Destructive, CommandRerun.TierOf(replace));
        Assert.NotEqual(CommandTier.Destructive, CommandRerun.TierOf(plain));

        var review = rerun.ReviewFor(replace, CommandRerun.TierOf(replace));
        Assert.Contains(review.Warnings, w => w.Title == OpenClawReplaceReview.Title);
        Assert.True(review.IsDestructive);
        Assert.DoesNotContain(rerun.ReviewFor(plain, CommandRerun.TierOf(plain)).Warnings, w => w.Title == OpenClawReplaceReview.Title);
    }

    [Fact]
    public async Task The_palette_refuses_replace_and_runs_nothing()
    {
        var tray = (TrayIconService)RuntimeHelpers.GetUninitializedObject(typeof(TrayIconService));
        var shown = new List<CommandReview>();
        var toasts = new List<string>();
        var actions = new ShellActions(_services, new PanelCatalog(_services), tray, () => null)
        {
            Toast = (title, text) => toasts.Add(title + ": " + text),
            ClipboardWriter = _ => { },
            Confirmer = review =>
            {
                shown.Add(review);
                return false;
            },
        };

        await actions.RunCuratedAsync(new CuratedCommand(new[] { "setup", "--yes", "openclaw", "--replace" }, "Setup", "Switch to OpenClaw.", string.Empty, Array.Empty<string>()));
        // The palette never passes --replace on (its argv check refuses an option nobody reviewed for it), so nothing is reviewed or run; the palette's own
        // openclaw entry is the fixed `setup openclaw --yes`. The bar ShellActions adds is for any later entry that would carry it.
        Assert.Empty(shown);
        Assert.Contains(toasts, t => t.Contains("Refused", StringComparison.Ordinal));
        Assert.Empty(_services.Cli.Activity);
    }

    [Fact]
    public void The_wizard_floor_and_bar_are_unchanged()
    {
        var replace = new[] { "setup", "openclaw", "--mode", "observe", "--replace", "--yes" };

        Assert.Equal(CommandTier.Destructive, WizardCautions.Floor(replace));
        Assert.Single(WizardCautions.For(replace), w => w.Title == OpenClawReplaceReview.Title);
        Assert.Single(WizardCautions.For(new[] { "setup", "--yes", "openclaw", "--replace" }), w => w.Title == OpenClawReplaceReview.Title);
    }
}
