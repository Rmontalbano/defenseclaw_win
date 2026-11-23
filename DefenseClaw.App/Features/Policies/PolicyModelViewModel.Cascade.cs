using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Config;
using DefenseClaw.Core.Policy;
using DefenseClaw.Core.Policy.Model;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The read-only Cascade block of the Policies panel (runtime 1.0.10, <see cref="RuntimeCapability.GuardrailCascade"/>): what config.yaml says about
/// <c>guardrail.cascade</c> (<see cref="GuardrailCascade"/>, no command run) and, only when the Explain button is pressed, the runtime's own account
/// from <c>defenseclaw guardrail cascade explain [--connector C] --json</c> (<see cref="GuardrailCascadeExplain"/>). The command never runs on load or
/// on a timer, its argv is built from constants and a connector name this panel already lists, and the text it prints is bounded and only drawn.
/// Nothing here writes configuration. On a runtime without the cascade nothing is shown and nothing is read.
/// </summary>
public sealed partial class PolicyModelViewModel
{
    /// <summary>The explain command starts the gateway binary; the CLI gives it 30 s, so the app waits a little longer.</summary>
    internal static readonly TimeSpan CascadeExplainTimeout = TimeSpan.FromSeconds(35);

    private GuardrailCascade _cascadeRead = GuardrailCascade.Default;
    private ConfigDocument? _cascadeSource;
    private IReadOnlyList<string>? _explainLines;
    private string _explainScope = string.Empty;
    private ConfigDocument? _explainConfig;

    /// <summary>The Cascade block's lines: config.yaml's, or the explain command's once it has run for this scope.</summary>
    public ObservableCollection<PolicyInspectorLine> CascadeLines { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExplainCascade))]
    private bool _isExplainingCascade;

    /// <summary>The runtime has the cascade (1.0.10 or later).</summary>
    public bool CascadeAvailable => Services.Runtime.Capabilities.Has(RuntimeCapability.GuardrailCascade);

    /// <summary>The block shows on the Posture view of a loaded panel, on a runtime that has the cascade.</summary>
    public bool ShowCascade => CascadeAvailable && State == PoliciesState.Loaded && string.Equals(View, "posture", StringComparison.Ordinal);

    /// <summary>The Explain button is on while no explain is running.</summary>
    public bool CanExplainCascade => CascadeAvailable && !IsExplainingCascade;

    /// <summary>The cascade as config.yaml has it, parsed once per parsed config.</summary>
    internal GuardrailCascade CurrentCascade()
    {
        var config = Services.Config;
        if (!ReferenceEquals(config, _cascadeSource))
        {
            _cascadeRead = GuardrailCascade.From(config);
            _cascadeSource = config;
        }

        return _cascadeRead;
    }

    /// <summary>What the Posture detail needs: null on a runtime without the cascade, which leaves the matrix exactly as it was.</summary>
    internal PostureCascade? PostureCascadeContext() => CascadeAvailable ? PostureCascade.Of(CurrentCascade()) : null;

    /// <summary>The connector the block is about: the chosen scope when it is one this panel lists; null for the global default.</summary>
    internal string? CascadeConnector()
    {
        var scope = SelectedScope;
        if (string.IsNullOrWhiteSpace(scope) || string.Equals(scope, PolicyScopes.Global, StringComparison.Ordinal) || !Scopes.Contains(scope, StringComparer.Ordinal))
        {
            return null;
        }

        var name = scope.ToLowerInvariant();
        // CascadeExplain leaves a name of an unusual form out (six words with it, four without).
        return PolicyActionGuard.CascadeExplain(name).Count == 6 ? name : null;
    }

    private IReadOnlyList<string> CascadeConnectors(string? connector) =>
        connector is not null ? new[] { connector } : Scopes.Where(s => !string.Equals(s, PolicyScopes.Global, StringComparison.Ordinal)).Take(8).ToArray();

    /// <summary>Redraws the block from config.yaml, or from the explain output while it is still about this scope and this config. No command runs.</summary>
    private void RefreshCascade()
    {
        CascadeLines.Clear();
        if (CascadeAvailable)
        {
            var connector = CascadeConnector();
            IReadOnlyList<string> lines;
            if (_explainLines is not null && ReferenceEquals(_explainConfig, Services.Config) && string.Equals(_explainScope, connector ?? string.Empty, StringComparison.Ordinal))
            {
                lines = _explainLines;
            }
            else
            {
                _explainLines = null;
                lines = CurrentCascade().Lines(CascadeConnectors(connector));
            }

            foreach (var line in lines)
            {
                CascadeLines.Add(new PolicyInspectorLine(line));
            }
        }

        OnPropertyChanged(nameof(CascadeAvailable));
        OnPropertyChanged(nameof(ShowCascade));
        OnPropertyChanged(nameof(CanExplainCascade));
    }

    /// <summary>
    /// Runs <c>guardrail cascade explain</c> now (the Explain button; never on load or a timer) and shows what it printed in place of the config.yaml
    /// lines, or the one-line reason it could not run.
    /// </summary>
    [RelayCommand]
    private async Task ExplainCascadeAsync()
    {
        if (IsExplainingCascade || !CascadeAvailable || _disposed)
        {
            return;
        }

        var connector = CascadeConnector();
        var argv = PolicyActionGuard.CascadeExplain(connector);
        var config = Services.Config;
        IsExplainingCascade = true;
        IReadOnlyList<string> result;
        try
        {
            var invocation = await RunReadAsync(argv, CancellationToken.None, background: true, timeout: CascadeExplainTimeout).ConfigureAwait(true);
            result = ExplainResult(invocation, connector);
        }
        catch (CliNotFoundException ex)
        {
            result = ErrorLines(GuardrailCascadeExplain.ErrorLine(ex.Message), connector);
        }
        finally
        {
            IsExplainingCascade = false;
        }

        _explainLines = result;
        _explainScope = connector ?? string.Empty;
        _explainConfig = config;
        RefreshCascade();
    }

    private IReadOnlyList<string> ExplainResult(CliInvocation invocation, string? connector)
    {
        if (invocation.FailureReason is { Length: > 0 } reason)
        {
            return ErrorLines(GuardrailCascadeExplain.ErrorLine(reason), connector);
        }

        if (invocation.ExitCode is not 0)
        {
            var stderr = invocation.OutputLines.FirstOrDefault(l => l.Stream == CliStream.StandardError && l.Text.Trim().Length > 0)?.Text;
            var code = invocation.ExitCode is { } exit ? "exited " + exit.ToString(System.Globalization.CultureInfo.InvariantCulture) : "did not finish";
            return ErrorLines(GuardrailCascadeExplain.ErrorLine(stderr ?? code), connector);
        }

        var text = string.Join('\n', invocation.OutputLines.Where(l => l.Stream == CliStream.StandardOutput).Select(l => l.Text));
        if (GuardrailCascadeExplain.Parse(text) is not { } explain)
        {
            return CurrentCascade().Lines(CascadeConnectors(connector)).Append(GuardrailCascadeExplain.UnreadableLine).ToArray();
        }

        // The explain output has no word on the judge: the alignment sentence comes from config.yaml.
        var sentences = CascadeConnectors(connector).Select(c => CurrentCascade().AlignmentNotCoveredLine(c)).OfType<string>();
        return explain.Lines.Concat(sentences).ToArray();
    }

    private IReadOnlyList<string> ErrorLines(string error, string? connector) =>
        CurrentCascade().Lines(CascadeConnectors(connector)).Append(error).ToArray();
}
