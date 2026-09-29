namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// How one secret-taking setup flag is satisfied <b>without this app ever holding the value</b>.
/// <para>
/// <b>Why the app cannot deliver a secret itself (verified, DefenseClaw 0.8.10 on Windows).</b>
/// Five setup flags take the secret <i>itself</i> — <c>setup gateway --token</c>,
/// <c>setup llm --api-key</c>, <c>setup splunk --access-token / --hec-token</c> and
/// <c>setup observability add --token</c>. Every one of them is argv-only: the CLI reads none of
/// them from stdin. The one verb built for storing a secret, <c>keys set NAME</c>, prompts through
/// <c>click.prompt(hide_input=True)</c>, which on Windows is <c>getpass.win_getpass</c> →
/// <c>msvcrt.getwch()</c>: it reads the <i>console input buffer</i>, never a redirected pipe (a probe
/// that launched the same prompt exactly as <see cref="Core.Cli.CliRunner"/> does — hidden console,
/// redirected stdin, value piped in — was still waiting for input after 8 s). So a value piped to any
/// of these commands was silently dropped, and <c>keys set --value</c> would put it on the command
/// line, which this app never does.
/// </para>
/// <para>
/// The route that does work is the one DefenseClaw itself is built around: the secret lives in
/// <c>~/.defenseclaw/.env</c> under a variable NAME, config.yaml stores only the name, and the CLI loads
/// <c>.env</c> into its environment at start-up (<c>config._load_dotenv_into_os</c>). The operator
/// stores the value once in a real console (<c>defenseclaw keys set NAME</c>), and the wizard's
/// command then refers to it by name (<c>llm --api-key-env NAME</c>) or lets the CLI's own
/// environment fallback pick it up (<c>splunk</c>: <c>SPLUNK_ACCESS_TOKEN</c> /
/// <c>DEFENSECLAW_SPLUNK_HEC_TOKEN</c>; <c>observability add</c>: the preset's <c>token_env</c>).
/// </para>
/// </summary>
public sealed class SecretRoute
{
    /// <summary>What the secret is, in words: "Splunk Observability Cloud access token".</summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// The variable NAME the CLI will read the secret from, given the current answers. Null when the
    /// name is not knowable (an unknown destination type, or a flag this app has no route for).
    /// </summary>
    public required Func<WizardValues, string?> EnvName { get; init; }

    /// <summary>What happens if the variable is still unset when the command runs.</summary>
    public required string IfMissing { get; init; }
}

