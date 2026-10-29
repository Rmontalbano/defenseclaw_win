using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;

namespace DefenseClaw.App.ViewModels;

/// <summary>One choice of a Settings picker: what it selects (<see cref="Value"/>), what it is called, and a sentence about it.</summary>
/// <param name="Value">The value the picker reports ("Linear", "Dark").</param>
/// <param name="Label">What the picker shows.</param>
/// <param name="Help">One sentence for the tooltip.</param>
public sealed record SettingsChoice(string Value, string Label, string Help)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings → General → Appearance (CUST-386): the style and light/dark choice the title bar's Appearance flyout makes, offered where the rest of
/// the app's preferences are. It is that same choice: a pick here calls the running appearance service (<see cref="IAppearanceControl"/>), which
/// re-skins the app and keeps it in the <c>appearance</c> section of the same settings file (<c>style</c>, <c>mode</c>); this page keeps no copy,
/// and follows a change made from the flyout or with Ctrl+Shift+L. A host built without the service has no card.
/// </summary>
public sealed partial class SettingsPanelViewModel
{
    private IAppearanceControl? _appearance;

    /// <summary>Default, Linear, TUI, Cisco: the flyout's styles, in its order.</summary>
    public IReadOnlyList<SettingsChoice> AppearanceStyles { get; } = AppearanceCatalog.Styles
        .Select(style => new SettingsChoice(style.ToString(), AppearanceCatalog.Name(style), AppearanceCatalog.Description(style)))
        .ToArray();

    /// <summary>System, Light, Dark.</summary>
    public IReadOnlyList<SettingsChoice> AppearanceModes { get; } = AppearanceCatalog.Modes
        .Select(mode => new SettingsChoice(mode.ToString(), AppearanceCatalog.Name(mode), AppearanceCatalog.Description(mode)))
        .ToArray();

    /// <summary>The chosen style's <see cref="SettingsChoice.Value"/>; null while there is no appearance service.</summary>
    [ObservableProperty]
    private string? _appearanceStyle;

    /// <summary>The chosen mode's <see cref="SettingsChoice.Value"/>; null while there is no appearance service.</summary>
    [ObservableProperty]
    private string? _appearanceMode;

    /// <summary>True when the app has an appearance service to drive (the card is shown only then).</summary>
    [ObservableProperty]
    private bool _hasAppearance;

    /// <summary>The caption under the pickers.</summary>
    public string AppearanceCaption =>
        $"Applies at once and is remembered. The title bar's Appearance button offers the same choices, and {ShellShortcuts.ToggleThemeText} switches between light and dark.";

    partial void OnAppearanceStyleChanged(string? value)
    {
        if (_syncing == 0 && _appearance is { } appearance && Enum.TryParse<AppearanceStyle>(value, out var style) && style != appearance.Style)
        {
            appearance.SetStyle(style);
        }
    }

    partial void OnAppearanceModeChanged(string? value)
    {
        if (_syncing == 0 && _appearance is { } appearance && Enum.TryParse<AppearanceMode>(value, out var mode) && mode != appearance.Mode)
        {
            appearance.SetMode(mode);
        }
    }

    /// <summary>Finds the running appearance service and follows it while the page is on screen.</summary>
    private void AttachAppearance()
    {
        DetachAppearance();
        _appearance = _platform.Appearance();
        if (_appearance is not null)
        {
            _appearance.Changed += OnAppearanceChanged;
        }

        ShowAppearance();
    }

    private void DetachAppearance()
    {
        if (_appearance is not null)
        {
            _appearance.Changed -= OnAppearanceChanged;
        }
    }

    /// <summary>Copies the service's style and mode into the pickers, without handing them back to it.</summary>
    private void ShowAppearance()
    {
        var appearance = _appearance;
        HasAppearance = appearance is not null;
        Sync(() =>
        {
            AppearanceStyle = appearance?.Style.ToString();
            AppearanceMode = appearance?.Mode.ToString();
        });
    }

    private void OnAppearanceChanged(object? sender, EventArgs e) => OnUiThread(ShowAppearance);
}
