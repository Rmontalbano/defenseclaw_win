namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// How one secret-taking setup flag is satisfied. There are two routes, and which one a flag gets is a
/// fact about the installed CLI, established from its source rather than assumed.
/// <para>
/// <b>Never on argv.</b> Five setup flags take the secret <i>itself</i> — <c>setup gateway --token</c>,
/// <c>setup llm --api-key</c>, <c>setup splunk --access-token / --hec-token</c> and
/// <c>setup observability add --token</c> — and this app never puts a value on a command line (argv is visible
/// to every process on the machine, and <see cref="Core.Cli.CliRunner"/> refuses it). None of them is read from
/// stdin either. The one verb built for storing a secret, <c>keys set NAME</c>, prompts through
/// <c>click.prompt(hide_input=True)</c>, which on Windows is <c>getpass.win_getpass</c> →
/// <c>msvcrt.getwch()</c>: it reads the <i>console input buffer</i>, never a redirected pipe (a probe that
/// launched the same prompt exactly as the runner does — hidden console, redirected stdin, value piped in — was
/// still waiting for input after 8 s).
/// </para>
/// <para>
/// <b>Route 1 — a real console (every flag).</b> The secret lives in <c>~/.defenseclaw/.env</c> under a variable
/// NAME, config.yaml stores only the name, and the CLI loads <c>.env</c> into its environment at start-up
/// (<c>config._load_dotenv_into_os</c>). The operator stores the value once in a real console
/// (<c>defenseclaw keys set NAME</c>) and the wizard's command reads it by name.
/// </para>
/// <para>
/// <b>Route 2 — typed in the app, delivered in the child's environment (only where the CLI reads one).</b>
/// Three of the flags have an environment-variable fallback that <i>replaces the prompt and the flag</i> and is
/// honoured with no console attached: <c>setup splunk --access-token</c> (<c>SPLUNK_ACCESS_TOKEN</c>),
/// <c>setup splunk --hec-token</c> (<c>DEFENSECLAW_SPLUNK_HEC_TOKEN</c>) and — through Click's own
/// <c>envvar=</c> on the option — <c>setup observability add --token</c>
/// (<c>DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN</c>). For those the wizard shows a password box and hands the
/// value to <see cref="Core.Cli.CliRunOptions.EnvironmentOverlay"/> for that one run: it is in the child's
/// environment block, not on argv, and the CLI then persists it to <c>.env</c> itself. The other two
/// (<c>gateway --token</c>, <c>llm --api-key</c>) have no environment fallback — their value is read from the
/// flag or not at all — so they keep Route 1 only.
/// </para>
/// <para>
/// A sixth flag, <c>setup splunk dashboards plan | apply | destroy --o11y-api-token</c> (the Splunk Observability Cloud API token, not the
/// ingest token), is bound to <c>SFX_AUTH_TOKEN</c> by Click's <c>envvar=</c> and its own help tells the operator to prefer the variable "so
/// the secret never appears in shell history or process listings", so it has Route 2 too — with one difference the card says: the CLI
/// does not store the value anywhere (it sets <c>TF_VAR_signalfx_auth_token</c> for its Terraform children), so a typed token is used for
/// that one run and then gone.
/// </para>
/// <para>
/// A fourth secret has a route without ever having been a flag: <c>setup galileo</c> reads <c>GALILEO_API_KEY</c>
/// from the environment and offers no flag for it, so <see cref="WizardSyntheticSecrets"/> adds a field that stands
/// for the variable. Unlike the others the CLI keeps such a key only when <c>--persist-api-key</c> is on — see
/// <see cref="PersistFlag"/> and <see cref="SecretRoutes"/>.
/// </para>
/// </summary>
public sealed class SecretRoute
{
    /// <summary>What the secret is, in words: "Splunk Observability Cloud access token".</summary>
    public required string Purpose { get; init; }

    /// <summary>
    /// The variable NAME the command ends up reading the secret from — where it is stored — given the current
    /// answers. Null when the name is not knowable (an unknown destination type, or a flag this app has no route
    /// for). This is what the credential card checks for a stored value.
    /// </summary>
    public required Func<WizardValues, string?> EnvName { get; init; }

    /// <summary>What happens if the variable is still unset when the command runs.</summary>
    public required string IfMissing { get; init; }

    /// <summary>
    /// The variable the CLI reads <i>as its replacement for the flag</i>, so that a value supplied in the child's
    /// environment is used exactly as if it had been typed after the flag. Null (the default) means the CLI has no
    /// such fallback and the route is terminal-only. May differ from <see cref="EnvName"/>: an observability
    /// destination is read from <c>DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN</c> but stored as its preset's own name.
    /// </summary>
    public Func<WizardValues, string?>? InAppEnvName { get; init; }

