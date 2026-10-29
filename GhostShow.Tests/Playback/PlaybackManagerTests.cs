using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Playback;

/// <summary>Several timelines at once: merging by priority, overrides that pause or keep the ambient, release, ducking.</summary>
public class PlaybackManagerTests : IDisposable
{
    readonly TempDirectory _dir = new();
    readonly ShowEngine _engine = new();
    readonly ManualClock _clock = new();
    readonly FakeAudioOutput _audio = new();
    readonly PlaybackManager _manager;

    public PlaybackManagerTests()
    {
        _manager = new PlaybackManager(_engine, _audio, _clock);
        File.WriteAllBytes(_dir.File("scream.wav"), [0]);
    }

    public void Dispose() => _dir.Dispose();

    static readonly Guid Ambient = Guid.NewGuid(), Scare = Guid.NewGuid(), Other = Guid.NewGuid();

    void Load(Guid id, ShowTimeline timeline) => _manager.Load(id, timeline.Name, Compile(timeline, Context(showFolder: _dir.Path)));

    static ShowTimeline Show(string name, TimelinePlayback? playback = null, double? length = null, bool loop = false, params TimelineTrack[] tracks) => new()
    {
        Name = name, Playback = playback ?? new TimelinePlayback(), Length = length is { } l ? S(l) : null, Loop = loop, Tracks = [.. tracks],
    };

    void After(double seconds)
    {
        _clock.Advance(seconds);
        _manager.Tick();
    }

    byte Output(int address)
    {
        var frame = new byte[512];
        _engine.TryCopyUniverse(1, frame, out _);
        return frame[address - 1];
    }

    /// <summary>A looping 10 s ambient: channel 1 fades 0→200 over 10 s; channel 2 held at 80.</summary>
    ShowTimeline AmbientLoop() => Show("Ambient", loop: true, length: 10, tracks: Lights(Raw(0, 1, to: 200, fade: 10, from: 0), Raw(0, 2, to: 80)));

    [Fact]
    public void Timelines_on_different_channels_play_together()
    {
        Load(Ambient, AmbientLoop());
        Load(Other, Show("Other", length: 5, tracks: Lights(Raw(0, 3, to: 33))));
        _manager.Play(Ambient);
        _manager.Play(Other);

        After(5);

        Assert.Equal((100, 80, 33), (Output(1), Output(2), Output(3)));
        Assert.Equal(2, _manager.Running.Count);
    }

    [Fact]
    public void On_a_shared_channel_higher_priority_wins_and_equal_priority_goes_to_the_later_start()
    {
        Load(Ambient, AmbientLoop());
        Load(Scare, Show("Scare", new TimelinePlayback(Priority: 10), length: 30, tracks: Lights(Raw(0, 2, to: 255))));
        Load(Other, Show("Other", length: 30, tracks: Lights(Raw(0, 2, to: 5))));

        _manager.Play(Scare);
        After(0.1);
        _manager.Play(Ambient);   // started later, but lower priority
        After(0.1);
        Assert.Equal(255, Output(2));

        _manager.Stop(Scare);
        After(0.1);
        _manager.Play(Other);     // same priority as the ambient, started later
        After(0.1);
        Assert.Equal(5, Output(2));
    }

    [Fact]
    public void An_override_takes_only_its_channels_and_a_kept_ambient_is_where_it_has_got_to_when_it_ends()
    {
        Load(Ambient, AmbientLoop());
        Load(Scare, Show("Scare", new TimelinePlayback(Priority: 10), length: 30, tracks: Lights(Raw(0, 1, to: 255))));
        _manager.Play(Ambient);
        After(1);
        _manager.Play(Scare);
        After(2);   // ambient at 3 s

        Assert.Equal((255, 80), (Output(1), Output(2)));   // channel 2 still the ambient's

        _manager.Stop(Scare);
        After(1);   // ambient kept running: at 4 s
        Assert.Equal(80, Output(1));
    }

    [Fact]
    public void An_override_that_pauses_the_ambient_resumes_it_from_where_it_was()
    {
        Load(Ambient, AmbientLoop());
        Load(Scare, Show("Scare", new TimelinePlayback(Priority: 10, PauseOverridden: true), length: 30, tracks: Lights(Raw(0, 1, to: 255))));
        _manager.Play(Ambient);
        After(2);   // ambient at 2 s: channel 1 at 40
        _manager.Play(Scare);
        After(0.01);
        After(5);

        Assert.Equal(TransportState.Paused, _manager.StatusOf(Ambient).State);
        Assert.InRange(_manager.StatusOf(Ambient).Position, S(2), S(2.02));   // where it was when the scare started

        _manager.Stop(Scare);
        After(0.01);
        Assert.Equal(TransportState.Playing, _manager.StatusOf(Ambient).State);
        After(1);   // ~3 s into the ambient
        Assert.InRange(Output(1), 59, 62);
    }

