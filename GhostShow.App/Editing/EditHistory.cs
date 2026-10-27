using System.Diagnostics;

namespace GhostShow.App.Editing;

/// <summary>
/// Undo and redo for timeline editing. Each edit is a pair of actions (apply, revert) that capture just the values
/// they change, so history costs a few objects per edit rather than copies of the show.
///
/// Typing into a field produces an edit per keystroke; edits with the same merge key within a short time become one,
/// so Undo takes the whole change back at once. Any new edit clears the redo list.
/// </summary>
public sealed class EditHistory
{
    static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(2);
    const int Limit = 500;

    sealed record Edit(string Description, Action Apply, Action Revert, object? MergeKey, long Time);

    readonly List<Edit> _undo = [];
    readonly List<Edit> _redo = [];

    /// <summary>Raised after every change, undo and redo, so views can refresh and playback recompile.</summary>
    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>Applies an edit and records it.</summary>
    /// <param name="mergeKey">Edits with equal keys in quick succession merge (e.g. typing into one field); null never merges.</param>
    public void Execute(string description, Action apply, Action revert, object? mergeKey = null)
    {
        apply();
        long now = Stopwatch.GetTimestamp();
        if (mergeKey is not null && _undo.Count > 0 && Equals(_undo[^1].MergeKey, mergeKey)
            && Stopwatch.GetElapsedTime(_undo[^1].Time, now) < MergeWindow)
        {
            _undo[^1] = _undo[^1] with { Apply = apply, Time = now };   // keep the first revert: it restores the original value
        }
        else
        {
            _undo.Add(new Edit(description, apply, revert, mergeKey, now));
            if (_undo.Count > Limit) _undo.RemoveAt(0);
        }
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        edit.Revert();
        _redo.Add(edit with { MergeKey = null });
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        edit.Apply();
        _undo.Add(edit with { MergeKey = null });   // a redone edit never merges with what follows
        Changed?.Invoke();
    }

    /// <summary>
    /// Forgets everything, e.g. when another show is opened. Not a change to the show, so <see cref="Changed"/> isn't
    /// raised (it would mark the show as modified).
    /// </summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
