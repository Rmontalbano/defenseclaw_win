using System.Text.RegularExpressions;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.ViewModels;

/// <summary>The Logs panel's side of the hook-row labels (CUST-449): which labels the runtime has earned, and the <c>surface:cowork</c> search token.</summary>
public sealed partial class LogsPanelViewModel
{
    private static readonly Regex SurfaceToken = new(@"(?<!\S)surface:cowork(?!\S)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private bool _coworkOnly;

    private HookRowGate HookGate => HookRowGate.From(Services.Runtime.Capabilities);

    /// <summary>
    /// The filter text with a <c>surface:cowork</c> word taken out, and whether it was there. The word is honoured only on a runtime that knows Cowork
    /// (<see cref="RuntimeCapability.CoworkProtection"/>); anywhere else it stays free text, as any unknown token does. Cowork is a surface of the
    /// claudecode connector, never a connector, so this is no connector token and no roster changes.
    /// </summary>
    private (string Text, bool CoworkOnly) SplitSurfaceToken(string? text)
    {
        if (string.IsNullOrEmpty(text) || !HookGate.Cowork)
        {
            return (text ?? string.Empty, false);
        }

        try
        {
            return SurfaceToken.IsMatch(text) ? (SurfaceToken.Replace(text, string.Empty), true) : (text, false);
        }
        catch (RegexMatchTimeoutException)
        {
            return (text, false);
        }
    }
}
