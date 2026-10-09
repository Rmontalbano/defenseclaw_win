using DefenseClaw.App.Services.Settings;

namespace DefenseClaw.App.Services;

/// <summary>
/// The command palette's memory of what it ran (CUST-264): the ids of the last <see cref="PaletteSettings.MaxRecent"/> commands,
/// newest first, which the palette lists ahead of everything else while its search is empty. The list lives in
/// <c>settings.json</c> (<see cref="AppSettings.Palette"/>), so it is there again after a restart. This type is the pure part:
/// what is worth remembering, how a new id joins the list, and how the list reorders the rows.
/// </summary>
internal static class PaletteRecents
{
    /// <summary>
    /// The rows that act on the Activity list itself - "Re-run last command" and "Cancel running command" - are never remembered: they are
    /// always one keystroke away at a fixed place, and remembering the first would make the palette's top row "run the last command again" for
    /// good, in front of the commands the operator actually ran.
    /// </summary>
    public static bool IsWorthRemembering(ShellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Id is { Length: > 0 } id &&
               !string.Equals(id, ShellCommandRegistry.RerunLastId, StringComparison.Ordinal) &&
               !string.Equals(id, ShellCommandRegistry.CancelRunningId, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="current"/> with <paramref name="id"/> at the front: moved there if it was in the list already, the oldest dropped to stay
    /// within the limit. A blank id changes nothing.
    /// </summary>
    public static IReadOnlyList<string> Push(IReadOnlyList<string> current, string id)
    {
        ArgumentNullException.ThrowIfNull(current);

        return string.IsNullOrWhiteSpace(id)
            ? current
            : PaletteSettings.Clean(current.Prepend(id.Trim()));
    }

    /// <summary>
    /// <paramref name="commands"/> with the remembered ones first, in the order they were remembered (newest first), then every other row in
    /// the order it came in. A remembered id with no row any more (the runtime's registry changed) is left out. A row that cannot be chosen
    /// now is still a recent one and is listed with them, greyed out, with the reason it gives in place of its description - the installation
    /// turned read-only since it was run (CUST-308), Docker or Terraform went away, the gateway is not running - so the operator is told why
    /// the command they ran last time is off, not left to look for it. The palette selects the first row that <i>can</i> be chosen, so a row
    /// that cannot never takes Enter from one that can. No row appears twice.
    /// </summary>
    /// <param name="promoted">How many rows were moved to the front: the first <paramref name="promoted"/> rows of the result are the recent ones.</param>
    public static IReadOnlyList<ShellCommand> Promote(IReadOnlyList<ShellCommand> commands, IReadOnlyList<string> recentIds, out int promoted)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(recentIds);

        var moved = new List<int>(recentIds.Count);
        foreach (var id in recentIds)
        {
            for (var i = 0; i < commands.Count; i++)
            {
                if (string.Equals(commands[i].Id, id, StringComparison.Ordinal))
                {
                    if (!moved.Contains(i))
                    {
                        moved.Add(i);
                    }

                    break;
                }
            }
        }

        promoted = moved.Count;
        if (moved.Count == 0)
        {
            return commands;
        }

        var ordered = new List<ShellCommand>(commands.Count);
        ordered.AddRange(moved.Select(i => commands[i]));
        ordered.AddRange(commands.Where((_, i) => !moved.Contains(i)));
        return ordered;
    }
}
