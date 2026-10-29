using System.Globalization;
using DefenseClaw.Core.Gateway.Models;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// The two rows of the Scanners card that the Mac's card has and this one lacked (CUST-389; the Mac's <c>ScannerProbe.statuses</c>, v1.1.34): <b>aibom</b>,
/// a built-in, and <b>keys</b>, the required credentials the last doctor run found missing. Neither reads anything new. The first states a fact; the second reads
/// the doctor cache the Doctor card already holds (<c>_doctorSnapshot</c>, <see cref="DoctorReconciliation.MissingRequiredCredentials"/>, the same list the
/// attention rows and the "Fill missing keys" action use), so nothing runs on Overview load. The Mac's "guardrail" row is the Services card and the
/// Guardrail enforcement tile here, and its "coverage" row (a scan on page open) is the AI BOM coverage card, which only reads the last AI BOM.
/// </summary>
public sealed partial class OverviewPanelViewModel
{
    /// <summary>How many missing keys the keys row names before it says "(+n more)", as the Mac's does.</summary>
    internal const int KeysNamed = 2;

    /// <summary><c>aibom</c> ships in the defenseclaw CLI like <c>codeguard</c> ships in the gateway: always there, nothing to probe.</summary>
    private static ScannerRow AibomRow() => new()
    {
        Name = "aibom",
        StateText = "built-in",
        StateKey = "Ok",
        Detail = "Ships in the defenseclaw CLI (aibom scan); nothing to install. Inventory generates it.",
    };

    /// <summary>
    /// The required keys, from the doctor cache: "all required set", "N missing" with the first names, or "not checked" while there is no cache
    /// (or one with no results) - the Mac's three outcomes, with its amber for the last two.
    /// </summary>
    internal static ScannerRow KeysRow(DoctorCacheSnapshot? doctor)
    {
        if (doctor is null || (doctor.Checks.Count == 0 && doctor.Passed + doctor.Failed + doctor.Warned + doctor.Skipped == 0))
        {
            return new ScannerRow
            {
                Name = "keys",
                StateText = "not checked",
                StateKey = "Warn",
                Detail = "Run defenseclaw doctor to check the required keys.",
            };
        }

        var missing = DoctorReconciliation.MissingRequiredCredentials(doctor);
        if (missing.Count == 0)
        {
            return new ScannerRow
            {
                Name = "keys",
                StateText = "all required set",
                StateKey = "Ok",
                Detail = "From the last doctor run.",
            };
        }

        var more = missing.Count > KeysNamed ? string.Create(CultureInfo.InvariantCulture, $" (+{missing.Count - KeysNamed} more)") : string.Empty;
        return new ScannerRow
        {
            Name = "keys",
            StateText = string.Create(CultureInfo.InvariantCulture, $"{missing.Count} missing"),
            StateKey = "Warn",
            Detail = string.Join(", ", missing.Take(KeysNamed)) + more + ": doctor found them unset. Fill missing keys sets them.",
        };
    }
}
