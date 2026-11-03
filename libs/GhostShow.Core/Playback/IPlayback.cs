using GhostShow.Core.Timeline;

namespace GhostShow.Core.Playback;

/// <summary>
/// What the screens ask of playback. Playback itself (the players, DMX and audio) only ever runs on the show
/// computer, where the hardware is plugged in, so there are two implementations:
/// - <see cref="ShowPlayback"/>, on the show computer: does it;
/// - RemotePlayback, in the browser build (GhostShow.Browser): sends each call to the show computer over the network
///   and shows the status that comes back.
/// The screens can't tell which they're talking to. Timelines are named by their ID; a "player" is one running
/// timeline or copy (see <see cref="PlaybackManager.Running"/>).
/// </summary>
public interface IPlayback
{
    /// <summary>
    /// Have this timeline ready to play, compiled from the show as it is now (the screens call it for the timeline
    /// they show). <see cref="Compiled"/> follows, and its problems are in <see cref="IssuesOf"/>.
    /// </summary>
    void Load(Guid timelineId);

    /// <summary>Have this timeline ready to play, if it isn't already.</summary>
    void EnsureLoaded(Guid timelineId);

    /// <summary>A timeline was compiled (its problems may have changed): its ID.</summary>
    event Action<Guid>? Compiled;

    /// <summary>A timeline's problems (missing fixtures, files, …) from its last compile; those parts are skipped.</summary>
    IReadOnlyList<TimelineIssue> IssuesOf(Guid timelineId);

    /// <summary>Another show is open: forget everything about the old one.</summary>
    void Reset();

    void Play(Guid timelineId);
    void Pause(Guid timelineId);
    void PlayOrPause(Guid timelineId);

    /// <summary>Plays from the start.</summary>
    void Restart(Guid timelineId);

    void Seek(Guid timelineId, TimeSpan position);

    /// <summary>Shows a timeline's look at a time: seeks there and, if it was stopped, holds it there paused.</summary>
    void Cue(Guid timelineId, TimeSpan position);

    /// <summary>Runs a timeline from its start, as a show button does: another copy if it's running, unless playOnceOnly.</summary>
    bool Launch(Guid timelineId, bool playOnceOnly = false);

    /// <summary>Stops a timeline and every copy of it.</summary>
    void StopTimeline(Guid timelineId);

    /// <summary>Stops one player: a timeline's own, or one copy.</summary>
    void StopPlayer(Guid playerId);

    void StopAll();

    bool IsRunning(Guid timelineId);

    /// <summary>Where a timeline is (stopped, playing, paused; its position and length).</summary>
    PlaybackStatus StatusOf(Guid timelineId);

    /// <summary>Everything playing or paused, highest priority first.</summary>
    IReadOnlyList<RunningTimeline> Running { get; }

    /// <summary>
    /// A sound file's length, once known (asking may start measuring it). The file is named as the show names it
    /// (<see cref="AudioEvent.File"/>: usually relative to the show's folder); the show computer finds the real file.
    /// </summary>
    TimeSpan? AudioLength(string file);

    /// <summary>A sound file's waveform, once known (asking may start measuring it). Named as for <see cref="AudioLength"/>.</summary>
    Waveform? AudioWaveform(string file);
}
