namespace GhostShow.Core.Editing;

/// <summary>
/// Undo and Redo, as the screens see them. There's one history, on the show computer, so there are two implementations:
/// - <see cref="ShowDocument"/>, on the show computer: does it;
/// - RemoteEdits, in the browser build (GhostShow.Browser): asks the show computer, and shows what it says is next.
///
/// Given the timeline open on the screen, "next" means the latest of that timeline's edits and the show's own (the
/// timeline tree, buttons); never another timeline's edits. No timeline open: only the show's.
/// </summary>
public interface IEditHistory
{
    /// <summary>What Undo would take back (e.g. "Move clip"); null if there's nothing to undo.</summary>
    string? NextUndo(Guid? timeline);

    /// <summary>What Redo would put back; null if there's nothing to redo.</summary>
    string? NextRedo(Guid? timeline);

    void UndoLatest(Guid? timeline);

    void RedoLatest(Guid? timeline);

    /// <summary>What Undo or Redo would do has changed: the screens update their labels.</summary>
    event Action? HistoryChanged;
}
