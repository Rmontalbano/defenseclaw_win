namespace DefenseClaw.App.ViewModels.ConfigEditor;

/// <summary>What the operator is about to do that would drop the edits in the editor.</summary>
public enum UnsavedChangesContext
{
    /// <summary>Closing the window (title-bar X, Alt+F4) or exiting the app from the tray.</summary>
    Close,

    /// <summary>The Reload button: replace the editor with the file on disk.</summary>
    Reload,

    /// <summary>Restore from backup: put the last-known-good file back and reload it.</summary>
    Restore,
}

/// <summary>The operator's answer. Closing the prompt any other way (its X, Esc) is <see cref="Cancel"/>.</summary>
public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// Everything the "you have unsaved changes" prompt shows, decided here so the wording and the defaults are
/// testable and the dialog itself stays a dumb renderer. The view-model asks for it through
/// <see cref="ConfigEditorWindowViewModel.UnsavedChangesPrompt"/>; tests answer it without any UI.
/// </summary>
/// <param name="Context">What triggered the question.</param>
/// <param name="Heading">The question.</param>
/// <param name="Message">What each answer does, in one paragraph.</param>
/// <param name="Warning">An extra reason for care (invalid YAML, a file that also changed on disk), or null.</param>
/// <param name="SaveLabel">Text of the Save button, or null when <paramref name="CanSave"/> is false and the button is not shown.</param>
/// <param name="DiscardLabel">Text of the button that carries on without saving.</param>
/// <param name="CanSave">False when a save would be refused (the file changed on disk since it was loaded) or makes no sense (Restore).</param>
/// <param name="DefaultChoice">The button Enter presses: Save, except where saving is unsafe or unavailable, and then Cancel. Never Discard.</param>
public sealed record UnsavedChangesRequest(
    UnsavedChangesContext Context,
    string Heading,
    string Message,
    string? Warning,
    string? SaveLabel,
    string DiscardLabel,
    bool CanSave,
    UnsavedChangesChoice DefaultChoice)
{
    /// <summary>
    /// Builds the prompt for <paramref name="context"/>.
    /// </summary>
    /// <param name="context">What is about to drop the edits.</param>
    /// <param name="hasParseError">The RAW text does not parse as YAML: Save is still offered, but Cancel is the default.</param>
    /// <param name="fileChangedOnDisk">config.yaml changed since it was loaded, so a save would be refused as drift and is not offered.</param>
    public static UnsavedChangesRequest Create(UnsavedChangesContext context, bool hasParseError, bool fileChangedOnDisk)
    {
        // Restore never saves the buffer: it is being thrown away in favour of the backup.
        var saveApplies = context != UnsavedChangesContext.Restore;
        var canSave = saveApplies && !fileChangedOnDisk;

        string? warning = null;
        if (saveApplies && fileChangedOnDisk)
        {
            warning = "config.yaml also changed on disk after you opened it, so a save would be refused. " +
                      "Cancel to keep your edits and compare them against the file, or discard them.";
        }
        else if (saveApplies && hasParseError)
        {
            warning = "The RAW text does not parse as YAML. Saving writes it to config.yaml anyway and the " +
                      "validation that follows is expected to fail, so Cancel is the default.";
        }

        // Enter presses Save only when Save is available and the text is worth saving.
        var defaultChoice = canSave && !hasParseError ? UnsavedChangesChoice.Save : UnsavedChangesChoice.Cancel;

        return context switch
        {
            UnsavedChangesContext.Reload => new UnsavedChangesRequest(
                context,
                "Save your changes before reloading?",
                "Reloading replaces the editor with the config.yaml on disk and drops your unsaved edits." +
                (canSave ? " Save backs up and writes config.yaml, validates it, and reloads only if that succeeds." : string.Empty),
                warning,
                canSave ? "Save and reload" : null,
                "Discard and reload",
                canSave,
                defaultChoice),

            UnsavedChangesContext.Restore => new UnsavedChangesRequest(
                context,
                "Discard your edits and restore the backup?",
                "Restoring puts the last known-good backup back over config.yaml and reloads the editor. " +
                "Edits you made since the last save are dropped.",
                warning,
                null,
                "Discard and restore",
                false,
                UnsavedChangesChoice.Cancel),

            _ => new UnsavedChangesRequest(
                context,
                "Save your changes before closing?",
                "You have edits that are not saved." +
                (canSave
                    ? " Save backs up and writes config.yaml, validates it, and closes only if that succeeds; " +
                      "if validation fails the editor stays open with the error."
                    : " Closing discards them."),
                warning,
                canSave ? "Save and close" : null,
                "Discard and close",
                canSave,
                defaultChoice),
        };
    }
}
