using System.Globalization;
using System.Text.Json;
using System.Windows;
using DefenseClaw.App.ViewModels.Wizards;
using DefenseClaw.App.Views.Wizards;
using DefenseClaw.Core.Gateway;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The ACP Guard wizard (CUST-294): the Mac's sheet (<c>SetupDefinitions.acpGuard</c>, v1.1.34: Client, Agent, Profile, Action mode, Managed enrollment and,
/// under it, the runtime data directory and the token file) as one page of the shared wizard, then its review. It is a <see cref="WizardDefinition"/> like
/// every catalog wizard, built from what <see cref="AcpGuardDiscovery"/> read rather than from <c>setup &lt;target&gt; --help</c> (the command is
/// <c>acp setup</c>, which the setup catalog does not list), and its one command comes from <see cref="AcpGuardArgv"/>, so the window, the review, the
/// installation guard, the Activity record and the stop confirmation are all the wizard framework's.
/// <para>
/// Opening it runs nothing that changes anything: the help screen and the two gateway reads, then the window. On a managed installation it opens
/// all the same (it is the only place a managed enrollment applies) and its review says why Execute is off, as every review does there.
/// </para>
/// </summary>
public static class AcpGuardWizard
{
    /// <summary>The pseudo-target the definition carries (the Mac's wizard id): never a <c>setup</c> noun.</summary>
    public const string Target = "acp-guard";

    /// <summary>The title, the Mac's.</summary>
    public const string Title = "ACP Guard";

    public const string ClientField = "acp.client";
    public const string AgentField = "acp.agent";
    public const string ProfileField = "acp.profile";
    public const string NewProfileField = "acp.profile.new";
    public const string ActivateField = "acp.activate";
    public const string ManagedField = "acp.managed";
    public const string RuntimeDataDirField = "acp.runtime-data-dir";
    public const string TokenFileField = "acp.token-file";

    /// <summary>The Profile choice that asks for a name of its own instead of one the gateway listed (never a profile name: those have no parentheses).</summary>
    public const string AnotherProfile = "(another)";

    /// <summary>
    /// Reads what the wizard offers (<see cref="AcpGuardDiscovery"/>) and shows it modally over <paramref name="owner"/>. Returns once the window closes.
    /// </summary>
    /// <param name="readHelp">Reads a CLI help screen; null is the Setup help probe (cached, no Activity entry).</param>
    /// <param name="fetch">Reads a gateway route; null is the app's verified-peer gateway client.</param>
    public static async Task ShowAsync(
        AppServices services,
        Window? owner = null,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<string>, CancellationToken, Task<HelpProbeResult>>? readHelp = null,
        Func<string, int, CancellationToken, Task<GatewayResult<JsonDocument>>>? fetch = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var context = await AcpGuardDiscovery.ReadAsync(
                readHelp ?? ((path, ct) => WizardCatalog.Shared(services).Probe.CliHelpAsync(path, ct)),
                fetch ?? ((path, max, ct) => services.Gateway.GetBoundedJsonAsync(path, max, requiresAuth: true, ct)),
                cancellationToken)
            .ConfigureAwait(true);

        var viewModel = new WizardViewModel(services, Build(context, services.Installation.Context.IsManaged));
        var window = new WizardWindow(viewModel)
        {
            Owner = owner ?? Application.Current?.MainWindow,
        };

        window.ShowDialog();
    }

