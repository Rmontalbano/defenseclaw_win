using DefenseClaw.App.Services.Wizards;
using DefenseClaw.Core.Paths;

namespace DefenseClaw.App.Tests.TestSupport;

/// <summary>
/// Wizards built the way the app builds them - through <see cref="WizardCatalog"/> - from help screens: the ones the installed 0.8.10 CLI printed
/// (<c>Fixtures/runtime-0.8.10/help</c>, captured with <c>--help</c>; nothing was run) or fake ones a test writes. No process starts: the
/// catalog is given a runner that answers from a dictionary.
/// </summary>
internal static class CatalogHelp
{
    /// <summary>A help screen the installed 0.8.10 CLI printed, by file name without extension (<c>setup-guardrail</c>).</summary>
    public static string Real(string name) =>
        LineEndings.Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "help", name + ".txt")));

    /// <summary>The roster screen of 0.8.10: <c>defenseclaw setup --help</c>.</summary>
    public static string Roster() =>
        LineEndings.Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "runtime-0.8.10", "setup.txt")));

    /// <summary>
    /// The catalog over a CLI whose screens are <paramref name="screens"/> - keyed by the words after <c>setup</c> (<c>guardrail</c>,
    /// <c>provider add</c>); the roster is 0.8.10's unless one is given. A screen the dictionary lacks fails like a CLI that does not know the command.
    /// </summary>
    public static WizardCatalog Catalog(IReadOnlyDictionary<string, string> screens, string? roster = null)
    {
        var top = roster ?? Roster();
        var paths = new DefenseClawPaths(binDirectory: @"C:\fake\bin", searchPath: Array.Empty<string>(), fileExists: _ => true);

        return new WizardCatalog(new SetupHelpProbe(paths, diskCache: null, (_, path, _) =>
        {
            var key = string.Join(' ', path);
            if (key.Length == 0)
            {
                return Task.FromResult(new HelpProbeResult(top, null));
            }

            return Task.FromResult(screens.TryGetValue(key, out var text)
                ? new HelpProbeResult(text, null)
                : new HelpProbeResult(string.Empty, "defenseclaw exited 2."));
        }));
    }

    /// <summary>The definition the catalog builds for <paramref name="target"/> from <paramref name="screens"/>.</summary>
    public static async Task<WizardDefinition> DefinitionAsync(string target, IReadOnlyDictionary<string, string> screens)
    {
        var catalog = Catalog(screens);
        _ = await catalog.LoadAsync();
        var definition = await catalog.EnsureDetailAsync(target);
        Assert.True(definition.IsDetailLoaded, definition.DetailError);
        Assert.Null(definition.DetailError);
        return definition;
    }

    /// <summary>The definition for a target whose screen is the real one of the same name.</summary>
    public static Task<WizardDefinition> RealAsync(string target, params (string Key, string Screen)[] more)
    {
        var screens = new Dictionary<string, string>(StringComparer.Ordinal) { [target] = Real("setup-" + target) };
        foreach (var (key, screen) in more)
        {
            screens[key] = Real(screen);
        }

        return DefinitionAsync(target, screens);
    }

    /// <summary>A screen with the options of <paramref name="optionLines"/> under a <c>setup</c> command's usual head.</summary>
    public static string Screen(string target, params string[] optionLines) =>
        LineEndings.Normalize(
            $"Usage: defenseclaw setup {target} [OPTIONS]\n\n  Configure {target}.\n\nOptions:\n" +
            string.Join('\n', optionLines.Select(l => "  " + l)) +
            "\n  --help  Show this message and exit.\n");
}
