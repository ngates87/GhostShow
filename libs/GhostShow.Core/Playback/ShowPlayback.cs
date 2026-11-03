using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;

namespace GhostShow.Core.Playback;

/// <summary>
/// Playback on the show computer: which timelines are loaded into the <see cref="PlaybackManager"/>, kept compiled
/// from the show as it changes, and the transport for each of them. Every screen that plays anything goes through
/// this, whether it runs on the show computer itself (the desktop app) or on a laptop over the network (then the
/// web remote calls it for the laptop).
///
/// How it fits together:
/// - The show (<see cref="ShowSession"/>) is the source; a timeline is <em>compiled</em> into what the players read.
/// - <see cref="Load"/> compiles a timeline and gives it to the manager, together with the timelines its event
///   tracks run (they must be ready before the playhead reaches those events: the playback thread never compiles).
/// - When the show changes (an edit, made here or on another device), every loaded timeline that changed is
///   compiled again, so edits during playback are heard and seen at once. A deleted timeline is stopped and unloaded.
/// - The transport works on a timeline by its ID (its first player; copies started by buttons have their own IDs,
///   see <see cref="PlaybackManager.Launch"/>).
///
/// Use it on the show's thread only (the UI thread on the desktop), like <see cref="ShowDocument"/>. The manager it
/// drives is thread-safe; its status can be read from anywhere.
/// </summary>
public sealed class ShowPlayback : IPlayback
{
    readonly ShowSession _session;
    readonly IAudioFiles _audio;
    readonly Dictionary<Guid, IReadOnlyList<TimelineIssue>> _issues = [];

    public ShowPlayback(ShowSession session, PlaybackManager manager, IAudioFiles audio, ILogger? logger = null)
    {
        _session = session;
        _audio = audio;
        Manager = manager;
        session.Document.Changed += OnShowChanged;
        session.Applied += ReloadAll;   // a patch change can make fixture channels valid or invalid
        manager.CueNotLoaded += cue => logger?.LogWarning("An event track runs a timeline that isn't loaded ({Timeline}); it was skipped.", cue.Timeline);
    }

    /// <summary>The players. Safe to read from any thread (status, what's running).</summary>
    public PlaybackManager Manager { get; }

    /// <summary>A timeline was compiled again (its problems may have changed): its ID.</summary>
    public event Action<Guid>? Compiled;

    // ---- loading ----

    /// <summary>
    /// Compiles a timeline from the show as it is now and gives it to its player (keeping its position if it's
    /// playing), loading the timelines its event tracks run too. Its problems are then in <see cref="IssuesOf"/>.
    /// </summary>
    public void Load(Guid timelineId)
    {
        if (Find(timelineId) is not { } timeline) return;
        var compiled = Compile(timeline);
        LoadTimelinesItRuns(compiled, [timelineId]);
        Compiled?.Invoke(timelineId);
    }

    /// <summary>Loads a timeline if it isn't loaded yet (so it can play without being open anywhere).</summary>
    public void EnsureLoaded(Guid timelineId)
    {
        if (!Manager.IsLoaded(timelineId)) Load(timelineId);
    }

    /// <summary>Compiles every loaded timeline again: after a patch change, or once a sound's length is known.</summary>
    public void ReloadAll()
    {
        foreach (var timeline in _session.Project.Timelines.Where(t => Manager.IsLoaded(t.Id)).ToList()) Load(timeline.Id);
    }

    /// <summary>A timeline's problems (missing fixtures or files, …) from when it was last compiled; those parts are skipped.</summary>
    public IReadOnlyList<TimelineIssue> IssuesOf(Guid timelineId) => _issues.GetValueOrDefault(timelineId) ?? [];

    /// <summary>Another show is open: everything stops at once (no release fades) and is forgotten.</summary>
    public void Reset()
    {
        Manager.UnloadAll();
        _issues.Clear();
    }

    CompiledTimeline Compile(ShowTimeline timeline)
    {
        foreach (var file in AudioFiles(timeline)) _audio.GetLength(file);   // starts measuring, in the background
        var compiled = _session.CompileTimeline(timeline, _audio.GetLength);
        Manager.Load(timeline.Id, timeline.Name, compiled);
        _issues[timeline.Id] = compiled.Issues;
        return compiled;
    }

    /// <summary>
    /// The timelines a timeline's event tracks run or stop, and theirs in turn, loaded now if they aren't: the playback
    /// thread starts them when their events come, and it never compiles anything itself.
    /// </summary>
    void LoadTimelinesItRuns(CompiledTimeline compiled, HashSet<Guid> seen)
    {
        foreach (var id in compiled.CueTargets)
        {
            if (!seen.Add(id) || Manager.IsLoaded(id) || Find(id) is not { } target) continue;
            LoadTimelinesItRuns(Compile(target), seen);
        }
    }

