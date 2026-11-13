using GhostShow.Core.Engine;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Playback;

public enum TransportState { Stopped, Playing, Paused }

/// <param name="AtEnd">A non-looping timeline played to its end and stopped there (Play starts it over).</param>
/// <param name="Loop">It starts again at its end: as what ran it said (<see cref="RunOptions.Loop"/>), or, played on its
/// own, its timeline's Loop.</param>
/// <param name="OneOnly">What ran it said "Play one only": running it again while it plays does nothing.</param>
public readonly record struct PlaybackStatus(TransportState State, TimeSpan Position, TimeSpan Length, bool AtEnd, bool Loop, bool OneOnly = false);

/// <summary>
/// Plays a <see cref="CompiledTimeline"/>: owns the transport and the show clock, and on every <see cref="Tick"/> sets
/// the engine's timeline layer and reconciles audio.
///
/// Clock: position is derived from a monotonic clock (position = anchor + (now - anchorTime)), never accumulated
/// from tick intervals, so late or missed ticks cause no drift; they just mean one update covers a longer interval.
///
/// Threading: transport methods (Play, Pause, Seek, ...) can be called from any thread; they take a short lock and
/// change only transport fields. <see cref="Tick"/> is called by one thread (<see cref="PlaybackLoop"/>), reads a
/// consistent snapshot under the lock, then does all engine and audio work outside it.
///
/// Transport rules (see docs/timeline.md):
/// - Stop: releases the timeline layer (channels go back to their manual levels), stops all audio, returns to 0.
/// - Pause: holds the current levels and pauses audio. Play resumes from the same position.
/// - Seek: moves the position (anywhere from 0, including past the end); levels are evaluated at the new position (mid-fade gives the mid-fade value) and audio
///   restarts at the matching file offset.
/// - Run (<see cref="Run"/>): plays from the start as a button or an event says (<see cref="RunOptions"/>): looping or
///   once. Played on its own (Play, Restart) it loops if its timeline is set to. Stopping, or reaching the end, forgets
///   what the run said; <see cref="EndLoop"/> makes a looping run stop at its next end.
/// - End: a looping timeline wraps to 0; otherwise it stops there (Play starts over), as VenueMagic's do. What its
///   channels do then is its "At the end" setting: Holds leaves them at their last levels (VenueMagic's "lamps remain
///   at last level"; see <see cref="EndFrame"/>), Releases lets go of them. A timeline whose length isn't known yet
///   (a sound not measured yet, or one looping forever) doesn't end on its own.
/// </summary>
public sealed class ShowPlayer
{
    readonly ShowEngine? _engine;   // null when a PlaybackManager merges this player's levels with others' (see Frame)
    readonly IAudioOutput _audio;
    readonly IPlaybackClock _clock;
    readonly Lock _lock = new();

    // Transport: guarded by _lock.
    CompiledTimeline _timeline = CompiledTimeline.Empty;
    TransportState _state;
    long _anchorPosition;     // the position at _anchorTime (or the position, when not playing)
    long _anchorTime;
    int _jumps;               // bumped by seek, stop, restart: audio resyncs
    int _edits;               // bumped by Load (a recompiled timeline): only changed sounds move
    int _loops;               // bumped when a looping timeline wraps: audio restarts
    bool _atEnd;
    bool? _runLoop;           // this run loops (or not), as what ran it said; null: as its timeline is set to
    bool _runOneOnly;         // what ran it said "Play one only"
    long _startedAt;          // when it last started playing from stopped: on equal priority, the later started wins
    long _cueFrom = -1;       // event cues after this position (ticks) and up to the playhead fire on the next update
    readonly List<TimelineCue> _fired = new();

    // Playback-thread state: only touched by Tick.
    LevelsFrame? _frame;
    CompiledTimeline? _frameTimeline;
    TransportState _lastState;
    bool _engineHolding;      // standalone: it ended holding, so the engine keeps its final look until Stop or Play
    int _seenJumps = -1, _seenEdits, _seenLoops, _seenAudioGeneration;
    readonly Dictionary<Guid, AudioCue> _activeAudio = new();
    readonly HashSet<Guid> _pausedAudio = new();
    readonly HashSet<Guid> _failedAudio = new();
    readonly HashSet<Guid> _dueAudio = new();
    readonly List<Guid> _scratch = new();

