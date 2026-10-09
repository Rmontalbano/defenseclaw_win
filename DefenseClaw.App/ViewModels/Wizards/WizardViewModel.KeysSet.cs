using System.Globalization;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels.Wizards;

/// <summary>
/// The wizard's second in-app secret route (CUST-328): a key stored with <c>defenseclaw keys set NAME</c>, typed in the app, as the first step of one reviewed plan.
/// <para>
/// <b>Which secrets.</b> The ones whose CLI reads them from nowhere but a flag (<c>setup llm --api-key</c>, <c>setup gateway --token</c>): no environment variable
/// carries them (<see cref="SecretRoute"/>), so the only way to store one without putting it on a command line is <c>keys set</c>, whose prompt reads a console.
/// The card used to hand the operator to a console window. Where the app can type the value itself it now offers a masked box (<see cref="WizardFieldViewModel.OffersKeysSetEntry"/>),
/// and the console button stays beside it as the way out.
/// </para>
/// <para>
/// <b>One plan.</b> With a value typed, the review lists the steps in order - <c>keys set NAME</c>, then the wizard's own command - and Execute runs them so:
/// the second only if the first stored the value (the shared multi-step rule, <see cref="PlanReport"/>). The wizard's command carries the variable's <i>name</i> (for
/// <c>setup llm</c>, <c>--api-key-env NAME</c>) and nothing else about the key. The value goes from the box to the pseudo-console's input and is gone: the box is
/// emptied when the first step starts, and what the step prints reaches the console and Activity already scrubbed of it (<see cref="SecretPtyRunner"/>).
/// A preview stores nothing.
/// </para>
/// <para>
/// <b>The console is the automatic fallback.</b> When the pseudo-console route fails with nothing typed (it could not start, or the CLI never showed its prompt) the wizard
/// remembers it, offers only the console for the rest of its life, opens the console window for the name, and does not run the wizard's command (the key is not stored yet).
/// </para>
/// </summary>
public sealed partial class WizardViewModel
{
    private bool _keysSetRouteFailed;

    private Func<bool>? _ptyAvailable;

    /// <summary>Whether a pseudo-console can be used; null asks Windows. A test replaces it so the console route can be exercised on any machine.</summary>
    internal Func<bool>? PtyAvailable
    {
        get => _ptyAvailable;
        set
        {
            _ptyAvailable = value;
            RefreshKeysSetRoute();
        }
    }

    /// <summary>
    /// How a value is stored: <c>(variable name, value, cancel)</c> to the result of the run. A test replaces it so no pseudo-console starts and no
    /// <c>defenseclaw</c> is run; the default is <see cref="SecretPtyRunner.SetKeyAsync"/> over the app's runner.
    /// </summary>
    internal Func<string, SecretValue, CancellationToken, Task<SecretPtyResult>>? RunSet { get; set; }

    /// <summary>The console window the fallback opens (<see cref="WizardCredentials.OpenKeysSetTerminal"/>); a test replaces it so no window opens.</summary>
    internal Func<string, string?>? OpenConsole { get; set; }

    /// <summary>The app can type a value at <c>keys set</c>'s prompt: Windows has a pseudo-console, the runtime is not a container, the route has not failed, and the installation may be changed.</summary>
    internal bool KeysSetAvailable =>
        !_keysSetRouteFailed &&
        (PtyAvailable?.Invoke() ?? SecretPtyRunner.IsSupportedHere) &&
        _services.Paths.Runtime.Kind != RuntimeKind.Container &&
        _services.Installation.BlockedReason is null;

    /// <summary>Tells every secret field whether its card offers the box, from the facts above. Cheap; called when any of them may have changed.</summary>
    private void RefreshKeysSetRoute()
    {
        var available = KeysSetAvailable;
        foreach (var field in _fields)
        {
            if (field.IsSecret)
            {
                field.SetKeysSetAvailable(available, "The value you entered was cleared because this app can no longer type it at the keys set prompt. Store it in a console, or enter it again.");
            }
        }
    }

    /// <summary>
    /// The visible secrets with a value typed for the <c>keys set</c> route, one per variable: the first steps of the plan. The one place that decides, so the
    /// review and the run cannot disagree.
    /// </summary>
    private IEnumerable<WizardFieldViewModel> KeysSetFields() =>
        _fields
            .Where(f => f.IsVisible && f.OffersKeysSetEntry && f.HasEntry)
            .GroupBy(f => f.CredentialEnvName, StringComparer.Ordinal)
            .Select(g => g.First());

