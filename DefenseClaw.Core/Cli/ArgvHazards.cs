using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DefenseClaw.Core.Cli;

/// <summary>
/// One argument the DefenseClaw CLI would rewrite before it looks at it, and what it would rewrite it to.
/// </summary>
/// <param name="Index">Position in the argv (0-based, without the executable).</param>
/// <param name="Token">The argument as this app hands it over.</param>
/// <param name="Expansion">
/// What the CLI's own argv expansion turns it into: usually one string, several when a wildcard matches
/// several files, and just <paramref name="Token"/> again when nothing changes.
/// </param>
public sealed record ArgvHazard(int Index, string Token, IReadOnlyList<string> Expansion)
{
    /// <summary>True when the CLI would see something other than <see cref="Token"/>.</summary>
    public bool Changes => Expansion.Count != 1 || !string.Equals(Expansion[0], Token, StringComparison.Ordinal);

    /// <summary>"“x*” would arrive as “xa”" - the sentence a review or a refusal uses.</summary>
    public string Describe()
    {
        var arrives = Expansion.Count switch
        {
            0 => "nothing",
            1 => $"“{Expansion[0]}”",
            _ => string.Join(", ", Expansion.Take(3).Select(e => $"“{e}”")) +
                 (Expansion.Count > 3 ? $" and {Expansion.Count - 3} more" : string.Empty) +
                 $" ({Expansion.Count} arguments)",
        };

        return $"“{Token}” would arrive as {arrives}";
    }
}

