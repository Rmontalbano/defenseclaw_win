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
}
