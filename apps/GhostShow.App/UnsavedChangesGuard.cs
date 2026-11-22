namespace GhostShow.App;

public enum SaveChoice { Save, DontSave, Cancel }

/// <summary>Asks the user what to do with unsaved changes, and before closing. The app shows dialogs; tests answer directly.</summary>
public interface ISaveChangesPrompt
{
    /// <param name="showName">Shown in the question, e.g. "haunt.json".</param>
    /// <param name="action">What's about to happen, e.g. "closing the show", "opening another show".</param>
    Task<SaveChoice> AskAsync(string showName, string action);

    /// <summary>
    /// Before the app closes, every time: what's still live, and with unsaved changes, whether to save first. Save is
    /// save and close; DontSave is close (without saving, if there's anything to save).
    /// </summary>
    Task<SaveChoice> AskCloseAsync(CloseWarning warning);
}

/// <summary>
/// Stands between the user and anything that would throw away unsaved show changes (New, Open, closing the window):
/// with changes pending it asks Save / Don't save / Cancel, and only lets the action go ahead if the work is saved or
/// the user chose to discard it.
/// </summary>
public sealed class UnsavedChangesGuard(Func<bool> isDirty, Func<string> showName, Func<Task> save, ISaveChangesPrompt prompt)
{
    /// <returns>True if it's fine to proceed: nothing unsaved, saved successfully, or the user chose to discard.</returns>
    public async Task<bool> ConfirmAsync(string action)
    {
        if (!isDirty()) return true;
        return await Settle(await prompt.AskAsync(showName(), action));
    }

    /// <summary>
    /// Before the app closes: always asks (closing stops the show), saying what's still live and offering to save.
    /// </summary>
    /// <returns>True if it's fine to close.</returns>
    public async Task<bool> ConfirmCloseAsync(CloseWarning warning) => await Settle(await prompt.AskCloseAsync(warning));

    async Task<bool> Settle(SaveChoice choice)
    {
        switch (choice)
        {
            case SaveChoice.Save:
                if (isDirty()) await save();
                return !isDirty();   // still dirty: the save was cancelled or failed, so don't lose the work
            case SaveChoice.DontSave:
                return true;
            default:
                return false;
        }
    }
}
