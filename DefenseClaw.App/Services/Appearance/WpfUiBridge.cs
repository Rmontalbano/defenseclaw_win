using System.Windows;
using System.Windows.Media;
using static DefenseClaw.App.Services.Appearance.AppearanceTokens;

namespace DefenseClaw.App.Services.Appearance;

/// <summary>
/// Maps a style's tokens onto WPF-UI's own resource keys, so WPF-UI's controls (buttons, text boxes, the sidebar,
/// info bars, scroll bars, tooltips, the window itself) take the style's colours, corner radius and text size instead
/// of only the <c>Dc*</c> styles doing so.
/// <para>
/// WPF-UI cannot be re-skinned by changing a few colours: it bakes most of its brushes when its theme dictionary loads
/// (a component brush such as <c>ButtonForeground</c> is a frozen copy of <c>TextFillColorPrimary</c>), so the only
/// thing that reaches them is a later dictionary that redefines the <i>brush</i> keys themselves. That is what this is:
/// the entries below become one dictionary merged after WPF-UI's, whose values are the style's own token objects.
/// The neutral overlay fills (button and text-box backgrounds, hover and pressed states) are deliberately absent -
/// they are translucent white or black, which reads correctly over any of the styles' backgrounds - and so is the
/// accent, which WPF-UI derives from its <c>SystemAccentColor*</c> colours (see AppearanceService.WriteAccent).
/// </para>
/// <para>
/// Default is never bridged: WPF-UI's own values are the Default look. A test holds every key here to a resource
/// WPF-UI really defines, in both modes, so a renamed key cannot silently stop doing anything.
/// </para>
/// </summary>
internal static class WpfUiBridge
{
    /// <summary>WPF-UI resource key, then the token whose value it takes.</summary>
    public static IReadOnlyList<(string WpfUiKey, string Token)> Entries { get; } = new (string, string)[]
    {
        // ----- Window and layer backgrounds
        ("ApplicationBackgroundBrush", WindowBackground),
        ("WindowBackground", WindowBackground),
        ("LoadingScreenBackground", WindowBackground),
        ("SolidBackgroundFillColorBaseBrush", WindowBackground),
        ("SolidBackgroundFillColorBaseAltBrush", WindowBackground),
        ("SolidBackgroundFillColorSecondaryBrush", Inset),
        ("SolidBackgroundFillColorTertiaryBrush", Surface),
        ("SolidBackgroundFillColorQuarternaryBrush", Raised),
        ("NavigationViewContentBackground", WindowBackground),

        // ----- Cards
        ("CardBackgroundFillColorDefaultBrush", Surface),
        ("CardBackground", Surface),
        ("CardStrokeColorDefaultBrush", Border),
        ("CardStrokeColorDefaultSolidBrush", Border),
        ("CardBorderBrush", Border),

        // ----- Popups and dialogs: raised, with a hairline instead of a shadow
        ("ContentDialogBackground", Dialog),
        ("MessageBoxBackground", Dialog),
        ("FlyoutBackground", Dialog),
        ("ContextMenuBackground", Dialog),
        ("ToolTipBackground", Dialog),
        ("ComboBoxDropDownBackground", Dialog),
        ("SnackBarBackground", Dialog),
        ("ContentDialogBorderBrush", Border),
        ("FlyoutBorderBrush", Border),
        ("ContextMenuBorderBrush", Border),
        ("ToolTipBorderBrush", Border),
        ("ComboBoxDropDownBorderBrush", ControlBorder),
        ("SnackBarBorderBrush", Border),
        ("SurfaceStrokeColorFlyoutBrush", Border),
        ("SurfaceStrokeColorDefaultBrush", Border),

        // ----- Text
        ("TextFillColorPrimaryBrush", TextPrimary),
        ("TextFillColorSecondaryBrush", TextSecondary),
        ("TextFillColorTertiaryBrush", TextTertiary),
        ("TextFillColorDisabledBrush", TextTertiary),
        ("TextPlaceholderColorBrush", TextTertiary),
        ("TextControlPlaceholderForeground", TextTertiary),
        ("WindowForeground", TextPrimary),
        ("ButtonForeground", TextPrimary),
        ("ButtonForegroundPointerOver", TextPrimary),
        ("ButtonForegroundPressed", TextSecondary),
        ("TextControlForeground", TextPrimary),
        ("ComboBoxForeground", TextPrimary),
        ("ComboBoxItemForeground", TextPrimary),
        ("CheckBoxForeground", TextPrimary),
        ("RadioButtonForeground", TextPrimary),
        ("ToggleButtonForeground", TextPrimary),
        ("ToggleSwitchContentForeground", TextPrimary),
        ("ListBoxItemForeground", TextPrimary),
        ("ListViewItemForeground", TextPrimary),
        ("TreeViewItemForeground", TextPrimary),
        ("ExpanderHeaderForeground", TextPrimary),
        ("TabViewForeground", TextPrimary),
        ("TabViewItemForegroundSelected", TextPrimary),
        ("ToolTipForeground", TextPrimary),
        ("ContextMenuForeground", TextPrimary),
        ("MessageBoxForeground", TextPrimary),
        ("ContentDialogForeground", TextPrimary),
        ("SnackBarForeground", TextPrimary),
        ("CardForeground", TextPrimary),
        ("InfoBarTitleForeground", TextPrimary),
        ("LabelForeground", TextSecondary),
        ("NavigationViewItemForeground", TextPrimary),
        ("NavigationViewItemForegroundLeftFluent", TextPrimary),
        ("NavigationViewItemForegroundPointerOver", TextPrimary),
        ("NavigationViewItemForegroundPointerOverLeftFluent", TextPrimary),
        ("NavigationViewItemForegroundPressed", TextSecondary),
        ("ComboBoxDropDownGlyphForeground", TextSecondary),
        ("ScrollBarButtonArrowForeground", TextTertiary),

        // ----- Strokes: flat 1 px hairlines in place of WPF-UI's gradient "elevation" edges
        ("ControlStrokeColorDefaultBrush", ControlBorder),
        ("ControlStrokeColorSecondaryBrush", ControlBorder),
        ("ControlElevationBorderBrush", ControlBorder),
        ("TextControlElevationBorderBrush", ControlBorder),
        ("CircleElevationBorderBrush", ControlBorder),
        ("AccentControlElevationBorderBrush", Accent),
        ("ControlStrongStrokeColorDefaultBrush", TextTertiary),
        ("DividerStrokeColorDefaultBrush", Border),
        ("SeparatorBorderBrush", Border),
        ("NavigationViewItemSeparatorForeground", Border),
        ("LeftNavigationViewSeparatorBrush", Border),
        ("NavigationViewContentGridBorderBrush", Border),
        ("TabViewBorderBrush", Border),
        ("ExpanderHeaderBorderBrush", Border),
        ("ExpanderHeaderBorderPointerOverBrush", Border),
        ("ScrollBarTrackFillPointerOver", Raised),
        ("TextControlBackgroundFocused", Surface),
        ("ControlFillColorInputActiveBrush", Surface),

        // ----- Text and glyphs that sit ON the accent (WPF-UI picks black or white for its own accent)
        ("TextOnAccentFillColorPrimaryBrush", OnAccent),
        ("TextOnAccentFillColorSecondaryBrush", OnAccent),
        ("TextOnAccentFillColorSelectedTextBrush", OnAccent),
        ("AccentButtonForeground", OnAccent),
        ("AccentButtonForegroundPointerOver", OnAccent),
        ("AccentButtonForegroundPressed", OnAccent),
        ("ToggleButtonForegroundChecked", OnAccent),
        ("ToggleButtonForegroundCheckedPointerOver", OnAccent),
        ("ToggleButtonForegroundCheckedPressed", OnAccent),
        ("ToggleSwitchKnobFillOn", OnAccent),
        ("ToggleSwitchKnobFillOnPointerOver", OnAccent),
        ("ToggleSwitchKnobFillOnPressed", OnAccent),
        ("CheckBoxCheckGlyphForeground", OnAccent),
        ("RadioButtonCheckGlyphFill", OnAccent),
        ("ListBoxItemSelectedForegroundThemeBrush", OnAccent),

        // ----- Focus
        ("FocusStrokeColorOuterBrush", FocusRing),
        ("FocusStrokeColorInnerBrush", FocusRingInner),
        ("KeyboardFocusBorderColorBrush", FocusRing),

        // ----- Tones: WPF-UI's status brushes take the style's five
        ("SystemFillColorCriticalBrush", ToneCritical),
        ("SystemFillColorCriticalBackgroundBrush", ToneCriticalSubtle),
        ("SystemFillColorCautionBrush", ToneHigh),
        ("SystemFillColorCautionBackgroundBrush", ToneHighSubtle),
        ("SystemFillColorAttentionBackgroundBrush", ToneMediumSubtle),
        ("SystemFillColorSuccessBrush", ToneOk),
        ("SystemFillColorSuccessBackgroundBrush", ToneOkSubtle),
        ("SystemFillColorNeutralBrush", ToneNeutral),
        ("SystemFillColorNeutralBackgroundBrush", ToneNeutralSubtle),
        ("InfoBarErrorSeverityIconBackground", ToneCritical),
        ("InfoBarErrorSeverityBackgroundBrush", ToneCriticalSubtle),
        ("InfoBarErrorSeverityBorderBrush", Border),
        ("InfoBarWarningSeverityIconBackground", ToneHigh),
        ("InfoBarWarningSeverityBackgroundBrush", ToneHighSubtle),
        ("InfoBarWarningSeverityBorderBrush", Border),
        ("InfoBarSuccessSeverityIconBackground", ToneOk),
        ("InfoBarSuccessSeverityBackgroundBrush", ToneOkSubtle),
        ("InfoBarSuccessSeverityBorderBrush", Border),
        ("InfoBarInformationalSeverityIconBackground", ToneMedium),
        ("InfoBarInformationalSeverityBackgroundBrush", ToneMediumSubtle),
        ("InfoBarInformationalSeverityBorderBrush", Border),
        ("InfoBarBorderBrush", Border),
        ("InfoBadgeCriticalSeverityBackgroundBrush", ToneCritical),
        ("InfoBadgeCautionSeverityBackgroundBrush", ToneHigh),
        ("InfoBadgeSuccessSeverityBackgroundBrush", ToneOk),
        ("InfoBadgeAttentionSeverityBackgroundBrush", ToneMedium),
        ("InfoBadgeInformationalSeverityBackgroundBrush", ToneNeutral),

        // ----- Shape, type: the resources WPF-UI's templates read for radius and size
        ("ControlCornerRadius", ControlRadius),
        ("OverlayCornerRadius", ControlRadius),
        ("PopupCornerRadius", RadiusL),
        ("ValueInfoBadgeStyleCornerRadius", RadiusL),
        ("ControlContentThemeFontSize", FontSize),
        ("ContentControlFontSize", FontSize),
        ("ContentControlThemeFontFamily", UiFontFamily),
    };

