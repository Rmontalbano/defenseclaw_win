using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Services.Appearance;

/// <summary>What the operating system currently says; a seam so the mode-resolution tests need no real Windows settings.</summary>
internal interface ISystemThemeSource
{
    /// <summary>True when Windows is set to dark app mode.</summary>
    bool IsDark { get; }

    /// <summary>True under a Windows high-contrast theme, which the app never restyles.</summary>
    bool IsHighContrast { get; }

    /// <summary>The Windows accent colour (only Default follows it).</summary>
    Color AccentColor { get; }
}

/// <summary>
/// Reads the real thing, through WPF-UI's own classification (the same one <c>ApplySystemTheme</c> uses), so "follow
/// the system" means exactly what it meant before this setting existed.
/// </summary>
internal sealed class WindowsSystemThemeSource : ISystemThemeSource
{
    public bool IsDark
    {
        get
        {
            SystemThemeManager.UpdateSystemThemeCache();
            return SystemThemeManager.GetCachedSystemTheme() is SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow;
        }
    }

    public bool IsHighContrast => SystemThemeManager.HighContrast;

    public Color AccentColor => ApplicationAccentColorManager.GetColorizationColor();
}

/// <summary>
/// Owns the app's look: which style (<see cref="AppearanceStyle"/>) and mode (<see cref="AppearanceMode"/>) are on
/// screen, and swaps them live.
/// <para>
/// <b>Layers.</b> A style is a <i>token dictionary</i> (Themes\Styles\*.xaml: colours, fonts, radii, density) merged after
/// everything else; the <c>Dc*</c> styles read tokens through DynamicResource, so replacing that one dictionary re-skins
/// every open window. For Linear and TUI the tokens are also <i>bridged</i> onto WPF-UI's own resource keys
/// (<see cref="WpfUiBridge"/>) so its controls follow, and the accent goes in through the same
/// <c>SystemAccentColor*</c> keys WPF-UI's accent manager writes. Default has no bridge and no accent of its own:
/// it hands WPF-UI back its own theme and accent, exactly as before.
/// </para>
/// <para>
/// <b>Two theme systems, one decision.</b> .NET 9's <see cref="Application.ThemeMode"/> styles the stock controls,
/// WPF-UI's <see cref="ApplicationThemeManager"/> styles its own; both are set from the same light/dark answer here.
/// Mode <see cref="AppearanceMode.System"/> resolves through <see cref="ISystemThemeSource"/> and keeps following it
/// (<see cref="OnSystemPreferenceChanged"/>); Light and Dark are explicit. Mica is used by Default only - the other
/// styles are solid, so their windows lose the backdrop. Under a Windows high-contrast theme every style steps aside
/// for Default and the system theme, on purpose.
/// </para>
/// <para>
/// <b>Order matters and is deliberate.</b> WPF-UI bakes brushes when its theme dictionary loads, so the accent keys
/// are written first, then the WPF-UI theme is swapped (which re-reads them), then the token layer is installed
/// (Default's tokens alias WPF-UI's brushes, so they must load after that swap). It runs before any window exists
/// at startup (<see cref="Initialize"/>), and everything here is UI-thread only.
/// </para>
/// </summary>
internal sealed class AppearanceService : IAppearanceControl
{
    private static readonly ConditionalWeakTable<Window, object> Attached = new();
    private static readonly object AttachedMarker = new();
    private static bool _classHandlerRegistered;
    private static AppearanceService? _current;

    private readonly Application _app;
    private readonly IAppearanceSettingsStore _store;
    private readonly ISystemThemeSource _system;
    private readonly bool _manageFluentThemeMode;
    private readonly HashSet<string> _writtenKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<(AppearanceStyle, bool), ResourceDictionary> _previewCache = new();

    private ResourceDictionary? _layer;
    private Applied? _applied;

    private sealed record Applied(AppearanceStyle Style, bool Dark, bool HighContrast, Color Accent);

