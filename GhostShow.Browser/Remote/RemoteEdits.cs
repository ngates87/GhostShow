using System.Text.Json.Nodes;
using GhostShow.Core.Editing;

namespace GhostShow.Browser.Remote;

/// <summary>
/// Editing from the browser. The show computer has the final say (see GhostShow.Web/RemoteServer.Edits.cs):
/// - An edit made here changes this copy of the show straight away, so it feels instant, and its operations are sent
///   to the show computer, which applies them to the real show.
/// - If it refuses one, or another device's change arrives while one of ours is on its way (the two could have been
///   applied in a different order there), this copy may no longer match: we ask for the whole show again.
/// - Undo and Redo are the show computer's: there's one history. We ask, and the change comes back like any other.
///
/// Everything here runs on the UI thread.
/// </summary>
sealed class RemoteEdits : IEditHistory
{
    readonly ShowDocument _document;
    readonly RemoteLink _link;

    /// <summary>Commands sent with our edits, not answered yet.</summary>
    readonly HashSet<int> _editsOnTheirWay = [];

    /// <summary>Another device's change arrived while edits of ours were on their way.</summary>
    bool _overtaken;

    object? _lastMergeKey;
    int _mergeNumber;
    int? _saveCommand;

    // What the show computer last said Undo and Redo would do, for the timeline shown here.
    Guid? _historyTimeline;
    string? _nextUndo, _nextRedo;
    // The same for the show's own edits, for a setup screen (a null timeline).
    string? _showUndo, _showRedo;

    public RemoteEdits(ShowDocument document, RemoteLink link)
    {
        _document = document;
        _link = link;
        document.Changed += OnDocumentChanged;
    }

    public event Action? HistoryChanged;

    /// <summary>Something to tell the user for a moment (a refused edit, "Saved").</summary>
    public event Action<string>? Notice;

    // ---- edits made here ----

    /// <summary>A change to this copy. Changes that came from the show computer have an origin: they aren't sent back.</summary>
    void OnDocumentChanged(ShowChange change)
    {
        if (change.Origin is not null) return;
        var ops = new JsonArray([.. change.Ops.Select(_document.Encode)]);   // now: the paths are read as they are now
        var command = _link.Send("edit", ("description", change.Description), ("ops", ops), ("merge", MergeNumberOf(change.MergeKey)));
        if (command is { } id) _editsOnTheirWay.Add(id);
        else Notice?.Invoke("Not connected: that edit didn't reach the show computer, and will be lost when the connection is back.");
    }

    /// <summary>
    /// Merge keys are objects here (a clip and a field, say), which can't be sent; the show computer only needs to know
    /// whether this edit has the same key as the one before it, so each new key gets a new number.
    /// </summary>
    int? MergeNumberOf(object? mergeKey)
    {
        if (mergeKey is null) return null;
        if (!Equals(mergeKey, _lastMergeKey))
        {
            _lastMergeKey = mergeKey;
            _mergeNumber++;
        }
        return _mergeNumber;
    }

    // ---- what the show computer says ----

    /// <summary>Call before applying a change from the show computer.</summary>
    public void OnRemoteChange()
    {
        if (_editsOnTheirWay.Count > 0) _overtaken = true;
    }

    /// <summary>A "result" message: the answer to a command.</summary>
    public void OnResult(JsonObject message)
    {
        int id = message["id"]?.GetValue<int>() ?? 0;
        bool ok = message["ok"]?.GetValue<bool>() == true;
        string? error = message["error"]?.GetValue<string>();

        if (id == _saveCommand)
        {
            _saveCommand = null;
            Notice?.Invoke(ok ? "Saved on the show computer." : error ?? "Couldn't save.");
        }
        else if (_editsOnTheirWay.Remove(id))
        {
            if (!ok)
            {
                Notice?.Invoke(error ?? "The show computer refused that edit.");
                _link.Resync();   // this copy has an edit the show doesn't
            }
            else if (_overtaken && _editsOnTheirWay.Count == 0)
            {
                _link.Resync();
            }
        }
        else if (!ok && error is not null)
        {
            Notice?.Invoke(error);
        }
    }

    /// <summary>The whole show arrived: this copy matches the show computer's again.</summary>
    public void OnHello()
    {
        _editsOnTheirWay.Clear();
        _overtaken = false;
        _saveCommand = null;
        _historyTimeline = null;
        (_nextUndo, _nextRedo, _showUndo, _showRedo) = (null, null, null, null);
        HistoryChanged?.Invoke();
    }

    /// <summary>A "history" message: what Undo and Redo would do next.</summary>
    public void OnHistory(JsonObject message)
    {
        _showUndo = message["showUndo"]?.GetValue<string>();
        _showRedo = message["showRedo"]?.GetValue<string>();
        _historyTimeline = message["timeline"]?.GetValue<string>() is { } timeline ? Guid.Parse(timeline) : null;
        _nextUndo = message["undo"]?.GetValue<string>();
        _nextRedo = message["redo"]?.GetValue<string>();
        HistoryChanged?.Invoke();
    }

    // ---- what the screens ask ----

    public string? NextUndo(Guid? timeline) => timeline is null ? _showUndo : timeline == _historyTimeline ? _nextUndo : null;

    public string? NextRedo(Guid? timeline) => timeline is null ? _showRedo : timeline == _historyTimeline ? _nextRedo : null;

    /// <summary>Undoes for the timeline this device shows (the show computer knows which: "watch"), or with none named, the show's own edits.</summary>
    public void UndoLatest(Guid? timeline)
    {
        if (timeline is null) _link.Send("undo", ("show", true));
        else _link.Send("undo");
    }

    public void RedoLatest(Guid? timeline)
    {
        if (timeline is null) _link.Send("redo", ("show", true));
        else _link.Send("redo");
    }

    public void Save()
    {
        _saveCommand = _link.Send("save");
        if (_saveCommand is null) Notice?.Invoke("Not connected to the show computer.");
    }
}