    [Fact]
    public void An_override_that_pauses_leaves_ambients_on_other_channels_alone()
    {
        Load(Ambient, AmbientLoop());
        Load(Other, Show("Other room", loop: true, length: 10, tracks: Lights(Raw(0, 9, to: 50))));
        Load(Scare, Show("Scare", new TimelinePlayback(Priority: 10, PauseOverridden: true), length: 30, tracks: Lights(Raw(0, 1, to: 255))));
        _manager.Play(Ambient);
        _manager.Play(Other);
        After(1);

        _manager.Play(Scare);
        After(0.1);

        Assert.Equal(TransportState.Paused, _manager.StatusOf(Ambient).State);
        Assert.Equal(TransportState.Playing, _manager.StatusOf(Other).State);
    }

    [Fact]
    public void Stopping_fades_the_channels_back_to_the_timeline_underneath()
    {
        Load(Other, Show("Under", length: 60, tracks: Lights(Raw(0, 1, to: 100))));
        Load(Scare, Show("Scare", new TimelinePlayback(Priority: 10, ReleaseFade: S(2)), length: 60, tracks: Lights(Raw(0, 1, to: 200))));
        _manager.Play(Other);
        _manager.Play(Scare);
        After(1);
        Assert.Equal(200, Output(1));

        _manager.Stop(Scare);
        After(0.001);
        After(1);   // half way
        Assert.InRange(Output(1), 149, 151);
        After(1.1);
        Assert.Equal(100, Output(1));
    }

    [Fact]
    public void With_nothing_underneath_a_release_fades_to_the_manual_level_and_then_lets_go()
    {
        _engine.SetChannel(1, 1, 40);
        Load(Scare, Show("Scare", new TimelinePlayback(ReleaseFade: S(2)), length: 60, tracks: Lights(Raw(0, 1, to: 240))));
        _manager.Play(Scare);
        After(1);

        _manager.Stop(Scare);
        After(0.001);
        After(1);
        Assert.InRange(Output(1), 139, 141);
        After(1.1);
        Assert.Equal(40, Output(1));
        Assert.False(_engine.IsTimelineControlled(1, 1));
    }

    [Fact]
    public void Releasing_at_the_end_lets_the_ambient_come_back()
    {
        Load(Ambient, AmbientLoop());
        var scare = Show("Scare", new TimelinePlayback(Priority: 10, PauseOverridden: true, ReleaseAtEnd: true), length: 2,
                         tracks: Lights(Raw(0, 2, to: 255)));
        Load(Scare, scare);
        _manager.Play(Ambient);
        After(1);
        _manager.Play(Scare);
        After(1);
        Assert.Equal(255, Output(2));

        After(1.5);   // past the scare's end
        After(0.01);

        Assert.Equal(TransportState.Stopped, _manager.StatusOf(Scare).State);
        Assert.Equal(TransportState.Playing, _manager.StatusOf(Ambient).State);
        Assert.Equal(80, Output(2));
    }

    [Fact]
    public void A_ducking_override_turns_lower_timelines_sounds_down_while_it_plays()
    {
        var ambient = Show("Ambient", loop: true, length: 30, tracks: new TimelineTrack { Kind = TrackKind.Audio, Events = [new AudioEvent { Name = "wind", File = "scream.wav", Length = S(30) }] });
        Load(Ambient, ambient);
        Load(Scare, Show("Scare", new TimelinePlayback(Priority: 10, DuckOthers: 0.25), length: 5, tracks: Lights(Raw(0, 1, to: 255))));
        _manager.Play(Ambient);
        After(0.1);
        var wind = ambient.Tracks[0].Events[0].Id;
        Assert.Equal(1, _audio.Volumes[wind], precision: 3);

        _manager.Play(Scare);
        After(0.1);
        After(0.1);
        Assert.Equal(0.25, _audio.Volumes[wind], precision: 3);

        _manager.Stop(Scare);
        After(0.1);
        After(0.1);
        Assert.Equal(1, _audio.Volumes[wind], precision: 3);
    }

