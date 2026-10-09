namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The one rule about TLS the LLM wizards share: a CA bundle and "skip verification" are alternatives. The CLI refuses the pair
/// (<c>setup llm</c>: "--insecure-skip-verify and --tls-ca-cert-file are mutually exclusive"; <c>setup provider add</c> says the same of
/// <c>--ca-cert-file</c>; the judge's twin is the same pair with <c>--judge-</c> in front), so the wizard says so before the review, and only when
/// both fields really are part of the command - a CA path left in a TLS page that the chosen provider no longer shows is not sent.
/// </summary>
internal static class WizardTlsRule
{
    /// <summary>(the CA bundle's flag, the skip-verification flag) of each command that has both.</summary>
    private static readonly (string Bundle, string SkipVerify)[] Pairs =
    {
        ("--tls-ca-cert-file", "--insecure-skip-verify"),
        ("--judge-tls-ca-cert-file", "--judge-insecure-skip-verify"),
        ("--ca-cert-file", "--insecure-skip-verify"),
    };

    /// <summary>The sentence to refuse the answers with, or null when they do not name both.</summary>
    public static string? Check(WizardDefinition definition, WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(values);

        List<WizardField>? active = null;
        foreach (var (bundleFlag, skipFlag) in Pairs)
        {
            if (!definition.AllFields.Any(f => f.Flag == bundleFlag) || !definition.AllFields.Any(f => f.Flag == skipFlag))
            {
                continue;
            }

            active ??= definition.VisibleFields(values).ToList();
            var bundle = active.FirstOrDefault(f => f.Flag == bundleFlag);
            var skip = active.FirstOrDefault(f => f.Flag == skipFlag);

            if (bundle is not null && skip is not null &&
                values[bundle.Id].Trim().Length > 0 &&
                string.Equals(values[skip.Id].Trim(), ToggleValues.On, StringComparison.OrdinalIgnoreCase))
            {
                return $"Use either a CA bundle ({bundleFlag}) or skip verification ({skipFlag}), not both: the CLI refuses the pair. " +
                       "A bundle keeps the endpoint verified; skipping is for a lab.";
            }
        }

        return null;
    }
}
