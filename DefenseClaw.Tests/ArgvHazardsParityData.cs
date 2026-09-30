// Generated from click.utils._expand_args run by the Python 3.13 / Click 8.4.2 that ships with DefenseClaw 0.8.10,
// on Windows, with the environment and directory below. Each case is what Python really returned.
namespace DefenseClaw.Tests;

internal static class ArgvHazardsParityData
{
    /// <summary>The files the expected results were produced against (relative paths, created in a temp directory).</summary>
    public static readonly string[] Files =
    {
        "config.yaml",
        "config.yaml.lock",
        "Config.txt",
        ".hidden",
        ".dotfile.yaml",
        "sub/a.txt",
        "sub/b.md",
        "sub/deep/c.txt",
        "sub/deep/d.txt",
        "data1.json",
        "data2.json",
        "data10.json",
        "[weird].txt",
        "a b.txt",
        "Readme.MD"
    };

    /// <summary>The environment the expected results were produced with.</summary>
    public static readonly Dictionary<string, string> Environment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = "C:\\Users\\alice",
        ["USERNAME"] = "alice",
        ["FOO"] = "bar",
        ["BAZ"] = "q*",
        ["LONG"] = "C:\\Program Files",
        ["SYSTEMROOT"] = "C:\\Windows",
        ["SUBDIR"] = "sub",
        ["HOMEDRIVE"] = "C:",
        ["HOMEPATH"] = "\\Users\\alice"
    };

    /// <summary>(token, what Click expands it to)</summary>
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "config.y?ml", new[] { "config.yaml" } };
        yield return new object[] { "conf*", new[] { "Config.txt", "config.yaml", "config.yaml.lock" } };
        yield return new object[] { "*.json", new[] { "data1.json", "data10.json", "data2.json" } };
        yield return new object[] { "data?.json", new[] { "data1.json", "data2.json" } };
        yield return new object[] { "data??.json", new[] { "data10.json" } };
        yield return new object[] { "[cd]*.json", new[] { "data1.json", "data10.json", "data2.json" } };
        yield return new object[] { "[!c]*.json", new[] { "data1.json", "data10.json", "data2.json" } };
        yield return new object[] { "sub/*", new[] { "sub\\a.txt", "sub\\b.md", "sub\\deep" } };
        yield return new object[] { "sub\\*.txt", new[] { "sub\\a.txt" } };
        yield return new object[] { "sub/**", new[] { "sub\\", "sub\\a.txt", "sub\\b.md", "sub\\deep", "sub\\deep\\c.txt", "sub\\deep\\d.txt" } };
        yield return new object[] { "**/c.txt", new[] { "sub\\deep\\c.txt" } };
        yield return new object[] { "**", new[] { "a b.txt", "Config.txt", "config.yaml", "config.yaml.lock", "data1.json", "data10.json", "data2.json", "Readme.MD", "sub", "sub\\a.txt", "sub\\b.md", "sub\\deep", "sub\\deep\\c.txt", "sub\\deep\\d.txt", "[weird].txt" } };
        yield return new object[] { "**/*.txt", new[] { "a b.txt", "Config.txt", "[weird].txt", "sub\\a.txt", "sub\\deep\\c.txt", "sub\\deep\\d.txt" } };
        yield return new object[] { "sub/**/*.txt", new[] { "sub\\a.txt", "sub\\deep\\c.txt", "sub\\deep\\d.txt" } };
        yield return new object[] { ".*", new[] { ".dotfile.yaml", ".hidden" } };
        yield return new object[] { "*hidden", new[] { "*hidden" } };
        yield return new object[] { ".h*", new[] { ".hidden" } };
        yield return new object[] { "*.MD", new[] { "Readme.MD" } };
        yield return new object[] { "*.md", new[] { "Readme.MD" } };
        yield return new object[] { "readme.*", new[] { "Readme.MD" } };
        yield return new object[] { "~", new[] { "C:\\Users\\alice" } };
        yield return new object[] { "~/x", new[] { "C:\\Users\\alice/x" } };
        yield return new object[] { "~\\x", new[] { "C:\\Users\\alice\\x" } };
        yield return new object[] { "~alice", new[] { "C:\\Users\\alice" } };
        yield return new object[] { "~alice/docs", new[] { "C:\\Users\\alice/docs" } };
        yield return new object[] { "~bob", new[] { "C:\\Users\\bob" } };
        yield return new object[] { "~bob/docs", new[] { "C:\\Users\\bob/docs" } };
        yield return new object[] { "~/*", new[] { "C:\\Users\\alice/*" } };
        yield return new object[] { "%FOO%", new[] { "bar" } };
        yield return new object[] { "%foo%", new[] { "bar" } };
        yield return new object[] { "a%FOO%b", new[] { "abarb" } };
        yield return new object[] { "%NOPE%", new[] { "%NOPE%" } };
        yield return new object[] { "%%", new[] { "%" } };
        yield return new object[] { "%%FOO%%", new[] { "%FOO%" } };
        yield return new object[] { "100%", new[] { "100%" } };
        yield return new object[] { "5%3", new[] { "5%3" } };
        yield return new object[] { "%FOO", new[] { "%FOO" } };
        yield return new object[] { "$FOO", new[] { "bar" } };
        yield return new object[] { "${FOO}", new[] { "bar" } };
        yield return new object[] { "$foo", new[] { "bar" } };
        yield return new object[] { "${foo}", new[] { "bar" } };
        yield return new object[] { "$NOPE", new[] { "$NOPE" } };
        yield return new object[] { "${NOPE}", new[] { "${NOPE}" } };
        yield return new object[] { "$$", new[] { "$" } };
        yield return new object[] { "$$FOO", new[] { "$FOO" } };
        yield return new object[] { "$", new[] { "$" } };
        yield return new object[] { "a$", new[] { "a$" } };
        yield return new object[] { "${", new[] { "${" } };
        yield return new object[] { "${FOO", new[] { "${FOO" } };
        yield return new object[] { "$FOO-x", new[] { "$FOO-x" } };
        yield return new object[] { "$FOO_x", new[] { "$FOO_x" } };
        yield return new object[] { "$FOO.x", new[] { "bar.x" } };
        yield return new object[] { "$FOO/x", new[] { "bar/x" } };
        yield return new object[] { "'$FOO'", new[] { "'$FOO'" } };
        yield return new object[] { "'%FOO%'", new[] { "'%FOO%'" } };
        yield return new object[] { "a'$FOO'b", new[] { "a'$FOO'b" } };
        yield return new object[] { "'unterminated $FOO", new[] { "'unterminated $FOO" } };
        yield return new object[] { "\"$FOO\"", new[] { "\"bar\"" } };
        yield return new object[] { "%LONG%\\x", new[] { "C:\\Program Files\\x" } };
        yield return new object[] { "$SUBDIR/*", new[] { "sub\\a.txt", "sub\\b.md", "sub\\deep" } };
        yield return new object[] { "%SUBDIR%\\*.txt", new[] { "sub\\a.txt" } };
        yield return new object[] { "%BAZ%", new[] { "q*" } };
        yield return new object[] { "$BAZ", new[] { "q*" } };
        yield return new object[] { "[weird].txt", new[] { "[weird].txt" } };
        yield return new object[] { "[[]weird].txt", new[] { "[weird].txt" } };
        yield return new object[] { "[[]w*", new[] { "[weird].txt" } };
        yield return new object[] { "a b*", new[] { "a b.txt" } };
        yield return new object[] { "sub/", new[] { "sub/" } };
        yield return new object[] { "sub\\", new[] { "sub\\" } };
        yield return new object[] { "*/", new[] { "sub\\" } };
        yield return new object[] { "[", new[] { "[" } };
        yield return new object[] { "[]", new[] { "[]" } };
        yield return new object[] { "[a", new[] { "[a" } };
        yield return new object[] { "*[", new[] { "*[" } };
        yield return new object[] { "?", new[] { "?" } };
        yield return new object[] { "??", new[] { "??" } };
        yield return new object[] { "sub/../*.json", new[] { "sub/..\\data1.json", "sub/..\\data10.json", "sub/..\\data2.json" } };
        yield return new object[] { "./*.json", new[] { ".\\data1.json", ".\\data10.json", ".\\data2.json" } };
        yield return new object[] { ".\\*.json", new[] { ".\\data1.json", ".\\data10.json", ".\\data2.json" } };
        yield return new object[] { "x*y", new[] { "x*y" } };
        yield return new object[] { "***", new[] { "a b.txt", "Config.txt", "config.yaml", "config.yaml.lock", "data1.json", "data10.json", "data2.json", "Readme.MD", "sub", "[weird].txt" } };
        yield return new object[] { "*?*", new[] { "a b.txt", "Config.txt", "config.yaml", "config.yaml.lock", "data1.json", "data10.json", "data2.json", "Readme.MD", "sub", "[weird].txt" } };
        yield return new object[] { "https://example.com/a?b=1", new[] { "https://example.com/a?b=1" } };
        yield return new object[] { "name?", new[] { "name?" } };
        yield return new object[] { "why?", new[] { "why?" } };
        yield return new object[] { "$HOME", new[] { "$HOME" } };
        yield return new object[] { "~x*", new[] { "C:\\Users\\x*" } };
        yield return new object[] { "[a-c]*", new[] { "a b.txt", "Config.txt", "config.yaml", "config.yaml.lock" } };
        yield return new object[] { "[!]*", new[] { "[!]*" } };
        yield return new object[] { "[!a]x", new[] { "[!a]x" } };
    }
}
