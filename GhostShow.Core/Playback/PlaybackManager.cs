using GhostShow.Core.Dmx;
using GhostShow.Core.Engine;
using GhostShow.Core.Timeline;

namespace GhostShow.Core.Playback;

/// <summary>A timeline that's playing or paused, for the "Playing" list.</summary>
/// <param name="Id">What to stop or pause it by: the timeline's ID, or for a second (third, …) copy of it, the copy's.</param>
public sealed record RunningTimeline(Guid Id, string Name, PlaybackStatus Status, int Priority, Guid TimelineId);

/// <summary>
/// Plays any number of timelines at once (a room's ambient loop, scares on top of it): one <see cref="ShowPlayer"/>
/// per timeline, each with its own transport and clock position, merged into the engine's timeline layer on every
/// update (<see cref="Tick"/>, from the playback thread). See <see cref="TimelinePlayback"/> for the settings.
///
/// Merging, per channel: of the timelines setting it, the one with the highest priority wins; on equal priority, the
/// one started most recently. Overrides (a timeline with a higher priority than another):
/// - Pause: if set, starting it pauses every playing lower-priority timeline that shares a channel with it, and
///   stopping (or its releasing end) resumes them where they were. Otherwise they keep running underneath.
/// - Duck: while it plays, lower-priority timelines' sounds are scaled by its duck volume.
/// - Release: when a timeline stops (or ends, if set to release), its channels fade from where it left them to
///   whatever is underneath (a lower timeline, or the manual level) over its release fade.
///
/// Copies: <see cref="Launch"/> (a show button's Run Timeline) plays a timeline from its start as a new copy when
/// it's already running (as VenueMagic does), so a scare fired twice plays twice, sounds overlapping. A copy is a
/// timeline of its own for the rules above, is forgotten once it stops, and follows edits to its timeline. The
/// timeline's own transport (the editor's) controls its first copy; <see cref="StopTimeline"/> stops them all.
///
/// Threading: transport methods and <see cref="Load"/> can be called from any thread; <see cref="Tick"/> from the
/// playback thread only.
/// </summary>
public sealed class PlaybackManager
{
    readonly ShowEngine _engine;
    readonly IAudioOutput _audio;
    readonly IPlaybackClock _clock;
    readonly Lock _lock = new();
    readonly Dictionary<Guid, Slot> _slots = new();

    // Playback-thread state.
    readonly List<Release> _releases = new();
    readonly Dictionary<int, byte[]> _manual = new();
    LevelsFrame? _merged;
    string _mergedKey = "";
    int[][] _winnerPriority = [];
    long[][] _winnerStarted = [];
    bool _layerSet;

    /// <summary>The most copies of one timeline playing at once; launching another stops the oldest.</summary>
    public const int MaxCopies = 8;

    /// <param name="timelineId">The timeline it plays; for its first copy, the same as the slot's ID.</param>
    sealed class Slot(Guid id, ShowPlayer player, Guid timelineId, long launched = 0)
    {
        public Guid Id { get; } = id;
        public Guid TimelineId { get; } = timelineId;
        public bool IsCopy => Id != TimelineId;
        public long Launched { get; } = launched;
        public ShowPlayer Player { get; } = player;
        public string Name { get; set; } = "";
        public CompiledTimeline Timeline { get; set; } = CompiledTimeline.Empty;

        // Playback thread only.
        public TransportState LastState;
        public LevelsFrame? LastFrame;                 // a copy of the last levels it played, for a release fade
        public readonly HashSet<Guid> PausedBy = new();   // overrides that paused it
        public readonly HashSet<Guid> Paused = new();     // timelines this one paused (as an override)
    }

    /// <summary>A stopped timeline's channels fading back to what's underneath.</summary>
    sealed record Release(int Priority, IReadOnlyList<(int Universe, int Index, byte Level)> Levels, long StartedAt, long Fade);

    public PlaybackManager(ShowEngine engine, IAudioOutput audio, IPlaybackClock clock)
    {
        _engine = engine;
        _audio = audio;
        _clock = clock;
    }


