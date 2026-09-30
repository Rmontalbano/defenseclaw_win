using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DefenseClaw.Core.Cli;
using DefenseClaw.Core.Paths;
using DefenseClaw.Tests.TestSupport;

namespace DefenseClaw.Tests;

/// <summary>
/// The clipboard form of a command. The unit tests pin the quoting rule; the parser test hands every hostile
/// name to the real PowerShell parser and asserts the paste is one command whose arguments are exactly the
/// names - no subexpression, no second statement, nothing that would run.
/// </summary>
public sealed class PowerShellQuotingTests
{
    private const string Exe = @"C:\Users\Some One\AppData\Local\Programs\DefenseClaw\bin\defenseclaw.exe";

    [Theory]
    [InlineData("skill", "skill")]
    [InlineData("--json", "--json")]
    [InlineData("--connector=claudecode", "--connector=claudecode")]
    [InlineData("a/b.c:d+e-f_g", "a/b.c:d+e-f_g")]
    [InlineData("", "''")]
    [InlineData("two words", "'two words'")]
    [InlineData("x&calc", "'x&calc'")]
    [InlineData("x;calc", "'x;calc'")]
    [InlineData("x$(calc)", "'x$(calc)'")]
    [InlineData("a $(calc) b", "'a $(calc) b'")]
    [InlineData("\"a $(calc) b\"", "'\"a $(calc) b\"'")]
    [InlineData("x`calc", "'x`calc'")]
    [InlineData("x'y", "'x''y'")]
    [InlineData("it’s", "'it’’s'")]
    [InlineData("@x", "'@x'")]
    [InlineData("a,b", "'a,b'")]
    [InlineData(@"C:\dir\file", @"'C:\dir\file'")]
    public void Quotes_everything_outside_the_safe_alphabet(string argument, string expected) =>
        Assert.Equal(expected, PowerShellQuoting.Argument(argument));

    [Fact]
    public void A_quoted_executable_is_called_with_the_call_operator_and_a_bare_one_is_not()
    {
        Assert.Equal(
            @"& 'C:\Users\Some One\AppData\Local\Programs\DefenseClaw\bin\defenseclaw.exe' skill quarantine -- 'x&calc'",
            PowerShellQuoting.CommandLine(Exe, new[] { "skill", "quarantine", "--", "x&calc" }));

        Assert.Equal(
            "defenseclaw skill list --json",
            PowerShellQuoting.CommandLine("defenseclaw", new[] { "skill", "list", "--json" }));
    }

    [Fact]
    public async Task The_invocation_copies_the_powershell_form_and_shows_the_readable_one()
    {
        using var temp = new TempDirectory();
        var runner = new CliRunner(
            new DefenseClawPaths(temp.Path, Path.Combine(temp.Path, "no-such-bin"), searchPath: Array.Empty<string>()),
            neutralWorkingDirectory: Path.Combine(temp.Path, "cwd"));
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var invocation = await runner.RunExecutableAsync(cmd, new[] { "/c", "echo", "my skill&calc" });

        Assert.Equal($"& '{cmd}' /c echo 'my skill&calc'", invocation.PowerShellCommandLine);

        // The display form is unchanged: it quotes whitespace only, for reading.
        Assert.Equal($"{cmd} /c echo \"my skill&calc\"", invocation.CommandLine);
    }

    private static readonly string[] HostileNames =
    {
        "x&calc",
        "x;calc",
        "x$(calc)",
        "a $(calc) b",
        "\"a $(calc) b\"",
        "x`calc",
        "x'y",
        "it’s",
        "‘quoted’",
        "$env:USERNAME",
        "x|calc",
        "x>out.txt",
        "x<in.txt",
        "%TEMP%",
        "#not-a-comment",
        "{ calc }",
        "a\nb",
        "@splat",
        "a,b",
        "--flag=$(evil)",
        "",
        "  ",
        "é ü",
        "1e3",
        "0x10",
        "-5",
        "--",
        "-",
    };

    [Fact]
    public void The_real_powershell_parser_reads_every_hostile_name_as_one_literal_argument()
    {
        Assert.True(OperatingSystem.IsWindows());

        var lines = HostileNames
            .Select(name => PowerShellQuoting.CommandLine(Exe, new[] { "skill", "quarantine", "--", name }))
            .ToArray();

        var results = ParseWithPowerShell(lines);
        Assert.Equal(HostileNames.Length, results.Count);

        for (var i = 0; i < HostileNames.Length; i++)
        {
            var parsed = results[i];
            var because = $"case {i} ({HostileNames[i]}) pasted as: {lines[i]}";

            Assert.True(parsed.Errors.Count == 0, because + " => parse errors: " + string.Join("; ", parsed.Errors));
            Assert.True(parsed.Statements == 1, because + $" => {parsed.Statements} statements");
            Assert.True(parsed.Operator == "Ampersand", because + " => not a call-operator invocation");
            Assert.True(
                parsed.NodeTypes.All(t => AllowedNodeTypes.Contains(t)),
                because + " => evaluated node types: " + string.Join(", ", parsed.NodeTypes.Except(AllowedNodeTypes)));
            Assert.True(
                parsed.Arguments.SequenceEqual(new[] { Exe, "skill", "quarantine", "--", HostileNames[i] }),
                because + " => arguments: [" + string.Join("] [", parsed.Arguments) + "]");
        }
    }

