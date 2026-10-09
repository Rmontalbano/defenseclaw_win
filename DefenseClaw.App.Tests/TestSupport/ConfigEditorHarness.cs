using DefenseClaw.App.Services;
using DefenseClaw.App.ViewModels.ConfigEditor;
using DefenseClaw.Core.Runtime;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// A <see cref="ConfigEditorWindowViewModel"/> loaded from a real <c>config.yaml</c> in a scratch directory. The masked
/// source view the FORM tab is built from is supplied through <see cref="ConfigEditorWindowViewModel.FormSourceOverride"/>,
/// so no CLI is involved and nothing outside the scratch directory is touched.
/// <para>
/// The file is written — and the masked source served — with the line ending the test asks for, so the same test
/// body runs against an LF file and against a CRLF one (the shape of the real <c>config.yaml</c>, which Windows tooling
/// wrote). <see cref="L"/> turns an LF-written expectation into that ending.
/// </para>
/// </summary>
internal sealed class ConfigEditorHarness : IDisposable
{
    private readonly TempDirectory _temp;
    private readonly AppServices _services;

    private ConfigEditorHarness(TempDirectory temp, AppServices services, ConfigEditorWindowViewModel vm, string eol, string raw)
    {
        _temp = temp;
        _services = services;
        ViewModel = vm;
        Eol = eol;
        Raw = raw;
    }

    public ConfigEditorWindowViewModel ViewModel { get; }

    /// <summary>The line ending the config.yaml on disk was written with.</summary>
    public string Eol { get; }

    /// <summary>The config.yaml text exactly as it sits on disk, in <see cref="Eol"/>.</summary>
    public string Raw { get; }

    public string ConfigPath => _temp.File("config.yaml");

    /// <summary>The isolated services the view-model runs on (a test that needs the shell's <c>ConfigReloaded</c> event raises it through these).</summary>
    public AppServices Services => _services;

    /// <summary>
    /// Makes the view-model's CLI resolve to something that cannot be started: a file called <c>defenseclaw.exe</c> that is not a
    /// program, in the bin directory the isolated paths point at. Every <c>config validate</c> after this ends in "validation could
    /// not run", the <see cref="SaveStage.ValidationFailed"/> outcome — the real CLI is never involved. Call it
    /// before the first save (a missing executable is cached for a moment).
    /// </summary>
    public void PlantUnrunnableCli()
    {
        var bin = Path.Combine(_temp.Path, "no-such-bin");
        _ = Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "defenseclaw.exe"), "this is not a program");
    }

    /// <summary>Rewrites config.yaml the way another tool would, behind the editor's back.</summary>
    public void WriteConfigExternally(string text) => File.WriteAllText(ConfigPath, text);

    /// <summary>An LF-written expectation in this file's line ending.</summary>
    public string L(string lfText) => LineEndings.With(lfText, Eol);

    /// <param name="eol">Line ending of the config.yaml on disk.</param>
    /// <param name="raw">The file's content (any line ending; converted to <paramref name="eol"/>). Defaults to <see cref="ConfigSamples.Raw"/>.</param>
    /// <param name="maskedSource">The masked view the CLI would print. Defaults to <see cref="ConfigSamples.MaskedSource"/>.</param>
    /// <param name="maskedSourceEol">Line ending of the masked view; defaults to <paramref name="eol"/>. The real CLI's stdout is CRLF whatever the file is.</param>
    /// <param name="withoutCli">Leave <see cref="ConfigEditorWindowViewModel.FormSourceOverride"/> unset, so the real read path runs against a runner with an empty PATH.</param>
    /// <param name="runtimeProbeRunner">
    /// What answers the runtime probes (<see cref="RuntimeFixtureRunner"/>). Null: nothing does, the runtime reads as unknown, and the form offers
    /// the 0.8.10 lists. When given, the probe has run (and its answer is in) before the form is built.
    /// </param>
    public static async Task<ConfigEditorHarness> LoadAsync(
        string eol = LineEndings.Lf,
        string? raw = null,
        string? maskedSource = null,
        string? maskedSourceEol = null,
        bool withoutCli = false,
        RuntimeProbeRunner? runtimeProbeRunner = null)
    {
        var fileText = LineEndings.With(raw ?? ConfigSamples.Raw, eol);

        var temp = new TempDirectory();
        var services = TestServices.Create(temp, fileText, runtimeProbeRunner);
        if (runtimeProbeRunner is not null)
        {
            _ = await services.Runtime.RefreshAsync();
        }

        // The shell's file watcher reads config.yaml (to hash it) on its own thread whenever the directory moves, and a
        // save's File.Replace onto a file that is open for that read fails with a sharing violation. That is a race this
        // suite must not depend on the timing of; a test that needs the shell's "config changed" event raises it itself
        // (AppServices.ReloadConfig), which never needed the watcher.
        services.ConfigWatcher.Dispose();

        var vm = new ConfigEditorWindowViewModel(services);
        if (!withoutCli)
        {
            var masked = LineEndings.With(maskedSource ?? ConfigSamples.MaskedSource, maskedSourceEol ?? eol);
            vm.FormSourceOverride = _ => Task.FromResult(masked);
        }

        await vm.LoadAsync();
        return new ConfigEditorHarness(temp, services, vm, eol, fileText);
    }

    public FormField Field(string dottedPath) => Groups().SelectMany(g => g.Fields).Single(f => f.Path == dottedPath);

    public FormListField List(string dottedPath) => Groups().SelectMany(g => g.Lists).Single(l => l.Path == dottedPath);

    private IEnumerable<FormGroup> Groups()
    {
        var stack = new Stack<FormGroup>(ViewModel.Sections);
        while (stack.Count > 0)
        {
            var group = stack.Pop();
            yield return group;
            foreach (var nested in group.SubGroups)
            {
                stack.Push(nested);
            }
        }
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }
}