/// <summary>The verified table of secret flags and where each one is read from.</summary>
public static class SecretRoutes
{
    /// <summary>
    /// <c>setup observability add &lt;preset&gt;</c> → the preset's <c>token_env</c>, from
    /// <c>defenseclaw/observability/presets.py</c> in the installed 0.8.10. Only used to show which
    /// variable to store and whether it is set; the CLI makes its own decision at run time.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ObservabilityTokenEnv =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["splunk-o11y"] = "SPLUNK_ACCESS_TOKEN",
            ["splunk-hec"] = "DEFENSECLAW_SPLUNK_HEC_TOKEN",
            ["splunk-enterprise"] = "DEFENSECLAW_SPLUNK_HEC_TOKEN",
            ["datadog"] = "DD_API_KEY",
            ["honeycomb"] = "HONEYCOMB_API_KEY",
            ["newrelic"] = "NEW_RELIC_LICENSE_KEY",
            ["grafana-cloud"] = "GRAFANA_OTLP_TOKEN",
            ["galileo"] = "GALILEO_API_KEY",
        };

    /// <summary>
    /// Attaches a <see cref="SecretRoute"/> to every <see cref="WizardFieldKind.Secret"/> field of a
    /// freshly built step list. A secret field with no known route still gets one (with no variable
    /// name), so it is always rendered as "store this in a terminal" and never as an input box.
    /// </summary>
    public static IReadOnlyList<WizardStep> Annotate(string target, IReadOnlyList<WizardStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var everyField = steps.SelectMany(s => s.Fields).ToArray();
        if (!everyField.Any(f => f.Kind == WizardFieldKind.Secret))
        {
            return steps;
        }

        var result = new List<WizardStep>(steps.Count);
        foreach (var step in steps)
        {
            if (!step.Fields.Any(f => f.Kind == WizardFieldKind.Secret))
            {
                result.Add(step);
                continue;
            }

            var fields = step.Fields
                .Select(f => f.Kind == WizardFieldKind.Secret ? f.WithCredential(RouteFor(target, f, everyField)) : f)
                .ToArray();

            result.Add(new WizardStep
            {
                Id = step.Id,
                Title = step.Title,
                Subtitle = step.Subtitle,
                Fields = fields,
                VisibleWhenFieldId = step.VisibleWhenFieldId,
                VisibleWhenValues = step.VisibleWhenValues,
            });
        }

        return result;
    }

    private static SecretRoute RouteFor(string target, WizardField field, IReadOnlyList<WizardField> everyField)
    {
        var flag = field.Flag ?? string.Empty;

        switch (target, flag)
        {
            case ("llm", "--api-key"):
            {
                var nameFieldId = everyField.FirstOrDefault(f => f.Flag == "--api-key-env")?.Id;
                return new SecretRoute
                {
                    Purpose = "LLM provider API key",
                    EnvName = values =>
                    {
                        var typed = nameFieldId is null ? string.Empty : values[nameFieldId].Trim();
                        return typed.Length > 0 ? typed : "DEFENSECLAW_LLM_KEY";
                    },
                    IfMissing = "The llm: block will point at this variable, and every component that calls the model " +
                                "(guardrail judge, scanners) fails to authenticate until the key is stored.",
                };
            }

            case ("gateway", "--token"):
                return new SecretRoute
                {
                    Purpose = "OpenClaw gateway token",
                    EnvName = _ => "OPENCLAW_GATEWAY_TOKEN",
                    IfMissing = "The sidecar cannot authenticate to a remote gateway until the token is stored.",
                };

            case ("splunk", "--access-token"):
                return new SecretRoute
                {
                    Purpose = "Splunk Observability Cloud access token",
                    EnvName = _ => "SPLUNK_ACCESS_TOKEN",
                    IfMissing = "The CLI stops with \"--access-token required (or set SPLUNK_ACCESS_TOKEN env var)\" and changes nothing.",
                };

            case ("splunk", "--hec-token"):
                return new SecretRoute
                {
                    Purpose = "Splunk Enterprise HEC token",
                    EnvName = _ => "DEFENSECLAW_SPLUNK_HEC_TOKEN",
                    IfMissing = "The CLI stops with \"--hec-token required (or set DEFENSECLAW_SPLUNK_HEC_TOKEN env var)\" and changes nothing.",
                };

            case ("observability", "--token"):
            {
                var presetFieldId = everyField
                    .FirstOrDefault(f => f.IsPositional && f.Id.EndsWith("-preset", StringComparison.Ordinal) &&
                                         f.VisibleWhenValues.SequenceEqual(field.VisibleWhenValues, StringComparer.Ordinal))
                    ?.Id;

                return new SecretRoute
                {
                    Purpose = "telemetry destination token",
                    EnvName = values =>
                    {
                        var preset = presetFieldId is null ? string.Empty : values[presetFieldId].Trim();
                        return ObservabilityTokenEnv.TryGetValue(preset, out var name) ? name : null;
                    },
                    IfMissing = "The destination is still created, but exporting to it fails to authenticate until the token is stored " +
                                "(the CLI prints a \"not set\" warning).",
                };
            }

            default:
                return new SecretRoute
                {
                    Purpose = field.Label,
                    EnvName = _ => null,
                    IfMissing = "The CLI expects this secret on its command line, which this app never does.",
                };
        }
    }
}