    /// <summary>
    /// The flag that makes the CLI <i>keep</i> a value it was handed in the environment. Null (the default) means
    /// the CLI stores an environment-supplied value on its own (splunk, observability). <c>setup galileo</c> does
    /// not: without <c>--persist-api-key</c> it uses <c>GALILEO_API_KEY</c> for that run and writes nothing
    /// (<c>token_value=resolved_key if api_key or persist_api_key else None</c>), so the value is gone when the
    /// child exits. The wizard turns the flag on when a value is typed and says what it decided.
    /// </summary>
    public string? PersistFlag { get; init; }

    /// <summary>
    /// Replaces the card's default sentence about what the CLI does with an environment-supplied value ("the CLI
    /// stores it in ~/.defenseclaw/.env"), for a route whose CLI does not always store it. Null keeps the default.
    /// </summary>
    public string? InAppStorage { get; init; }

    /// <summary>
    /// A sentence for the result line of a <c>--dry-run</c> preview, which is never given the typed value: what
    /// the preview will therefore do. Null when the preview is unaffected.
    /// </summary>
    public string? PreviewNote { get; init; }

    /// <summary>
    /// The valid variable name the in-app route would use for the current answers, or null when this flag has
    /// no such route (or, for an observability preset, no token at all). A name that is not a legal environment
    /// variable name is treated as no route rather than passed on.
    /// </summary>
    public string? InAppVariable(WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var name = InAppEnvName?.Invoke(values)?.Trim();
        return Core.Cli.CliRunOptions.IsValidEnvironmentName(name) ? name : null;
    }
}

/// <summary>The verified table of secret flags and where each one is read from.</summary>
public static class SecretRoutes
{
    /// <summary>
    /// The variable <c>setup observability add --token</c> is bound to by Click's <c>envvar=</c>
    /// (<c>cmd_setup_observability.py</c>, 0.8.10). The CLI advertises it in the option's help
    /// (<c>[env var: …]</c>), which is what <see cref="RouteFor"/> checks before offering the in-app route.
    /// </summary>
    public const string ObservabilityTokenEnvVar = "DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN";

    /// <summary>
    /// The variable <c>setup galileo</c> reads its API key from (<c>_KEY_ENV</c>, <c>cmd_setup_galileo.py:42</c>):
    /// the process environment first, then <c>~/.defenseclaw/.env</c> (<c>_resolve_secret</c>, lines 412-413).
    /// </summary>
    public const string GalileoKeyEnvVar = "GALILEO_API_KEY";

