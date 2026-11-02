using System.Text.Json.Nodes;
using GhostShow.Core.Storage;

namespace GhostShow.Web;

// What devices get from the show computer besides the show itself: the patch and the fixture profiles it uses, sound
// files' lengths and waveforms, the levels set by hand on the show computer, and its audio devices. The full app on a
// laptop needs them to show the same screens as the desktop (channel names, waveforms, Snapshot, track outputs).
public sealed partial class RemoteServer
{
    /// <summary>The levels set by hand last sent, per universe: only changes are sent again.</summary>
    readonly Dictionary<int, byte[]> _manualSent = [];

    void SubscribeToShowData()
    {
        _host.Session.Applied += OnPatchApplied;
        _host.Playback.AudioMeasured += OnAudioMeasured;
        _host.AudioDevicesChanged += OnAudioDevicesChanged;
        SubscribeToLibrary();
    }

    void UnsubscribeFromShowData()
    {
        _host.Session.Applied -= OnPatchApplied;
        _host.Playback.AudioMeasured -= OnAudioMeasured;
        _host.AudioDevicesChanged -= OnAudioDevicesChanged;
        UnsubscribeFromLibrary();
    }

    void SendToAll(string message)
    {
        List<RemoteConnection> connections;
        lock (_lock) connections = [.. _connections];
        foreach (var connection in connections) connection.Send(message);
    }

    // ---- the patch and fixture profiles ----

    /// <summary>
    /// The fixture profiles the show's fixtures use (every device has the built-in ones already; these are the rest:
    /// QLC+, Open Fixture Library, custom), so a device names channels as the show computer does.
    /// </summary>
    JsonArray ProfilesJson()
    {
        var session = _host.Session;
        var used = session.Project.Fixtures.Select(f => f.ProfileId).Distinct()
            .Select(session.Profiles.Find).OfType<Core.Fixtures.FixtureProfile>();
        return new JsonArray([.. used.Select(p => JsonNode.Parse(FixtureProfileLoader.ToJson(p)))]);
    }

    /// <summary>On the show's thread: the patch was applied (fixtures or universes changed): devices get the new one.</summary>
    void OnPatchApplied()
    {
        if (_host.Session.Document.Version > _observedDocumentVersion)
        {
            _patchBroadcastPending = true;
            return;
        }
        BroadcastPatch();
    }

    void BroadcastPatch()
    {
        lock (_lock)
            if (_connections.Count == 0) return;
        var project = _host.Session.Project;
        SendToAll(new JsonObject
        {
            ["type"] = "patch",
            ["fixtures"] = ShowJson.ToNode(project.Fixtures),
            ["universes"] = ShowJson.ToNode(project.Universes),
            ["profiles"] = ProfilesJson(),
        }.ToJsonString());
    }

    // ---- sound files ----

    /// <summary>
    /// The "audio" command: a sound file's length and waveform, for the device to draw. If the show computer hasn't
    /// measured it yet, it's sent once it has (<see cref="OnAudioMeasured"/>).
    /// </summary>
    string? SendAudioFile(RemoteConnection connection, string file)
    {
        if (_host.Playback.AudioLength(file) is null) connection.WaitingForAudio.Add(file);
        else connection.Send(AudioFileJson(file));
        return null;
    }

    /// <summary>On the show's thread: a sound file was measured; devices waiting for it get it.</summary>
    void OnAudioMeasured(string path)
    {
        List<RemoteConnection> connections;
        lock (_lock) connections = [.. _connections];
        foreach (var connection in connections)
            foreach (var file in connection.WaitingForAudio.Where(f => _host.Playback.ResolveAudio(f) == path).ToList())
            {
                connection.WaitingForAudio.Remove(file);
                connection.Send(AudioFileJson(file));
            }
    }

    string AudioFileJson(string file) => new JsonObject
    {
        ["type"] = "audio",
        ["file"] = file,
        ["length"] = _host.Playback.AudioLength(file)?.TotalSeconds,
        ["waveform"] = _host.Playback.AudioWaveform(file) is { } waveform ? Convert.ToBase64String(waveform.ToArray()) : null,
    }.ToJsonString();

    // ---- levels set by hand (for Snapshot) ----

    /// <summary>A universe's levels set by hand (the Control and Channels screens' faders): 512 bytes, as base64.</summary>
    static string ManualJson(int universe, byte[] levels) => new JsonObject
    {
        ["type"] = "manual",
        ["universe"] = universe,
        ["levels"] = Convert.ToBase64String(levels),
    }.ToJsonString();

    /// <summary>Every universe's levels set by hand, to a device that has just connected.</summary>
    void SendManualLevels(RemoteConnection connection)
    {
        var engine = _host.Session.Engine;
        foreach (var universe in engine.Universes)
        {
            var levels = new byte[512];
            if (engine.TryCopyManual(universe, levels)) connection.Send(ManualJson(universe, levels));
        }
    }

    /// <summary>From the status loop (any thread; the engine is thread-safe): levels set by hand that changed, to every device.</summary>
    void SendManualLevelsIfChanged()
    {
        var engine = _host.Session.Engine;
        foreach (var universe in engine.Universes)
        {
            var levels = new byte[512];
            if (!engine.TryCopyManual(universe, levels)) continue;
            if (_manualSent.TryGetValue(universe, out var sent) && sent.AsSpan().SequenceEqual(levels)) continue;
            _manualSent[universe] = levels;
            SendToAll(ManualJson(universe, levels));
        }
    }

    // ---- audio devices ----

    JsonArray AudioDevicesJson() => new([.. _host.AudioDevices.Select(d => JsonValue.Create(d))]);

    void OnAudioDevicesChanged() => SendToAll(new JsonObject { ["type"] = "audioDevices", ["devices"] = AudioDevicesJson() }.ToJsonString());
}