    /// <summary>Gives a timeline its (re)compiled content: before playing it, and after every edit or patch change.</summary>
    public void Load(Guid id, string name, CompiledTimeline timeline)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(id, out var slot))
            {
                var player = new ShowPlayer(null, _audio, _clock);
                _slots[id] = slot = new Slot(id, player, id);
            }
            foreach (var each in _slots.Values.Where(s => s.TimelineId == id))
            {
                each.Name = name;
                each.Timeline = timeline;
                each.Player.Load(timeline);
            }
        }
    }

    long _launches;

    /// <summary>
    /// Plays a loaded timeline from its start: its own player if that's stopped, otherwise a new copy (beyond
    /// <see cref="MaxCopies"/>, the oldest copy stops). Returns what it's playing as (to stop or pause by), or null if
    /// the timeline isn't loaded.
    /// </summary>
    public Guid? Launch(Guid timelineId)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(timelineId, out var own)) return null;
            if (own.Player.Status.State == TransportState.Stopped)
            {
                own.Player.Restart();
                return timelineId;
            }
            var running = _slots.Values.Where(s => s.TimelineId == timelineId && s.Player.Status.State != TransportState.Stopped).ToList();
            if (running.Count >= MaxCopies)
                running.Where(s => s.IsCopy).OrderBy(s => s.Launched).FirstOrDefault()?.Player.Stop();
            var id = Guid.NewGuid();
            var copy = new Slot(id, new ShowPlayer(null, new InstanceAudioOutput(_audio, id), _clock), timelineId, ++_launches)
            {
                Name = own.Name, Timeline = own.Timeline,
            };
            copy.Player.Load(own.Timeline);
            copy.Player.Restart();
            _slots[id] = copy;
            return id;
        }
    }

    /// <summary>Stops a timeline and every copy of it playing (each releasing its channels with its fade).</summary>
    public void StopTimeline(Guid timelineId)
    {
        lock (_lock)
            foreach (var slot in _slots.Values.Where(s => s.TimelineId == timelineId)) slot.Player.Stop();
    }

    /// <summary>Whether a timeline (any copy of it) is playing or paused.</summary>
    public bool IsRunning(Guid timelineId)
    {
        lock (_lock) return _slots.Values.Any(s => s.TimelineId == timelineId && s.Player.Status.State != TransportState.Stopped);
    }

    /// <summary>A timeline removed from the show: it stops (releasing its channels) and is forgotten.</summary>
    public void Unload(Guid id)
    {
        lock (_lock)
            foreach (var slot in _slots.Values.Where(s => s.TimelineId == id))
            {
                slot.Player.Stop();
                // Removed on the next tick, once its stop has been handled (release fade, resuming what it paused).
                _toRemove.Add(slot.Id);
            }
    }

    readonly System.Collections.Concurrent.ConcurrentBag<Guid> _toRemove = new();

    /// <summary>Another show: everything stops at once (no release fades) and is forgotten.</summary>
    public void UnloadAll()
    {
        StopAllNow();
        lock (_lock)
            foreach (var id in _slots.Keys) _toRemove.Add(id);
    }

    public bool IsLoaded(Guid id)
    {
        lock (_lock) return _slots.ContainsKey(id);
    }

    public void Play(Guid id) => With(id, p => p.Play());
    public void Pause(Guid id) => With(id, p => p.Pause());
    public void Stop(Guid id) => With(id, p => p.Stop());
    public void Restart(Guid id) => With(id, p => p.Restart());
    public void Seek(Guid id, TimeSpan position) => With(id, p => p.Seek(position));

    /// <summary>Stops every timeline (each releases its channels with its own fade).</summary>
    public void StopAll()
    {
        lock (_lock)
            foreach (var slot in _slots.Values) slot.Player.Stop();
    }

    /// <summary>Stops everything at once, without release fades (blackout, closing).</summary>
    public void StopAllNow()
    {
        StopAll();
        lock (_lock) _skipReleases = true;
    }

    bool _skipReleases;

    /// <summary>What a timeline is playing (its compiled form), or null if it isn't loaded.</summary>
    public CompiledTimeline? TimelineOf(Guid id)
    {
        lock (_lock) return _slots.GetValueOrDefault(id)?.Timeline;
    }

    public PlaybackStatus StatusOf(Guid id)
    {
        lock (_lock) return _slots.TryGetValue(id, out var slot) ? slot.Player.Status : default;
    }

    /// <summary>Every timeline playing or paused, highest priority first.</summary>
    public IReadOnlyList<RunningTimeline> Running
    {
        get
        {
            lock (_lock)
                return _slots.Values.Select(s => new RunningTimeline(s.Id, s.Name, s.Player.Status, s.Timeline.Playback.Priority, s.TimelineId))
                    .Where(r => r.Status.State != TransportState.Stopped)
                    .OrderByDescending(r => r.Priority).ThenBy(r => r.Name)
                    .ToList();
        }
    }

    void With(Guid id, Action<ShowPlayer> action)
    {
        ShowPlayer? player;
        lock (_lock) player = _slots.GetValueOrDefault(id)?.Player;
        if (player is not null) action(player);
    }

    // ---- the playback thread ----

    /// <summary>One update of every timeline, then the merged levels to the engine. Playback thread only.</summary>
    public void Tick()
    {
        Slot[] slots;
        bool skipReleases;
        lock (_lock)
        {
            slots = _slots.Values.ToArray();
            skipReleases = _skipReleases;
            _skipReleases = false;
        }
        long now = _clock.Now;
        if (skipReleases) _releases.Clear();

        // Ducking, from what's playing now: each sound volume scaled by every playing higher-priority ducker.
        foreach (var slot in slots)
        {
            double scale = 1;
            foreach (var other in slots)
                if (other != slot && other.LastState == TransportState.Playing && other.Timeline.Playback.Priority > slot.Timeline.Playback.Priority)
                    scale *= Math.Clamp(other.Timeline.Playback.DuckOthers, 0, 1);
            slot.Player.VolumeScale = scale;
        }

        foreach (var slot in slots) slot.Player.Tick();

        foreach (var slot in slots)
        {
            var state = slot.Player.Status.State;
            bool wasRunning = slot.LastState != TransportState.Stopped, running = state != TransportState.Stopped;
            if (running && !wasRunning) OnStarted(slot, slots);
            if (!running && wasRunning)
            {
                OnStopped(slot, slots, now, skipReleases);
                if (slot.IsCopy) _toRemove.Add(slot.Id);   // a copy is done once it stops
            }
            slot.LastState = state;
            if (slot.Player.Frame is { } frame) slot.LastFrame = Copy(frame, slot.LastFrame);
        }

        Merge(slots, now);

        while (_toRemove.TryTake(out var id))
            lock (_lock)
                if (_slots.TryGetValue(id, out var slot) && slot.Player.Status.State == TransportState.Stopped) _slots.Remove(id);
    }

    /// <summary>An override that pauses: pause every playing lower-priority timeline sharing a channel with it.</summary>
    void OnStarted(Slot started, Slot[] slots)
    {
        var settings = started.Timeline.Playback;
        if (!settings.PauseOverridden) return;
        foreach (var other in slots)
        {
            if (other == started || other.Timeline.Playback.Priority >= settings.Priority) continue;
            if (other.Player.Status.State != TransportState.Playing && other.PausedBy.Count == 0) continue;
            if (!other.Timeline.Channels.Overlaps(started.Timeline.Channels)) continue;
            if (other.PausedBy.Count == 0) other.Player.Pause();
            other.PausedBy.Add(started.Id);
            started.Paused.Add(other.Id);
        }
    }

    /// <summary>A timeline stopped: its channels release (with its fade); what it paused resumes.</summary>
    void OnStopped(Slot stopped, Slot[] slots, long now, bool skipReleases)
    {
        var settings = stopped.Timeline.Playback;
        if (!skipReleases && settings.ReleaseFade > TimeSpan.Zero && stopped.LastFrame is { } last)
        {
            var levels = new List<(int, int, byte)>();
            for (int u = 0; u < last.Universes.Count; u++)
                for (int i = 0; i < DmxUniverse.ChannelCount; i++)
                    if (last.Controlled[u][i]) levels.Add((last.Universes[u], i, last.Values[u][i]));
            if (levels.Count > 0) _releases.Add(new Release(settings.Priority, levels, now, settings.ReleaseFade.Ticks));
        }
        stopped.LastFrame = null;

        foreach (var id in stopped.Paused)
        {
            var other = slots.FirstOrDefault(s => s.Id == id);
            if (other is null || !other.PausedBy.Remove(stopped.Id) || other.PausedBy.Count > 0) continue;
            if (other.Player.Status.State == TransportState.Paused) other.Player.Play();   // where it was
        }
        stopped.Paused.Clear();
    }

    /// <summary>Every playing timeline's levels and every release in progress, merged into the engine's timeline layer.</summary>
    void Merge(Slot[] slots, long now)
    {
        _releases.RemoveAll(r => now - r.StartedAt >= r.Fade);
        var frames = slots.Where(s => s.Player.Frame is not null).Select(s => (Slot: s, Frame: s.Player.Frame!)).ToList();
        if (frames.Count == 0 && _releases.Count == 0)
        {
            if (_layerSet) _engine.ReleaseTimeline();
            _layerSet = false;
            return;
        }

        var universes = frames.SelectMany(f => f.Frame.Universes).Concat(_releases.SelectMany(r => r.Levels.Select(l => l.Universe)))
            .Distinct().Order().ToArray();
        var merged = MergedFrame(universes);
        merged.Clear();
        for (int u = 0; u < universes.Length; u++)
        {
            Array.Fill(_winnerPriority[u], int.MinValue);
            Array.Clear(_winnerStarted[u]);
        }

        foreach (var (slot, frame) in frames)
        {
            int priority = slot.Timeline.Playback.Priority;
            long started = slot.Player.StartedAt;
            for (int fu = 0; fu < frame.Universes.Count; fu++)
            {
                int u = Array.IndexOf(universes, frame.Universes[fu]);
                var values = frame.Values[fu];
                var controlled = frame.Controlled[fu];
                for (int i = 0; i < DmxUniverse.ChannelCount; i++)
                {
                    if (!controlled[i]) continue;
                    int wp = _winnerPriority[u][i];
                    if (priority < wp || (priority == wp && started < _winnerStarted[u][i])) continue;
                    merged.Values[u][i] = values[i];
                    merged.Controlled[u][i] = true;
                    _winnerPriority[u][i] = priority;
                    _winnerStarted[u][i] = started;
                }
            }
        }

        // Releases: from where the stopped timeline left a channel, towards what's underneath now. A higher-priority
        // timeline that has the channel keeps it.
        foreach (var release in _releases)
        {
            double progress = Math.Clamp((double)(now - release.StartedAt) / release.Fade, 0, 1);
            foreach (var (universe, index, level) in release.Levels)
            {
                int u = Array.IndexOf(universes, universe);
                if (merged.Controlled[u][index] && _winnerPriority[u][index] > release.Priority) continue;
                byte underneath = merged.Controlled[u][index] ? merged.Values[u][index] : Manual(universe)[index];
                merged.Values[u][index] = (byte)Math.Round(level + (underneath - level) * progress);
                merged.Controlled[u][index] = true;
            }
        }

        _engine.SetTimelineLevels(merged);
        _layerSet = true;
    }

    /// <summary>The merged frame for a set of universes (reused while the set doesn't change: no allocation per tick).</summary>
    LevelsFrame MergedFrame(int[] universes)
    {
        string key = string.Join(',', universes);
        if (_merged is null || key != _mergedKey)
        {
            _merged = new LevelsFrame(universes);
            _winnerPriority = universes.Select(_ => new int[DmxUniverse.ChannelCount]).ToArray();
            _winnerStarted = universes.Select(_ => new long[DmxUniverse.ChannelCount]).ToArray();
            _mergedKey = key;
        }
        return _merged;
    }

    byte[] Manual(int universe)
    {
        if (!_manual.TryGetValue(universe, out var buffer)) _manual[universe] = buffer = new byte[DmxUniverse.ChannelCount];
        if (!_engine.TryCopyManual(universe, buffer)) Array.Clear(buffer);
        return buffer;
    }

    static LevelsFrame Copy(LevelsFrame frame, LevelsFrame? into)
    {
        if (into is null || !into.Universes.SequenceEqual(frame.Universes)) into = new LevelsFrame(frame.Universes);
        for (int u = 0; u < frame.Universes.Count; u++)
        {
            frame.Values[u].CopyTo(into.Values[u], 0);
            frame.Controlled[u].CopyTo(into.Controlled[u], 0);
        }
        return into;
    }
}