    /// <param name="app">The application whose resources are re-skinned.</param>
    /// <param name="store">Where the choice is saved.</param>
    /// <param name="system">What Windows says (light/dark, high contrast, accent).</param>
    /// <param name="manageFluentThemeMode">
    /// Whether to drive .NET's <see cref="Application.ThemeMode"/> too. The app does; a test that renders views under
    /// the test host's plain <see cref="Application"/> can leave it alone so the stock controls keep their measurements.
    /// </param>
    public AppearanceService(
        Application app,
        IAppearanceSettingsStore store,
        ISystemThemeSource system,
        bool manageFluentThemeMode = true)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _manageFluentThemeMode = manageFluentThemeMode;
        Settings = AppearanceSettings.Defaults;
    }

    /// <summary>The running service, or null before <see cref="Initialize"/> (and in tests that never build one).</summary>
    public static AppearanceService? Current => _current;

    public event EventHandler? Changed;

    /// <summary>What the operator chose; what is saved.</summary>
    public AppearanceSettings Settings { get; private set; }

    public AppearanceStyle Style => Settings.Style;

    public AppearanceMode Mode => Settings.Mode;

    /// <summary>The style actually on screen: the chosen one, except that a Windows high-contrast theme forces Default.</summary>
    public AppearanceStyle EffectiveStyle => _applied?.Style ?? Settings.Style;

    public bool IsDark => _applied?.Dark ?? false;

    /// <summary>
    /// Reads the saved choice and applies it. Called once, before the tray or any window is created, so nothing ever
    /// paints in the wrong look first. The dashboard stays lazy: only <see cref="Application.Windows"/> (empty at
    /// that point) and resources are touched.
    /// </summary>
    public void Initialize()
    {
        Settings = _store.Load();
        RegisterClassHandler();

        // Live regions (AutomationProperties.LiveSetting) only speak once this hook is in, and it can only be added before an
        // element has used the property: the same "before any window exists" this method is called at. Not about appearance,
        // but this is the one startup call with that guarantee.
        LiveRegion.Install();
        _current = this;

        // The first use of WPF-UI's ApplicationAccentColorManager - whichever call makes it, including the first
        // ApplicationThemeManager.Apply, which reads it after the theme swap - runs its static constructor, and that
        // writes the Windows accent onto the application. Touch it now, before anything of ours is written, or a saved
        // Linear / TUI accent would be overwritten by the system's on the very first apply of the session.
        _ = ApplicationAccentColorManager.SystemAccent;

        // A look that cannot be applied (a dictionary that will not load) must not stop the app starting: fall back to
        // Default, which is what a fresh install gets. Default itself failing is not survivable, and is not caught.
        if (!TryApply() && Settings != AppearanceSettings.Defaults)
        {
            Settings = AppearanceSettings.Defaults;
            Apply();
            RaiseChanged();
        }
    }

    /// <summary>Undoes everything this service put on the application (tests; the app never calls it).</summary>
    public void Uninstall()
    {
        if (_layer is not null)
        {
            _ = _app.Resources.MergedDictionaries.Remove(_layer);
            _layer = null;
        }

        RemoveWrittenKeys();
        if (_manageFluentThemeMode)
        {
            _app.ThemeMode = ThemeMode.None;
        }

        _applied = null;
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
    }

    public void SetStyle(AppearanceStyle style) => Set(Settings with { Style = style });

    public void SetMode(AppearanceMode mode) => Set(Settings with { Mode = mode });

    public void ToggleLightDark() => SetMode(IsDark ? AppearanceMode.Light : AppearanceMode.Dark);

    private void Set(AppearanceSettings settings)
    {
        if (settings == Settings && _applied is not null)
        {
            return;
        }

        var previous = Settings;
        Settings = settings;
        if (!TryApply())
        {
            // Nothing was saved and the operator's screen is put back as it was: a cosmetic setting must never be the
            // reason the dashboard misbehaves.
            Settings = previous;
            _ = TryApply();
            return;
        }

        _store.Save(settings);
    }

    /// <summary>Applies the current settings; false (logged) when applying threw, so callers can fall back.</summary>
    private bool TryApply()
    {
        try
        {
            Apply();
        }
#pragma warning disable CA1031 // A look that fails to apply is reported and undone, never allowed to reach the dispatcher's last-resort handler.
        catch (Exception ex)
        {
            Trace.TraceError($"Appearance {Settings} could not be applied: {ex}");
            return false;
        }
#pragma warning restore CA1031

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Windows changed something (the theme, the accent, high contrast): follow it if that matters. Cheap when nothing
    /// relevant changed - it is called for every "General" preference change Windows broadcasts.
    /// </summary>
    public void OnSystemPreferenceChanged()
    {
        if (_applied is null)
        {
            return;
        }

        var highContrast = _system.IsHighContrast;
        var followsDark = Settings.Mode == AppearanceMode.System && _system.IsDark != _applied.Dark;
        var highContrastChanged = highContrast != _applied.HighContrast;

        // Only Default wears the Windows accent, so only Default has anything to redo when it changes.
        var accentChanged = _applied.Style == AppearanceStyle.Default && _system.AccentColor != _applied.Accent;

        if (followsDark || highContrastChanged || accentChanged)
        {
            _ = TryApply();
        }
    }

    // ------------------------------------------------------------------ apply

    private void Apply()
    {
        var highContrast = _system.IsHighContrast;
        var style = highContrast ? AppearanceStyle.Default : Settings.Style;
        var dark = Settings.Mode switch
        {
            AppearanceMode.Light => false,
            AppearanceMode.Dark => true,
            _ => _system.IsDark,
        };

        if (_manageFluentThemeMode)
        {
            var themeMode = Settings.Mode switch
            {
                AppearanceMode.Light => ThemeMode.Light,
                AppearanceMode.Dark => ThemeMode.Dark,
                _ => ThemeMode.System,
            };

            if (_app.ThemeMode != themeMode)
            {
                _app.ThemeMode = themeMode;
            }
        }

        ResourceDictionary tokens;
        if (style == AppearanceStyle.Default)
        {
            // Everything WPF-UI's accent manager writes is written again here, so what this style put on the
            // application does not outlive it; then WPF-UI is handed back its own theme and the Windows accent.
            RemoveWrittenKeys();

            if (highContrast)
            {
                ApplicationThemeManager.ApplySystemTheme();
            }
            else
            {
                ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, updateAccent: true);
            }

            tokens = LoadTokens(AppearanceStyle.Default, dark);
        }
        else
        {
            tokens = LoadTokens(style, dark);
            WriteAccent(tokens);
            ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.None, updateAccent: false);
        }

        var layer = new ResourceDictionary();
        layer.MergedDictionaries.Add(tokens);
        if (style != AppearanceStyle.Default)
        {
            WpfUiBridge.AddTo(layer, tokens);
        }

        InstallLayer(layer);

        _applied = new Applied(style, dark, highContrast, _system.AccentColor);

        foreach (Window window in _app.Windows)
        {
            ApplyToWindow(window);
        }
    }

    /// <summary>Tells subscribers the look changed. A subscriber that throws is logged, not mistaken for a look that failed to apply.</summary>
    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
