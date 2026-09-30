using System.Text;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// Turns an executable and an argv into text that is safe to paste into a PowerShell prompt: every argument
/// arrives as exactly one literal string, whatever it contains.
/// <para>
/// <b>Why.</b> The runner hands arguments to the process as a list, so no shell ever interprets them - but a
/// <i>copied</i> command line is run by whatever shell the operator pastes into. A skill or server name comes
/// from outside (a folder name, a config key), and <c>x&amp;calc</c>, <c>x;calc</c>, <c>x$(calc)</c> or
/// <c>a `whoami` b</c> pasted as-is are commands, not a name. The display form the app shows
/// (<see cref="CliInvocation.CommandLine"/>) quotes only whitespace and stays readable; this is the form that
/// goes to the clipboard.
/// </para>
/// <para>
/// <b>Rule.</b> An argument made only of <c>A-Z a-z 0-9 _ . / : = + -</c> is left bare (it is one word to PowerShell
/// and to every other shell); anything else - including the empty string - is single-quoted with the quote
/// doubled, the one escape a single-quoted string has. <c>,</c> and <c>@</c> are quoted although they are common in
/// paths and emails: a bare <c>@name</c> is splatting and would silently drop the argument. An executable that
/// needed quoting (a path with a backslash or a space) is called with <c>&amp;</c>, the only way to run a quoted
/// path.
/// </para>
/// </summary>
public static class PowerShellQuoting
{
    /// <summary>True when <paramref name="argument"/> can be pasted bare: one non-empty word of the safe alphabet.</summary>
    public static bool IsBare(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length == 0)
        {
            return false;
        }

        foreach (var c in argument)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or ':' or '=' or '+' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <paramref name="value"/> between single quotes with each embedded quote doubled. PowerShell also treats the
    /// typographic single quotes (U+2018, U+2019, U+201A, U+201B) as quote characters, so each of those is doubled
    /// too - otherwise a folder named with an apostrophe would end the string early. Nothing else is special inside
    /// single quotes: <c>$</c>, backtick, <c>&amp;</c> and <c>;</c> stay literal.
    /// </summary>
    public static string SingleQuoted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            builder.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>One argument, bare when it can be and <c>'single-quoted'</c> when it cannot.</summary>
    public static string Argument(string argument) =>
        IsBare(argument) ? argument : "'" + SingleQuoted(argument) + "'";

    /// <summary>
    /// The executable followed by each argument, ready to paste. A quoted executable is prefixed with the call
    /// operator: <c>&amp; 'C:\...\defenseclaw.exe' skill quarantine -- 'x&amp;calc'</c>.
    /// </summary>
    public static string CommandLine(string executable, IEnumerable<string> argv)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(argv);

        var program = Argument(executable);
        var builder = new StringBuilder();
        if (!IsBare(executable))
        {
            builder.Append("& ");
        }

        builder.Append(program);
        foreach (var argument in argv)
        {
            builder.Append(' ').Append(Argument(argument));
        }

        return builder.ToString();
    }
}
