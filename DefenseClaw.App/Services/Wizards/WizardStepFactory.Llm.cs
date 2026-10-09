using DefenseClaw.App.ViewModels.ConfigEditor;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The curated layouts built around an LLM provider: <c>setup llm</c>, <c>setup provider add</c>, and the judge section of
/// <c>setup guardrail</c> (see <c>WizardStepFactory.Guardrail.cs</c>). All three share one idea, which is the TUI's: the provider picks which
/// cloud's settings are asked about. Bedrock, Vertex AI, Azure and the TLS overrides are pages of their own, gated on the provider with
/// <see cref="WizardGate"/>, so a Bedrock run is never shown a Vertex project and never sends a Vertex flag.
/// <para>
/// Every flag a layout names is looked up in the live <c>--help</c>: one the installed CLI does not have is not offered, and a group whose
/// provider flag is missing is not gated on a field that is not there. What a layout does not place falls to the trailing "More options" page.
/// </para>
/// </summary>
public static partial class WizardStepFactory
{
    private static readonly (string? FieldId, IReadOnlyList<string> Values) NoGate = (null, Array.Empty<string>());

    // ------------------------------------------------------------------ small helpers shared by the curated layouts

    /// <summary>The field for <paramref name="flag"/> as the CLI documents it, changed by <paramref name="tweak"/>; null when the installed CLI has no such flag.</summary>
    private static WizardField? FieldOf(ParsedHelp help, string flag, Func<WizardField, WizardField>? tweak = null, string? idPrefix = null)
    {
        if (help.Option(flag) is not { } option || WizardFieldBuilder.From(option, idPrefix: idPrefix) is not { } field)
        {
            return null;
        }

        return tweak is null ? field : tweak(field);
    }

    /// <summary>A page of the fields that exist (a null is a flag the CLI does not have), shown when <paramref name="gate"/> holds.</summary>
    private static WizardStep PageOf(
        string id,
        string title,
        string subtitle,
        (string? FieldId, IReadOnlyList<string> Values) gate,
        params WizardField?[] fields) => new()
    {
        Id = id,
        Title = title,
        Subtitle = subtitle,
        Fields = fields.Where(f => f is not null).Select(f => f!).ToArray(),
        VisibleWhenFieldId = gate.FieldId,
        VisibleWhenValues = gate.Values ?? Array.Empty<string>(),
    };

    private static (string? FieldId, IReadOnlyList<string> Values) GateOf(params WizardGate.Clause[] clauses) =>
        clauses.Length == 0 ? NoGate : WizardGate.All(clauses);

    /// <summary>The id the wizard gives the field for <paramref name="flag"/> (the flag without its dashes, behind <paramref name="idPrefix"/>).</summary>
    private static string IdOf(string flag, string? idPrefix = null) => (idPrefix ?? string.Empty) + WizardFieldBuilder.Identifier(flag);

