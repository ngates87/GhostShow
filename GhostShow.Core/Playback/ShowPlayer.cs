using GhostShow.Core.Engine;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Playback;

public enum TransportState { Stopped, Playing, Paused }

/// <param name="AtEnd">A non-looping timeline reached its end and is holding its final look.</param>
public readonly record struct PlaybackStatus(TransportState State, TimeSpan Position, TimeSpan Length, bool AtEnd, bool Loop);

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
/// - End: a looping timeline wraps to 0; otherwise it pauses at the end holding the final look (Play starts over).
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
    long _startedAt;          // when it last started playing from stopped: on equal priority, the later started wins

    // Playback-thread state: only touched by Tick.
    LevelsFrame? _frame;
    CompiledTimeline? _frameTimeline;
    TransportState _lastState;
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

    /// <summary>Scales every sound's volume, e.g. while a higher-priority timeline ducks this one. Set by the playback thread.</summary>
    public double VolumeScale { get; set; } = 1;

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
                return new PlaybackStatus(_state, TimeSpan.FromTicks(PositionAt(_clock.Now)), TimeSpan.FromTicks(_timeline.Length), _atEnd, _timeline.Loop);
        }
    }

    public CompiledTimeline Timeline
    {
        get { lock (_lock) return _timeline; }
    }

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
            if (_state == TransportState.Stopped) _startedAt = _clock.Now;
            long position = _atEnd ? 0 : _anchorPosition;   // at the end, Play starts the show again
            _atEnd = false;
            if (position != _anchorPosition) _jumps++;
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
            Anchor(0);
            _jumps++;
        }
    }

    /// <summary>Plays from the start.</summary>
    public void Restart()
    {
        lock (_lock)
        {
            _startedAt = _clock.Now;
            _state = TransportState.Playing;
            _atEnd = false;
            Anchor(0);
            _jumps++;
        }
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
        }
    }

    /// <summary>One playback update: levels and audio for the current position. Called by one thread only.</summary>
    public void Tick()
    {
        var (state, position, timeline, jumps, edits, loops) = AdvanceTransport();
        bool jumped = jumps != _seenJumps;
        bool wrapped = loops != _seenLoops;

        UpdateLevels(state, position, timeline);
        ReconcileAudio(state, position, timeline, resync: jumped || wrapped, edited: edits != _seenEdits);

        _lastState = state;
        _seenJumps = jumps;
        _seenEdits = edits;
        _seenLoops = loops;
    }

    /// <summary>Takes the transport snapshot for this tick, handling the end of the timeline (loop or hold).</summary>
    (TransportState, long, CompiledTimeline, int Jumps, int Edits, int Loops) AdvanceTransport()
    {
        lock (_lock)
        {
            long now = _clock.Now;
            long position = PositionAt(now);
            long length = _timeline.Length;
            if (_state == TransportState.Playing && position >= length)
            {
                if (_timeline.Loop && length > 0)
                {
                    position %= length;
                    _anchorPosition = position;
                    _anchorTime = now;
                    _loops++;
                }
                else if (_timeline.Playback.ReleaseAtEnd)
                {
                    // Let go of its channels (and sounds) instead of holding the final look: from this update it's
                    // stopped at 0.
                    position = length;
                    _state = TransportState.Stopped;
                    _anchorPosition = 0;
                    _anchorTime = now;
                }
                else
                {
                    position = length;
                    _state = TransportState.Paused;
                    _atEnd = true;
                    _anchorPosition = position;
                }
            }
            return (_state, position, _timeline, _jumps, _edits, _loops);
        }
    }

    void UpdateLevels(TransportState state, long position, CompiledTimeline timeline)
    {
        if (state == TransportState.Stopped)
        {
            if (_lastState != TransportState.Stopped) _engine?.ReleaseTimeline();
            Frame = null;
            return;
        }
        if (_frameTimeline != timeline)
        {
            _frame = timeline.CreateFrame();   // only when a new timeline is loaded, not per tick
            _frameTimeline = timeline;
        }
        timeline.EvaluateLevels(position, _frame!);
        _engine?.SetTimelineLevels(_frame!);
        Frame = _frame;
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
            // The device was (re)opened and every instance is gone: start afresh from the current position.
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
            double volume = cue.VolumeAt(position) * VolumeScale;
            long? fileLength = _audio.GetLength(cue.Path)?.Ticks;
            var offset = TimeSpan.FromTicks(cue.FileOffsetAt(position, fileLength));

            if (!_activeAudio.TryGetValue(cue.EventId, out var active) || active.Path != cue.Path || active.Loop != cue.Loop)
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