    /// <summary>
    /// The wizard's definition for <paramref name="context"/>. <paramref name="managedInstallation"/> is whether this installation is administrator-managed
    /// (<c>deployment_mode: managed_enterprise</c>): only there does Managed enrollment apply, and there it starts on.
    /// </summary>
    public static WizardDefinition Build(AcpGuardContext context, bool managedInstallation)
    {
        ArgumentNullException.ThrowIfNull(context);

        var managedAllowed = managedInstallation && context.HasManagedFlags;
        var managedDisabled = !context.HasManagedFlags
            ? "This DefenseClaw's acp setup has no --managed: managed enrollment is not available with it."
            : managedInstallation ? string.Empty : AcpGuardArgv.ManagedNeedsManagedInstallation;
        var managedOn = new[] { ToggleValues.On };

        var fields = new List<WizardField>
        {
            PairField(ClientField, "Client", AcpGuardArgv.ClientFlag, context.Clients,
                "The editor that runs the agent. Setup writes a DefenseClaw entry into its settings: Zed's %APPDATA%\\Zed\\settings.json, or JetBrains' %USERPROFILE%\\.jetbrains\\acp.json."),
            PairField(AgentField, "Agent", AcpGuardArgv.AgentFlag, context.Agents,
                "The ACP agent to guard. Setup looks for its executable on PATH, so the agent must be installed (and signed in once from a terminal) first."),
            ProfileFieldFor(context),
            new()
            {
                Id = NewProfileField,
                Label = "New profile name",
                Kind = WizardFieldKind.Text,
                Flag = AcpGuardArgv.ProfileFlag,
                IsRequired = true,
                Placeholder = "for example kiro-only",
                Help = "An acp.profiles key: lowercase letters, digits, '-' and '_'. Setup creates it for this pair with the mode chosen below.",
                VisibleWhenFieldId = ProfileField,
                VisibleWhenValues = new[] { AnotherProfile },
            },
            new()
            {
                Id = ActivateField,
                Label = "Action mode (block denied operations)",
                Kind = WizardFieldKind.Switch,
                Flag = AcpGuardArgv.ActivateFlag,
                DefaultValue = ToggleValues.Off,
                BaselineValue = ToggleValues.Off,
                Help = "Off is observe mode: the guard records findings and blocks nothing. On, it blocks a prompt, output, permission, file or terminal step that policy " +
                       "denies, and the profile fails closed.",
            },
            new()
            {
                Id = ManagedField,
                Label = "Managed enrollment",
                Kind = WizardFieldKind.Switch,
                Flag = AcpGuardArgv.ManagedFlag,
                DefaultValue = managedAllowed ? ToggleValues.On : ToggleValues.Off,
                BaselineValue = ToggleValues.Off,
                DisabledReason = managedDisabled,
                Help = "Enroll the user-side files only, with the policy and the credential your administrator provisioned. Nothing secret is typed here: the token is named by its file.",
            },
            new()
            {
                Id = RuntimeDataDirField,
                Label = "Runtime data directory",
                Kind = WizardFieldKind.Path,
                Flag = AcpGuardArgv.RuntimeDataDirFlag,
                Placeholder = "%USERPROFILE%\\.defenseclaw (written out in full)",
                Help = "Your per-user ACP runtime folder, the one your administrator enrolled you with. A full path.",
                VisibleWhenFieldId = ManagedField,
                VisibleWhenValues = managedOn,
            },
            new()
            {
                Id = TokenFileField,
                Label = "Administrator-provisioned token file",
                Kind = WizardFieldKind.Path,
                Flag = AcpGuardArgv.TokenFileFlag,
                Placeholder = "<runtime data directory>\\acp\\<client>-<agent>.token",
                Help = "Select a file path, never paste a token value. The CLI accepts it only at <runtime data directory>\\acp\\<client>-<agent>.token.",
                VisibleWhenFieldId = ManagedField,
                VisibleWhenValues = managedOn,
            },
        };

        var step = new WizardStep
        {
            Id = "acp",
            Title = Title,
            Subtitle = "Put DefenseClaw between an ACP editor and the agent it runs. Setup starts in observe mode unless action mode is on.",
            Fields = fields,
        };

        return new WizardDefinition
        {
            Target = Target,
            Title = Title,
            Group = WizardGroups.GuardrailAndPolicy,
            CommandName = "acp setup",
            Description = "Observe or enforce policy on editor-to-agent ACP traffic, or enroll with administrator-provisioned policy and token files.",
            Steps = new[] { step },
            PlatformStatus = PlatformStatus.NotApplicable,
            IsDetailLoaded = true,
            IsCurated = true,
            HelpText = context.HelpText,
            BaselineNote = context.SourceNote,
            FinalArgvBuilder = (_, values) => AcpGuardArgv.Build(RequestFrom(values)),
            CrossValidator = values => AcpGuardArgv.Problem(RequestFrom(values), managedAllowed),
        };
    }

