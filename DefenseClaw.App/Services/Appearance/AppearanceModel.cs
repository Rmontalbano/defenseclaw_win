namespace DefenseClaw.App.Services.Appearance;

/// <summary>
/// The look of the app. Independent of <see cref="AppearanceMode"/>: every style has a light and a dark
/// form, so the operator picks a style once and flips light/dark as often as they like.
/// </summary>
internal enum AppearanceStyle
{
    /// <summary>The Fluent look the app has always had: Mica, the Windows accent, WPF-UI's own palette.</summary>
    Default,

    /// <summary>A clean product UI: solid backgrounds, hairline borders, compact density, an indigo accent.</summary>
    Linear,

    /// <summary>A terminal look derived from the DefenseClaw CLI's own TUI theme: monospace, square, flat.</summary>
    Tui,
}

/// <summary>Whether the app is light, dark, or follows the operating system (and keeps following it).</summary>
internal enum AppearanceMode
{
    System,
    Light,
    Dark,
}

/// <summary>What is persisted: the two choices. Immutable; the defaults are what a fresh install gets.</summary>
internal sealed record AppearanceSettings(AppearanceStyle Style, AppearanceMode Mode)
{
    /// <summary>The Default style, following the system - today's behaviour before this setting existed.</summary>
    public static AppearanceSettings Defaults { get; } = new(AppearanceStyle.Default, AppearanceMode.System);
}

/// <summary>
/// What the UI needs from the appearance service, so the shell's command list, the title-bar flyout and the
/// tests can work against a fake without an <see cref="System.Windows.Application"/>.
/// </summary>
internal interface IAppearanceControl
{
    /// <summary>The style the operator chose (the effective style differs only under Windows high contrast).</summary>
    AppearanceStyle Style { get; }

    AppearanceMode Mode { get; }

    /// <summary>True when the app is dark right now, whichever way that was decided (mode System follows the OS).</summary>
    bool IsDark { get; }

    /// <summary>Raised on the UI thread after any change to the style, the mode, or the OS theme the app follows.</summary>
    event EventHandler? Changed;

    void SetStyle(AppearanceStyle style);

    void SetMode(AppearanceMode mode);

    /// <summary>
    /// Flips to the explicit opposite of what is on screen. From <see cref="AppearanceMode.System"/> that means the
    /// opposite of the current OS-driven look, and the mode becomes explicit.
    /// </summary>
    void ToggleLightDark();

    /// <summary>
    /// The four colours a style's swatch shows in the mode on screen: window, card, accent, text. Read from the style's
    /// own dictionary, so a swatch is always what choosing it would look like.
    /// </summary>
    (System.Windows.Media.Color Window, System.Windows.Media.Color Surface, System.Windows.Media.Color Accent, System.Windows.Media.Color Text) Swatch(AppearanceStyle style);
}

/// <summary>Display names and where each style's tokens live.</summary>
internal static class AppearanceCatalog
{
    public static IReadOnlyList<AppearanceStyle> Styles { get; } = new[]
    {
        AppearanceStyle.Default,
        AppearanceStyle.Linear,
        AppearanceStyle.Tui,
    };

    public static IReadOnlyList<AppearanceMode> Modes { get; } = new[]
    {
        AppearanceMode.System,
        AppearanceMode.Light,
        AppearanceMode.Dark,
    };

    public static string Name(AppearanceStyle style) => style switch
    {
        AppearanceStyle.Linear => "Linear",
        AppearanceStyle.Tui => "TUI",
        _ => "Default",
    };

    public static string Description(AppearanceStyle style) => style switch
    {
        AppearanceStyle.Linear => "Clean product UI: solid, hairline borders, indigo accent.",
        AppearanceStyle.Tui => "Terminal look: monospace, square, matches the CLI.",
        _ => "The Windows look: Mica and your accent colour.",
    };

    public static string Name(AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => "Light",
        AppearanceMode.Dark => "Dark",
        _ => "System",
    };

    public static string Description(AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => "Always light.",
        AppearanceMode.Dark => "Always dark.",
        _ => "Follow Windows, and switch when it does.",
    };

