using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.AiRuntime;
using DefenseClaw.Core.Cli;

namespace DefenseClaw.App.ViewModels;

/// <summary>Where the prerequisites card is in reading <c>permissions --json</c>.</summary>
public enum AiRuntimePrerequisitesState
{
    /// <summary>Not asked yet.</summary>
    NotRead,

    /// <summary>The CLI is being asked.</summary>
    Loading,

    /// <summary>The runtime's guidance is on the card.</summary>
    Ready,

    /// <summary>The runtime has no <c>permissions</c> command, so there is nothing to ask.</summary>
    NotSupported,

    /// <summary>The CLI could not be asked, or its answer was not the document.</summary>
    Unavailable,
}

/// <summary>
/// The prerequisites card: what each plane needs on this PC, from <c>agent discovery runtime permissions --json</c>. Read-only by construction -
/// the one argv is <see cref="AiRuntimeCommands.ReadPermissions"/> (checked again before every run), never <c>--grant</c> or <c>--revert</c>, which change the
/// machine's audit policy and are not this app's to run. The <c>auditpol</c> and <c>reg add</c> lines it shows are text to copy into an elevated prompt.
/// </summary>
public sealed partial class AiRuntimePanelViewModel
{
    /// <summary>
    /// What this PC's gateway can and cannot see without a grant, from <c>windows.go</c>: the card says it before the list, because the list reads
    /// "missing" for plane A and B when only the elevated part of them is.
    /// </summary>
    public const string WindowsNote =
        "Planes A and B work without elevation for your own processes and sockets; an elevated gateway widens them to the whole machine. " +
        "Plane C (agent actions) needs an elevated gateway and Advanced Audit Policy, and the command-line audit setting for argument-based detections.";

    public const string CopyOnlyNote =
        "These lines are text to copy. This app never runs them, and never runs 'permissions --grant'. Run them yourself in an elevated prompt, then check again.";

    private AiRuntimePermissions? _permissions;

    /// <summary>Test seam: runs the permissions read instead of <c>Services.Cli.RunAsync</c>, so a test sees the exact argv and starts no process.</summary>
    internal Func<IReadOnlyList<string>, CancellationToken, Task<CliInvocation>>? RunPermissionsRead { get; set; }

    /// <summary>Test seam: what Copy writes with; true when it landed. Null uses the real clipboard.</summary>
    internal Func<string, bool>? ClipboardWriter { get; set; }