    [Fact]
    public void The_unquoted_display_form_is_what_the_parser_would_have_run_which_is_why_it_is_not_copied()
    {
        // The regression this guards: the readable form only quotes whitespace, so this name pastes as a command.
        var display = CommandLineForDisplay("x&calc");

        var parsed = Assert.Single(ParseWithPowerShell(new[] { display }));

        Assert.True(parsed.Statements > 1 || !parsed.NodeTypes.All(t => AllowedNodeTypes.Contains(t)) || parsed.Errors.Count > 0,
            "the display form parsed as a single literal command; the test premise is wrong: " + display);
    }

    private static string CommandLineForDisplay(string name) =>
        string.Join(' ', new[] { "defenseclaw", "skill", "quarantine", "--", name });

    // What a pasted command may consist of. Anything else - an expandable string, a sub-expression, a variable, a
    // pipeline of two commands - is PowerShell evaluating something.
    private static readonly HashSet<string> AllowedNodeTypes = new(StringComparer.Ordinal)
    {
        "ScriptBlockAst",
        "NamedBlockAst",
        "PipelineAst",
        "CommandAst",
        "StringConstantExpressionAst",
        "CommandParameterAst",
        "ConstantExpressionAst",
    };

    private sealed record Parsed(IReadOnlyList<string> Errors, int Statements, string Operator, IReadOnlyList<string> NodeTypes, IReadOnlyList<string> Arguments);

    private const string ParserScript = @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$lines = [System.IO.File]::ReadAllText($env:DC_PARSE_INPUT, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$out = @()
foreach ($line in $lines) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($line, [ref]$tokens, [ref]$errors)
    $statements = @($ast.EndBlock.Statements)
    $op = ''; $args2 = @()
    if ($statements.Count -ge 1 -and $statements[0] -is [System.Management.Automation.Language.PipelineAst] -and $statements[0].PipelineElements.Count -eq 1) {
        $cmd = $statements[0].PipelineElements[0]
        if ($cmd -is [System.Management.Automation.Language.CommandAst]) {
            $op = [string]$cmd.InvocationOperator
            foreach ($e in $cmd.CommandElements) {
                if ($e -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $args2 += [string]$e.Value }
                else { $args2 += [string]$e.Extent.Text }
            }
        }
    }
    $types = @($ast.FindAll({ $true }, $true) | ForEach-Object { $_.GetType().Name } | Sort-Object -Unique)
    $out += [pscustomobject]@{
        errors = @($errors | ForEach-Object { $_.Message })
        statements = $statements.Count
        op = $op
        types = $types
        args = $args2
    }
}
ConvertTo-Json -InputObject @($out) -Depth 6 -Compress
";

    private static List<Parsed> ParseWithPowerShell(IReadOnlyList<string> lines)
    {
        var input = Path.Combine(Path.GetTempPath(), $"dcw-ps-{Guid.NewGuid():n}.json");
        File.WriteAllText(input, JsonSerializer.Serialize(lines), new UTF8Encoding(false));
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(ParserScript)));
            start.Environment["DC_PARSE_INPUT"] = input;

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(60_000), "powershell did not finish");
            Assert.True(process.ExitCode == 0, "powershell failed: " + stderr.Result);

            using var document = JsonDocument.Parse(stdout.Result);
            return document.RootElement.EnumerateArray().Select(e => new Parsed(
                    Strings(e, "errors"),
                    e.GetProperty("statements").GetInt32(),
                    e.GetProperty("op").GetString() ?? string.Empty,
                    Strings(e, "types"),
                    Strings(e, "args")))
                .ToList();
        }
        finally
        {
            File.Delete(input);
        }

        static List<string> Strings(JsonElement element, string name)
        {
            var value = element.GetProperty(name);
            return value.ValueKind switch
            {
                JsonValueKind.Array => value.EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToList(),
                JsonValueKind.String => new List<string> { value.GetString() ?? string.Empty },
                _ => new List<string>(),
            };
        }
    }
}
