using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The first page of the OpenClaw setup card on a runtime where OpenClaw runs on native Windows (1.0.10,
/// <see cref="RuntimeCapability.OpenClawNativeWindows"/>): what has to be true before <c>defenseclaw setup openclaw</c> changes anything, in the runtime's own
/// words. Before 1.0.10 the CLI calls OpenClaw unsupported on Windows, the card is not offered, and nothing is added here.
/// <para>
/// Each line is from the runtime: OpenClaw 2026.8.35 or later and the <c>npm install -g openclaw@latest</c> fix (platform_support.py and the setup refusal),
/// <c>defenseclaw init --connector openclaw</c> first on a new install and OpenClaw's own gateway running, else "No changes made." (docs
/// <c>connectors/openclaw.mdx</c>, Setup; <c>_require_openclaw_gateway_before_setup</c>), the preflight command that prints <c>openclaw preflight: ok</c>
/// or the reason (same page, Setup preflight), OpenClaw running alone (<c>_refuse_proxy_next_to_hook_connectors</c>: "runs alone in this release"; its
/// <c>--replace</c> removes the hook connectors), and the restart of OpenClaw's gateway so it loads the plugin (<c>_restart_services</c>, which setup's
/// default <c>--restart</c> runs). The preflight command is text to copy: the app never runs it.
/// </para>
/// </summary>
public static class OpenClawSetupIntro
{
    public const string Target = "openclaw";

    /// <summary>The id of the intro page, for a test and for the "already added" check.</summary>
    public const string StepId = "openclaw-before-you-start";

    /// <summary>The command the intro tells the operator to run in a terminal. Shown, never run by the app.</summary>
    public const string PreflightCommand = "defenseclaw-gateway connector openclaw preflight";

    /// <summary>True when the runtime has OpenClaw on native Windows, so the OpenClaw card opens on the intro.</summary>
    public static bool IsOffered(RuntimeCapabilities? capabilities) =>
        capabilities is not null && capabilities.Has(RuntimeCapability.OpenClawNativeWindows);

    /// <summary>Puts the intro in front of <paramref name="steps"/> for the OpenClaw card on a runtime that has OpenClaw on Windows; every other target or runtime comes back unchanged.</summary>
    public static IReadOnlyList<WizardStep> Apply(string target, IReadOnlyList<WizardStep> steps, RuntimeCapabilities? capabilities)
    {
        ArgumentNullException.ThrowIfNull(steps);

        if (!string.Equals(target, Target, StringComparison.Ordinal) ||
            !IsOffered(capabilities) ||
            steps.Count == 0 ||
            steps.Any(static s => s.Id == StepId))
        {
            return steps;
        }

        var card = new WizardGuideCard
        {
            Title = "OpenClaw on Windows",
            Summary = "DefenseClaw guards OpenClaw's tool calls and prompts through OpenClaw's own plugin hooks. No guardrail proxy runs on Windows.",
            Needs = new[]
            {
                "OpenClaw 2026.8.35 or later. Setup refuses an older one. Update it with npm install -g openclaw@latest.",
                "On a new install, run defenseclaw init --connector openclaw first.",
                "OpenClaw's own gateway running (openclaw gateway run). If nothing answers at the address in openclaw.json, setup stops with \"No changes made.\"",
                "No other connector configured. OpenClaw runs alone in this release, so setup refuses while hook connectors are configured. " +
                "The --replace switch removes them, or remove them first with Remove a connector… in the Setup panel.",
                "Optional check to run yourself in a terminal: " + PreflightCommand +
                ". It prints \"openclaw preflight: ok\", or the reason setup would stop.",
            },
            WillDo = "Registers the DefenseClaw plugin with OpenClaw and sets openclaw as the guarded connector. OpenClaw's own gateway must restart to load the plugin. Setup's --restart, on by default, restarts it along with DefenseClaw's gateway. " +
                     "Until it restarts, OpenClaw is not guarded. The last page shows the exact command first.",
        };

        var intro = new WizardStep
        {
            Id = StepId,
            Title = "Before you start",
            Subtitle = "What has to be in place for OpenClaw setup to change anything.",
            Guide = new WizardGuide
            {
                Intro = "OpenClaw runs on native Windows from DefenseClaw 1.0.10. Setup checks these before it changes anything.",
                Cards = new[] { card },
            },
        };

        return new[] { intro }.Concat(steps).ToArray();
    }
}