    /// <summary>The XAML dictionary holding <paramref name="style"/>'s tokens for the given mode.</summary>
    public static Uri TokenSource(AppearanceStyle style, bool dark)
    {
        var file = style switch
        {
            AppearanceStyle.Linear => dark ? "Linear.Dark.xaml" : "Linear.Light.xaml",
            AppearanceStyle.Tui => dark ? "Tui.Dark.xaml" : "Tui.Light.xaml",

            // One file for both modes: Default's tokens alias WPF-UI's brushes, and WPF-UI decides light or dark.
            _ => "Default.xaml",
        };

        return new Uri($"pack://application:,,,/DefenseClaw.App;component/Themes/Styles/{file}", UriKind.Absolute);
    }
}

/// <summary>
/// The token keys the code (rather than XAML) refers to, and the full list the style dictionaries must each define
/// (a test holds them to it). The vocabulary is documented in <c>Themes\Styles\Default.xaml</c>.
/// </summary>
internal static class AppearanceTokens
{
    public const string WindowBackground = "DcWindowBackgroundBrush";
    public const string Surface = "DcSurfaceBrush";
    public const string Raised = "DcRaisedBrush";
    public const string Inset = "DcInsetBrush";
    public const string Dialog = "DcDialogBrush";
    public const string Subtle = "DcSubtleBrush";
    public const string Selected = "DcSelectedBrush";
    public const string Border = "DcBorderBrush";
    public const string ControlBorder = "DcControlBorderBrush";
    public const string TextPrimary = "DcTextPrimaryBrush";
    public const string TextSecondary = "DcTextSecondaryBrush";
    public const string TextTertiary = "DcTextTertiaryBrush";
    public const string Accent = "DcAccentBrush";
    public const string AccentHover = "DcAccentHoverBrush";
    public const string OnAccent = "DcOnAccentBrush";
    public const string FocusRing = "DcFocusRingBrush";
    public const string FocusRingInner = "DcFocusRingInnerBrush";

    public const string ToneCritical = "DcToneCriticalBrush";
    public const string ToneCriticalSubtle = "DcToneCriticalSubtleBrush";
    public const string ToneHigh = "DcToneHighBrush";
    public const string ToneHighSubtle = "DcToneHighSubtleBrush";
    public const string ToneMedium = "DcToneMediumBrush";
    public const string ToneMediumSubtle = "DcToneMediumSubtleBrush";
    public const string ToneOk = "DcToneOkBrush";
    public const string ToneOkSubtle = "DcToneOkSubtleBrush";
    public const string ToneNeutral = "DcToneNeutralBrush";
    public const string ToneNeutralSubtle = "DcToneNeutralSubtleBrush";

    // Icons. The default and the muted icon colour, the named tint palette, and the section -> tint mapping twice: the
    // sidebar and page headers (NavIcon*) and the card headers (Section*), so a style can tint one and not the other.
    public const string Icon = "DcIconBrush";
    public const string IconMuted = "DcIconMutedBrush";

    public const string TintIndigo = "DcTintIndigoBrush";
    public const string TintBlue = "DcTintBlueBrush";
    public const string TintViolet = "DcTintVioletBrush";
    public const string TintTeal = "DcTintTealBrush";
    public const string TintGreen = "DcTintGreenBrush";
    public const string TintAmber = "DcTintAmberBrush";
    public const string TintOrange = "DcTintOrangeBrush";
    public const string TintRed = "DcTintRedBrush";
    public const string TintPink = "DcTintPinkBrush";
    public const string TintGray = "DcTintGrayBrush";

    public const string SectionOverview = "DcSectionOverviewBrush";
    public const string SectionObserve = "DcSectionObserveBrush";
    public const string SectionGovern = "DcSectionGovernBrush";
    public const string SectionDiscover = "DcSectionDiscoverBrush";
    public const string SectionSetup = "DcSectionSetupBrush";
    public const string SectionUpdates = "DcSectionUpdatesBrush";

