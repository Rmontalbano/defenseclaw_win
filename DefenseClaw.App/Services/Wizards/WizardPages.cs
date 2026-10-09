namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The ids of the pages the curated layouts build and the goals name (<see cref="WizardGoals"/>). One list, so a goal and a layout cannot
/// spell a page two ways. The ids the guardrail layout had before (<c>detection</c>, <c>judge</c>, <c>cisco</c>, <c>apply</c>) are kept.
/// </summary>
internal static class WizardPages
{
    // setup llm
    public const string LlmProvider = "llm-provider";
    public const string LlmKey = "llm-key";
    public const string LlmBedrock = "llm-bedrock";
    public const string LlmVertex = "llm-vertex";
    public const string LlmAzure = "llm-azure";
    public const string LlmTls = "llm-tls";
    public const string LlmApply = "llm-apply";

    // setup guardrail
    public const string GuardrailScope = "scope";
    public const string GuardrailMode = "mode";
    public const string GuardrailApproval = "approval";
    public const string GuardrailDetection = "detection";
    public const string GuardrailJudge = "judge";
    public const string GuardrailJudgeBedrock = "judge-bedrock";
    public const string GuardrailJudgeVertex = "judge-vertex";
    public const string GuardrailJudgeAzure = "judge-azure";
    public const string GuardrailJudgeTls = "judge-tls";
    public const string GuardrailCisco = "cisco";
    public const string GuardrailApply = "apply";

    // setup splunk (WizardWalkthroughs builds these)
    public const string SplunkO11y = "o11y";
    public const string SplunkLocal = "local";
    public const string SplunkEnterprise = "enterprise";
    public const string SplunkHec = "hec-destination";

    // setup provider add (the ids are prefixed with the subcommand, as every page of a group is)
    public const string ProviderAddBasics = "add:provider";
    public const string ProviderAddModels = "add:models";
    public const string ProviderAddBedrock = "add:bedrock";
    public const string ProviderAddVertex = "add:vertex";
    public const string ProviderAddAzure = "add:azure";
    public const string ProviderAddTls = "add:tls";

    /// <summary>The page that holds every flag a curated layout did not place.</summary>
    public const string MoreOptions = "more-options";
}
