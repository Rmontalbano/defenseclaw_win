using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

public sealed partial class AiDiscoveryPanelViewModel
{
    /// <summary>
    /// Takes an <see cref="AiDiscoveryScan"/> (the palette's and Ctrl+Shift+A's "Scan AI components") and opens the scan's review
    /// dialog, the same one the panel's button opens: nothing runs until the operator confirms it. An <see cref="AiDiscoveryTune"/> (the Setup panel's
    /// AI Discovery tile, CUST-385) opens the tuning dialog, as Tune… does, unless another command of this panel is open or running. Any other payload is ignored.
    /// </summary>
    public void Accept(object payload)
    {
        if (payload is AiDiscoveryScan)
        {
            RunScan();
        }
        else if (payload is AiDiscoveryTune && CanOpenTuning())
        {
            Tuning.Open();
        }
    }
}