#pragma warning disable CA1031 // One misbehaving subscriber must not undo or hide a look that did apply.
        catch (Exception ex)
        {
            Trace.TraceError($"An appearance change handler failed: {ex}");
        }
#pragma warning restore CA1031
    }

    private static ResourceDictionary LoadTokens(AppearanceStyle style, bool dark) =>
        new() { Source = AppearanceCatalog.TokenSource(style, dark) };

    private void InstallLayer(ResourceDictionary layer)
    {
        var merged = _app.Resources.MergedDictionaries;
        var index = _layer is null ? -1 : merged.IndexOf(_layer);
        if (index >= 0)
        {
            merged[index] = layer;
        }
        else
        {
            merged.Add(layer);
        }

        _layer = layer;
    }

    // ------------------------------------------------------------------ the accent

    /// <summary>
    /// Writes the style's accent onto the application in the shape WPF-UI's <c>ApplicationAccentColorManager</c> does
    /// (same keys, same roles), because its accent-coloured controls - the primary button, the checked toggle, the
    /// focused text box's underline, the sidebar's selection bar - read exactly these. The hover colour doubles as
    /// the accent-coloured text (a link) because it is the step that clears contrast in its mode.
    /// </summary>
    private void WriteAccent(ResourceDictionary tokens)
    {
        var accent = ColorOf(tokens, AppearanceTokens.Accent);
        var hover = ColorOf(tokens, AppearanceTokens.AccentHover);
        var onAccent = ColorOf(tokens, AppearanceTokens.OnAccent);
        var medium = ColorOf(tokens, AppearanceTokens.ToneMedium);
        var text = ColorOf(tokens, AppearanceTokens.TextPrimary);

        PutColor("SystemAccentColor", accent);
        PutColor("SystemAccentColorPrimary", accent);
        PutColor("SystemAccentColorSecondary", hover);
        PutColor("SystemAccentColorTertiary", hover);
        PutBrush("SystemAccentBrush", accent);
        PutBrush("SystemAccentColorBrush", accent);
        PutBrush("SystemAccentColorPrimaryBrush", accent);
        PutBrush("SystemAccentColorSecondaryBrush", hover);
        PutBrush("SystemAccentColorTertiaryBrush", hover);

        // WPF-UI files "attention" under the accent; this app's Medium tone is its own blue, so the two part company here.
        PutBrush("SystemFillColorAttentionBrush", medium);

        PutBrush("AccentTextFillColorPrimaryBrush", hover);
        PutBrush("AccentTextFillColorSecondaryBrush", hover);
        PutBrush("AccentTextFillColorTertiaryBrush", accent);
        PutBrush("AccentFillColorSelectedTextBackgroundBrush", accent);

        PutColor("AccentFillColorDefault", accent);
        PutBrush("AccentFillColorDefaultBrush", accent);
        PutColor("AccentFillColorSecondary", hover);
        PutBrush("AccentFillColorSecondaryBrush", hover);
        var pressed = WithAlpha(accent, 0xCC);
        PutColor("AccentFillColorTertiary", pressed);
        PutBrush("AccentFillColorTertiaryBrush", pressed);

        PutColor("TextOnAccentFillColorPrimary", onAccent);
        PutColor("TextOnAccentFillColorSecondary", WithAlpha(onAccent, 0x80));
        PutColor("TextOnAccentFillColorDisabled", WithAlpha(onAccent, 0x87));
        PutColor("TextOnAccentFillColorSelectedText", onAccent);
        PutColor("AccentTextFillColorDisabled", WithAlpha(text, 0x5D));
    }

    private void PutColor(string key, Color color)
    {
        _app.Resources[key] = color;
        _ = _writtenKeys.Add(key);
    }

    private void PutBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        _app.Resources[key] = brush;
        _ = _writtenKeys.Add(key);
    }

    private void RemoveWrittenKeys()
    {
        foreach (var key in _writtenKeys)
        {
            _app.Resources.Remove(key);
        }

        _writtenKeys.Clear();
    }

    private static Color ColorOf(ResourceDictionary tokens, string key) =>
        tokens[key] is SolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException($"Appearance token {key} is missing or is not a solid brush.");

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    // ------------------------------------------------------------------ windows

    /// <summary>
    /// Brings <paramref name="window"/> into the current look, and keeps it there: applies it now and, if the native
    /// window does not exist yet, again once it does (after WPF-UI's own <c>SourceInitialized</c> work, which would
    /// otherwise leave a Mica backdrop on a solid style for the first paint). Safe to call more than once. Windows are
    /// also attached automatically when they load, so a window that never calls this still follows.
    /// </summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (Attached.TryGetValue(window, out _))
        {
            return;
        }

        Attached.Add(window, AttachedMarker);
        ApplyToWindow(window);

        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            // Under Default WPF-UI's own SourceInitialized work (Mica from the XAML) is already what is wanted, so it is
            // left alone; the solid styles undo it here, before the first paint.
            window.SourceInitialized += (_, _) =>
            {
                if (_applied is { Style: not AppearanceStyle.Default })
                {
                    ApplyToWindow(window);
                }
            };
        }
    }

    /// <summary>
    /// The per-window part: the UI font (a window's font is what everything in it inherits, and WPF-UI sets none of
    /// its own), and for a <see cref="FluentWindow"/> the backdrop, the dark title bar and the solid background.
    /// </summary>
    private void ApplyToWindow(Window window)
    {
        if (_applied is null || _applied.Style == AppearanceStyle.Default)
        {
            // Default keeps what a window inherits with nothing set (the system message font), so a locale whose
            // message font is not Segoe UI is unchanged; this also lifts the reference a previous style put here.
            window.ClearValue(Control.FontFamilyProperty);
        }
        else
        {
            window.SetResourceReference(Control.FontFamilyProperty, AppearanceTokens.UiFontFamily);
        }

        if (window is not FluentWindow || _applied is null)
        {
            return;
        }

        try
        {
            if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
            {
                return;
            }

            var solid = _applied.Style != AppearanceStyle.Default;
            WindowBackgroundManager.UpdateBackground(
                window,
                ApplicationThemeManager.GetAppTheme(),
                solid ? WindowBackdropType.None : WindowBackdropType.Mica);

            if (solid)
            {
                // UpdateBackground leaves WPF-UI's fixed fallback grey; the token keeps the window on the style's own.
                window.SetResourceReference(Control.BackgroundProperty, AppearanceTokens.WindowBackground);
            }
        }
#pragma warning disable CA1031 // A window mid-close or without a native handle must not fail the switch for the others.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Appearance could not update window '{window.Title}': {ex.Message}");
        }
