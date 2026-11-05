using System.Text.Json.Nodes;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;

namespace GhostShow.Browser.Remote;

/// <summary>
/// Playback, as the browser sees it: nothing plays here (the DMX adapters and speakers are on the show computer), so
/// every request is sent to the show computer (<see cref="RemoteLink"/>), and what it reports back (where each timeline
/// is, what's running, each timeline's problems) is kept here for the screens to read. Implements the same
/// <see cref="IPlayback"/> as the show computer's own <see cref="ShowPlayback"/>, so the screens are the same code.
/// </summary>
sealed class RemotePlayback(RemoteLink link) : IPlayback
{
    readonly Dictionary<Guid, PlaybackStatus> _status = [];
    readonly Dictionary<Guid, IReadOnlyList<TimelineIssue>> _issues = [];
    readonly Dictionary<string, (TimeSpan? Length, Waveform? Waveform)> _audioFiles = [];
    IReadOnlyList<RunningTimeline> _running = [];
    Guid? _watching;

    public event Action<Guid>? Compiled;

    // ---- asking the show computer ----

    /// <summary>"I'm showing this timeline": the show computer loads it and sends its problems and where it is.</summary>
    public void Load(Guid timelineId)
    {
        _watching = timelineId;
        link.Send("watch", ("timeline", timelineId.ToString()));
    }

    /// <summary>Nothing to do here: the show computer loads whatever it's asked to play.</summary>
    public void EnsureLoaded(Guid timelineId) { }

    public void Play(Guid timelineId) => link.Send("play", ("timeline", timelineId.ToString()));

    public void Pause(Guid timelineId) => link.Send("pause", ("timeline", timelineId.ToString()));

    public void PlayOrPause(Guid timelineId)
    {
        if (StatusOf(timelineId).State == TransportState.Playing) Pause(timelineId);
        else Play(timelineId);
    }

    public void Restart(Guid timelineId) => link.Send("restart", ("timeline", timelineId.ToString()));

    public void Seek(Guid timelineId, TimeSpan position) =>
        link.Send("seek", ("timeline", timelineId.ToString()), ("position", position.TotalSeconds));

    public void Cue(Guid timelineId, TimeSpan position) =>
        link.Send("cue", ("timeline", timelineId.ToString()), ("position", position.TotalSeconds));

    /// <summary>Asks for it; whether it started shows in the next status. Answers true (it was asked).</summary>
    public bool Launch(Guid timelineId)
    {
        link.Send("run", ("timeline", timelineId.ToString()));
        return true;
    }

    public void StopTimeline(Guid timelineId) => link.Send("stopTimeline", ("timeline", timelineId.ToString()));

    public void StopPlayer(Guid playerId) => link.Send("stopRunning", ("running", playerId.ToString()));

    public void StopAll() => link.Send("stopAll");

    // ---- what the show computer last said ----

    public bool IsRunning(Guid timelineId) => _running.Any(r => r.TimelineId == timelineId);

    public PlaybackStatus StatusOf(Guid timelineId) =>
        _status.TryGetValue(timelineId, out var status) ? status
        : _running.FirstOrDefault(r => r.Id == timelineId)?.Status ?? default;

    public IReadOnlyList<RunningTimeline> Running => _running;

    public IReadOnlyList<TimelineIssue> IssuesOf(Guid timelineId) => _issues.GetValueOrDefault(timelineId) ?? [];

    /// <summary>
    /// A sound file's length, as the show computer measured it. The first ask sends for it (null meanwhile); when it
    /// comes (<see cref="OnAudio"/>) the timeline redraws.
    /// </summary>
    public TimeSpan? AudioLength(string file) => AudioFile(file).Length;

    public Waveform? AudioWaveform(string file) => AudioFile(file).Waveform;

    (TimeSpan? Length, Waveform? Waveform) AudioFile(string file)
    {
        if (_audioFiles.TryGetValue(file, out var known)) return known;
        _audioFiles[file] = (null, null);   // asked once: the show computer answers when it can
        link.Send("audio", ("file", file));
        return (null, null);
    }

    /// <summary>An "audio" message: a sound file's length and waveform. The timeline shown is redrawn with them.</summary>
    public void OnAudio(JsonObject message)
    {
        var file = message["file"]!.GetValue<string>();
        var length = message["length"]?.GetValue<double>() is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        var waveform = message["waveform"]?.GetValue<string>() is { } peaks ? new Waveform(Convert.FromBase64String(peaks)) : null;
        _audioFiles[file] = (length, waveform);
        if (_watching is { } shown) Compiled?.Invoke(shown);   // as on the desktop, where a measured sound recompiles
    }

    public void Reset()
    {
        _status.Clear();
        _issues.Clear();
        _audioFiles.Clear();
        _running = [];
        _watching = null;
    }

    /// <summary>A "status" message: what's running, and where each watched timeline is.</summary>
    public void OnStatus(JsonObject message)
    {
        _running = [.. message["running"]!.AsArray().OfType<JsonObject>().Select(r => new RunningTimeline(
            Guid.Parse(r["id"]!.GetValue<string>()),
            r["name"]!.GetValue<string>(),
            StatusFrom(r),
            r["priority"]?.GetValue<int>() ?? 0,
            Guid.Parse(r["timeline"]!.GetValue<string>())))];
        _status.Clear();
        foreach (var t in message["timelines"]?.AsArray().OfType<JsonObject>() ?? [])
            _status[Guid.Parse(t["timeline"]!.GetValue<string>())] = StatusFrom(t);
    }

    /// <summary>An "issues" message: a watched timeline was compiled on the show computer, with these problems.</summary>
    public void OnIssues(JsonObject message)
    {
        var timeline = Guid.Parse(message["timeline"]!.GetValue<string>());
        _issues[timeline] = [.. message["issues"]!.AsArray().OfType<JsonObject>().Select(i => new TimelineIssue(
            Enum.Parse<TimelineIssueSeverity>(i["severity"]!.GetValue<string>(), ignoreCase: true),
            i["event"]?.GetValue<string>() is { } id ? Guid.Parse(id) : null,
            i["message"]!.GetValue<string>()))];
        Compiled?.Invoke(timeline);
    }

    static PlaybackStatus StatusFrom(JsonObject s) => new(
        Enum.Parse<TransportState>(s["state"]!.GetValue<string>(), ignoreCase: true),
        TimeSpan.FromSeconds(s["position"]!.GetValue<double>()),
        TimeSpan.FromSeconds(s["length"]!.GetValue<double>()),
        s["atEnd"]?.GetValue<bool>() ?? false,
        s["loop"]?.GetValue<bool>() ?? false);
}
