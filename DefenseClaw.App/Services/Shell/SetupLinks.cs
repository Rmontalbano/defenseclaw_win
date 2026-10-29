namespace DefenseClaw.App.Services;

/// <summary>
/// Payload for <c>setup</c> (CUST-385): bring the Setup panel's inline config editor into view. Nothing is loaded, saved or run by it; the editor shows the
/// draft it already holds. The shell's "Open config editor" (<c>ShellActions.OpenConfigEditor</c>) opens the same draft in its own window instead.
/// </summary>
public sealed record ConfigEditorFocus
{
    /// <summary>The one value the payload needs.</summary>
    public static ConfigEditorFocus Instance { get; } = new();
}

/// <summary>
/// Payload for <c>ai-discovery</c> (CUST-385, the Setup panel's AI Discovery tile): open the panel's tuning dialog (mode, cadence, scope and sources over
/// <c>agent discovery enable</c>) on what config.yaml says now. Nothing is run by it; the dialog reviews every change.
/// </summary>
public sealed record AiDiscoveryTune
{
    /// <summary>The one value the payload needs.</summary>
    public static AiDiscoveryTune Instance { get; } = new();
}
