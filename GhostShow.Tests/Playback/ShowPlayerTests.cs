using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Playback;

public class ShowPlayerTests
{
    readonly ShowEngine _engine = new();
    readonly ManualClock _clock = new();
    readonly FakeAudioOutput _audio = new();
    readonly ShowPlayer _player;
    readonly List<string> _markers = [];

    public ShowPlayerTests()
    {
        _player = new ShowPlayer(_engine, _audio, _clock);
        _player.MarkerReached += m => _markers.Add(m.Name);
    }

    void Load(params TimelineTrack[] tracks) => _player.Load(Compile(Timeline(tracks)));

    void LoadTimeline(ShowTimeline timeline) => _player.Load(Compile(timeline));

    /// <summary>Moves time on and runs one playback update, as the playback thread would.</summary>
    void After(double seconds)
    {
        _clock.Advance(seconds);
        _player.Tick();
    }

    byte Output(int address)
    {
        var frame = new byte[512];
        _engine.TryCopyUniverse(1, frame, out _);
        return frame[address - 1];
    }

    static TimelineTrack Markers(params (double At, string Name)[] markers) => new()
    {
        Kind = TrackKind.Markers, Events = [.. markers.Select(m => (TimelineEvent)new MarkerEvent { Name = m.Name, Start = S(m.At) })],
    };

    // ---- transport ----

    [Fact]
    public void Starts_stopped_at_zero_with_no_timeline_output()
    {
        Load(Lights(Raw(0, 1, to: 200)));
        _player.Tick();

        Assert.Equal(TransportState.Stopped, _player.Status.State);
        Assert.Equal(0, Output(1));
    }