    /// <param name="engine">Where the levels go. Null when a <see cref="PlaybackManager"/> merges several players'
    /// levels: it reads <see cref="Frame"/> after each tick instead.</param>
    public ShowPlayer(ShowEngine? engine, IAudioOutput audio, IPlaybackClock clock)
    {
        _engine = engine;
        _audio = audio;
        _clock = clock;
    }

    /// <summary>The levels from the last tick; null while stopped (the timeline controls nothing then).</summary>
    public LevelsFrame? Frame { get; private set; }

    /// <summary>
    /// Its levels at its very end, on the one update where it played to its end and stopped; null otherwise. A
    /// <see cref="PlaybackManager"/> keeps them (Holds) or fades them out (Releases). Playback thread only.
    /// </summary>
    public LevelsFrame? EndFrame { get; private set; }

    /// <summary>Scales every sound's volume, e.g. while a higher-priority timeline ducks this one. Set by the playback thread.</summary>
    public Func<AudioCue, double> VolumeScale { get; set; } = _ => 1;

    /// <summary>Clock time it last started playing from stopped (see <see cref="IPlaybackClock"/>).</summary>
    public long StartedAt
    {
        get { lock (_lock) return _startedAt; }
    }

    public PlaybackStatus Status
    {
        get
        {
            lock (_lock)
                return new PlaybackStatus(_state, TimeSpan.FromTicks(PositionAt(_clock.Now)), TimeSpan.FromTicks(_timeline.Length), _atEnd, Loops,
                                          _state != TransportState.Stopped && _runOneOnly);
        }
    }

    public CompiledTimeline Timeline
    {
        get { lock (_lock) return _timeline; }
    }

    bool Loops => _runLoop ?? _timeline.Loop;   // under _lock