    /// <summary>A value is typed for either route: a run started now would use at least one.</summary>
    private bool HasTypedSecrets => HasSuppliedSecrets || KeysSetFields().Any();

    /// <summary>The review's steps: the <c>keys set</c> steps for the values typed, then the command itself.</summary>
    private CommandReviewStep[] PlanSteps(IReadOnlyList<string> argv)
    {
        var floor = ReviewFloor(argv);
        var stores = KeysSetFields().ToArray();
        if (stores.Length == 0)
        {
            return new[] { new CommandReviewStep(argv, floor: floor) };
        }

        var steps = new List<CommandReviewStep>();
        for (var i = 0; i < stores.Length; i++)
        {
            var name = stores[i].CredentialEnvName;
            steps.Add(new CommandReviewStep(
                SecretPtyRunner.SetKeyArgv(name),
                $"Stores the value you typed for {name} (hidden) in ~/.defenseclaw/.env. This app types it at the command's own hidden prompt; it is not on the command line.",
                CommandTier.StateChanging,
                number: i + 1));
        }

        steps.Add(new CommandReviewStep(
            argv,
            "The command itself. It reads the key by the variable's name.",
            floor,
            number: stores.Length + 1));
        return steps.ToArray();
    }

    /// <summary>
    /// Stores the typed values, in order, before the command runs. True when every one was stored and the command may run; otherwise the badge and the
    /// message already say why and the command must not run (the key it reads is not there).
    /// </summary>
    private async Task<bool> StoreKeysAsync(IReadOnlyList<WizardFieldViewModel> stores, CancellationToken token)
    {
        var total = stores.Count + 1;
        for (var i = 0; i < stores.Count; i++)
        {
            var field = stores[i];
            var name = field.CredentialEnvName;
            var argv = SecretPtyRunner.SetKeyArgv(name);
            var number = (i + 1).ToString(CultureInfo.CurrentCulture);

            // The value leaves the box for this one call, and the box is empty from here on.
            var secret = field.MaterializeSecret();
            field.ClearEntry();
            if (secret is null)
            {
                Fail($"Step {number} of {total}: the value for {name} was cleared before it could be stored. Enter it again. The wizard's command was not run.");
                return false;
            }

            ResultMessage = $"Step {number} of {total}: storing {name}. This app is typing the value at the CLI's hidden prompt. Cancel stops it.";

            _pendingArgv = argv;
            _invocation = null;
            _outputCursor = 0;

            var result = RunSet is { } run
                ? await run(name, secret, token).ConfigureAwait(true)
                : await new SecretPtyRunner(_services.Cli).SetKeyAsync(name, secret, token).ConfigureAwait(true);

            _invocation = result.Invocation;
            PullOutput();

            if (result.Succeeded)
            {
                continue;
            }

            if (result.RouteFailed)
            {
                // The pseudo-console does not work here and nothing was typed: the console window does what was asked, and the command waits for the key.
                _keysSetRouteFailed = true;
                RefreshKeysSetRoute();
                var failure = (OpenConsole ?? Credentials.OpenKeysSetTerminal)(name);
                ExitBadgeText = "not run";
                ExitBadgeKey = "Warn";
                ResultMessage = $"The app could not type the value itself ({result.Message.TrimEnd('.')}). " +
                                (failure is null
                                    ? $"A console window opened for {name}: type the value there, press Re-check, then Execute again. The wizard's command was not run."
                                    : failure + " The wizard's command was not run.");
                return false;
            }

            var cancelled = result.Outcome == SecretPtyOutcome.Cancelled;
            ExitBadgeText = cancelled ? "cancelled" : result.Invocation?.ExitCode is { } code ? "exit " + code.ToString(CultureInfo.CurrentCulture) : "failed";
            ExitBadgeKey = cancelled ? "Warn" : "Bad";
            ResultMessage = $"Step {number} of {total} did not store {name}: {result.Message} The wizard's command was not run, so nothing was changed by it. " +
                            "The command and its output are in the Activity panel.";
            return false;
        }

        // The next output belongs to the command itself.
        _invocation = null;
        _outputCursor = 0;
        return true;
    }
}
