using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services.Settings;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>One choice in the runtime selector's list.</summary>
/// <param name="Kind">What it selects.</param>
/// <param name="Label">What the list shows.</param>
public sealed record RuntimeKindOption(RuntimeKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings -> Advanced (CUST-291): the developer runtime selector, and what runtime the app found. <b>Off by default; off means the
/// installed runtime, exactly as before.</b> Three choices when on: the installed runtime, an explicit CLI with its own
/// <c>DEFENSECLAW_HOME</c> and (optionally) its own gateway address, or a Docker container (<c>docker exec</c>, a published
/// loopback gateway, and a read-only host copy of its data folder).
/// <para>
/// The form is applied with a button, not as you type, and a choice that does not validate is refused with the reason and saves
/// nothing. A saved choice takes effect the next time the app starts: the data directory and the gateway client are built once.
/// The page says so, and says what the running app is using right now.
/// </para>
/// <para>Nothing here is a secret. The gateway address is a loopback host and port, no credentials.</para>
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    /// <summary>The three choices, in the order the list shows them.</summary>
    public IReadOnlyList<RuntimeKindOption> RuntimeKinds { get; } =
    [
        new(RuntimeKind.Installed, "Installed runtime (default)"),
        new(RuntimeKind.Cli, "Explicit CLI, home and gateway"),
        new(RuntimeKind.Container, "Docker container"),
    ];

    /// <summary>The "Use a different runtime" switch (<c>developer.enabled</c>). Writes at once; it changes nothing until the app restarts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCliFields))]
    [NotifyPropertyChangedFor(nameof(ShowContainerFields))]
    [NotifyPropertyChangedFor(nameof(ShowRuntimeForm))]
    private bool _developerEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCliFields))]
    [NotifyPropertyChangedFor(nameof(ShowContainerFields))]
    [NotifyPropertyChangedFor(nameof(ShowRuntimeForm))]
    private RuntimeKindOption _selectedRuntimeKind = null!;

    [ObservableProperty]
    private string _runtimeCliPath = string.Empty;

    [ObservableProperty]
    private string _runtimeHomeDirectory = string.Empty;

    [ObservableProperty]
    private string _runtimeGatewayUrl = string.Empty;

    [ObservableProperty]
    private string _runtimeContainerName = string.Empty;

    [ObservableProperty]
    private string _runtimeHostDataFolder = string.Empty;

    /// <summary>Why the form was refused, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeProblem))]
    private string _runtimeProblem = string.Empty;

    public bool HasRuntimeProblem => RuntimeProblem.Length > 0;

    /// <summary>What the last Apply did, or the pending-restart reminder.</summary>
    [ObservableProperty]
    private string _runtimeMessage = string.Empty;

    /// <summary>The runtime this run of the app is using: the selection it started with.</summary>
    [ObservableProperty]
    private string _runtimeInUseText = "Installed runtime";

    public bool ShowRuntimeForm => DeveloperEnabled && SelectedRuntimeKind.Kind != RuntimeKind.Installed;

    public bool ShowCliFields => DeveloperEnabled && SelectedRuntimeKind.Kind == RuntimeKind.Cli;

    public bool ShowContainerFields => DeveloperEnabled && SelectedRuntimeKind.Kind == RuntimeKind.Container;

    // ---- the runtime the app found (About) ----

    /// <summary>"defenseclaw-cli 1.0.0", or why nothing is known.</summary>
    [ObservableProperty]
    private string _runtimeIdentityText = "Not detected";

    /// <summary>One line per capability: its name, then whether this runtime has it.</summary>
    public ObservableCollection<RuntimeCapabilityRow> RuntimeCapabilityRows { get; } = new();

    /// <summary>Caveats from the probes, one sentence each; empty when there are none.</summary>
    [ObservableProperty]
    private string _runtimeCapabilityNotes = string.Empty;

    partial void OnDeveloperEnabledChanged(bool value) =>
        Write(settings => settings with { Developer = settings.Developer with { Enabled = value } });

    /// <summary>Copies the stored selector into the form, without writing it back.</summary>
    private void LoadDeveloperFromSettings()
    {
        var developer = Services.Settings.Current.Developer;
        Sync(() =>
        {
            DeveloperEnabled = developer.Enabled;
            SelectedRuntimeKind = RuntimeKinds.First(o => o.Kind == developer.Kind);
            RuntimeCliPath = developer.CliPath ?? string.Empty;
            RuntimeHomeDirectory = developer.HomeDirectory ?? string.Empty;
            RuntimeGatewayUrl = developer.GatewayUrl ?? string.Empty;
            RuntimeContainerName = developer.ContainerName ?? string.Empty;
            RuntimeHostDataFolder = developer.HostDataFolder ?? string.Empty;
        });

        RuntimeInUseText = Services.Runtime.Selection.Describe();
        ShowRuntimePendingRestart();
    }

    /// <summary>The form as typed.</summary>
    internal RuntimeSelection FormSelection() => SelectedRuntimeKind.Kind switch
    {
        RuntimeKind.Cli => RuntimeSelection.ForCli(RuntimeCliPath, RuntimeHomeDirectory, RuntimeGatewayUrl),
        RuntimeKind.Container => RuntimeSelection.ForContainer(RuntimeContainerName, RuntimeGatewayUrl, RuntimeHostDataFolder),
        _ => RuntimeSelection.Installed,
    };

    /// <summary>
    /// Validates the form and, if it passes, saves it as <c>developer.*</c> (every field, so switching kind back and forth keeps what
    /// was typed). A refused form says why and saves nothing. Takes effect at the next start.
    /// </summary>
    [RelayCommand]
    internal void ApplyRuntime()
    {
        var selection = FormSelection();
        if (selection.Validate() is { } problem)
        {
            RuntimeProblem = problem;
            RuntimeMessage = string.Empty;
            return;
        }

        RuntimeProblem = string.Empty;
        var kind = SelectedRuntimeKind.Kind;
        Write(settings => settings with
        {
            Developer = settings.Developer with
            {
                Kind = kind,
                CliPath = RuntimeCliPath,
                HomeDirectory = RuntimeHomeDirectory,
                GatewayUrl = RuntimeGatewayUrl,
                ContainerName = RuntimeContainerName,
                HostDataFolder = RuntimeHostDataFolder,
            },
        });

        LoadDeveloperFromSettings();
        if (RuntimeMessage.Length == 0)
        {
            RuntimeMessage = "Saved.";
        }
    }

    /// <summary>Back to the installed runtime: the form's kind and the switch are reset, the other fields kept.</summary>
    [RelayCommand]
    private void UseInstalledRuntime()
    {
        RuntimeProblem = string.Empty;
        Write(settings => settings with { Developer = settings.Developer with { Enabled = false, Kind = RuntimeKind.Installed } });
        LoadDeveloperFromSettings();
    }

    [RelayCommand]
    private void BrowseRuntimeCli()
    {
        var start = RuntimeCliPath.Length > 0 ? System.IO.Path.GetDirectoryName(RuntimeCliPath) : null;
        if (_platform.PickCliExecutable(start) is { } picked)
        {
            RuntimeCliPath = picked;
        }
    }

    [RelayCommand]
    private void BrowseRuntimeHome()
    {
        if (_platform.PickFolder("Choose the DEFENSECLAW_HOME folder", RuntimeHomeDirectory) is { } picked)
        {
            RuntimeHomeDirectory = picked;
        }
    }

    [RelayCommand]
    private void BrowseRuntimeDataFolder()
    {
        if (_platform.PickFolder("Choose the host copy of the container's data folder", RuntimeHostDataFolder) is { } picked)
        {
            RuntimeHostDataFolder = picked;
        }
    }

    /// <summary>
    /// Says, when the saved selector would start a different runtime from the one running now, that a restart is needed. A saved
    /// choice that does not validate (a file moved since) says the app will use the installed runtime.
    /// </summary>
    private void ShowRuntimePendingRestart()
    {
        var developer = Services.Settings.Current.Developer;
        var wanted = developer.ToSelection();
        var running = Services.Runtime.Selection;

        if (developer.Enabled && developer.Kind != RuntimeKind.Installed && wanted.IsDefault)
        {
            RuntimeMessage = "The saved choice is not usable right now (" + (developer.Raw().Validate() ?? "it is incomplete") +
                "), so the installed runtime is used.";
        }
        else if (!Equals(wanted, running))
        {
            RuntimeMessage = "Saved. Restart DefenseClaw for Windows to use " + wanted.Describe() + ".";
        }
        else
        {
            RuntimeMessage = string.Empty;
        }
    }

    /// <summary>Shows what the probes found: identity, one row per capability, the notes. From memory; probing is the detector's.</summary>
    private void ShowRuntimeIdentity()
    {
        var snapshot = Services.Runtime.Current;
        RuntimeIdentityText = RuntimeSummary.Identity(snapshot);

        RuntimeCapabilityRows.Clear();
        foreach (var capability in RuntimeCapabilityCatalog.All)
        {
            var has = snapshot.Capabilities.Has(capability);
            RuntimeCapabilityRows.Add(new RuntimeCapabilityRow(
                RuntimeCapabilityCatalog.DisplayName(capability),
                snapshot.IsKnown ? (has ? "Available" : "Not in this runtime") : "Unknown",
                has,
                RuntimeCapabilityCatalog.Marker(capability)));
        }

        RuntimeCapabilityNotes = snapshot.IsKnown && snapshot.Capabilities.Notes.Count > 0
            ? string.Join(" ", snapshot.Capabilities.Notes)
            : string.Empty;
    }

    private void OnRuntimeChanged(object? sender, EventArgs e) => OnUiThread(ShowRuntimeIdentity);

    /// <summary>"Check again": forces a fresh probe of the runtime.</summary>
    [RelayCommand]
    private async Task RecheckRuntimeAsync()
    {
        _ = await Services.Runtime.RefreshAsync(force: true).ConfigureAwait(true);
        ShowRuntimeIdentity();
    }
}

/// <summary>One line of the capability list in About: name, status, whether it is present, and how it was decided.</summary>
public sealed record RuntimeCapabilityRow(string Name, string Status, bool IsPresent, string Marker);
