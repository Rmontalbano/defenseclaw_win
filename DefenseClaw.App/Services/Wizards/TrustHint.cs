using DefenseClaw.Core.Setup;

namespace DefenseClaw.App.Services.Wizards;

/// <summary>
/// The folder a failed connector setup asks the operator to trust (CUST-270). When action-mode setup cannot probe a connector's program because it
/// sits outside a trusted install prefix, 0.8.10 ends its output with these two lines (<c>cmd_setup._emit_untrusted_prefix_setup_hints</c>;
/// <c>agent discover</c> prints the second for every such folder):
/// <code>
///   Binary resolves to: C:\Users\...\claude.exe
///   Trust it with: defenseclaw setup trusted-paths add C:\Users\...
/// </code>
/// The folder is the rest of the second line, unquoted, so it can hold spaces. The TUI does not read it from the failed run: it checks the last
/// discovery scan before it starts the setup and opens the trusted-folder editor with the folder filled in (<c>_route_untrusted_binary_to_panel</c>).
/// This app has no read-only scan to check with, so it offers the same step after the run, from what the run printed: a button on the wizard's result
/// bar that opens the same editor with the same folder filled in.
/// </summary>
/// <param name="Directory">The folder to trust, as the CLI printed it.</param>
internal sealed record TrustHint(string Directory)
{
    private const string Marker = "Trust it with: defenseclaw setup trusted-paths add ";

    /// <summary>
    /// The first line of <paramref name="lines"/> that names a folder to trust, or null. A line that is not that sentence, and a folder that is not
    /// one the editors suggest (<see cref="TrustedFolders.IsOffered"/>: an absolute path, not read as an option, with no control or direction
    /// character in it) are not offered: what goes into the editor's box is what the operator would have had to type, so it has to be something they
    /// would have typed.
    /// </summary>
    public static TrustHint? Find(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (var raw in lines)
        {
            var line = raw?.Trim();
            if (line is null || !line.StartsWith(Marker, StringComparison.Ordinal))
            {
                continue;
            }

            var directory = line[Marker.Length..].Trim();
            if (TrustedFolders.IsOffered(directory))
            {
                return new TrustHint(directory);
            }
        }

        return null;
    }
}
