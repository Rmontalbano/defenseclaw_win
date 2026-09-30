using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using Wpf.Ui.Appearance;

namespace DefenseClaw.App.Tests.Appearance;

/// <summary>
/// The appearance service against the real dictionaries: mode resolution, the swap actually changing what a live
/// element resolves, Default staying exactly what it was, and the bridge onto WPF-UI. All on the shared UI thread.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class AppearanceServiceTests
{
    private static Color Brush(string key) => AppearanceFixture.ColorOf(key);

    private static CornerRadius Radius(string key) => (CornerRadius)AppearanceFixture.Find(key)!;

    // ------------------------------------------------------------------ mode resolution

    [Theory]
    [InlineData("System", true, true)]
    [InlineData("System", false, false)]
    [InlineData("Light", true, false)]
    [InlineData("Dark", false, true)]
    public void Mode_resolves_to_light_or_dark_and_only_System_asks_Windows(string modeName, bool osIsDark, bool expectedDark)
    {
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, mode), configureSystem: s => s.IsDark = osIsDark);

        Assert.Equal(expectedDark, fixture.Service.IsDark);
        Assert.Equal(expectedDark ? ApplicationTheme.Dark : ApplicationTheme.Light, UiThread.Run(ApplicationThemeManager.GetAppTheme));
    }

    [Fact]
    public void System_mode_follows_Windows_live_and_an_explicit_mode_does_not()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.System));
        var changes = 0;
        fixture.Service.Changed += (_, _) => changes++;

        Assert.True(fixture.Service.IsDark);
        Assert.Equal(Color.FromRgb(0x0F, 0x10, 0x11), UiThread.Run(() => Brush("DcWindowBackgroundBrush")));

        fixture.System.IsDark = false;
        UiThread.Run(fixture.Service.OnSystemPreferenceChanged);

        Assert.False(fixture.Service.IsDark);
        Assert.Equal(1, changes);
        Assert.Equal(Color.FromRgb(0xFC, 0xFC, 0xFD), UiThread.Run(() => Brush("DcWindowBackgroundBrush")));
        Assert.Equal(0, fixture.Store.Saves);   // following Windows is not a choice, so nothing is saved

        // Once the operator picks Dark, Windows going light again is no longer their business.
        UiThread.Run(() => fixture.Service.SetMode(AppearanceMode.Dark));
        fixture.System.IsDark = true;
        UiThread.Run(fixture.Service.OnSystemPreferenceChanged);
        fixture.System.IsDark = false;
        UiThread.Run(fixture.Service.OnSystemPreferenceChanged);
        Assert.True(fixture.Service.IsDark);
    }

    [Fact]
    public void An_irrelevant_Windows_preference_change_does_no_work()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.System));
        var changes = 0;
        fixture.Service.Changed += (_, _) => changes++;

        UiThread.Run(fixture.Service.OnSystemPreferenceChanged);
        UiThread.Run(fixture.Service.OnSystemPreferenceChanged);

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Toggle_goes_to_the_explicit_opposite_of_what_is_on_screen_and_saves_it()
    {
        // In System mode on a dark PC the screen is dark, so the toggle means Light - not "Dark" and not "System".
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.System));

        UiThread.Run(fixture.Service.ToggleLightDark);
        Assert.Equal(AppearanceMode.Light, fixture.Service.Mode);
        Assert.False(fixture.Service.IsDark);
        Assert.Equal(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Light), fixture.Store.Stored);

        UiThread.Run(fixture.Service.ToggleLightDark);
        Assert.Equal(AppearanceMode.Dark, fixture.Service.Mode);
        Assert.True(fixture.Service.IsDark);
    }

    [Fact]
    public void Choosing_what_is_already_chosen_changes_and_saves_nothing()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));
        var changes = 0;
        fixture.Service.Changed += (_, _) => changes++;

        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Linear));
        UiThread.Run(() => fixture.Service.SetMode(AppearanceMode.Dark));

        Assert.Equal(0, changes);
        Assert.Equal(0, fixture.Store.Saves);
    }

    [Fact]
    public void The_saved_choice_is_what_starts_up()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.Light));

        Assert.Equal(AppearanceStyle.Tui, fixture.Service.Style);
        Assert.Equal(AppearanceMode.Light, fixture.Service.Mode);
        Assert.False(fixture.Service.IsDark);
        Assert.Equal(Color.FromRgb(0xF3, 0xF6, 0xFB), UiThread.Run(() => Brush("DcWindowBackgroundBrush")));
    }

    [Fact]
    public void High_contrast_sets_every_style_aside_for_Default_and_the_system_theme()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark), configureSystem: s => s.IsHighContrast = true);

        Assert.Equal(AppearanceStyle.Default, fixture.Service.EffectiveStyle);
        Assert.Equal(AppearanceStyle.Linear, fixture.Service.Style);   // the choice survives, for when high contrast ends
        Assert.NotEqual(Color.FromRgb(0x0F, 0x10, 0x11), UiThread.Run(() => Brush("DcWindowBackgroundBrush")));
    }

    [Fact]
    public void A_saved_look_that_cannot_be_applied_falls_back_to_Default_instead_of_stopping_startup()
    {
        using var fixture = new AppearanceFixture(
            new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.System),
            configureSystem: s => s.ThrowOnNextRead = true);

        Assert.Equal(AppearanceStyle.Default, fixture.Service.Style);
        Assert.Equal(AppearanceMode.System, fixture.Service.Mode);
        Assert.Equal(AppearanceStyle.Default, fixture.Service.EffectiveStyle);
    }

    [Fact]
    public void A_choice_that_cannot_be_applied_is_undone_and_not_saved_and_does_not_throw()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Tui, AppearanceMode.System));
        var before = UiThread.Run(() => Brush("DcWindowBackgroundBrush"));

        fixture.System.ThrowOnNextRead = true;
        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Linear));

        Assert.Equal(AppearanceStyle.Tui, fixture.Service.Style);
        Assert.Equal(0, fixture.Store.Saves);
        Assert.Equal(before, UiThread.Run(() => Brush("DcWindowBackgroundBrush")));

        // ...and the next attempt, with Windows answering again, goes through.
        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Linear));
        Assert.Equal(AppearanceStyle.Linear, fixture.Service.Style);
        Assert.Equal(1, fixture.Store.Saves);
    }

    // ------------------------------------------------------------------ the swap changes what a live element resolves

    [Fact]
    public void Switching_style_changes_the_brushes_fonts_radii_and_padding_a_live_element_resolves()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var window = new Window();
            try
            {
                var card = new Border { Style = (Style)Application.Current.FindResource("DcCard") };
                var text = new TextBlock { Style = (Style)Application.Current.FindResource("DcBody"), Text = "x" };
                var mono = new TextBlock { Style = (Style)Application.Current.FindResource("DcMono"), Text = "x" };
                var badge = new Border { Style = (Style)Application.Current.FindResource("DcBadge"), Tag = "Critical" };
                window.Content = new StackPanel { Children = { card, text, mono, badge } };

                var defaultCard = Snapshot(card, text, mono, badge);

                fixture.Service.SetStyle(AppearanceStyle.Linear);
                var linear = Snapshot(card, text, mono, badge);

                fixture.Service.SetStyle(AppearanceStyle.Tui);
                var tui = Snapshot(card, text, mono, badge);

                Assert.Equal(new CornerRadius(8), defaultCard.CardRadius);
                Assert.Equal(new Thickness(16), defaultCard.CardPadding);
                Assert.Equal(14, defaultCard.BodySize);

                Assert.Equal(new CornerRadius(8), linear.CardRadius);
                Assert.Equal(new Thickness(12), linear.CardPadding);
                Assert.Equal(13, linear.BodySize);
                Assert.Equal(Color.FromRgb(0x15, 0x16, 0x19), linear.CardFill);
                Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xF8), linear.BodyText);
                Assert.Equal(Color.FromRgb(0xEB, 0x57, 0x57), linear.CriticalText);
                Assert.Equal("JetBrains Mono, Cascadia Mono, Consolas", linear.MonoFamily);

                Assert.Equal(new CornerRadius(0), tui.CardRadius);
                Assert.Equal(new CornerRadius(0), tui.BadgeRadius);
                Assert.Equal(Color.FromRgb(0x0D, 0x12, 0x20), tui.CardFill);
                Assert.Equal(Color.FromRgb(0xF8, 0x71, 0x71), tui.CriticalText);
                Assert.Equal("Cascadia Mono, Cascadia Code, Consolas, Courier New", tui.MonoFamily);

                // ...and none of it was the same as Default's (a swap that does nothing would pass the lines above by luck).
                Assert.NotEqual(defaultCard.CardFill, linear.CardFill);
                Assert.NotEqual(linear.CardFill, tui.CardFill);
                Assert.NotEqual(defaultCard.CriticalText, linear.CriticalText);

                fixture.Service.SetStyle(AppearanceStyle.Default);
                Assert.Equal(defaultCard, Snapshot(card, text, mono, badge));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private sealed record Look(
        CornerRadius CardRadius,
        Thickness CardPadding,
        Color CardFill,
        double BodySize,
        Color BodyText,
        string MonoFamily,
        CornerRadius BadgeRadius,
        Color CriticalText);

    private static Look Snapshot(Border card, TextBlock body, TextBlock mono, Border badge) => new(
        card.CornerRadius,
        card.Padding,
        ((SolidColorBrush)card.Background).Color,
        body.FontSize,
        ((SolidColorBrush)body.Foreground).Color,
        mono.FontFamily.Source,
        badge.CornerRadius,
        ((SolidColorBrush)(Brush)badge.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)).Color);

    // ------------------------------------------------------------------ Default is the look the app always had

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Default_tokens_are_exactly_the_WPF_UI_brushes_the_Dc_styles_used_before_tokens_existed(string modeName)
    {
        var mode = Enum.Parse<AppearanceMode>(modeName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, mode));

        UiThread.Run(() =>
        {
            foreach (var (token, wpfUi) in DefaultAliases)
            {
                var expected = Effective((SolidColorBrush)Application.Current.FindResource(wpfUi));
                var actual = Effective((SolidColorBrush)Application.Current.FindResource(token));
                Assert.True(
                    (expected.R, expected.G, expected.B) == (actual.R, actual.G, actual.B) && Math.Abs(expected.A - actual.A) <= 1,
                    $"{token} should be {wpfUi} ({expected}) in {mode} but is {actual}");
            }

            Assert.Equal(new CornerRadius(4), Radius("DcRadiusS"));
            Assert.Equal(new CornerRadius(6), Radius("DcRadiusM"));
            Assert.Equal(new CornerRadius(8), Radius("DcRadiusL"));
            Assert.Equal(new Thickness(16), (Thickness)AppearanceFixture.Find("DcCardPadding")!);
            Assert.Equal(14.0, (double)AppearanceFixture.Find("DcFontSize")!);
            Assert.Equal("Cascadia Mono, Consolas, Courier New", ((FontFamily)AppearanceFixture.Find("DcMonoFontFamily")!).Source);
        });
    }

    /// <summary>A brush's colour with its Opacity folded into alpha: WPF-UI writes some as opaque colour + Opacity, the token layer as alpha.</summary>
    private static Color Effective(SolidColorBrush brush) =>
        Color.FromArgb((byte)Math.Round(brush.Color.A * brush.Opacity), brush.Color.R, brush.Color.G, brush.Color.B);

    /// <summary>Each Default token and the WPF-UI brush the Dc styles referenced directly before the token layer.</summary>
    private static readonly (string Token, string WpfUi)[] DefaultAliases =
    {
        ("DcWindowBackgroundBrush", "ApplicationBackgroundBrush"),
        ("DcDialogBrush", "ApplicationBackgroundBrush"),
        ("DcSurfaceBrush", "CardBackgroundFillColorDefaultBrush"),
        ("DcRaisedBrush", "ControlFillColorDefaultBrush"),
        ("DcInsetBrush", "SolidBackgroundFillColorSecondaryBrush"),
        ("DcSubtleBrush", "SubtleFillColorSecondaryBrush"),
        ("DcSelectedBrush", "ControlFillColorSecondaryBrush"),
        ("DcZebraBrush", "SubtleFillColorTertiaryBrush"),
        ("DcBorderBrush", "CardStrokeColorDefaultBrush"),
        ("DcControlBorderBrush", "ControlStrokeColorDefaultBrush"),
        ("DcTextPrimaryBrush", "TextFillColorPrimaryBrush"),
        ("DcTextSecondaryBrush", "TextFillColorSecondaryBrush"),
        ("DcTextTertiaryBrush", "TextFillColorTertiaryBrush"),
        ("DcFocusRingBrush", "FocusStrokeColorOuterBrush"),
        ("DcFocusRingInnerBrush", "FocusStrokeColorInnerBrush"),
        ("DcToneCriticalBrush", "SystemFillColorCriticalBrush"),
        ("DcToneCriticalSubtleBrush", "SystemFillColorCriticalBackgroundBrush"),
        ("DcToneHighBrush", "SystemFillColorCautionBrush"),
        ("DcToneHighSubtleBrush", "SystemFillColorCautionBackgroundBrush"),
        ("DcToneMediumBrush", "SystemFillColorAttentionBrush"),
        ("DcToneMediumSubtleBrush", "SystemFillColorAttentionBackgroundBrush"),
        ("DcToneOkBrush", "SystemFillColorSuccessBrush"),
        ("DcToneOkSubtleBrush", "SystemFillColorSuccessBackgroundBrush"),
        ("DcToneNeutralBrush", "SystemFillColorNeutralBrush"),
        ("DcToneNeutralSubtleBrush", "SystemFillColorNeutralBackgroundBrush"),
        ("DcAccentBrush", "AccentFillColorDefaultBrush"),
        ("DcAccentHoverBrush", "AccentFillColorSecondaryBrush"),
        ("DcOnAccentBrush", "TextOnAccentFillColorPrimaryBrush"),
    };

    // ------------------------------------------------------------------ the bridge onto WPF-UI

    [Fact]
    public void Linear_and_TUI_reach_WPF_UI_controls_colours_radius_and_text_size_through_the_bridge()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xF8), Brush("TextFillColorPrimaryBrush"));
            Assert.Equal(Color.FromRgb(0xF7, 0xF8, 0xF8), Brush("ButtonForeground"));
            Assert.Equal(Color.FromRgb(0x0F, 0x10, 0x11), Brush("ApplicationBackgroundBrush"));
            Assert.Equal(Color.FromRgb(0xEB, 0x57, 0x57), Brush("SystemFillColorCriticalBrush"));
            Assert.Equal(new CornerRadius(6), Radius("ControlCornerRadius"));
            Assert.Equal(13.0, (double)AppearanceFixture.Find("ControlContentThemeFontSize")!);
        });

        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Tui));

        UiThread.Run(() =>
        {
            Assert.Equal(Color.FromRgb(0xE6, 0xF1, 0xFF), Brush("TextFillColorPrimaryBrush"));
            Assert.Equal(new CornerRadius(0), Radius("ControlCornerRadius"));
            Assert.Equal(new CornerRadius(0), Radius("PopupCornerRadius"));
        });
    }

    [Fact]
    public void The_accent_reaches_WPF_UI_through_its_own_accent_keys_and_medium_stays_a_separate_blue()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            Assert.Equal(Color.FromRgb(0x5E, 0x6A, 0xD2), Brush("AccentFillColorDefaultBrush"));
            Assert.Equal(Color.FromRgb(0x5E, 0x6A, 0xD2), (Color)Application.Current.Resources["SystemAccentColorPrimary"]);
            Assert.Equal(Color.FromRgb(0x4E, 0xA7, 0xFC), Brush("SystemFillColorAttentionBrush"));
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), (Color)Application.Current.Resources["TextOnAccentFillColorPrimary"]);
        });
    }

    [Fact]
    public void Going_back_to_Default_leaves_nothing_of_the_other_style_behind()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Default, AppearanceMode.Dark));

        var before = UiThread.Run(() => (Brush("AccentFillColorDefaultBrush"), Brush("SystemFillColorAttentionBrush"), Brush("TextFillColorPrimaryBrush"), Radius("ControlCornerRadius")));

        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Tui));
        UiThread.Run(() => fixture.Service.SetStyle(AppearanceStyle.Default));

        var after = UiThread.Run(() => (Brush("AccentFillColorDefaultBrush"), Brush("SystemFillColorAttentionBrush"), Brush("TextFillColorPrimaryBrush"), Radius("ControlCornerRadius")));
        Assert.Equal(before, after);
        Assert.DoesNotContain("SystemAccentColorPrimaryBrush", UiThread.Run(() => Application.Current.Resources.Keys.Cast<object>().Select(k => k.ToString()).ToArray()));
    }

    [Fact]
    public void Every_bridged_WPF_UI_key_exists_in_WPF_UI_in_both_modes()
    {
        var missing = new List<string>();
        foreach (var mode in new[] { ApplicationTheme.Dark, ApplicationTheme.Light })
        {
            UiThread.Run(() =>
            {
                ApplicationThemeManager.Apply(mode, updateAccent: false);
                foreach (var (key, _) in WpfUiBridge.Entries)
                {
                    if (Application.Current.TryFindResource(key) is null)
                    {
                        missing.Add($"{key} ({mode})");
                    }
                }
            });
        }

        UiThread.Run(() => ApplicationThemeManager.Apply(ApplicationTheme.Dark, updateAccent: false));

        Assert.True(missing.Count == 0, "Bridged keys WPF-UI does not define: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_bridge_entry_names_a_token_that_every_style_defines()
    {
        var tokens = new HashSet<string>(AppearanceTokens.All, StringComparer.Ordinal);
        Assert.All(WpfUiBridge.Entries, entry => Assert.Contains(entry.Token, tokens));
    }

    // ------------------------------------------------------------------ Fluent ThemeMode

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("System")]
    public void The_Fluent_theme_mode_follows_the_same_decision(string expected)
    {
        var mode = Enum.Parse<AppearanceMode>(expected);
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, mode), manageFluentThemeMode: true);

        Assert.Equal(expected, UiThread.Run(() => Application.Current.ThemeMode.Value));

        UiThread.Run(fixture.Service.Uninstall);
        Assert.Equal("None", UiThread.Run(() => Application.Current.ThemeMode.Value));
    }
}