#pragma warning restore CA1031
    }

    private static void RegisterClassHandler()
    {
        if (_classHandlerRegistered)
        {
            return;
        }

        _classHandlerRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                {
                    _current?.Attach(window);
                }
            }));
    }

    // ------------------------------------------------------------------ previews

    /// <summary>
    /// The colours a style's swatch shows in the mode that is on screen: window, surface, accent, text. Read from the
    /// style's own dictionary; Default's aliases resolve against WPF-UI's current brushes, which is what Default is.
    /// </summary>
    public (Color Window, Color Surface, Color Accent, Color Text) Swatch(AppearanceStyle style)
    {
        var dark = IsDark;
        var key = (style, dark);
        if (style == AppearanceStyle.Default || !_previewCache.TryGetValue(key, out var tokens))
        {
            tokens = LoadTokens(style, dark);
            if (style != AppearanceStyle.Default)
            {
                _previewCache[key] = tokens;
            }
        }

        return (
            Flatten(ColorOf(tokens, AppearanceTokens.WindowBackground), Colors.Black),
            Flatten(ColorOf(tokens, AppearanceTokens.Surface), ColorOf(tokens, AppearanceTokens.WindowBackground)),

            // Default's accent is whatever Windows says; read from the live WPF-UI keys while another style is on screen it would
            // be that style's accent, so Default's swatch takes the system's colour instead.
            style == AppearanceStyle.Default ? _system.AccentColor : ColorOf(tokens, AppearanceTokens.Accent),
            ColorOf(tokens, AppearanceTokens.TextPrimary));
    }

    /// <summary>Composites a translucent colour over an opaque one (Default's card fill is 5% white).</summary>
    private static Color Flatten(Color top, Color behind)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte b) => (byte)Math.Round((t * a) + (b * (1 - a)));
        return Color.FromRgb(Mix(top.R, behind.R), Mix(top.G, behind.G), Mix(top.B, behind.B));
    }
}