    public const string NavIconOverview = "DcNavIconOverviewBrush";
    public const string NavIconObserve = "DcNavIconObserveBrush";
    public const string NavIconGovern = "DcNavIconGovernBrush";
    public const string NavIconDiscover = "DcNavIconDiscoverBrush";
    public const string NavIconSetup = "DcNavIconSetupBrush";
    public const string NavIconUpdates = "DcNavIconUpdatesBrush";

    /// <summary>Which shapes the status and severity glyphs draw: "Dot" (Default), "Linear" or "Terminal". A string, not a brush.</summary>
    public const string GlyphFamily = "DcGlyphFamily";

    public const string UiFontFamily = "DcUiFontFamily";
    public const string MonoFontFamily = "DcMonoFontFamily";
    public const string FontSize = "DcFontSize";

    public const string RadiusS = "DcRadiusS";
    public const string RadiusM = "DcRadiusM";
    public const string RadiusL = "DcRadiusL";
    public const string ControlRadius = "DcControlRadius";
    public const string BarRadius = "DcBarRadius";
    public const string BorderThickness = "DcBorderThickness";
    public const string FocusRadius = "DcFocusRadius";
    public const string FocusRadiusInner = "DcFocusRadiusInner";
    public const string FocusRadiusInset = "DcFocusRadiusInset";
    public const string FocusRadiusInsetInner = "DcFocusRadiusInsetInner";

    public const string CardPadding = "DcCardPadding";
    public const string CardPaddingCompact = "DcCardPaddingCompact";
    public const string CardPaddingFlush = "DcCardPaddingFlush";

    public const string FlyoutShadow = "DcFlyoutShadow";

    /// <summary>Every colour token, in the order the swatches and the contrast test read them.</summary>
    public static IReadOnlyList<string> Brushes { get; } = new[]
    {
        WindowBackground, Surface, Raised, Inset, Dialog, Subtle, Selected, Border, ControlBorder,
        TextPrimary, TextSecondary, TextTertiary,
        Accent, AccentHover, OnAccent, FocusRing, FocusRingInner,
        ToneCritical, ToneCriticalSubtle, ToneHigh, ToneHighSubtle, ToneMedium, ToneMediumSubtle,
        ToneOk, ToneOkSubtle, ToneNeutral, ToneNeutralSubtle,
        Icon, IconMuted,
        TintIndigo, TintBlue, TintViolet, TintTeal, TintGreen, TintAmber, TintOrange, TintRed, TintPink, TintGray,
        SectionOverview, SectionObserve, SectionGovern, SectionDiscover, SectionSetup, SectionUpdates,
        NavIconOverview, NavIconObserve, NavIconGovern, NavIconDiscover, NavIconSetup, NavIconUpdates,
    };

    /// <summary>
    /// The icon colours that stand on the window or a card and carry meaning as shapes, so the contrast test holds each to
    /// 3:1 there (WCAG non-text contrast): the default and muted icon, the tint palette, and both section mappings. The
    /// tone brushes, which the status and severity glyphs use, are held by the tone rules already.
    /// </summary>
    public static IReadOnlyList<string> IconColours { get; } = new[]
    {
        Icon, IconMuted,
        TintIndigo, TintBlue, TintViolet, TintTeal, TintGreen, TintAmber, TintOrange, TintRed, TintPink, TintGray,
        SectionOverview, SectionObserve, SectionGovern, SectionDiscover, SectionSetup, SectionUpdates,
        NavIconOverview, NavIconObserve, NavIconGovern, NavIconDiscover, NavIconSetup, NavIconUpdates,
    };

    /// <summary>Every token a style dictionary must define: the brushes plus fonts, size, shape, density and effect.</summary>
    public static IReadOnlyList<string> All { get; } = Brushes.Concat(new[]
    {
        GlyphFamily,
        UiFontFamily, MonoFontFamily, FontSize,
        RadiusS, RadiusM, RadiusL, ControlRadius, BarRadius, BorderThickness,
        FocusRadius, FocusRadiusInner, FocusRadiusInset, FocusRadiusInsetInner,
        CardPadding, CardPaddingCompact, CardPaddingFlush,
        FlyoutShadow,
    }).ToArray();
}
