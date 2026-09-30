using System.Windows;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using Wpf.Ui.Appearance;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>An in-memory settings store that records what was saved.</summary>
internal sealed class MemoryAppearanceStore : IAppearanceSettingsStore
{
    public AppearanceSettings Stored { get; set; } = AppearanceSettings.Defaults;

    public int Saves { get; private set; }

    public AppearanceSettings Load() => Stored;

    public void Save(AppearanceSettings settings)
    {
        Stored = settings;
        Saves++;
    }
}

/// <summary>A Windows that says whatever the test needs it to.</summary>
internal sealed class FakeSystemThemeSource : ISystemThemeSource
{
    private bool _isDark = true;

    /// <summary>When set, the next read of <see cref="IsDark"/> throws once, as a look that cannot be applied would.</summary>
    public bool ThrowOnNextRead { get; set; }

    public bool IsDark
    {
        get
        {
            if (ThrowOnNextRead)
            {
                ThrowOnNextRead = false;
                throw new InvalidOperationException("Windows would not say.");
            }

            return _isDark;
        }

        set => _isDark = value;
    }

    public bool IsHighContrast { get; set; }

    public System.Windows.Media.Color AccentColor { get; set; } = System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4);
}

/// <summary>
/// An <see cref="AppearanceService"/> running on the shared UI thread's <see cref="Application"/> (the app's real
/// dictionaries, merged by hand), with the process-wide state it changes put back on dispose: the WPF-UI theme, the
/// accent keys written to the application, the token layer, and the Fluent <see cref="Application.ThemeMode"/>. Every
/// test that builds one carries the UI collection, so nothing else is rendering while it is applied.
/// </summary>
internal sealed class AppearanceFixture : IDisposable
{
    private readonly HashSet<object> _keysBefore = new();

    public AppearanceFixture(
        AppearanceSettings? saved = null,
        bool manageFluentThemeMode = false,
        Action<FakeSystemThemeSource>? configureSystem = null)
    {
        Store.Stored = saved ?? AppearanceSettings.Defaults;
        configureSystem?.Invoke(System);

        UiThread.Run(() =>
        {
            foreach (var key in Application.Current.Resources.Keys)
            {
                _ = _keysBefore.Add(key);
            }

            Service = new AppearanceService(Application.Current, Store, System, manageFluentThemeMode);
            Service.Initialize();
        });
    }

    public MemoryAppearanceStore Store { get; } = new();

    public FakeSystemThemeSource System { get; } = new();

    public AppearanceService Service { get; private set; } = null!;

    /// <summary>Looks up a resource the way a DynamicResource on a window would: application scope, later dictionaries first.</summary>
    public static object? Find(string key) => Application.Current.TryFindResource(key);

    public static System.Windows.Media.Color ColorOf(string key) =>
        Find(key) is System.Windows.Media.SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"{key} is not a solid brush.");

    public void Dispose()
    {
        UiThread.Run(() =>
        {
            Service.Uninstall();

            var app = Application.Current;
            foreach (var key in app.Resources.Keys.Cast<object>().ToArray())
            {
                if (!_keysBefore.Contains(key))
                {
                    app.Resources.Remove(key);
                }
            }

            ApplicationThemeManager.Apply(ApplicationTheme.Dark, updateAccent: false);
        });
    }
}
