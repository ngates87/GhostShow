using System.Text.Json.Nodes;
using GhostShow.Core.Editing;
using GhostShow.Core.Storage;

namespace GhostShow.Web;

// Editing from a device (the full app on a laptop). The show computer has the final say:
//
// 1. The device makes the edit on its own copy of the show, so it feels instant, and sends the operations ("edit").
// 2. The show computer applies them to the real show, in the one undo history. Playback picks the change up, and the
//    desktop and every OTHER device hear of it as a "change". The device that made it already has it: it only gets
//    the command's "result".
// 3. If the edit doesn't fit (say, another device just deleted that clip), the result says why and nothing changes
//    here; the device then gets the whole show again, so the two copies can't drift apart.
//
// Undo and Redo aren't made on the device's copy first: the device asks, and the change comes back to it like any
// other. Either way "history" tells each device what Undo and Redo would do next, for its Undo button's label.
public sealed partial class RemoteServer
{
    /// <summary>The device whose edit is being applied right now (show's thread only): it isn't told of its own edit.</summary>
    RemoteConnection? _applyingEditFrom;

    /// <summary>Searches the fixture library on the show computer; the browser need not download the whole library.</summary>
    string? SearchProfiles(RemoteConnection connection, string query)
    {
        query = query.Trim();
        var profiles = _host.Session.Profiles.Profiles
            .Where(p => query.Length == 0 || p.Manufacturer.Contains(query, StringComparison.OrdinalIgnoreCase)
                                           || p.Model.Contains(query, StringComparison.OrdinalIgnoreCase)
                                           || p.Type.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .Select(p => JsonNode.Parse(FixtureProfileLoader.ToJson(p)))
            .ToArray();
        connection.Send(new JsonObject
        {
            ["type"] = "profiles",
            ["query"] = query,
            ["profiles"] = new JsonArray(profiles),
        }.ToJsonString());
        return null;
    }

    /// <summary>The "edit" command: <c>{ description, ops: [...], merge?: number }</c>.</summary>
    string? ApplyEdit(RemoteConnection connection, JsonObject message)
    {
        var description = message["description"]?.GetValue<string>() ?? "Edit";
        var ops = (message["ops"]?.AsArray() ?? []).OfType<JsonNode>().Select(ShowDocument.Decode).ToList();
        // Edits with one merge number in quick succession are one undo step (a slider's drag), as on the desktop.
        // Numbered per device: two devices' numbers mean nothing to each other.
        object? mergeKey = message["merge"]?.GetValue<int>() is { } number ? (connection.Device.Id, number) : null;
        _applyingEditFrom = connection;
        try
        {
            _host.Session.Document.Apply(new Transaction(description, ops), mergeKey, origin: connection.Device.Name);
        }
        finally
        {
            _applyingEditFrom = null;
        }
        return null;
    }

    /// <summary>The "undo" and "redo" commands: for the timeline the device shows (and the show's own edits).</summary>
    string? UndoOrRedo(RemoteConnection connection, bool undo)
    {
        var document = _host.Session.Document;
        if (undo) document.UndoLatest(connection.Watching, origin: connection.Device.Name);
        else document.RedoLatest(connection.Watching, origin: connection.Device.Name);
        return null;
    }

    /// <summary>On the show's thread: what Undo and Redo would do next on this device, for its Undo button's label.</summary>
    void SendHistory(RemoteConnection connection)
    {
        if (connection.Watching is not { } timeline) return;
        var document = _host.Session.Document;
        connection.Send(new JsonObject
        {
            ["type"] = "history",
            ["timeline"] = timeline.ToString(),
            ["undo"] = document.NextUndo(timeline),
            ["redo"] = document.NextRedo(timeline),
        }.ToJsonString());
    }
}
