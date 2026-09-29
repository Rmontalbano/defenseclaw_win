using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// View-model for the MCPs panel: MCP servers configured for each connector, read with
/// <c>defenseclaw mcp list --json</c>, plus block / allow / unblock / unset per row and an "Add MCP server"
/// form (<c>mcp set</c>).
/// <para>
/// Real 0.8.10 items are <c>{name, transport, [connector], [command], [args], [url], [severity], [actions],
/// verdict}</c>; <c>env</c> is never printed. On Claude Code the CLI reads only <c>mcpServers</c> in
/// <c>settings.json</c>, not the servers <c>claude mcp add</c> writes to <c>~\.claude.json</c>, so an empty list
/// is a coverage limit and the empty state says so.
/// </para>
/// <para>
/// <b>The set form</b> follows <c>mcp set --help</c>: exactly one of <c>--command</c> / <c>--url</c> (the CLI
/// rejects both, and neither), <c>--transport</c> from the two values the help names, <c>--args</c> as a JSON
/// array (the form takes one argument per line and sends the array, so commas inside an argument are safe),
/// and <c>--env</c> once per KEY=VAL line.
/// </para>
/// </summary>
public sealed partial class McpsPanelViewModel : GovernPanelViewModelBase
{
    /// <summary>The combo's "send no --transport" choice.</summary>
    public const string TransportInfer = "(let DefenseClaw infer)";