/// <summary>
/// Finds the arguments the DefenseClaw CLI re-expands on Windows, so what an operator reviews is what runs.
/// <para>
/// <b>Why it exists.</b> The CLI is a Click program, and Click on Windows (<c>windows_expand_args</c>, on by
/// default) runs <c>os.path.expanduser</c>, <c>os.path.expandvars</c> and <c>glob.glob</c> over every element
/// of <c>sys.argv</c> — including targets after <c>--</c> and option values. None of that happens in this
/// app's <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, so <c>skill block -- config.y?ml</c>
/// is reviewed as one name and executed as another (<c>config.yaml</c>), and <c>%USERNAME%</c>, <c>$HOME</c>
/// and <c>~</c> silently become the operator's own values. Measured on 0.8.10 (Click 8.4.2).
/// </para>
/// <para>
/// <b>Cheap to ask.</b> <see cref="IsHazardous"/> is the syntactic test (<c>* ? [ % $</c>, or a leading
/// <c>~</c>); only a token that passes it is expanded, in this process, the way Click would
/// (<see cref="Expand"/>: expanduser, expandvars, then a glob against <c>workingDirectory</c>). The glob
/// reads directories, so it is skipped for UNC and device paths — a dead network share must never stall a
/// review — and a token that only <i>could</i> expand (a wildcard nothing matches) is not reported by
/// <see cref="FindChanges"/>.
/// </para>
/// <para>
/// This describes the Python CLI only. The Go gateway does not expand its arguments, so callers do not ask
/// about <c>defenseclaw-gateway</c>; see <see cref="AppliesTo"/>.
/// </para>
/// </summary>
public static class ArgvHazards
{
    /// <summary>
    /// True when the CLI behind <paramref name="executable"/> (a path or a bare name) expands its argv: the
    /// Python <c>defenseclaw</c> command. The Go gateway and installers do not.
    /// </summary>
    public static bool AppliesTo(string executable) =>
        string.Equals(Path.GetFileNameWithoutExtension(executable), "defenseclaw", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The syntactic test: contains a glob character (<c>* ? [</c>), a variable marker (<c>% $</c>), or starts
    /// with <c>~</c>. Necessary, not sufficient — most such tokens (a reason ending in <c>?</c>, a price with
    /// <c>$</c>) expand to themselves.
    /// </summary>
    public static bool IsHazardous(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Length > 0 && (token[0] == '~' || token.AsSpan().IndexOfAny("*?[%$") >= 0);
    }

    /// <summary>
    /// Every hazardous token in <paramref name="argv"/> with what the CLI would turn it into — including the
    /// ones that expand to themselves. Tokens after <c>--</c> are included: Click expands them too.
    /// </summary>
    /// <param name="argv">The arguments as handed to the CLI, without the executable.</param>
    /// <param name="workingDirectory">
    /// The directory the child will run in, which relative wildcards match against. <c>null</c> uses this
    /// process's current directory.
    /// </param>
    /// <param name="environment">
    /// Environment lookup for <c>%VAR%</c>/<c>$VAR</c> (null when unset). <c>null</c> uses the process
    /// environment. Windows names are case-insensitive.
    /// </param>
    public static IReadOnlyList<ArgvHazard> Find(
        IReadOnlyList<string> argv,
        string? workingDirectory = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(argv);

        List<ArgvHazard>? found = null;
        for (var i = 0; i < argv.Count; i++)
        {
            var token = argv[i];
            if (token is null || !IsHazardous(token))
            {
                continue;
            }

            (found ??= new List<ArgvHazard>()).Add(new ArgvHazard(i, token, Expand(token, workingDirectory, environment)));
        }

        return found ?? (IReadOnlyList<ArgvHazard>)Array.Empty<ArgvHazard>();
    }

    /// <summary>The subset of <see cref="Find"/> whose expansion is not the token itself: the reviewed argv is not the executed one.</summary>
    public static IReadOnlyList<ArgvHazard> FindChanges(
        IReadOnlyList<string> argv,
        string? workingDirectory = null,
        Func<string, string?>? environment = null) =>
        Find(argv, workingDirectory, environment).Where(h => h.Changes).ToArray();

    /// <summary>
    /// The subset of <see cref="FindChanges"/> that sits after the <c>--</c> terminator: the <b>targets</b>, names that
    /// came from outside (a skill folder, an MCP server key). Nothing before the terminator is a target, and an argv
    /// without one has none.
    /// </summary>
    public static IReadOnlyList<ArgvHazard> FindChangedTargets(
        IReadOnlyList<string> argv,
        string? workingDirectory = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(argv);

        var terminator = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], "--", StringComparison.Ordinal))
            {
                terminator = i;
                break;
            }
        }

        return terminator < 0
            ? Array.Empty<ArgvHazard>()
            : FindChanges(argv, workingDirectory, environment).Where(h => h.Index > terminator).ToArray();
    }

    /// <summary>
    /// What Click's <c>_expand_args</c> makes of one argument on Windows: <c>expanduser</c>, then
    /// <c>expandvars</c>, then <c>glob</c> (recursive) — the matches replace the argument when there are any.
    /// </summary>
    public static IReadOnlyList<string> Expand(string token, string? workingDirectory = null, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(token);

        var lookup = environment ?? ProcessEnvironment;
        var expanded = ExpandVars(ExpandUser(token, lookup), lookup);
        var matches = PythonGlob.Glob(expanded, workingDirectory ?? Environment.CurrentDirectory);
        return matches.Count == 0 ? new[] { expanded } : matches;
    }

    private static string? ProcessEnvironment(string name)
    {
        try
        {
            return Environment.GetEnvironmentVariable(name);
        }
        catch (ArgumentException)
        {
            // A name the platform will not look up (an embedded NUL) is a variable that is not set.
            return null;
        }
    }

    // ---- os.path.expanduser (ntpath, Python 3.13) --------------------------------------------------------------

    private static string ExpandUser(string path, Func<string, string?> env)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var i = 1;
        while (i < path.Length && path[i] is not ('\\' or '/'))
        {
            i++;
        }

        string userHome;
        if (env("USERPROFILE") is { } profile)
        {
            userHome = profile;
        }
        else if (env("HOMEPATH") is { } homePath)
        {
            userHome = (env("HOMEDRIVE") ?? string.Empty) + homePath;
        }
        else
        {
            return path;
        }

        if (i != 1)
        {
            // "~user": only the current user's name, or a sibling profile folder when the home directory is
            // a normal profile folder named after the current user.
            var targetUser = path[1..i];
            var currentUser = env("USERNAME");
            if (!string.Equals(targetUser, currentUser, StringComparison.Ordinal))
            {
                var (head, tail) = SplitLast(userHome);
                if (!string.Equals(currentUser, tail, StringComparison.Ordinal))
                {
                    return path;
                }

                userHome = JoinPath(head, targetUser);
            }
        }

        return userHome + path[i..];
    }

    // ---- os.path.expandvars (ntpath, Python 3.13) --------------------------------------------------------------

    private static string ExpandVars(string path, Func<string, string?> env)
    {
        if (path.IndexOf('$') < 0 && path.IndexOf('%') < 0)
        {
            return path;
        }

        // A line-for-line port: Python re-slices `path` as it consumes a marker, and so does this.
        var res = new StringBuilder(path.Length);
        var index = 0;
        var pathLen = path.Length;
        while (index < pathLen)
        {
            var c = path[index];
            if (c == '\'')
            {
                // No expansion inside single quotes; the quotes stay.
                path = path[(index + 1)..];
                pathLen = path.Length;
                var close = path.IndexOf('\'');
                if (close >= 0)
                {
                    index = close;
                    res.Append('\'').Append(path, 0, index + 1);
                }
                else
                {
                    res.Append('\'').Append(path);
                    index = pathLen - 1;
                }
            }
            else if (c == '%')
            {
                if (index + 1 < path.Length && path[index + 1] == '%')
                {
                    res.Append(c);
                    index++;
                }
                else
                {
                    path = path[(index + 1)..];
                    pathLen = path.Length;
                    var close = path.IndexOf('%');
                    if (close < 0)
                    {
                        res.Append('%').Append(path);
                        index = pathLen - 1;
                    }
                    else
                    {
                        index = close;
                        var name = path[..index];
                        res.Append(env(name) ?? "%" + name + "%");
                    }
                }
            }
            else if (c == '$')
            {
                var next = index + 1 < path.Length ? path[index + 1] : '\0';
                if (next == '$')
                {
                    res.Append(c);
                    index++;
                }
                else if (next == '{')
                {
                    path = path[(index + 2)..];
                    pathLen = path.Length;
                    var close = path.IndexOf('}');
                    if (close < 0)
                    {
                        res.Append("${").Append(path);
                        index = pathLen - 1;
                    }
                    else
                    {
                        index = close;
                        var name = path[..index];
                        res.Append(env(name) ?? "${" + name + "}");
                    }
                }
                else
                {
                    var name = new StringBuilder();
                    index++;
                    while (index < path.Length && IsVarChar(path[index]))
                    {
                        name.Append(path[index]);
                        index++;
                    }

                    res.Append(name.Length > 0 && env(name.ToString()) is { } value ? value : "$" + name);
                    if (index < path.Length)
                    {
                        index--;
                    }
                }
            }
            else
            {
                res.Append(c);
            }

            index++;
        }

        return res.ToString();

        static bool IsVarChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
    }

    // ---- ntpath helpers ---------------------------------------------------------------------------------------

    private static bool IsSep(char c) => c is '\\' or '/';

    /// <summary>ntpath.splitdrive: <c>C:</c> or <c>\\server\share</c> up front, the rest after.</summary>
    internal static (string Drive, string Remainder) SplitDrive(string path)
    {
        if (path.Length >= 2 && IsSep(path[0]) && IsSep(path[1]) && (path.Length < 3 || !IsSep(path[2])))
        {
            // UNC: the drive is \\server\share (the first two components), or the whole thing if there are fewer.
            var firstSep = IndexOfSep(path, 2);
            if (firstSep < 0)
            {
                return (path, string.Empty);
            }

            var secondSep = IndexOfSep(path, firstSep + 1);
            return secondSep < 0 ? (path, string.Empty) : (path[..secondSep], path[secondSep..]);
        }

        return path.Length >= 2 && path[1] == ':' ? (path[..2], path[2..]) : (string.Empty, path);
    }

    private static int IndexOfSep(string path, int start)
    {
        for (var i = start; i < path.Length; i++)
        {
            if (IsSep(path[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>ntpath.split: everything up to the last separator (trailing separators trimmed unless that is all there is), and the rest.</summary>
    internal static (string Head, string Tail) SplitLast(string path)
    {
        var (drive, rest) = SplitDrive(path);
        var i = rest.Length;
        while (i > 0 && !IsSep(rest[i - 1]))
        {
            i--;
        }

        var head = rest[..i];
        var tail = rest[i..];
        var trimmed = head.TrimEnd('\\', '/');
        return (drive + (trimmed.Length > 0 ? trimmed : head), tail);
    }

    /// <summary>ntpath.join for two parts: an absolute second part wins, otherwise the two are joined with one separator.</summary>
    internal static string JoinPath(string a, string b)
    {
        if (a.Length == 0)
        {
            return b;
        }

        var (bDrive, bRest) = SplitDrive(b);
        if (bRest.Length > 0 && IsSep(bRest[0]))
        {
            var (aDrive, _) = SplitDrive(a);
            if (bDrive.Length > 0 || aDrive.Length == 0)
            {
                return b;
            }

            return aDrive + bRest;
        }

        if (bDrive.Length > 0)
        {
            var (aDrive, aRest) = SplitDrive(a);
            if (!string.Equals(aDrive, bDrive, StringComparison.OrdinalIgnoreCase))
            {
                return b;
            }

            a = aDrive + aRest;
            b = bRest;
        }

        return a.Length > 0 && !IsSep(a[^1]) && a[^1] != ':' ? a + "\\" + b : a + b;
    }

    // ---- glob.glob(recursive=True) with fnmatch on Windows (Python 3.13) ---------------------------------------

    /// <summary>
    /// The part of Python's <c>glob</c> Click relies on: <c>*</c>, <c>?</c>, <c>[seq]</c> and <c>**</c>, matched
    /// case-insensitively, with hidden (dot) names skipped unless the pattern names them. Reads the real file
    /// system; anything that cannot be read is simply not a match, as in Python.
    /// </summary>
    internal static class PythonGlob
    {
        /// <summary>
        /// How long one <see cref="Glob"/> may keep listing directories. Python has no such limit, but a pattern such
        /// as <c>C:\**</c> would walk a whole drive on the thread that is building a review; past this the walk stops and
        /// what was found so far is what is reported.
        /// </summary>
        private static readonly long BudgetTicks = Stopwatch.Frequency / 2;

        [ThreadStatic]
        private static long t_deadline;

        private static bool OutOfTime => t_deadline != 0 && Stopwatch.GetTimestamp() > t_deadline;

        public static IReadOnlyList<string> Glob(string pattern, string root)
        {
            // Wildcards in a UNC or device path would list a network share or a device namespace, which can
            // block for as long as a dead server takes to time out. A review never waits for that.
            if (pattern.Length == 0 || !HasMagic(pattern) || IsRemoteOrDevice(pattern))
            {
                return Array.Empty<string>();
            }

            List<string> results;
            t_deadline = Stopwatch.GetTimestamp() + BudgetTicks;
            try
            {
                results = IGlob(pattern, root, dironly: false).ToList();
            }
            finally
            {
                t_deadline = 0;
            }

            // glob.iglob drops the leading empty match a "**" produces.
            if (results.Count > 0 && results[0].Length == 0)
            {
                results.RemoveAt(0);
            }

            return results;
        }

        private static bool IsRemoteOrDevice(string pattern) =>
            pattern.Length >= 2 && IsSep(pattern[0]) && IsSep(pattern[1]);

        private static bool HasMagic(string s) => s.AsSpan().IndexOfAny("*?[") >= 0;

        private static bool IsHidden(string name) => name.Length > 0 && name[0] == '.';

        private static IEnumerable<string> IGlob(string pathname, string root, bool dironly)
        {
            var (dirname, basename) = SplitLast(pathname);
            if (!HasMagic(pathname))
            {
                if (basename.Length > 0)
                {
                    if (Lexists(JoinPath(root, pathname)))
                    {
                        yield return pathname;
                    }
                }
                else if (Directory.Exists(JoinPath(root, dirname)))
                {
                    yield return pathname;
                }

                yield break;
            }

            if (dirname.Length == 0)
            {
                foreach (var name in InDirectory(root, basename, dironly))
                {
                    yield return name;
                }

                yield break;
            }

            // A drive or UNC prefix is its own dirname; do not recurse into it.
            var dirs = dirname != pathname && HasMagic(dirname)
                ? IGlob(dirname, root, dironly: true)
                : new[] { dirname };

            foreach (var dir in dirs)
            {
                foreach (var name in InDirectory(JoinPath(root, dir), basename, dironly))
                {
                    yield return JoinPath(dir, name);
                }
            }
        }

        private static IEnumerable<string> InDirectory(string directory, string basename, bool dironly)
        {
            if (!HasMagic(basename))
            {
                // glob0: a literal last component.
                if (basename.Length > 0 ? Lexists(JoinPath(directory, basename)) : Directory.Exists(directory))
                {
                    yield return basename;
                }

                yield break;
            }

            if (basename == "**")
            {
                yield return string.Empty;
                foreach (var name in RecursiveList(directory, dironly))
                {
                    yield return name;
                }

                yield break;
            }

            var names = List(directory, dironly);
            if (!IsHidden(basename))
            {
                names = names.Where(n => !IsHidden(n));
            }

            var regex = Translate(basename);
            foreach (var name in names)
            {
                if (Matches(regex, name))
                {
                    yield return name;
                }
            }
        }

        private static bool Matches(Regex regex, string name)
        {
            try
            {
                return !OutOfTime && regex.IsMatch(name);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static IEnumerable<string> RecursiveList(string directory, bool dironly)
        {
            foreach (var name in List(directory, dironly).ToList())
            {
                if (IsHidden(name))
                {
                    continue;
                }

                yield return name;
                foreach (var inner in RecursiveList(JoinPath(directory, name), dironly))
                {
                    yield return JoinPath(name, inner);
                }
            }
        }

        private static IEnumerable<string> List(string directory, bool dironly)
        {
            if (OutOfTime)
            {
                return Array.Empty<string>();
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            };

            try
            {
                var target = directory.Length == 0 ? "." : directory;
                return (dironly
                        ? Directory.EnumerateDirectories(target, "*", options)
                        : Directory.EnumerateFileSystemEntries(target, "*", options))
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Array.Empty<string>();
            }
        }

        private static bool Lexists(string path)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>fnmatch.translate: <c>*</c>, <c>?</c>, <c>[seq]</c> and <c>[!seq]</c>, anchored, case-insensitive on Windows.</summary>
        internal static Regex Translate(string pattern)
        {
            var sb = new StringBuilder("^");
            var i = 0;
            while (i < pattern.Length)
            {
                var c = pattern[i++];
                switch (c)
                {
                    case '*':
                        while (i < pattern.Length && pattern[i] == '*')
                        {
                            i++;
                        }

                        sb.Append(".*");
                        break;
                    case '?':
                        sb.Append('.');
                        break;
                    case '[':
                        var j = i;
                        if (j < pattern.Length && pattern[j] == '!')
                        {
                            j++;
                        }

                        if (j < pattern.Length && pattern[j] == ']')
                        {
                            j++;
                        }

                        while (j < pattern.Length && pattern[j] != ']')
                        {
                            j++;
                        }

                        if (j >= pattern.Length)
                        {
                            sb.Append("\\[");
                            break;
                        }

                        var stuff = pattern[i..j];
                        i = j + 1;
                        if (stuff.Length == 0)
                        {
                            sb.Append("(?!)");
                        }
                        else if (stuff == "!")
                        {
                            sb.Append('.');
                        }
                        else
                        {
                            var negate = stuff[0] == '!';
                            if (negate)
                            {
                                stuff = stuff[1..];
                            }

                            var set = new StringBuilder();
                            foreach (var ch in stuff)
                            {
                                if (ch is '\\' or '[' or ']' or '^')
                                {
                                    set.Append('\\');
                                }

                                set.Append(ch);
                            }

                            sb.Append('[').Append(negate ? "^" : string.Empty).Append(set).Append(']');
                        }

                        break;
                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            sb.Append('$');
            try
            {
                return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                // Python raises re.error for the same shapes and Click treats it as "no matches".
                return new Regex("(?!)");
            }
        }
    }
}
