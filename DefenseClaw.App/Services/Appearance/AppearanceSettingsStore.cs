using DefenseClaw.App.Services.Settings;

namespace DefenseClaw.App.Services.Appearance;

/// <summary>Where the appearance choice lives between runs.</summary>
internal interface IAppearanceSettingsStore
{
    /// <summary>The saved choice, or the defaults when there is none or it cannot be read. Never throws.</summary>
    AppearanceSettings Load();

    /// <summary>Saves the choice. Failures are logged, not thrown: a setting that cannot be saved must not break the app.</summary>
    void Save(AppearanceSettings settings);
}

/// <summary>
/// The appearance choice, kept in the <c>appearance</c> section of the app's settings file,
/// <c>%LOCALAPPDATA%\DefenseClaw.App\settings.json</c>. A thin client of <see cref="AppSettingsStore"/>, which owns the file:
/// its shape (<c>{ "schemaVersion": 1, "appearance": { "style": "linear", "mode": "dark" }, … }</c>), its tolerance for a missing,
/// truncated or wrongly shaped file and for values this build does not know, its atomic temp-file-and-move writes, and the
/// guarantee that saving the appearance keeps every other section and a newer <c>schemaVersion</c> exactly as found.
/// <para>
/// There is one store per file in the process (<see cref="AppSettingsStore.ForPath"/>), so this and the rest of the app's
/// settings share one lock and one writer; two of these over the same path are the same store.
/// </para>
/// </summary>
internal sealed class FileAppearanceSettingsStore : IAppearanceSettingsStore
{
    /// <summary>The version this build writes. A file that already says more keeps its number.</summary>
    public const int SchemaVersion = AppSettingsStore.SchemaVersion;

    private readonly AppSettingsStore _store;

    public FileAppearanceSettingsStore(string? path = null)
    {
        _store = AppSettingsStore.ForPath(path);
    }

    public static string DefaultPath => AppSettingsStore.DefaultPath;

    /// <inheritdoc />
    public AppearanceSettings Load() => _store.Load().Appearance;

    /// <inheritdoc />
    public void Save(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _ = _store.Update(all => all with { Appearance = settings });
    }
}