    [Fact]
    public void Stop_all_now_lets_go_of_everything_without_release_fades()
    {
        Load(Scare, Show("Scare", new TimelinePlayback(ReleaseFade: S(5)), length: 60, tracks: Lights(Raw(0, 1, to: 240))));
        _manager.Play(Scare);
        After(1);

        _manager.StopAllNow();
        After(0.01);

        Assert.Equal(0, Output(1));
        Assert.Empty(_manager.Running);
    }

    [Fact]
    public void An_unloaded_timeline_stops_and_is_forgotten()
    {
        Load(Scare, Show("Scare", length: 60, tracks: Lights(Raw(0, 1, to: 240))));
        _manager.Play(Scare);
        After(1);

        _manager.Unload(Scare);
        After(0.01);

        Assert.False(_manager.IsLoaded(Scare));
        Assert.Equal(0, Output(1));
    }
    // ---- copies (a show button's Run Timeline) ----

    ShowTimeline ScareWithScream() => Show("Scare", length: 4, tracks:
    [
        Lights(Raw(0, 5, to: 255)),
        new TimelineTrack { Kind = TrackKind.Audio, Events = [new AudioEvent { Name = "scream", File = "scream.wav", Length = S(3) }] },
    ]);

    [Fact]
    public void Launching_a_running_timeline_plays_another_copy_with_its_own_sounds()
    {
        Load(Scare, ScareWithScream());

        Assert.Equal(Scare, _manager.Launch(Scare));   // stopped: its own player
        After(1);
        var copy = _manager.Launch(Scare)!.Value;      // running: a copy, from the start
        After(0.5);

        Assert.NotEqual(Scare, copy);
        Assert.True(_manager.IsRunning(Scare));
        Assert.Equal([(Scare, 1.5), (copy, 0.5)], _manager.Running.OrderByDescending(r => r.Status.Position).Select(r => (r.Id, Math.Round(r.Status.Position.TotalSeconds, 2))));
        Assert.All(_manager.Running, r => Assert.Equal(Scare, r.TimelineId));
        Assert.Equal(2, _audio.States.Count(s => s.Value == AudioInstanceState.Playing));   // two screams, overlapping
        Assert.Equal(255, Output(5));

        _manager.StopTimeline(Scare);
        After(0.1);

        Assert.False(_manager.IsRunning(Scare));
        Assert.Empty(_manager.Running);
        Assert.Empty(_audio.States);
        Assert.True(_manager.IsLoaded(Scare));   // the timeline stays; the copy is gone
        Assert.Equal(TransportState.Stopped, _manager.StatusOf(copy).State);
    }

    [Fact]
    public void A_copy_is_forgotten_when_it_stops_and_follows_edits_while_it_plays()
    {
        Load(Scare, ScareWithScream());
        _manager.Launch(Scare);
        var copy = _manager.Launch(Scare)!.Value;
        After(0.5);

        Load(Scare, Show("Scare", length: 4, tracks: Lights(Raw(0, 5, to: 77))));   // edited while both play
        After(0.1);
        Assert.Equal(77, Output(5));

        _manager.Stop(copy);
        After(0.1);
        Assert.Equal([Scare], _manager.Running.Select(r => r.Id));
        Assert.Null(_manager.TimelineOf(copy));
    }

    [Fact]
    public void Launching_beyond_the_most_copies_stops_the_oldest_copy()
    {
        Load(Scare, Show("Scare", length: 60, tracks: Lights(Raw(0, 5, to: 255))));
        var launched = Enumerable.Range(0, PlaybackManager.MaxCopies).Select(_ => { var id = _manager.Launch(Scare)!.Value; After(0.1); return id; }).ToList();

        var another = _manager.Launch(Scare)!.Value;
        After(0.1);

        Assert.Equal(PlaybackManager.MaxCopies, _manager.Running.Count);
        Assert.DoesNotContain(launched[1], _manager.Running.Select(r => r.Id));   // the oldest copy (the first is the timeline's own)
        Assert.Contains(launched[0], _manager.Running.Select(r => r.Id));
        Assert.Contains(another, _manager.Running.Select(r => r.Id));
        Assert.Null(_manager.Launch(Other));   // not loaded
    }

    // ---- event tracks ----

    static TimelineTrack Events(params (double At, Guid Timeline, TimelineCommand Command)[] events) => new()
    {
        Kind = TrackKind.Event,
        Events = [.. events.Select(e => new TimelineCommandEvent { Start = S(e.At), TimelineId = e.Timeline, Command = e.Command })],
    };

    static TimelineTrack OnceOnly(TimelineTrack track)
    {
        foreach (var e in track.Events.OfType<TimelineCommandEvent>()) e.PlayOnceOnly = true;
        return track;
    }

