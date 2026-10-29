using CommunityToolkit.Mvvm.ComponentModel;
using DefenseClaw.App.Services;

namespace DefenseClaw.App.ViewModels;

/// <summary>
/// Settings in four areas (CUST-386), after the Mac's settings window (<c>Features/AppSettingsView.swift</c> of v1.1.34: a <c>TabView</c> of
/// General, Monitoring, Notifications and Connection). The areas are presentation only: every control is still a read or write of the same
/// <see cref="AppServices.Settings"/> key it always was, and which area is on screen is not saved. The page remembers it for the session (the
/// catalog keeps one view-model per run), so the footer entry, Ctrl+, and the tray reopen it where the operator left it; a
/// <see cref="SettingsAreaLink"/> (the Overview's "Review Installation") opens a given area.
/// </summary>
public sealed partial class SettingsPanelViewModel : IAcceptsNavigation
{
    /// <summary>The area on screen. General until the operator (or a link) picks another; not persisted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralArea))]
    [NotifyPropertyChangedFor(nameof(IsMonitoringArea))]
    [NotifyPropertyChangedFor(nameof(IsNotificationsArea))]
    [NotifyPropertyChangedFor(nameof(IsConnectionArea))]
    private SettingsArea _selectedArea = SettingsArea.General;

    public bool IsGeneralArea => SelectedArea == SettingsArea.General;

    public bool IsMonitoringArea => SelectedArea == SettingsArea.Monitoring;

    public bool IsNotificationsArea => SelectedArea == SettingsArea.Notifications;

    public bool IsConnectionArea => SelectedArea == SettingsArea.Connection;

    /// <summary>
    /// Raised (on the UI thread) when a link asks for a block within an area (<see cref="SettingsAreaLink.Section"/>): the view scrolls it into
    /// view once the area has been laid out.
    /// </summary>
    internal event EventHandler<string>? SectionRequested;

    /// <summary>A <see cref="SettingsAreaLink"/> shows its area (and asks the view for its block); anything else is ignored.</summary>
    public void Accept(object payload)
    {
        if (payload is not SettingsAreaLink link || !Enum.IsDefined(link.Area))
        {
            return;
        }

        SelectedArea = link.Area;
        if (link.Section is { Length: > 0 } section)
        {
            SectionRequested?.Invoke(this, section);
        }
    }

    // ------------------------------------------------------------------ where a Connection message is said

    /// <summary>The card the last <see cref="ConnectionMessage"/> belongs to, so it is said beside the button that caused it.</summary>
    private enum MessageCard
    {
        Files,
        Cli,
        Companion,
    }

    private MessageCard _messageCard = MessageCard.Cli;

    /// <summary>The last message when a Files row said it (Copy, Open folder); empty otherwise.</summary>
    public string FilesMessage => _messageCard == MessageCard.Files ? ConnectionMessage : string.Empty;

    /// <summary>The last message when the CLI card said it (Reload config.yaml, copy or show defenseclaw.exe); empty otherwise.</summary>
    public string CliMessage => _messageCard == MessageCard.Cli ? ConnectionMessage : string.Empty;

    /// <summary>The last message when a Companion app files row said it; empty otherwise.</summary>
    public string CompanionMessage => _messageCard == MessageCard.Companion ? ConnectionMessage : string.Empty;

    private void Say(MessageCard card, string message)
    {
        _messageCard = card;
        ConnectionMessage = message;
        OnPropertyChanged(nameof(FilesMessage));
        OnPropertyChanged(nameof(CliMessage));
        OnPropertyChanged(nameof(CompanionMessage));
    }
}