    /// <summary>
    /// Adds the bridge for <paramref name="tokens"/> to <paramref name="target"/>. The values are the token objects
    /// themselves, so a bridged WPF-UI key and its token can never disagree.
    /// </summary>
    public static void AddTo(ResourceDictionary target, ResourceDictionary tokens, AppearanceStyle style)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tokens);

        foreach (var (wpfUiKey, token) in Entries)
        {
            if (tokens[token] is { } value)
            {
                target[wpfUiKey] = value;
            }
        }

        if (style == AppearanceStyle.Cisco && tokens[Accent] is SolidColorBrush accent)
        {
            // WPF-UI's "informational" is this app's Medium, which is blue in the other styles and YELLOW in Cisco (the Mac's
            // severity ramp), where an information bar or badge in yellow would read as a warning. Information takes the accent.
            var tint = new SolidColorBrush(Color.FromArgb(0x26, accent.Color.R, accent.Color.G, accent.Color.B));
            tint.Freeze();

            target["InfoBarInformationalSeverityIconBackground"] = accent;
            target["InfoBarInformationalSeverityBackgroundBrush"] = tint;
            target["InfoBadgeAttentionSeverityBackgroundBrush"] = accent;
            target["SystemFillColorAttentionBackgroundBrush"] = tint;
        }
    }
}
