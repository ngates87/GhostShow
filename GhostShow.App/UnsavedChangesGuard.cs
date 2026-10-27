namespace GhostShow.App;

public enum SaveChoice { Save, DontSave, Cancel }

/// <summary>Asks the user what to do with unsaved changes. The app shows a dialog; tests answer directly.</summary>
public interface ISaveChangesPrompt
{
    /// <param name="showName">Shown in the question, e.g. "haunt.json".</param>
    /// <param name="action">What's about to happen, e.g. "closing", "opening another show".</param>
    Task<SaveChoice> AskAsync(string showName, string action);
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
        switch (await prompt.AskAsync(showName(), action))
        {
            case SaveChoice.Save:
                await save();
                return !isDirty();   // still dirty: the save was cancelled or failed, so don't lose the work
            case SaveChoice.DontSave:
                return true;
            default:
                return false;
        }
    }
}
