using System.Text;

namespace DefenseClaw.Core.Audit;

/// <summary>
/// What kind of thing an audit event is about, decided from its <em>action</em> as the 0.8.10 TUI's Audit panel does (<c>_target_type_from_action</c>): its
/// TYPE column, its <c>type:</c> search token, and the key of its "Current State" row (<c>get_action(target_type, target)</c>). <c>skill-block</c> is about a
/// <c>skill</c>, <c>mcp-unset</c> about an <c>mcp</c>, <c>scan-finding</c> about a <c>scan</c>; an action that names none of them is about nothing in particular
/// (the empty string).
/// <para>
/// <b>The first rule that matches wins</b>, in the TUI's order: <c>skill</c>, <c>mcp</c>, <c>plugin</c>, <c>tool</c>, <c>scan</c> (also <c>finding</c>),
/// <c>credential</c> (also <c>key</c>, <c>token</c>, <c>secret</c>), <c>alert</c>, <c>config</c> (also <c>setup</c>, <c>init</c>); so <c>scan-skill</c> is a
/// <c>skill</c> event. The words are matched as case-insensitive substrings of the action. <see cref="Rules"/> is the one table: <see cref="FromAction"/>
/// reads it, and so does <see cref="SqlCase"/>, which writes the same decision as SQL (a fixed text built from the table, never from anything typed) so a
/// <c>type:</c> search can be answered by the database.
/// </para>
/// </summary>
public static class AuditTargetType
{
    /// <summary>The kinds of target the Govern panels list and the <c>actions</c> table holds an enforcement state for.</summary>
    public static IReadOnlyList<string> Governed { get; } = new[] { "skill", "mcp", "plugin", "tool" };

    /// <summary>The TUI's table, in its order: the type, and the words of an action that make it that type.</summary>
    internal static IReadOnlyList<(string Type, string[] Words)> Rules { get; } = new (string, string[])[]
    {
        ("skill", new[] { "skill" }),
        ("mcp", new[] { "mcp" }),
        ("plugin", new[] { "plugin" }),
        ("tool", new[] { "tool" }),
        ("scan", new[] { "scan", "finding" }),
        ("credential", new[] { "credential", "key", "token", "secret" }),
        ("alert", new[] { "alert" }),
        ("config", new[] { "config", "setup", "init" }),
    };

    /// <summary>The type of what <paramref name="action"/> is about; the empty string when it names nothing.</summary>
    public static string FromAction(string? action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return string.Empty;
        }

        foreach (var (type, words) in Rules)
        {
            foreach (var word in words)
            {
                if (action.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>True for a type the Govern panels manage (skill, mcp, plugin, tool): the ones whose events have a current state to look up.</summary>
    public static bool IsGoverned(string? type)
    {
        foreach (var governed in Governed)
        {
            if (string.Equals(governed, type, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see cref="FromAction"/> as an SQL expression over <paramref name="actionColumn"/> (a column name the caller owns, such as <c>e.action</c>). The
    /// text is built from <see cref="Rules"/> alone: its only variable part is the column, and the user's text is compared with its result as a bound
    /// parameter by the caller.
    /// </summary>
    internal static string SqlCase(string actionColumn)
    {
        var sql = new StringBuilder("(CASE");
        foreach (var (type, words) in Rules)
        {
            _ = sql.Append(" WHEN ");
            for (var i = 0; i < words.Length; i++)
            {
                _ = sql.Append(i == 0 ? string.Empty : " OR ").Append(actionColumn).Append(" LIKE '%").Append(words[i]).Append("%'");
            }

            _ = sql.Append(" THEN '").Append(type).Append('\'');
        }

        return sql.Append(" ELSE '' END)").ToString();
    }
}
