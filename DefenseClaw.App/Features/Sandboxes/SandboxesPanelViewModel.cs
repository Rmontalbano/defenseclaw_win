using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the Sandboxes panel (CUST-383): the Mac's Sandboxes slot in the Govern group, which on Windows can only say why there is
/// nothing here. The runtime's OpenShell sandboxes target Linux and macOS (<see cref="RuntimeCapability.Sandbox"/>, which
/// <see cref="RuntimeCapabilityCatalog.IsUsableHere"/> rules out on Windows), and running them from this app was canceled (CUST-296).
/// <para>
/// <b>Nothing here runs.</b> The page mirrors the Mac's off state (<c>SandboxesView.swift</c>: a title, the explanation, Open Setup) and
/// leaves out every control that would do something: no run command to copy (it would not work on this PC), no sandbox doctor, no refresh
/// (re-probing the runtime would run the CLI). Its one command shows another panel. It reads what the runtime probe already knows, and
/// only while it is on screen.
/// </para>
/// </summary>
public sealed partial class SandboxesPanelViewModel : PanelViewModelBase
{
    /// <summary>What the runtime itself runs to turn sandboxes on, on the platforms that have them (shown as words, never offered to run or copy).</summary>
    public const string ElsewhereSetupCommand = "defenseclaw sandbox setup";

    private string _runtimeFact = string.Empty;

    public SandboxesPanelViewModel(AppServices services)
        : base(services)
    {
        _runtimeFact = DescribeRuntime();
    }

    public override string Title => "Sandboxes";

    public override string Description =>
        "Run a coding agent in an NVIDIA OpenShell sandbox that sees only your project folder. The DefenseClaw runtime offers sandboxes on Linux and macOS only.";

    /// <summary>True when the runtime's sandboxes can be used on this machine; never on Windows (the decision is the runtime capability catalog's).</summary>
    public bool IsUsableHere => RuntimeCapabilityCatalog.IsUsableHere(RuntimeCapability.Sandbox);

    /// <summary>The toolbar's caption.</summary>
    public string CaptionText => IsUsableHere ? string.Empty : "Linux and macOS only";

    public string StateTitle => IsUsableHere ? "Sandboxes are managed by the DefenseClaw runtime" : "Sandboxes are not available on Windows";

    public string StateDetail => IsUsableHere
        ? "This app does not run or manage sandboxes; the runtime's own commands do."
        : "DefenseClaw can run a coding agent in an NVIDIA OpenShell sandbox that sees only your project folder. The runtime supports sandboxes on "
          + "Linux and macOS only, so there is nothing to set up on this PC, and this app offers no sandbox actions.";

    /// <summary>Where they do work, and with what: informational only (<see cref="ElsewhereSetupCommand"/> is not a button here).</summary>
    public string ElsewhereText =>
        $"On a Linux or macOS host, the runtime turns them on with: {ElsewhereSetupCommand}. The rest of this PC's protection is configured in Setup.";

    /// <summary>What the connected runtime says about its sandbox commands, in a sentence; follows the runtime probe while the page is on screen.</summary>
    public string RuntimeFact
    {
        get => _runtimeFact;
        private set => SetProperty(ref _runtimeFact, value);
    }

    /// <summary>Shows the Setup panel. A navigation, not a command: nothing is run or changed.</summary>
    [RelayCommand]
    private void OpenSetup() => RequestNavigation("setup");

    protected override void OnActivated()
    {
        Services.Runtime.Changed += OnRuntimeChanged;
        RuntimeFact = DescribeRuntime();
    }

    protected override void OnDeactivated() => Services.Runtime.Changed -= OnRuntimeChanged;

    /// <summary>The probe answered again (raised on its own thread): re-read it on the UI thread.</summary>
    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RuntimeFact = DescribeRuntime();
        }
        else
        {
            _ = dispatcher.BeginInvoke(() => RuntimeFact = DescribeRuntime());
        }
    }

    private string DescribeRuntime()
    {
        var snapshot = Services.Runtime.Current;
        if (!snapshot.IsKnown)
        {
            return "The connected runtime has not been identified yet.";
        }

        var who = snapshot.Identity!.Display;
        return snapshot.Capabilities.Has(RuntimeCapability.Sandbox)
            ? $"The connected runtime ({who}) has the sandbox commands; they target Linux and macOS."
            : $"The connected runtime ({who}) has no sandbox commands.";
    }
}
