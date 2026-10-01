using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

public sealed partial class AiDiscoveryPanelViewModel
{
    /// <summary>
    /// Takes an <see cref="AiDiscoveryScan"/> (the palette's and Ctrl+Shift+A's "Scan AI components") and opens the scan's review
    /// dialog, the same one the panel's button opens: nothing runs until the operator confirms it. Any other payload is ignored.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is AiDiscoveryScan)
        {
            RunScan();
        }
    }
}
