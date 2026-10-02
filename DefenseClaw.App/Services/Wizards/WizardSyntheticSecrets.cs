namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// Secrets a setup command reads from its environment but has <b>no flag</b> for, which the parsed help therefore
/// never yields a field for. Today that is one: <c>setup galileo</c>'s API key.
/// <para>
/// <c>galileo</c> has no <c>--api-key</c> / <c>--token</c>; <c>_resolve_secret</c> reads <c>GALILEO_API_KEY</c> from
/// the environment, then from <c>~/.defenseclaw/.env</c> (<c>cmd_setup_galileo.py:412-413</c>), and with
/// <c>--non-interactive</c> — which every wizard turns on — there is no hidden prompt either (<c>:98-113</c>).
/// Without a field for it the wizard would run, fail with "GALILEO_API_KEY is not set" (<c>:123-124</c>), and never say
/// why. So a <see cref="WizardFieldKind.Secret"/> field is added that stands for the variable, gated exactly like the
/// guided flags. It carries <b>no flag</b>, so it can never reach argv (<see cref="WizardDefinition.BuildArgv"/> emits
/// nothing for a secret), and <see cref="SecretRoutes"/> gives it the in-app route.
/// </para>
/// <para>
/// It is offered only while the installed CLI still advertises the binding: the <c>--persist-api-key</c> option must
/// be there and say <c>GALILEO_API_KEY</c> in its own help. A CLI that dropped or renamed it gets no card, exactly as
/// an observability token loses its password box when its <c>[env var: …]</c> marker disappears.
/// </para>
/// </summary>
public static class WizardSyntheticSecrets
{
    /// <summary>The id of the synthetic field; <see cref="SecretRoutes"/> recognises the route by it.</summary>
    public const string GalileoKeyFieldId = "galileo-api-key";

    /// <summary>The flag that makes <c>setup galileo</c> keep an environment-supplied key.</summary>
    public const string PersistFlag = "--persist-api-key";

    /// <summary>What the choice is called on the page (the flag's chip still shows <see cref="PersistFlag"/>).</summary>
    public const string PersistLabel = "Save the key to DefenseClaw's .env";

    private const string PersistHelp =
        "Copies the key into ~/.defenseclaw/.env (the CLI's --persist-api-key). Off is the CLI's default, and then a key " +
        "supplied from this app or from the environment is used for this run only: nothing keeps it, so trace export " +
        "cannot authenticate afterwards unless it is stored some other way. This app switches it on when you type a key " +
        "above; turn it off to use that key once. Leave it off when the key is already stored and you typed nothing.";

    private const string KeyHelp =
        "Galileo authenticates trace export with this key. The CLI reads it from the GALILEO_API_KEY variable — in this " +
        "app's environment, or in ~/.defenseclaw/.env — and never from a flag, so there is no flag for it here. If a key " +
        "is already stored you can leave this blank.";

    /// <summary>
    /// Adds the synthetic secrets <paramref name="target"/> needs to <paramref name="steps"/> (already built and
    /// Windows-filtered, not yet annotated by <see cref="SecretRoutes.Annotate"/>). Unchanged for every other target.
    /// </summary>
    public static IReadOnlyList<WizardStep> Add(string target, IReadOnlyList<WizardStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        return target == "galileo" ? AddGalileoKey(steps) : steps;
    }

    private static bool IsPersistSwitch(WizardField field) =>
        field.Kind == WizardFieldKind.Switch &&
        string.Equals(field.Flag, PersistFlag, StringComparison.Ordinal) &&
        field.Help.Contains(SecretRoutes.GalileoKeyEnvVar, StringComparison.Ordinal);

    private static IReadOnlyList<WizardStep> AddGalileoKey(IReadOnlyList<WizardStep> steps)
    {
        var holder = steps.FirstOrDefault(s => s.Fields.Any(IsPersistSwitch));
        if (holder is null)
        {
            return steps;
        }

        var persist = holder.Fields.First(IsPersistSwitch);

        var key = new WizardField
        {
            Id = GalileoKeyFieldId,
            Label = "Galileo API key",
            Kind = WizardFieldKind.Secret,
            Help = KeyHelp,
            ViaEnvironment = SecretRoutes.GalileoKeyEnvVar,
            VisibleWhenFieldId = persist.VisibleWhenFieldId,
            VisibleWhenValues = persist.VisibleWhenValues,
        };

        // The key and the choice about keeping it are one decision, so they share a page of their own: the
        // switch leaves the page it was generated onto, and the page is dropped if that leaves it empty.
        var apiKeyStep = new WizardStep
        {
            Id = "api-key",
            Title = "API key",
            Subtitle = "Galileo authenticates the trace export with an API key. It is never a command-line flag.",
            Fields = new[] { key, persist.WithWording(PersistLabel, PersistHelp) },
            VisibleWhenFieldId = holder.VisibleWhenFieldId,
            VisibleWhenValues = holder.VisibleWhenValues,
        };

        var remaining = holder.Fields.Where(f => !ReferenceEquals(f, persist)).ToArray();
        var result = new List<WizardStep>(steps.Count + 1);
        foreach (var step in steps)
        {
            if (!ReferenceEquals(step, holder))
            {
                result.Add(step);
                continue;
            }

            if (remaining.Length > 0)
            {
                result.Add(new WizardStep
                {
                    Id = step.Id,
                    Title = step.Title,
                    Subtitle = step.Subtitle,
                    Fields = remaining,
                    VisibleWhenFieldId = step.VisibleWhenFieldId,
                    VisibleWhenValues = step.VisibleWhenValues,
                    Guide = step.Guide,
                });
            }

            result.Add(apiKeyStep);
        }

        return result;
    }
}