    [Fact]
    public void An_event_track_runs_and_stops_other_timelines_when_its_playhead_gets_there()
    {
        Load(Ambient, Show("Play room", length: 10, tracks: Events((1, Scare, TimelineCommand.Run), (5, Scare, TimelineCommand.Stop))));
        Load(Scare, Show("Room lights", length: 60, tracks: Lights(Raw(0, 5, to: 200))));
        _manager.Play(Ambient);

        After(0.5);
        Assert.False(_manager.IsRunning(Scare));
        After(0.6);   // passes 1 s
        After(0.1);   // started on the update after
        Assert.True(_manager.IsRunning(Scare));
        Assert.Equal(200, Output(5));

        After(4);     // passes 5 s
        Assert.False(_manager.IsRunning(Scare));
    }

    [Fact]
    public void Events_at_the_start_fire_on_play_and_again_on_every_loop_as_new_copies_unless_play_once_only()
    {
        Load(Ambient, Show("Loop", length: 3, loop: true, tracks: Events((0, Scare, TimelineCommand.Run))));
        Load(Scare, Show("Scream", length: 60, tracks: Lights(Raw(0, 5, to: 200))));
        Load(Other, Show("Once", length: 3, loop: true, tracks: OnceOnly(Events((0, Scare, TimelineCommand.Run)))));

        _manager.Play(Ambient);
        After(0.1);
        Assert.Single(_manager.Running, r => r.TimelineId == Scare);
        After(3);     // looped: runs again, as a copy
        After(0.1);
        Assert.Equal(2, _manager.Running.Count(r => r.TimelineId == Scare));

        _manager.StopAll();
        After(0.1);
        _manager.Play(Other);
        After(0.1);
        After(3);
        After(0.1);
        Assert.Single(_manager.Running, r => r.TimelineId == Scare);   // play once only: not again while it runs
    }

    [Fact]
    public void A_seek_skips_the_events_it_jumps_over_and_events_at_the_very_end_still_fire()
    {
        Load(Ambient, Show("Play room", length: 4, tracks: Events((1, Scare, TimelineCommand.Run), (4, Other, TimelineCommand.Run))));
        Load(Scare, Show("Lights", length: 60, tracks: Lights(Raw(0, 5, to: 200))));
        Load(Other, Show("Sound", length: 60, tracks: Lights(Raw(0, 6, to: 100))));
        _manager.Play(Ambient);
        After(0.1);

        _manager.Seek(Ambient, S(2));
        After(0.1);
        After(0.1);
        Assert.False(_manager.IsRunning(Scare));

        After(2.5);   // reaches the end (4 s) and holds there
        After(0.1);
        Assert.True(_manager.IsRunning(Other));
    }

    [Fact]
    public void Timelines_run_by_events_can_run_others_in_turn_and_an_unloaded_one_is_reported()
    {
        var third = Guid.NewGuid();
        var missing = Guid.NewGuid();
        Load(Ambient, Show("Start zone", length: 10, tracks: Events((0, Scare, TimelineCommand.Run), (0, missing, TimelineCommand.Run))));
        Load(Scare, Show("Play room", length: 10, tracks: Events((0, third, TimelineCommand.Run))));
        Load(third, Show("Room lights", length: 10, tracks: Lights(Raw(0, 7, to: 99))));
        var notLoaded = new List<Guid>();
        _manager.CueNotLoaded += cue => notLoaded.Add(cue.Timeline);

        _manager.Play(Ambient);
        for (int i = 0; i < 4; i++) After(0.05);

        Assert.True(_manager.IsRunning(third));
        Assert.Equal(99, Output(7));
        Assert.Equal([missing], notLoaded);
    }

    [Fact]
    public void The_compiler_reports_an_event_whose_timeline_is_not_in_the_show()
    {
        var gone = new TimelineCommandEvent { TimelineId = Guid.NewGuid(), Start = S(1) };
        var context = Context() with { Timelines = new HashSet<Guid> { Scare } };

        var compiled = Compile(Show("Play", tracks: new TimelineTrack { Kind = TrackKind.Event, Events = [gone, new TimelineCommandEvent { TimelineId = Scare, Start = S(2) }] }), context);

        Assert.Contains(compiled.Issues, i => i.EventId == gone.Id && i.Message.Contains("isn't in the show"));
        Assert.Equal([(S(2).Ticks, Scare)], compiled.Cues.Select(c => (c.Time, c.Timeline)));
        Assert.Equal(S(2).Ticks, compiled.Length);
    }
}