    /// <summary>After any change to the show: deleted timelines stop; loaded ones that changed are compiled again.</summary>
    void OnShowChanged(ShowChange change)
    {
        foreach (var op in change.Ops)
        {
            if (op is RemoveOp { Path: var path } && path.StartsWith("timelines/", StringComparison.Ordinal)
                && Guid.TryParse(path["timelines/".Length..], out var deleted))
            {
                Manager.Unload(deleted);
                _issues.Remove(deleted);
            }
        }
        foreach (var id in change.Timelines)
            if (Manager.IsLoaded(id) && Find(id) is not null) Load(id);
    }

    // ---- the transport, per timeline ----

    public void Play(Guid timelineId)
    {
        EnsureLoaded(timelineId);
        Manager.Play(timelineId);
    }

    public void Pause(Guid timelineId) => Manager.Pause(timelineId);

    public void PlayOrPause(Guid timelineId)
    {
        if (Manager.StatusOf(timelineId).State == TransportState.Playing) Pause(timelineId);
        else Play(timelineId);
    }

    /// <summary>Plays from the start.</summary>
    public void Restart(Guid timelineId)
    {
        EnsureLoaded(timelineId);
        Manager.Restart(timelineId);
    }

    public void Seek(Guid timelineId, TimeSpan position)
    {
        EnsureLoaded(timelineId);
        Manager.Seek(timelineId, position);
    }

    /// <summary>
    /// Shows a timeline's look at a time: seeks there and, if it was stopped, holds it there paused (so its levels
    /// drive the lights). While it plays, nothing changes. For "update the lamps while editing".
    /// </summary>
    public void Cue(Guid timelineId, TimeSpan position)
    {
        EnsureLoaded(timelineId);
        if (Manager.StatusOf(timelineId).State == TransportState.Playing) return;
        Manager.Seek(timelineId, position);
        if (Manager.StatusOf(timelineId).State == TransportState.Stopped)
        {
            Manager.Play(timelineId);
            Manager.Pause(timelineId);
            Manager.Seek(timelineId, position);
        }
    }

    /// <summary>
    /// Runs a timeline from its start, as a show button does: a new copy if it's already running (as VenueMagic
    /// does), or, with playOnceOnly, nothing while it runs. Returns whether it started.
    /// </summary>
    public bool Launch(Guid timelineId, bool playOnceOnly = false)
    {
        EnsureLoaded(timelineId);
        if (playOnceOnly && Manager.IsRunning(timelineId)) return false;
        return Manager.Launch(timelineId) is not null;
    }

    /// <summary>Stops a timeline and every copy of it (each with its release fade).</summary>
    public void StopTimeline(Guid timelineId) => Manager.StopTimeline(timelineId);

    /// <summary>Stops one player: a timeline's own, or one copy (an ID from <see cref="PlaybackManager.Running"/>).</summary>
    public void StopPlayer(Guid playerId) => Manager.Stop(playerId);

    /// <summary>Stops every timeline (each with its release fade).</summary>
    public void StopAll() => Manager.StopAll();

    public bool IsRunning(Guid timelineId) => Manager.IsRunning(timelineId);

    public PlaybackStatus StatusOf(Guid timelineId) => Manager.StatusOf(timelineId);

    public IReadOnlyList<RunningTimeline> Running => Manager.Running;

    // ---- sound files ----

    /// <summary>A sound file's length, once measured (asking starts measuring it). Named as the show names it.</summary>
    public TimeSpan? AudioLength(string file) => _audio.GetLength(ResolveAudio(file));

    /// <summary>A sound file's waveform, once measured (asking starts measuring it). Named as the show names it.</summary>
    public Waveform? AudioWaveform(string file) => _audio.GetWaveform(ResolveAudio(file));

    /// <summary>Where a sound file the show names (relative to its folder, or absolute) is on this computer.</summary>
    public string ResolveAudio(string file) => AudioPaths.Resolve(file, _session.ShowFolder);

    /// <summary>A sound file has been measured (its full path), so timelines that use it are compiled again.</summary>
    public event Action<string>? AudioMeasured;

    /// <summary>
    /// Call when a sound file has been measured (the desktop's audio service says so, on a worker thread: call this
    /// on the show's thread). Loaded timelines are compiled again, now that its length is known.
    /// </summary>
    public void OnAudioMeasured(string path)
    {
        ReloadAll();
        AudioMeasured?.Invoke(path);
    }

    ShowTimeline? Find(Guid id) => _session.Project.Timelines.FirstOrDefault(t => t.Id == id);

    IEnumerable<string> AudioFiles(ShowTimeline timeline) =>
        timeline.Tracks.SelectMany(t => t.Events).OfType<AudioEvent>()
            .Where(e => !string.IsNullOrWhiteSpace(e.File))
            .Select(e => AudioPaths.Resolve(e.File, _session.ShowFolder));
}