    /// <summary>The request the answers make: the profile typed when "another" is chosen, managed only while its switch is on.</summary>
    public static AcpGuardRequest RequestFrom(WizardValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var profile = values[ProfileField].Trim();
        var managed = IsOn(values[ManagedField]);
        return new AcpGuardRequest(
            values[ClientField].Trim(),
            values[AgentField].Trim(),
            string.Equals(profile, AnotherProfile, StringComparison.Ordinal) ? values[NewProfileField].Trim() : profile,
            IsOn(values[ActivateField]),
            managed,
            managed ? values[RuntimeDataDirField].Trim() : string.Empty,
            managed ? values[TokenFileField].Trim() : string.Empty);
    }

    /// <summary>A choice of what the CLI accepts, or a typed id when nothing named the choices.</summary>
    private static WizardField PairField(string id, string label, string flag, IReadOnlyList<WizardChoice> choices, string help) => choices.Count > 0
        ? new WizardField
        {
            Id = id,
            Label = label,
            Kind = WizardFieldKind.Choice,
            Flag = flag,
            IsRequired = true,
            Choices = choices,
            Help = help,
        }
        : new WizardField
        {
            Id = id,
            Label = label,
            Kind = WizardFieldKind.Text,
            Flag = flag,
            IsRequired = true,
            Placeholder = "id, as defenseclaw acp catalog lists it",
            Help = help,
        };

    private static WizardField ProfileFieldFor(AcpGuardContext context)
    {
        const string Rule = "Setup refuses to change the mode of a profile another pair already uses; give this pair a profile of its own for that.";
        if (!context.HasProfileChoices)
        {
            return new WizardField
            {
                Id = ProfileField,
                Label = "Profile",
                Kind = WizardFieldKind.Text,
                Flag = AcpGuardArgv.ProfileFlag,
                IsRequired = true,
                DefaultValue = context.StartingProfile,
                Placeholder = AcpGuardArgv.DefaultProfile,
                Help = "The ACP policy profile (acp.profiles) the guard enforces for this pair. " + Rule,
            };
        }

        var choices = context.Profiles!.Profiles
            .Select(p => new WizardChoice(p.Name, ProfileLabel(p)))
            .Append(new WizardChoice(AnotherProfile, "Another profile…"))
            .ToArray();
        return new WizardField
        {
            Id = ProfileField,
            Label = "Profile",
            Kind = WizardFieldKind.Choice,
            Flag = AcpGuardArgv.ProfileFlag,
            IsRequired = true,
            Choices = choices,
            DefaultValue = context.StartingProfile,
            Help = "The ACP policy profile (acp.profiles) the guard enforces for this pair. " + Rule,
        };
    }

    /// <summary>"default — observe, fails open (editors: zed; agents: kiro)".</summary>
    internal static string ProfileLabel(AcpProfile profile)
    {
        var mode = profile.Mode.Length > 0 ? profile.Mode : "global mode";
        var fail = profile.FailMode.Length > 0 ? ", fails " + profile.FailMode : string.Empty;
        var admits = profile.AllowedClients.Count > 0 || profile.AllowedAgents.Count > 0
            ? string.Create(CultureInfo.CurrentCulture, $" (editors: {Join(profile.AllowedClients)}; agents: {Join(profile.AllowedAgents)})")
            : string.Empty;
        return profile.Name + " — " + mode + fail + admits;
    }

    private static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    private static bool IsOn(string value) => string.Equals(value.Trim(), ToggleValues.On, StringComparison.OrdinalIgnoreCase);
}