    /// <summary>
    /// Swaps in a (re)compiled timeline, e.g. after an edit. Playback continues from the same position with the new
    /// content from the next tick (an edit never moves the playhead; if the timeline became shorter than the position,
    /// the next tick handles it as reaching the end). Audio whose event changed is restarted or re-seeked to match.
    /// </summary>
    public void Load(CompiledTimeline timeline)
    {
        lock (_lock)
        {
            Anchor(PositionAt(_clock.Now));
            _timeline = timeline;
            _edits++;
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_state == TransportState.Playing) return;
            bool fromStopped = _state == TransportState.Stopped;
            if (fromStopped) _startedAt = _clock.Now;
            long position = _atEnd ? 0 : _anchorPosition;   // at the end, Play starts the show again
            _atEnd = false;
            if (position != _anchorPosition) _jumps++;
            if (fromStopped || position != _anchorPosition) _cueFrom = position - 1;   // events right where it starts fire
            _state = TransportState.Playing;
            Anchor(position);
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_state != TransportState.Playing) return;
            Anchor(PositionAt(_clock.Now));
            _state = TransportState.Paused;
        }
    }

    /// <summary>Stops playback, releases timeline control of the lights, stops audio, and returns to the start.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _state = TransportState.Stopped;
            _atEnd = false;
            ForgetRun();
            Anchor(0);
            _jumps++;
        }
    }

    /// <summary>Plays from the start (looping, or once, as the run it's playing said; played on its own, as its timeline says).</summary>
    public void Restart()
    {
        lock (_lock) RestartLocked();
    }

    /// <summary>Plays from the start as a button or an event says: looping or once (<see cref="RunOptions"/>).</summary>
    public void Run(RunOptions how)
    {
        lock (_lock)
        {
            _runLoop = how.Loop;
            _runOneOnly = how.OneOnly;
            RestartLocked();
        }
    }

    /// <summary>A looping run finishes the pass it's on, then stops at its end (VenueMagic's End loop).</summary>
    public void EndLoop()
    {
        lock (_lock) _runLoop = false;
    }

    void ForgetRun()
    {
        _runLoop = null;
        _runOneOnly = false;
    }

    void RestartLocked()
    {
        _startedAt = _clock.Now;
        _state = TransportState.Playing;
        _atEnd = false;
        Anchor(0);
        _jumps++;
        _cueFrom = -1;
    }

    /// <summary>
    /// Moves to a position (not before 0). Keeps playing, paused or stopped as before. A position past the end is
    /// allowed, so the editor can place events there; playing from it is the same as reaching the end.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            _atEnd = false;
            Anchor(Math.Max(0, position.Ticks));
            _jumps++;
            _cueFrom = Math.Max(0, position.Ticks);   // a seek skips over events (they don't all fire)
        }
    }

    /// <summary>One playback update: levels and audio for the current position. Called by one thread only.</summary>
    public void Tick()
    {
        var (state, position, timeline, jumps, edits, loops, ended) = AdvanceTransport();
        bool jumped = jumps != _seenJumps;
        bool wrapped = loops != _seenLoops;

        FireCues(state, position, timeline, wrapped, ended);
        UpdateLevels(state, position, timeline, ended, jumped);
        ReconcileAudio(state, position, timeline, resync: jumped || wrapped, edited: edits != _seenEdits);

        _lastState = state;
        _seenJumps = jumps;
        _seenEdits = edits;
        _seenLoops = loops;
    }

    /// <summary>
    /// The event track commands the playhead passed in the last update (run or stop another timeline), for the
    /// <see cref="PlaybackManager"/> to carry out. Playback thread only; replaced every update.
    /// </summary>
    public IReadOnlyList<TimelineCue> FiredCues => _fired;

    /// <summary>
    /// While playing, the cues after where the last update got to, up to the playhead (on a loop: to the end, then from
    /// the start). Starting or restarting fires cues right at the start; a seek skips the cues it jumps over.
    /// </summary>
    void FireCues(TransportState state, long position, CompiledTimeline timeline, bool wrapped, bool ended)
    {
        _fired.Clear();
        long from;
        lock (_lock) from = _cueFrom;
        // Playing, or has just reached the end in this update (and stopped or holds there).
        if (state == TransportState.Playing || ended)
        {
            foreach (var cue in timeline.Cues)
                if (wrapped ? cue.Time > from || cue.Time <= position : cue.Time > from && cue.Time <= position) _fired.Add(cue);
        }
        lock (_lock)
            if (_cueFrom == from) _cueFrom = state == TransportState.Stopped ? -1 : position;   // unless a transport call moved it meanwhile
    }

    /// <summary>Takes the transport snapshot for this tick, handling the end of the timeline (loop or hold).</summary>
    (TransportState, long, CompiledTimeline, int Jumps, int Edits, int Loops, bool Ended) AdvanceTransport()
    {
        lock (_lock)
        {
            long now = _clock.Now;
            long position = PositionAt(now);
            long length = _timeline.Length;
            bool reachedEnd = _state == TransportState.Playing && position >= length && !_timeline.OpenEnded;
            bool ended = reachedEnd && !(Loops && length > 0);
            if (reachedEnd)
            {
                if (!ended)
                {
                    position %= length;
                    _anchorPosition = position;
                    _anchorTime = now;
                    _loops++;
                }
                else
                {
                    // Stopped at its end: its sounds stop, its channels hold or release (see EndFrame), and it leaves
                    // the Playing list. The playhead stays at the end; Play starts over.
                    position = length;
                    _state = TransportState.Stopped;
                    _atEnd = true;
                    ForgetRun();
                    _anchorPosition = length;
                    _anchorTime = now;
                }
            }
            return (_state, position, _timeline, _jumps, _edits, _loops, ended);
        }
    }

    void UpdateLevels(TransportState state, long position, CompiledTimeline timeline, bool ended, bool jumped)
    {
        EndFrame = null;
        if (state == TransportState.Stopped)
        {
            Frame = null;
            if (ended)
            {
                // Its exact final look (the last update may have been a few ms before the end, mid-fade).
                EndFrame = Evaluate(position, timeline);
                if (_engine is not null && !timeline.Playback.ReleaseAtEnd)
                {
                    // Standalone (no manager to keep it): the engine keeps the final look until Stop or Play.
                    _engine.SetTimelineLevels(EndFrame);
                    _engineHolding = true;
                    return;
                }
            }
            if (_lastState != TransportState.Stopped || (_engineHolding && jumped))
            {
                _engine?.ReleaseTimeline();
                _engineHolding = false;
            }
            return;
        }
        _engineHolding = false;
        var frame = Evaluate(position, timeline);
        _engine?.SetTimelineLevels(frame);
        Frame = frame;
    }

    LevelsFrame Evaluate(long position, CompiledTimeline timeline)
    {
        if (_frameTimeline != timeline || _frame is null)
        {
            _frame = timeline.CreateFrame();   // only when a new timeline is loaded, not per tick
            _frameTimeline = timeline;
        }
        timeline.EvaluateLevels(position, _frame);
        return _frame;
    }

    /// <summary>
    /// Makes audio match the timeline: every cue due at this position is playing at the right file offset, volume and
    /// pause state; nothing else is. State-based rather than edge-triggered, so late ticks, seeks, edits and
    /// pause/resume are all handled the same way, and a cue already playing is never started twice.
    /// A cue that failed isn't retried until the next seek, stop, loop or edit (no retry storm every 10 ms).
    /// resync (seek, stop, loop): every sound is put at its exact place. edited (a recompiled timeline): only sounds
    /// whose event changed are moved; the rest carry on untouched, and a sound that finished stays finished.
    /// </summary>
    void ReconcileAudio(TransportState state, long position, CompiledTimeline timeline, bool resync, bool edited)
    {
        int generation = _audio.Generation;
        if (generation != _seenAudioGeneration)
        {
            // A device was (re)opened or lost: start every sound afresh from the current position (on its device).
            _activeAudio.Clear();
            _pausedAudio.Clear();
            _seenAudioGeneration = generation;
            resync = true;
        }
        if (resync || edited) _failedAudio.Clear();

        _dueAudio.Clear();
        if (state != TransportState.Stopped)
            foreach (var cue in timeline.AudioCues)
                if (cue.IsDueAt(position)) _dueAudio.Add(cue.EventId);

        // Stop instances that are no longer due (passed their end, seeked away, removed or disabled by an edit).
        _scratch.Clear();
        foreach (var id in _activeAudio.Keys) if (!_dueAudio.Contains(id)) _scratch.Add(id);
        foreach (var id in _scratch) StopAudio(id);

        foreach (var cue in timeline.AudioCues)
        {
            if (!_dueAudio.Contains(cue.EventId) || _failedAudio.Contains(cue.EventId)) continue;
            double volume = cue.VolumeAt(position) * VolumeScale(cue);
            long? fileLength = _audio.GetLength(cue.Path)?.Ticks;
            var offset = TimeSpan.FromTicks(cue.FileOffsetAt(position, fileLength));

            if (!_activeAudio.TryGetValue(cue.EventId, out var active) || active.Path != cue.Path || active.Loop != cue.Loop || active.Device != cue.Device || active.Side != cue.Side)
            {
                _audio.Start(cue, offset, volume);
                _activeAudio[cue.EventId] = cue;
                _pausedAudio.Remove(cue.EventId);
            }
            else
            {
                bool moved = active.Start != cue.Start || active.Offset != cue.Offset;
                switch (_audio.GetState(cue.EventId))
                {
                    case AudioInstanceState.Failed:
                        _failedAudio.Add(cue.EventId);
                        StopAudio(cue.EventId);
                        continue;
                    case AudioInstanceState.Finished when !(resync || moved) || (fileLength is { } length && offset.Ticks >= length):
                        continue;   // played to the end of its file (and the place it should be is still past the end)
                    case AudioInstanceState.Finished:
                        _audio.Start(cue, offset, volume);
                        break;
                    default:
                        if (resync || moved) _audio.Seek(cue.EventId, offset);
                        break;
                }
                _activeAudio[cue.EventId] = cue;
                _audio.SetVolume(cue.EventId, volume);
            }

            bool shouldPause = state == TransportState.Paused;
            if (shouldPause && _pausedAudio.Add(cue.EventId)) _audio.Pause(cue.EventId);
            else if (!shouldPause && _pausedAudio.Remove(cue.EventId)) _audio.Resume(cue.EventId);
        }
    }

    void StopAudio(Guid id)
    {
        _audio.Stop(id);
        _activeAudio.Remove(id);
        _pausedAudio.Remove(id);
    }

    long PositionAt(long now) => _state == TransportState.Playing ? _anchorPosition + (now - _anchorTime) : _anchorPosition;

    void Anchor(long position)
    {
        _anchorPosition = position;
        _anchorTime = _clock.Now;
    }
}