    /// <summary>
    /// A text flag the CLI documents a list of values for becomes a choice of them: the TUI's providers first, in its order
    /// (<see cref="ConfigFieldCatalog.LlmProviders"/>), then any others the CLI lists.
    /// </summary>
    private static WizardField WithProviderChoices(WizardField field)
    {
        if (field.Kind != WizardFieldKind.Choice)
        {
            return field;
        }

        var cli = field.Choices.Where(c => c.Value.Length > 0).Select(c => c.Value).ToArray();
        if (cli.Length == 0)
        {
            return field;
        }

        var ordered = ConfigFieldCatalog.LlmProviders
            .Select(p => cli.FirstOrDefault(c => string.Equals(c, p, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c is not null)
            .Select(c => c!)
            .Concat(cli.Where(c => !ConfigFieldCatalog.LlmProviders.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .ToArray();

        return field.WithChoices(new[] { WizardChoice.Unchanged }.Concat(ordered.Select(WizardChoice.Of)).ToArray());
    }

    /// <summary>
    /// The judge's provider is free text in the CLI ("e.g. anthropic, bedrock, vertex_ai"); the wizard offers the TUI's providers and
    /// <c>custom</c> so the cloud pages that follow have a value to be gated on.
    /// </summary>
    private static WizardField AsProviderChoice(WizardField field) => field.Kind == WizardFieldKind.Choice
        ? WithProviderChoices(field)
        : field.AsChoice(
            new[] { WizardChoice.Unchanged }
                .Concat(ConfigFieldCatalog.LlmProviders.Concat(new[] { "custom" }).Select(WizardChoice.Of))
                .ToArray());

    // ------------------------------------------------------------------ the cloud groups

    /// <summary>
    /// How one family of cloud pages is built, so the same four pages serve <c>setup llm</c> (<c>--bedrock-region</c>), the judge
    /// (<c>--judge-bedrock-region</c>) and <c>provider add</c> (<c>--bedrock-region</c>, gated on the base provider type).
    /// </summary>
    private sealed class CloudGroups
    {
        /// <summary>Builds the field for a flag (null when the CLI has none).</summary>
        public required Func<string, WizardField?> Field { get; init; }

        /// <summary>The id the wizard gives the field of a flag.</summary>
        public required Func<string, string> IdOf { get; init; }

        /// <summary>"--" for the unified block, "--judge-" for the judge's.</summary>
        public required string FlagPrefix { get; init; }

        /// <summary>The id of the field whose value picks the cloud.</summary>
        public required string ProviderFieldId { get; init; }

        /// <summary>
        /// Whether the CLI has that field. Without it nothing could ever open a cloud page, and the flags on them would be unreachable, so
        /// none is built and they fall to the "More options" page instead.
        /// </summary>
        public required bool HasProviderField { get; init; }

        /// <summary>Conditions every page shares (the subcommand, the scope, the strategy), on top of the provider.</summary>
        public WizardGate.Clause[] Conditions { get; init; } = Array.Empty<WizardGate.Clause>();

        public required string BedrockPage { get; init; }

        public required string VertexPage { get; init; }

        public required string AzurePage { get; init; }

        public required string TlsPage { get; init; }

        /// <summary>The values of the provider field that mean Vertex AI (the judge's free-text provider also accepts <c>vertex</c>).</summary>
        public string[] VertexValues { get; init; } = { "vertex_ai" };

        /// <summary>The two TLS flags; <c>provider add</c> names them differently (<c>--ca-cert-file</c>).</summary>
        public (string CaCert, string Insecure) Tls { get; init; } = ("tls-ca-cert-file", "insecure-skip-verify");

        /// <summary>
        /// Whether the TLS page is for the clouds and custom endpoints (the TUI's rule: those are the providers behind a private CA) or for every
        /// provider: <c>provider add</c> registers an endpoint of any kind.
        /// </summary>
        public bool TlsForEveryProvider { get; init; }

        public string Title { get; init; } = string.Empty;

        public IReadOnlyList<WizardStep> Build()
        {
            if (!HasProviderField)
            {
                return Array.Empty<WizardStep>();
            }

            string Flag(string name) => FlagPrefix + name;

            WizardField? F(string name, Func<WizardField, WizardField>? tweak = null)
            {
                var field = Field(Flag(name));
                return field is null || tweak is null ? field : tweak(field);
            }

            WizardGate.Clause Provider(params string[] values) => WizardGate.When(ProviderFieldId, values);

            // The sign-in fields that belong to one auth mode are gated on it - when the CLI has the mode to gate on.
            var bedrockAuth = IdOf(Flag("bedrock-auth-mode"));
            var gatedOnAuth = Field(Flag("bedrock-auth-mode")) is not null;

            Func<WizardField, WizardField>? OnAuth(string mode) =>
                gatedOnAuth ? f => f.AndGate(WizardGate.When(bedrockAuth, mode)) : null;

            var steps = new List<WizardStep>
            {
                PageOf(
                    BedrockPage,
                    Title + "AWS Bedrock",
                    "Region and sign-in for Bedrock. Credentials are referenced by the NAME of an environment variable, never typed here.",
                    GateOf(Conditions.Append(Provider("bedrock")).ToArray()),
                    F("bedrock-region"),
                    F("bedrock-auth-mode"),
                    F("bedrock-access-key-env", OnAuth("iam_credentials")),
                    F("bedrock-secret-key-env", OnAuth("iam_credentials")),
                    F("bedrock-session-token-env", OnAuth("iam_credentials")),
                    F("bedrock-profile-name", OnAuth("profile")),
                    F("bedrock-inference-profile"),
                    F("bedrock-deployment")),
                PageOf(
                    VertexPage,
                    Title + "Google Vertex AI",
                    "Project, region and sign-in for Vertex AI.",
                    GateOf(Conditions.Append(Provider(VertexValues)).ToArray()),
                    F("vertex-project-id"),
                    F("vertex-region"),
                    F("vertex-auth-mode"),
                    F("vertex-service-account-json-env")),
                PageOf(
                    AzurePage,
                    Title + "Azure OpenAI",
                    "Endpoint, API version and sign-in for an Azure OpenAI resource, and the deployment each model name maps to.",
                    GateOf(Conditions.Append(Provider("azure")).ToArray()),
                    F("azure-endpoint"),
                    F("azure-api-version"),
                    F("azure-auth-mode"),
                    F("azure-deployment-alias")),
                PageOf(
                    TlsPage,
                    Title + "TLS",
                    "For an endpoint behind a private or self-signed certificate. Skipping verification is for a lab only.",
                    TlsForEveryProvider
                        ? GateOf(Conditions)
                        : GateOf(Conditions.Append(Provider(new[] { "bedrock", "azure", "custom" }.Concat(VertexValues).ToArray())).ToArray()),
                    Field(Flag(Tls.CaCert)),
                    Field(Flag(Tls.Insecure))),
            };

            return Compact(steps.ToArray());
        }
    }

    // ------------------------------------------------------------------ setup llm

    private static IReadOnlyList<WizardStep> LlmSteps(ParsedHelp help)
    {
        WizardField? F(string flag, Func<WizardField, WizardField>? tweak = null) => FieldOf(help, flag, tweak);

        var provider = WizardFieldBuilder.Identifier("--provider");
        var instance = WizardFieldBuilder.Identifier("--instance-name");

        var steps = new List<WizardStep>
        {
            PageOf(
                WizardPages.LlmProvider,
                "Provider and model",
                "Which block the settings are written to, and which provider and model it uses. Anything left unchanged keeps what config.yaml has.",
                NoGate,
                F("--role"),
                F("--provider", WithProviderChoices),
                F("--instance-name"),
                F("--model", f => f.WithPicker(WizardFieldPickers.Model, provider, instance))),
            PageOf(
                WizardPages.LlmKey,
                "Key and endpoint",
                "The key is referenced by the NAME of an environment variable, which is what config.yaml stores. Type the key itself in the box below (the review stores it with defenseclaw keys set, then applies this), or store it once yourself with defenseclaw keys set.",
                NoGate,
                F("--api-key-env"),
                F("--api-key"),
                F("--base-url"),
                F("--timeout"),
                F("--max-retries")),
        };

        steps.AddRange(
            new CloudGroups
            {
                Field = flag => FieldOf(help, flag),
                IdOf = flag => IdOf(flag),
                FlagPrefix = "--",
                ProviderFieldId = provider,
                HasProviderField = help.Option("--provider") is not null,
                BedrockPage = WizardPages.LlmBedrock,
                VertexPage = WizardPages.LlmVertex,
                AzurePage = WizardPages.LlmAzure,
                TlsPage = WizardPages.LlmTls,
            }.Build());

        steps.Add(PageOf(
            WizardPages.LlmApply,
            "Apply",
            "Copy a sibling component's settings in first if you want to start from them, and check afterwards that the provider answers.",
            NoGate,
            F("--inherit-from"),
            F("--ping", f => f.WithWording(
                "Ping after saving",
                f.Help + " That is a real request to the provider's endpoint, made by the CLI with the key your variable holds, so it can be billed or rate-limited; it is one message of one word asking for one token.")),
            F("--non-interactive")));

        return Compact(steps.ToArray());
    }

    // ------------------------------------------------------------------ setup provider add

    /// <summary>The curated pages of a group's subcommand, or null when the subcommand has none (every other group's, and every other verb).</summary>
    private static IReadOnlyList<WizardStep>? CuratedSubcommand(
        string? target,
        string subcommand,
        ParsedHelp help,
        string idPrefix,
        IReadOnlyList<string> gateValues)
    {
        if (!string.Equals(target, "provider", StringComparison.Ordinal) || !string.Equals(subcommand, "add", StringComparison.Ordinal) ||
            help.Option("--base-provider-type") is null)
        {
            return null;
        }

        WizardField? F(string flag, Func<WizardField, WizardField>? tweak = null) => FieldOf(help, flag, tweak, idPrefix);

        var baseType = IdOf("--base-provider-type", idPrefix);
        var onAdd = WizardGate.When("subcommand", gateValues.ToArray());

        var steps = new List<WizardStep>
        {
            PageOf(
                WizardPages.ProviderAddBasics,
                "Provider",
                "Name the provider, say which family of API it speaks and where it is. The base provider type decides which settings follow.",
                GateOf(onAdd),
                F("--name", f => f.WithRequired(true)),
                F("--base-provider-type"),
                F("--base-url"),
                F("--domain"),
                F("--env-key")),
            PageOf(
                WizardPages.ProviderAddModels,
                "Models and requests",
                "What the endpoint serves. The model ids you list here are the ones the model picker offers for this provider.",
                GateOf(onAdd),
                F("--available-model"),
                F("--allowed-request"),
                F("--request-path-override"),
                F("--profile-id"),
                F("--ollama-port")),
        };

        steps.AddRange(
            new CloudGroups
            {
                Field = flag => FieldOf(help, flag, null, idPrefix),
                IdOf = flag => IdOf(flag, idPrefix),
                FlagPrefix = "--",
                ProviderFieldId = baseType,
                HasProviderField = true,
                Conditions = new[] { onAdd },
                BedrockPage = WizardPages.ProviderAddBedrock,
                VertexPage = WizardPages.ProviderAddVertex,
                AzurePage = WizardPages.ProviderAddAzure,
                TlsPage = WizardPages.ProviderAddTls,
                Tls = ("ca-cert-file", "insecure-skip-verify"),
                TlsForEveryProvider = true,
            }.Build());

        // The reload switch, and anything a later CLI adds, still has a page.
        var placed = steps.SelectMany(s => s.Fields).Select(f => f.Flag).Where(f => f is not null).Select(f => f!).ToHashSet(StringComparer.Ordinal);
        var rest = help.Options
            .Where(o => !placed.Contains(o.Flag))
            .Select(o => WizardFieldBuilder.From(o, idPrefix: idPrefix))
            .Where(f => f is not null)
            .Select(f => f!)
            .ToArray();
        if (rest.Length > 0)
        {
            steps.Add(PageOf(
                idPrefix + "apply",
                "Apply",
                "How the entry is written.",
                GateOf(onAdd),
                rest.Cast<WizardField?>().ToArray()));
        }

        return Compact(steps.ToArray());
    }

    /// <summary>What a layout leaves for the trailing "More options" page may need a gate of its own: the guardrail's are global-only.</summary>
    private static (string? FieldId, IReadOnlyList<string> Values) RemainderGate(string target, ParsedHelp help) =>
        string.Equals(target, "guardrail", StringComparison.Ordinal) ? GuardrailRemainderGate(help) : NoGate;
}
