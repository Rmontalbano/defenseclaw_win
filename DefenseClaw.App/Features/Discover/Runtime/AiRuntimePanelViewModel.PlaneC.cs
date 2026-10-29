using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;
using DefenseClaw.Core.AiRuntime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The "Plane C readiness" card (CUST-324; the CUST-316 recommendation): what stands between this PC and plane C reading the Security event log,
/// for a developer. Shown only while the developer flag (Settings, Advanced) is on. <b>Read-only by construction:</b> four Win32/.NET reads
/// (<see cref="IPlaneCProbes"/>), the plane C row of the snapshot already on screen, and text to copy. It starts no process, elevates nothing,
/// and never runs <c>auditpol</c> or <c>reg</c>; this partial has no reference to the CLI runner or to the review dialog at all.
/// </summary>
public sealed partial class AiRuntimePanelViewModel
{
    public const string PlaneCCopyOnlyNote =
        "These lines are text to copy. This app never runs them. Run them yourself in an elevated prompt, on the side-by-side runtime only, then restart its gateway and check again.";

    private IPlaneCProbes? _planeCCaptured;
    private bool _planeCReading;

    /// <summary>Test seam: the machine reads. Null uses the real Win32/.NET probes.</summary>
    internal IPlaneCProbes? PlaneCProbes { get; set; }

    /// <summary>The checks, in the order of the card.</summary>
    public ObservableCollection<PlaneCCheck> PlaneCChecks { get; } = new();

    /// <summary>The card exists only for a developer: the Settings, Advanced switch.</summary>
    public bool ShowPlaneC => Services.Settings.Current.Developer.Enabled;

    public string PlaneCAppTokenNote => PlaneCReadiness.AppTokenNote;

    /// <summary>The first three checks are the app's; plane C itself is the gateway's.</summary>
    public bool IsReadingPlaneC => _planeCReading;

    public string PlaneCVerifyText => string.Join(Environment.NewLine, PlaneCReadiness.VerifyLines);

    public string PlaneCRevertText => string.Join(Environment.NewLine, PlaneCReadiness.RevertLines);

    public string PlaneCEventLogReadersText => string.Join(Environment.NewLine, PlaneCReadiness.EventLogReadersLines);

    /// <summary>The grant lines the runtime itself reported (the Prerequisites card's), or empty until they have been read.</summary>
    public string PlaneCGrantText => CommandsText;

    public bool HasPlaneCGrant => PlaneCGrantText.Length > 0;

    /// <summary>
    /// One line for the user's own elevated terminal, against the side-by-side runtime from Settings, Advanced (placeholders where it is not set).
    /// Never run by the app.
    /// </summary>
    public string PlaneCSideBySideText
    {
        get
        {
            var developer = Services.Settings.Current.Developer;
            var home = developer.HomeDirectory ?? "<side-by-side home>";
            var cli = developer.CliPath is { Length: > 0 } path
                ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? string.Empty, "defenseclaw-gateway.exe")
                : "<side-by-side install>\\defenseclaw-gateway.exe";
            return $"$env:DEFENSECLAW_HOME = '{home}'; & '{cli}' restart";
        }
    }

    /// <summary>Reads the machine again and rebuilds the checks. The reads are quick and run off the UI thread.</summary>
    [RelayCommand]
    private Task CheckPlaneCAsync() => ReadPlaneCAsync();

    /// <summary>Puts one copy-only block on the clipboard. Nothing is run.</summary>
    [RelayCommand]
    private void CopyPlaneC(string? block)
    {
        var text = block switch
        {
            "grant" => PlaneCGrantText,
            "verify" => PlaneCVerifyText,
            "revert" => PlaneCRevertText,
            "readers" => PlaneCEventLogReadersText,
            "side-by-side" => PlaneCSideBySideText,
            _ => string.Empty,
        };
        if (text.Length == 0)
        {
            return;
        }

        var copied = ClipboardWriter is { } write ? write(text) : Views.Controls.DcClipboard.TrySetText(text);
        CopyNote = copied ? "Copied. Paste it into your own elevated prompt." : Views.Controls.DcClipboard.FailureText;
    }

    internal async Task ReadPlaneCAsync()
    {
        OnPropertyChanged(nameof(ShowPlaneC));
        OnPropertyChanged(nameof(PlaneCSideBySideText));
        if (!ShowPlaneC || _planeCReading)
        {
            return;
        }

        _planeCReading = true;
        OnPropertyChanged(nameof(IsReadingPlaneC));
        try
        {
            var probes = PlaneCProbes ?? new WindowsPlaneCProbes();
            _planeCCaptured = await Task.Run(() => PlaneCReadiness.Capture(probes)).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed check is a state of the card, never an unhandled exception.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"Plane C readiness read failed: {ex}");
            _planeCCaptured = null;
        }
#pragma warning restore CA1031
        finally
        {
            _planeCReading = false;
            OnPropertyChanged(nameof(IsReadingPlaneC));
        }

        RebuildPlaneC(_snapshot);
    }

    /// <summary>Rebuilds the rows from what the machine said and the snapshot on screen (plane C is the gateway's, so it follows every poll).</summary>
    private void RebuildPlaneC(AiRuntimeSnapshot? snapshot)
    {
        PlaneCChecks.Clear();
        if (!ShowPlaneC)
        {
            return;
        }

        var plane = snapshot?.Planes.FirstOrDefault(p => p.Id == "c");
        foreach (var check in PlaneCReadiness.Build(_planeCCaptured ?? new NotReadProbes(), plane))
        {
            PlaneCChecks.Add(check);
        }

        OnPropertyChanged(nameof(PlaneCGrantText));
        OnPropertyChanged(nameof(HasPlaneCGrant));
    }

    /// <summary>What the checks say before the machine has been read: every one of them "could not read".</summary>
    private sealed class NotReadProbes : IPlaneCProbes
    {
        private const string Problem = "not read yet";

        public ProbeReading<TokenElevation> ReadElevation() => ProbeReading<TokenElevation>.Failed(Problem);

        public ProbeReading<bool> ReadEventLogReadersMembership() => ProbeReading<bool>.Failed(Problem);

        public ProbeReading<bool> ReadSecurityChannelOpens() => ProbeReading<bool>.Failed(Problem);

        public ProbeReading<int?> ReadCommandLineAudit() => ProbeReading<int?>.Failed(Problem);
    }
}
