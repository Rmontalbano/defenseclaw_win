namespace DefenseClaw.Core.Runtime;

/// <summary>The one-line texts About and the Updates window show for a probe result. Pure.</summary>
public static class RuntimeSummary
{
    /// <summary>"defenseclaw-cli 1.0.0 (C:\...\defenseclaw.exe)", or the reason nothing is known.</summary>
    public static string Identity(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Identity is { } identity
            ? $"{identity.Display} ({identity.Source})"
            : snapshot.UnknownReason ?? "Not detected";
    }

    /// <summary>
    /// "Policy model, ACP guard, ..." for a runtime that has newer features; "No features beyond the installed 0.8.10 baseline" for
    /// one that has none; "Features unknown until the runtime answers" when nothing is known.
    /// </summary>
    public static string Features(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsKnown)
        {
            return "Features unknown until the runtime answers";
        }

        var present = snapshot.Capabilities.Present.Select(RuntimeCapabilityCatalog.DisplayName).ToArray();
        return present.Length == 0 ? "No features beyond the 0.8.10 baseline" : string.Join(", ", present);
    }
}
