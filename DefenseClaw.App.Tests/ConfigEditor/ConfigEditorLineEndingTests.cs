using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.Core.Config;

namespace DefenseClaw.App.Tests.ConfigEditor;

/// <summary>
/// A FORM edit against a config.yaml with Windows line endings — the file DefenseClaw's own CLI writes, CRLF on every
/// line — must land, change only the lines it means to, and leave the file CRLF. Each test loads a synthetic
/// <c>config.yaml</c> from a scratch directory through the real load path (never a copy of the operator's file), under
/// both line endings, and compares the whole text: a stray bare LF, a lost <c>\r</c> or an edit that silently did not
/// apply all show up as a different string.
/// </summary>
public sealed class ConfigEditorLineEndingTests
{
    public static TheoryData<string, string> FileAndCliEndings { get; } = BuildFileAndCliEndings();

    private static TheoryData<string, string> BuildFileAndCliEndings()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in new[] { LineEndings.Lf, LineEndings.Crlf })
        {
            foreach (var cli in new[] { LineEndings.Lf, LineEndings.Crlf })
            {
                data.Add(file, cli);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task The_file_is_read_from_disk_with_its_line_endings_intact(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);

        Assert.Equal(eol, harness.ViewModel.RawText.Contains("\r\n", StringComparison.Ordinal) ? LineEndings.Crlf : LineEndings.Lf);
        Assert.Equal(await File.ReadAllTextAsync(harness.ConfigPath), harness.ViewModel.RawText);
        Assert.Equal(harness.Raw, harness.ViewModel.RawText);
        Assert.True(harness.ViewModel.IsFormEditable);
        Assert.True(harness.ViewModel.Sections.Count > 0);
    }

    [Theory]
    [MemberData(nameof(FileAndCliEndings))]
    public async Task A_form_edit_applies_whichever_line_endings_the_file_and_the_cli_output_have(string fileEol, string cliEol)
    {
        // The CLI's stdout is CRLF on Windows even when the file it describes is LF (and the other way round for a
        // file a Windows editor saved): the masked view and config.yaml are two texts with independent endings.
        using var harness = await ConfigEditorHarness.LoadAsync(fileEol, maskedSourceEol: cliEol);

        harness.Field("guardrail.mode").TextValue = "action";

        Assert.Null(harness.ViewModel.FieldErrorMessage);
        Assert.Equal(
            harness.Raw.Replace(harness.L("  mode: observe\n"), harness.L("  mode: action\n"), StringComparison.Ordinal),
            harness.ViewModel.RawText);
        Assert.True(LineEndings.IsUniform(harness.ViewModel.RawText, fileEol));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task Each_kind_of_form_edit_changes_only_its_own_lines_and_keeps_the_files_line_ending(string eol)
    {
        using var harness = await ConfigEditorHarness.LoadAsync(eol);
        var vm = harness.ViewModel;
        var expected = harness.Raw;

        // string
        harness.Field("guardrail.mode").TextValue = "action";
        expected = Swap(harness, expected, "  mode: observe\n", "  mode: action\n");
        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(expected, vm.RawText);

        // integer
        harness.Field("gateway.api_port").NumberValue = 4000;
        expected = Swap(harness, expected, "  api_port: 18970\n", "  api_port: 4000\n");
        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(expected, vm.RawText);

        // boolean
        harness.Field("guardrail.enabled").BoolValue = false;
        expected = Swap(harness, expected, "  enabled: true\n", "  enabled: false\n");
        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(expected, vm.RawText);

        // a value that has to be quoted: the quote must not swallow the line break
        harness.Field("llm.model").TextValue = "gpt: 5";
        expected = Swap(harness, expected, "  model: gpt-4o\n", "  model: 'gpt: 5'\n");
        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(expected, vm.RawText);

        // list: append, then remove from the middle
        harness.List("guardrail.scan_roots").Items.Add("E:\\work");
        expected = Swap(harness, expected, "  - D:\\repos\n", "  - D:\\repos\n  - E:\\work\n");
        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(expected, vm.RawText);

        harness.List("guardrail.scan_roots").Items.RemoveAt(0);
        expected = Swap(harness, expected, "  - C:\\src\n", string.Empty);
        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(expected, vm.RawText);

        // list: emptied
        harness.List("guardrail.scan_roots").Items.Clear();
        expected = Swap(harness, expected, "  scan_roots:\n  - D:\\repos\n  - E:\\work\n", "  scan_roots: []\n");
        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(expected, vm.RawText);

        Assert.True(LineEndings.IsUniform(vm.RawText, eol));
        Assert.True(vm.IsRawModified);
        _ = ConfigStore.Parse(vm.RawText);
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task An_edit_to_the_last_section_of_a_file_with_no_final_newline_ends_it_with_the_files_own_line_ending(string eol)
    {
        // Real shape: the CLI wrote the file, the operator's editor dropped the final newline. The last section is the
        // one FORM patches, and the line it rewrites has no terminator to inherit, so the document has to supply one —
        // and it must be this file's, not a hard-coded LF.
        const string raw = "config_version: 8\ngateway:\n  host: 127.0.0.1\nobservability:\n  enabled: true";
        const string masked = "config_version: 8\ngateway:\n  host: 127.0.0.1\nobservability:\n  enabled: true\n";

        using var harness = await ConfigEditorHarness.LoadAsync(eol, raw, masked);
        var vm = harness.ViewModel;
        Assert.False(harness.Raw.EndsWith('\n'));

        harness.Field("observability.enabled").BoolValue = false;

        Assert.Null(vm.FieldErrorMessage);
        Assert.Equal(harness.L("config_version: 8\ngateway:\n  host: 127.0.0.1\nobservability:\n  enabled: false\n"), vm.RawText);
        Assert.True(LineEndings.IsUniform(vm.RawText, eol));
    }

    [Theory]
    [MemberData(nameof(LineEndings.Both), MemberType = typeof(LineEndings))]
    public async Task A_middle_section_edit_in_a_file_with_no_final_newline_leaves_the_missing_newline_missing(string eol)
    {
        const string raw = "config_version: 8\ngateway:\n  host: 127.0.0.1\nobservability:\n  enabled: true";
        const string masked = "config_version: 8\ngateway:\n  host: 127.0.0.1\nobservability:\n  enabled: true\n";

        using var harness = await ConfigEditorHarness.LoadAsync(eol, raw, masked);

        harness.Field("gateway.host").TextValue = "localhost";

        Assert.Null(harness.ViewModel.FieldErrorMessage);
        Assert.Equal(harness.L("config_version: 8\ngateway:\n  host: localhost\nobservability:\n  enabled: true"), harness.ViewModel.RawText);
    }

    private static string Swap(ConfigEditorHarness harness, string current, string lfFrom, string lfTo)
    {
        var from = harness.L(lfFrom);
        Assert.Contains(from, current, StringComparison.Ordinal);
        return current.Replace(from, harness.L(lfTo), StringComparison.Ordinal);
    }
}