    private static readonly Regex EnvKeyPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions ArgsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [ObservableProperty] private bool _isSetFormOpen;
    [ObservableProperty] private string _setName = string.Empty;
    [ObservableProperty] private string _setCommand = string.Empty;
    [ObservableProperty] private string _setArgs = string.Empty;
    [ObservableProperty] private string _setUrl = string.Empty;
    [ObservableProperty] private string _setTransport = TransportInfer;
    [ObservableProperty] private string _setEnv = string.Empty;
    [ObservableProperty] private bool _setSkipScan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetFormError))]
    private string _setFormError = string.Empty;

    public McpsPanelViewModel(AppServices services)
        : base(services)
    {
    }

    public override string Title => "MCPs";

    public override string Description => "MCP servers configured for each connector, their scan results and enforcement decisions.";

    protected override string Noun => "mcp";

    protected override string NounLabel => "MCP server";

    protected override string NounPlural => "MCP servers";

    protected override string ItemsKey => "mcp_servers";

    public IReadOnlyList<string> TransportChoices { get; } = new[] { TransportInfer, "stdio", "sse" };

    public bool HasSetFormError => SetFormError.Length > 0;

    protected override string BuildEmptyTitle(string scope) => $"No MCP servers configured for {scope}";

    protected override string BuildEmptyDetail(string scope) =>
        "For Claude Code, DefenseClaw 0.8.10 reads the mcpServers key of settings.json. Servers added with 'claude mcp add' " +
        "are stored in ~\\.claude.json instead and are not listed here, so an empty list means nothing was found in the " +
        "file it reads. Use Add MCP server to register one through DefenseClaw (it is scanned first).";

    protected override GovernRow? ParseRow(JsonElement item, string? groupConnector)
    {
        var name = GovernJson.Str(item, "name");
        if (name is null)
        {
            return null;
        }

        var state = GovernJson.Interpret(item);

        // MCP items carry no status of their own; a server with no decision on it is simply configured.
        if (state.Status is null)
        {
            state = state with { Status = "configured" };
        }

        var connector = ResolveConnector(GovernJson.Str(item, "connector"), groupConnector);
        var transport = GovernJson.Str(item, "transport");
        var command = GovernJson.Str(item, "command");
        var args = GovernJson.JoinedArray(item, "args");
        var url = GovernJson.Str(item, "url");
        var launch = command is null ? null : (args is null ? command : command + " " + args);

        var fields = new List<GovernField>();
        AddField(fields, "Name", name);
        AddField(fields, "Connector", connector);
        AddField(fields, "Transport", transport);
        AddField(fields, "Command", launch);
        AddField(fields, "URL", url);
        AddField(fields, "Enforcement", state.ActionsText);
        AddField(fields, "Scan", state.ScanLabel);

        // No 'mcp info' exists, so the raw JSON in the details is the per-server view. Info is not offered.
        var verbs = (StandardVerbs(state, canDisable: false, canQuarantine: false) & ~GovernVerbs.Info) | GovernVerbs.Unset;

        return new GovernRow(this)
        {
            Noun = Noun,
            Name = name,
            Connector = connector,
            MetaLine = JoinMeta(("transport", transport), ("command", launch), ("url", url)),
            StateLabel = state.Label,
            StateTone = state.Tone,
            ScanLabel = state.ScanLabel,
            ScanTone = state.ScanTone,
            ActionsText = state.ActionsText,
            IsBlocked = state.Blocked,
            IsAllowed = state.Allowed,
            IsQuarantined = state.Quarantined,
            IsDisabled = state.Disabled,
            NeedsAttention = state.NeedsAttention,
            RawJson = GovernJson.Pretty(item),
            Fields = fields,
            Verbs = verbs,
        };
    }

    protected override string? NoteFor(GovernVerbs verb, GovernRow row) => verb switch
    {
        GovernVerbs.Block or GovernVerbs.Allow =>
            "In DefenseClaw 0.8.10 this decision gates its own 'mcp set' and 'mcp scan'; it does not by itself stop a server that is already configured.",
        GovernVerbs.Unset =>
            "Edits the connector's MCP config file (for Claude Code, settings.json) and removes the server there. Add it back with Add MCP server.",
        _ => base.NoteFor(verb, row),
    };

    // ---- Add / update an MCP server (mcp set) --------------------------------------------------------------------

    [RelayCommand]
    private void ToggleSetForm()
    {
        IsSetFormOpen = !IsSetFormOpen;
        SetFormError = string.Empty;
    }

    protected override bool CloseTransientUi()
    {
        if (!IsSetFormOpen)
        {
            return false;
        }

        IsSetFormOpen = false;
        SetFormError = string.Empty;
        return true;
    }

    [RelayCommand]
    private void SubmitSetForm()
    {
        var name = SetName.Trim();
        var command = SetCommand.Trim();
        var url = SetUrl.Trim();
        var transport = SetTransport == TransportInfer ? string.Empty : SetTransport.Trim();

        if (name.Length == 0)
        {
            SetFormError = "Enter a server name.";
            return;
        }

        // 'mcp set' refuses both and neither: a mixed entry would scan one thing and run another.
        if (command.Length == 0 && url.Length == 0)
        {
            SetFormError = "Enter either a command (a local server) or a URL (a remote server).";
            return;
        }

        if (command.Length > 0 && url.Length > 0)
        {
            SetFormError = "Enter a command or a URL, not both: the scan and the running server would differ.";
            return;
        }

        if (url.Length > 0 && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            SetFormError = "The URL must be an absolute http:// or https:// address.";
            return;
        }

        if (transport == "stdio" && url.Length > 0)
        {
            SetFormError = "The stdio transport runs a local command; clear the URL or choose sse.";
            return;
        }

        if (transport == "sse" && command.Length > 0)
        {
            SetFormError = "The sse transport connects to a URL; clear the command or choose stdio.";
            return;
        }

        if (!TryBuildArgs(out var argsJson, out var argsError))
        {
            SetFormError = argsError;
            return;
        }

        if (argsJson is not null && command.Length == 0)
        {
            SetFormError = "Arguments belong to a command; clear them or enter a command instead of a URL.";
            return;
        }

        if (!TryBuildEnv(out var envPairs, out var envError))
        {
            SetFormError = envError;
            return;
        }

        SetFormError = string.Empty;

        var options = new List<string>();
        if (command.Length > 0)
        {
            options.Add("--command");
            options.Add(command);
        }

        if (argsJson is not null)
        {
            options.Add("--args");
            options.Add(argsJson);
        }

        if (url.Length > 0)
        {
            options.Add("--url");
            options.Add(url);
        }

        if (transport.Length > 0)
        {
            options.Add("--transport");
            options.Add(transport);
        }

        foreach (var pair in envPairs)
        {
            options.Add("--env");
            options.Add(pair);
        }

        if (SetSkipScan)
        {
            options.Add("--skip-scan");
        }

        var connector = ToolbarConnector();
        if (connector is not null)
        {
            options.Add("--connector");
            options.Add(connector);
        }

        var notes = new List<string>();
        notes.Add(SetSkipScan
            ? "The security scan is skipped: this server is added with no check at all."
            : "Unless the scan is skipped, DefenseClaw starts the server (runs the command, or connects to the URL) to scan it, and refuses it on HIGH or CRITICAL findings.");
        if (envPairs.Count > 0)
        {
            notes.Add("Environment values are part of this command line: they are shown here, recorded in Activity and visible to other processes on this machine. Do not put secrets in them.");
        }

        BeginReview(new GovernPlan
        {
            Heading = $"Add or update MCP server “{name}” in {ScopeText(connector)}?",
            Argv = BuildArgv(Noun, "set", options, name),
            Note = string.Join(" ", notes),
            SuccessMessage = $"Saved “{name}”.",
            OnSuccess = () => IsSetFormOpen = false,
        });
    }

    /// <summary>
    /// One argument per line becomes a JSON array (<c>--args</c> accepts a JSON array or a comma list; the array
    /// keeps commas inside an argument intact). A single line that is already a JSON array is validated and passed as is.
    /// </summary>
    private bool TryBuildArgs(out string? argsJson, out string error)
    {
        argsJson = null;
        error = string.Empty;

        var lines = SetArgs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return true;
        }

        if (lines.Length == 1 && lines[0].StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(lines[0]);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = "The arguments look like JSON but are not an array. Use [\"-y\", \"pkg\"] or one argument per line.";
                    return false;
                }

                argsJson = lines[0];
                return true;
            }
            catch (JsonException)
            {
                error = "The arguments start with “[” but are not valid JSON. Use [\"-y\", \"pkg\"] or one argument per line.";
                return false;
            }
        }

        argsJson = JsonSerializer.Serialize(lines, ArgsJson);
        return true;
    }

    /// <summary><c>--env</c> is repeatable KEY=VAL, one flag per line (a comma is legal inside a value, so it never splits).</summary>
    private bool TryBuildEnv(out List<string> pairs, out string error)
    {
        pairs = new List<string>();
        error = string.Empty;

        foreach (var entry in SetEnv.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !EnvKeyPattern.IsMatch(entry[..eq]))
            {
                error = $"“{entry}” is not KEY=VAL. Put one KEY=VAL per line; the key may use letters, digits and underscores.";
                return false;
            }

            pairs.Add(entry);
        }

        return true;
    }
}
