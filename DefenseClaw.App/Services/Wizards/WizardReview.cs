namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The wizard-specific part of the review page's story: the restart paragraph, which can point at the
/// wizard's own "Restart" toggle. Everything else (the tier badge, the exact argv, whether a command restarts the
/// gateway) lives in <see cref="CommandReview"/>, the model every confirmation surface shares.
/// </summary>
public static class WizardReview
{
    /// <summary>The sentence the brief requires on every command that restarts the live gateway.</summary>
    public const string RestartSentence = CommandReview.RestartSentence;

    /// <summary>True when running <paramref name="argv"/> restarts the live gateway; see <see cref="CommandReview.RestartsGatewayFor"/>.</summary>
    public static bool RestartsGateway(IReadOnlyList<string> argv) => CommandReview.RestartsGatewayFor(argv);

    /// <summary>The full restart paragraph for a wizard whose definition may offer a restart toggle.</summary>
    public static string RestartWarning(WizardDefinition definition, WizardValues values, IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(argv);

        if (!RestartsGateway(argv))
        {
            return string.Empty;
        }

        var offersToggle = definition.VisibleFields(values).Any(f => f.Kind == WizardFieldKind.Toggle && f.NegativeFlag == "--no-restart");
        return CommandReview.RestartNotice +
               (offersToggle ? " Choose \"Disable (--no-restart)\" for Restart on the last page to skip the restart." : string.Empty);
    }

    /// <summary>
    /// The bar shown when a value typed into an ordinary field — a URL that is itself the credential, such as a Slack
    /// webhook, or one that embeds <c>user:password@</c> — is about to go on the command line; null when none does.
    /// Fields that <i>are</i> secrets (<see cref="WizardFieldKind.Secret"/>) never reach argv and are not judged here.
    /// The wizard's commands have no option that reads such a value from the environment, hence the advice.
    /// </summary>
    public static CommandReviewWarning? SecretValueWarning(IReadOnlyList<string> argv) =>
        SecretFieldWarnings.ForArgv(
            argv,
            "This command has no option to read it from an environment variable, so it has to go on the command line. If that " +
            "matters, enter a placeholder here and set the real value in config.yaml afterwards (Config editor), where it " +
            "stays off the command line.");
}