    /// <summary>The grants, in the runtime's order.</summary>
    public ObservableCollection<AiRuntimeGrantRow> Grants { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrants), nameof(ShowPrerequisitesMessage), nameof(PrerequisitesMessage))]
    private AiRuntimePrerequisitesState _prerequisitesState = AiRuntimePrerequisitesState.NotRead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrerequisitesMessage))]
    private string _prerequisitesProblem = string.Empty;

    [ObservableProperty]
    private string _prerequisitesSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopyNote))]
    private string _copyNote = string.Empty;

    public bool HasCopyNote => CopyNote.Length > 0;

    public bool ShowGrants => PrerequisitesState == AiRuntimePrerequisitesState.Ready;

    public bool ShowPrerequisitesMessage => PrerequisitesState != AiRuntimePrerequisitesState.Ready;

    public bool IsReadingPrerequisites => PrerequisitesState == AiRuntimePrerequisitesState.Loading;

    /// <summary>What stands in for the grants while there are none to show.</summary>
    public string PrerequisitesMessage => PrerequisitesState switch
    {
        AiRuntimePrerequisitesState.Loading => "Asking the runtime what each plane needs…",
        AiRuntimePrerequisitesState.NotSupported => "This DefenseClaw runtime has no 'agent discovery runtime permissions' command, so there is nothing to check here.",
        AiRuntimePrerequisitesState.Unavailable => "The permissions check did not complete" + (PrerequisitesProblem.Length > 0 ? ": " + PrerequisitesProblem : ".") + " Nothing is assumed to be granted.",
        AiRuntimePrerequisitesState.NotRead => "Not read yet.",
        _ => string.Empty,
    };

    /// <summary>The OS the guidance is for when it is not Windows (a container runtime reports its own).</summary>
    public string GuidanceOs => _permissions is { IsWindows: false } other ? other.Os : string.Empty;

    public bool HasGuidanceOs => GuidanceOs.Length > 0;

    public string GuidanceOsNote => HasGuidanceOs
        ? $"This guidance is for {GuidanceOs}, where the runtime runs, not for this PC."
        : string.Empty;

    /// <summary>Every copy-only command line of every open grant, one per line.</summary>
    public string CommandsText => _permissions is null ? string.Empty : string.Join(Environment.NewLine, _permissions.CommandLines);

    public bool HasCommands => CommandsText.Length > 0;

    /// <summary>Checks the prerequisites again (after the operator ran the lines).</summary>
    [RelayCommand]
    private Task CheckPrerequisitesAsync() => LoadPrerequisitesAsync();

    /// <summary>Puts the copy-only lines on the clipboard.</summary>
    [RelayCommand]
    private void CopyCommands()
    {
        var text = CommandsText;
        if (text.Length == 0)
        {
            return;
        }

        var copied = ClipboardWriter is { } write ? write(text) : Views.Controls.DcClipboard.TrySetText(text);
        CopyNote = copied ? "Copied. Paste them into an elevated prompt." : Views.Controls.DcClipboard.FailureText;
    }

    internal async Task LoadPrerequisitesAsync()
    {
        if (PrerequisitesState == AiRuntimePrerequisitesState.Loading)
        {
            return;
        }

        CopyNote = string.Empty;
        if (!Services.Runtime.Capabilities.HasAiRuntimeCommand(AiRuntimeCommands.PermissionsCommand))
        {
            ClearGrants();
            PrerequisitesState = AiRuntimePrerequisitesState.NotSupported;
            return;
        }

        PrerequisitesState = AiRuntimePrerequisitesState.Loading;
        OnPropertyChanged(nameof(IsReadingPrerequisites));
        try
        {
            var argv = AiRuntimeCommands.ReadPermissions;

            // Belt and braces: the only command this card may run is the read. A change anywhere else cannot turn it into a --grant.
            if (!AiRuntimeCommands.IsPermissionsRead(argv))
            {
                throw new InvalidOperationException("The prerequisites read is not the read-only permissions command.");
            }

            var invocation = await (RunPermissionsRead is { } run
                ? run(argv, CancellationToken.None)
                : Services.Cli.RunAsync(
                    argv,
                    cancellationToken: CancellationToken.None,
                    options: CliRunOptions.JsonRead with { Timeout = TimeSpan.FromSeconds(60) })).ConfigureAwait(true);

            if (invocation.FailureReason is { Length: > 0 } reason)
            {
                Unavailable(reason);
                return;
            }

            if (invocation.ExitCode != 0)
            {
                Unavailable($"the command exited with code {(invocation.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?")}");
                return;
            }

            var read = AiRuntimePermissionsReader.Parse(DiscoverCli.Stdout(invocation));
            if (read.Permissions is not { } permissions)
            {
                Unavailable(read.Message);
                return;
            }

            _permissions = permissions;
            Grants.Clear();
            foreach (var grant in permissions.Grants)
            {
                Grants.Add(new AiRuntimeGrantRow(grant));
            }

            PrerequisitesSummary = Summarise(permissions);
            PrerequisitesState = AiRuntimePrerequisitesState.Ready;
            RaisePrerequisites();
        }
        catch (CliNotFoundException)
        {
            Unavailable("the DefenseClaw command line was not found");
        }
#pragma warning disable CA1031 // A failed check is a state of the card, never an unhandled exception.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"Runtime prerequisites read failed: {ex}");
            Unavailable("it failed unexpectedly (" + ex.GetType().Name + ")");
        }
#pragma warning restore CA1031
        finally
        {
            OnPropertyChanged(nameof(IsReadingPrerequisites));
        }
    }

    private void Unavailable(string problem)
    {
        ClearGrants();
        PrerequisitesProblem = problem.TrimEnd('.');
        PrerequisitesState = AiRuntimePrerequisitesState.Unavailable;
    }

    private void ClearGrants()
    {
        _permissions = null;
        Grants.Clear();
        PrerequisitesSummary = string.Empty;
        RaisePrerequisites();
    }

    private void RaisePrerequisites()
    {
        OnPropertyChanged(nameof(GuidanceOs));
        OnPropertyChanged(nameof(HasGuidanceOs));
        OnPropertyChanged(nameof(GuidanceOsNote));
        OnPropertyChanged(nameof(CommandsText));
        OnPropertyChanged(nameof(HasCommands));
        OnPropertyChanged(nameof(PlaneCGrantText));
        OnPropertyChanged(nameof(HasPlaneCGrant));
    }

    private static string Summarise(AiRuntimePermissions permissions)
    {
        var missing = permissions.MissingCount;
        var unknown = permissions.UnknownCount;
        var parts = new List<string>();
        if (missing > 0)
        {
            parts.Add($"{missing.ToString(CultureInfo.InvariantCulture)} missing");
        }

        if (unknown > 0)
        {
            parts.Add($"{unknown.ToString(CultureInfo.InvariantCulture)} could not be verified from here");
        }

        if (parts.Count == 0)
        {
            return "Nothing this check can see is missing.";
        }

        return string.Join(", ", parts) + (unknown > 0 ? ". An unknown is not a failure: it is a grant this process cannot verify, not one that is absent." : ".");
    }
}