    /// <summary>
    /// <c>setup observability add &lt;preset&gt;</c> → the preset's <c>token_env</c>, from
    /// <c>defenseclaw/observability/presets.py</c> in the installed 0.8.10. Where the token is stored and
    /// whether it is set; the CLI makes its own decision at run time.
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
                Guide = step.Guide,
            });
        }

        return result;
    }

    /// <summary>
    /// <c>setup galileo</c>: typed in the app, like the Splunk tokens — <c>_resolve_secret</c> reads
    /// <c>GALILEO_API_KEY</c> from the environment before <c>.env</c> (<c>cmd_setup_galileo.py:412-413</c>) — but
    /// with two differences that come from the source and are said to the operator on the card:
    /// <list type="bullet">
    ///   <item>An environment-supplied key is <b>not stored</b> unless <c>--persist-api-key</c> is on
    ///     (<c>cmd_setup_galileo.py:137</c>: <c>token_value=resolved_key if api_key or persist_api_key else None</c>).
    ///     The key would otherwise live only in that one child's environment.</item>
    ///   <item>The key is needed in <b>every</b> mode, a <c>--dry-run</c> and a <c>--disabled</c> setup included:
    ///     the "not set" refusal (<c>:122-124</c>) comes before the writer is called with <c>dry_run</c>.</item>
    /// </list>
    /// </summary>
    private static SecretRoute GalileoRoute() => new()
    {
        Purpose = "Galileo API key for trace export",
        EnvName = _ => GalileoKeyEnvVar,
        InAppEnvName = _ => GalileoKeyEnvVar,
        PersistFlag = WizardSyntheticSecrets.PersistFlag,
        InAppStorage = "the CLI writes it to ~/.defenseclaw/.env only when \"" + WizardSyntheticSecrets.PersistLabel +
                       "\" is on; otherwise it uses the key for this run and keeps nothing.",
        PreviewNote = "The CLI looks for the key even in a preview, and a preview is not given the value typed here, " +
                      "so a preview passes only if the key is already stored.",
        IfMissing = "The CLI stops with \"GALILEO_API_KEY is not set; export it or omit --non-interactive for a hidden prompt\" " +
                    "and changes nothing. It checks before it previews, so a --dry-run preview stops the same way.",
    };

    private static SecretRoute RouteFor(string target, WizardField field, IReadOnlyList<WizardField> everyField)
    {
        // The one secret with no flag at all: added by WizardSyntheticSecrets, so it is recognised by its id.
        if (target == "galileo" && field.Id == WizardSyntheticSecrets.GalileoKeyFieldId)
        {
            return GalileoRoute();
        }

        var flag = field.Flag ?? string.Empty;

        switch (target, flag)
        {
            // Terminal only: `--api-key` is read from the flag and from nowhere else (cmd_setup.py:
            // `if api_key: _save_secret_to_dotenv(...)`; the option has no envvar=), so a value in the child's
            // environment would be ignored.
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

            // Terminal only: `if token is not None: _save_secret_to_dotenv(...)` — the flag is the only source. An
            // OPENCLAW_GATEWAY_TOKEN in the environment is consulted (`gw.resolved_token()`) but never written to .env.
            case ("gateway", "--token"):
                return new SecretRoute
                {
                    Purpose = "OpenClaw gateway token",
                    EnvName = _ => "OPENCLAW_GATEWAY_TOKEN",
                    IfMissing = "The sidecar cannot authenticate to a remote gateway until the token is stored.",
                };

            // In-app: `token = access_token or os.environ.get("SPLUNK_ACCESS_TOKEN", "")` (cmd_setup.py, _setup_o11y);
            // a non-empty result skips the hide_input prompt, and the value is then persisted to .env.
            case ("splunk", "--access-token"):
                return new SecretRoute
                {
                    Purpose = "Splunk Observability Cloud access token",
                    EnvName = _ => "SPLUNK_ACCESS_TOKEN",
                    InAppEnvName = _ => "SPLUNK_ACCESS_TOKEN",
                    IfMissing = "The CLI stops with \"--access-token required (or set SPLUNK_ACCESS_TOKEN env var)\" and changes nothing.",
                };

            // In-app: `token = hec_token or os.environ.get("DEFENSECLAW_SPLUNK_HEC_TOKEN", "")` (_setup_enterprise).
            case ("splunk", "--hec-token"):
                return new SecretRoute
                {
                    Purpose = "Splunk Enterprise HEC token",
                    EnvName = _ => "DEFENSECLAW_SPLUNK_HEC_TOKEN",
                    InAppEnvName = _ => "DEFENSECLAW_SPLUNK_HEC_TOKEN",
                    IfMissing = "The CLI stops with \"--hec-token required (or set DEFENSECLAW_SPLUNK_HEC_TOKEN env var)\" and changes nothing.",
                };

            // In-app, but only while the installed CLI still advertises the binding in its own help: Click's
            // `envvar="DEFENSECLAW_SETUP_OBSERVABILITY_TOKEN"` prints "[env var: …]" on the option. A CLI that
            // dropped it would ignore the environment, so the route quietly falls back to the terminal instead.
            case ("observability", "--token"):
            {
                var presetFieldId = everyField
                    .FirstOrDefault(f => f.IsPositional && f.Id.EndsWith("-preset", StringComparison.Ordinal) &&
                                         f.VisibleWhenValues.SequenceEqual(field.VisibleWhenValues, StringComparer.Ordinal))
                    ?.Id;

                string? StoredName(WizardValues values)
                {
                    var preset = presetFieldId is null ? string.Empty : values[presetFieldId].Trim();
                    return ObservabilityTokenEnv.TryGetValue(preset, out var name) ? name : null;
                }

                var advertised = field.Help.Contains(ObservabilityTokenEnvVar, StringComparison.Ordinal);

                return new SecretRoute
                {
                    Purpose = "telemetry destination token",
                    EnvName = StoredName,

                    // Also needs a preset that has a token at all: for the generic OTLP/HTTP presets the CLI
                    // accepts --token and discards it, and there is no stored name to check for either.
                    InAppEnvName = advertised
                        ? values => StoredName(values) is null ? null : ObservabilityTokenEnvVar
                        : null,
                    IfMissing = "The destination is still created, but exporting to it fails to authenticate until the token is stored " +
                                "(the CLI prints a \"not set\" warning).",
                };
            }

            // In-app, but only while the installed CLI's own help still names the variable (Click does not print an option's envvar
            // unless asked to, so the help text is where the 0.8.10 CLI says it: "Prefer the SFX_AUTH_TOKEN environment variable"). A CLI that
            // stopped saying so may have stopped reading it, and the route quietly falls back to the terminal instead. Never stored by the
            // CLI: `_prepare_run` copies it into TF_VAR_signalfx_auth_token for its Terraform children and nowhere else.
            case (SplunkDashboards.Target, SplunkDashboards.TokenFlag):
            {
                var advertised = field.Help.Contains(SplunkDashboards.TokenVariable, StringComparison.Ordinal);

                return new SecretRoute
                {
                    Purpose = "Splunk Observability Cloud API token (the user API access token, not the ingest token)",
                    EnvName = _ => SplunkDashboards.TokenVariable,
                    InAppEnvName = advertised ? _ => SplunkDashboards.TokenVariable : null,
                    InAppStorage = "the CLI does not store it: it hands it to Terraform for that run and nothing keeps it afterwards.",
                    IfMissing = "The CLI stops with \"Splunk O11y token not found\" before it copies or runs anything, and changes nothing.",
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