    [Fact]
    public void Playing_follows_the_clock()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));
        _player.Play();

        After(2.5);

        Assert.Equal(S(2.5), _player.Status.Position);
        Assert.Equal(50, Output(1));
    }

    [Fact]
    public void Pause_freezes_position_and_levels_and_play_resumes_from_there()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));
        _player.Play();
        After(2.5);

        _player.Pause();
        After(30);
        Assert.Equal((TransportState.Paused, S(2.5)), (_player.Status.State, _player.Status.Position));
        Assert.Equal(50, Output(1));

        _player.Play();
        After(2.5);
        Assert.Equal(S(5), _player.Status.Position);
        Assert.Equal(100, Output(1));
    }

    [Fact]
    public void Stop_releases_timeline_control_so_manual_levels_return_and_goes_back_to_the_start()
    {
        _engine.SetChannel(1, 1, 77);   // a manual level underneath
        Load(Lights(Raw(0, 1, to: 200)));
        _player.Play();
        After(1);
        Assert.Equal(200, Output(1));

        _player.Stop();
        _player.Tick();

        Assert.Equal((TransportState.Stopped, TimeSpan.Zero), (_player.Status.State, _player.Status.Position));
        Assert.Equal(77, Output(1));
    }

    [Fact]
    public void Manual_changes_on_a_timeline_channel_are_kept_and_show_after_stop()
    {
        Load(Lights(Raw(0, 1, to: 200)));
        _player.Play();
        After(1);

        _engine.SetChannel(1, 1, 33);
        _engine.SetChannel(1, 2, 44);   // a channel the timeline doesn't touch

        Assert.Equal((200, 44), (Output(1), Output(2)));
        Assert.True(_engine.IsTimelineControlled(1, 1));
        var controlled = new bool[512];
        _engine.CopyTimelineControlled(1, controlled);
        Assert.Equal([0], controlled.Index().Where(c => c.Item).Select(c => c.Index));   // only address 1, for the faders' badge

        _player.Stop();
        _player.Tick();
        Assert.Equal((33, 44), (Output(1), Output(2)));
        _engine.CopyTimelineControlled(1, controlled);
        Assert.DoesNotContain(true, controlled);
    }

    [Fact]
    public void Restart_plays_from_the_start()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));
        _player.Play();
        After(5);

        _player.Restart();
        After(1);

        Assert.Equal(S(1), _player.Status.Position);
        Assert.Equal(20, Output(1));
    }

    [Theory]
    [InlineData(7.5, 150)]   // forward, into the middle of the fade
    [InlineData(1.0, 20)]    // backward
    public void Seek_evaluates_the_new_position_directly(double to, byte expected)
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));
        _player.Play();
        After(4);

        _player.Seek(S(to));
        _player.Tick();

        Assert.Equal(expected, Output(1));
        Assert.Equal(TransportState.Playing, _player.Status.State);
    }

    [Fact]
    public void Seeking_while_stopped_moves_the_playhead_without_taking_control()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));

        _player.Seek(S(5));
        _player.Tick();
        Assert.Equal(0, Output(1));

        _player.Play();
        After(0);
        Assert.Equal(100, Output(1));
    }

    [Fact]
    public void Seek_stops_at_zero_but_may_go_past_the_end_where_playing_holds_the_final_look()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10)));

        _player.Seek(S(-3));
        Assert.Equal(TimeSpan.Zero, _player.Status.Position);
        _player.Seek(S(99));
        Assert.Equal(S(99), _player.Status.Position);   // the editor places new events at the playhead

        _player.Play();
        After(0.01);
        Assert.Equal(new PlaybackStatus(TransportState.Paused, S(10), S(10), AtEnd: true, Loop: false), _player.Status);
        Assert.Equal(200, Output(1));
    }

    [Fact]
    public void A_late_update_lands_exactly_where_the_clock_says()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0), Raw(3, 2, to: 99)));
        _player.Play();
        After(0.01);

        After(6);   // the playback thread was stalled for 6 s

        Assert.Equal(S(6.01), _player.Status.Position);
        Assert.Equal(120, Output(1));
        Assert.Equal(99, Output(2));   // an event crossed during the stall still applies
    }

    [Fact]
    public void Position_does_not_drift_over_many_updates()
    {
        Load(Lights(Raw(0, 1, to: 1)) , new TimelineTrack { Kind = TrackKind.Markers, Events = [new MarkerEvent { Start = TimeSpan.FromHours(2) }] });
        _player.Play();

        for (int i = 0; i < 360_000; i++) After(0.01);   // an hour of 10 ms updates

        Assert.Equal(TimeSpan.FromHours(1), _player.Status.Position);
    }

    [Fact]
    public void A_non_looping_timeline_holds_its_final_look_at_the_end_and_play_starts_again()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));
        _player.Play();

        After(15);

        Assert.Equal((TransportState.Paused, true, S(10)), (_player.Status.State, _player.Status.AtEnd, _player.Status.Position));
        Assert.Equal(200, Output(1));
        _player.Play();
        After(1);
        Assert.Equal(S(1), _player.Status.Position);
    }

    [Fact]
    public void A_looping_timeline_wraps_and_keeps_its_timing()
    {
        var timeline = Timeline(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));
        timeline.Loop = true;
        LoadTimeline(timeline);
        _player.Play();

        After(12.5);

        Assert.Equal((TransportState.Playing, S(2.5)), (_player.Status.State, _player.Status.Position));
        Assert.Equal(50, Output(1));
    }

    [Fact]
    public void Repeated_transport_commands_in_any_order_stay_consistent()
    {
        Load(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)));
        var random = new Random(7);
        for (int i = 0; i < 5_000; i++)
        {
            switch (random.Next(5))
            {
                case 0: _player.Play(); break;
                case 1: _player.Pause(); break;
                case 2: _player.Stop(); break;
                case 3: _player.Seek(S(random.NextDouble() * 12)); break;
                case 4: _player.Restart(); break;
            }
            After(random.NextDouble() * 0.05);
            var status = _player.Status;
            // Playing never runs past the end; paused or stopped it can sit wherever a seek put it (up to 12 s here).
            Assert.InRange(status.Position, TimeSpan.Zero, status.State == TransportState.Playing ? S(10) : S(12));
            if (status.State == TransportState.Stopped) Assert.Equal(0, Output(1));
        }
    }

    // ---- live edits ----

    [Fact]
    public void Disabling_a_track_during_playback_releases_its_channels_on_the_next_update()
    {
        var track = Lights(Raw(0, 1, to: 200));
        var timeline = Timeline(track, Lights(Raw(0, 2, to: 50, fade: 60)));
        LoadTimeline(timeline);
        _player.Play();
        After(1);
        Assert.Equal(200, Output(1));

        track.Enabled = false;
        LoadTimeline(timeline);
        After(0.01);

        Assert.Equal(0, Output(1));
        Assert.Equal(TransportState.Playing, _player.Status.State);
        Assert.Equal(S(1.01), _player.Status.Position);   // an edit doesn't move the playhead
    }

    // ---- markers ----

    [Fact]
    public void Markers_are_reported_once_when_crossed_even_by_a_late_update()
    {
        Load(Markers((1, "A"), (2, "B"), (2, "C"), (5, "D")));
        _player.Play();
        After(0.5);

        After(2);   // late: crosses A, B and C in one update
        After(0.5);
        After(0.5);

        Assert.Equal(["A", "B", "C"], _markers);
    }

    [Fact]
    public void Seeking_over_markers_does_not_report_them()
    {
        Load(Markers((1, "A"), (5, "B")));
        _player.Play();
        After(0.1);

        _player.Seek(S(4));
        After(0.01);
        After(1);

        Assert.Equal(["B"], _markers);
    }

    [Fact]
    public void Markers_are_reported_across_a_loop_wrap()
    {
        var timeline = Timeline(Markers((0, "Top"), (9, "Near end")));
        timeline.Length = S(10);
        timeline.Loop = true;
        LoadTimeline(timeline);
        _player.Play();
        After(8);

        After(3);   // 8 -> 11 = wraps to 1

        Assert.Equal(["Near end", "Top"], _markers);
    }

    [Fact]
    public void An_edit_while_playing_does_not_swallow_markers_crossed_on_that_update()
    {
        // Found by the playback soak test: an edit swapped in between two updates hid a marker crossed in that gap.
        Load(Markers((1, "A")));
        _player.Play();
        After(0.995);

        Load(Markers((1, "A")));   // an edit lands
        After(0.01);

        Assert.Equal(["A"], _markers);
    }

    [Fact]
    public void Paused_playback_reports_no_markers()
    {
        Load(Markers((1, "A")));
        _player.Play();
        After(0.5);
        _player.Pause();
        After(5);

        Assert.Empty(_markers);
    }
}
