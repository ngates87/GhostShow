using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Playback;

public class ShowPlayerAudioTests : IDisposable
{
    readonly TempDirectory _dir = new();
    readonly ShowEngine _engine = new();
    readonly ManualClock _clock = new();
    readonly FakeAudioOutput _audio = new();
    readonly ShowPlayer _player;

    public ShowPlayerAudioTests()
    {
        _player = new ShowPlayer(_engine, _audio, _clock);
        foreach (var name in new[] { "thunder.wav", "scream.mp3" }) File.WriteAllBytes(_dir.File(name), [0]);
    }

    public void Dispose() => _dir.Dispose();

    AudioEvent Sound(string file, double start, double? length = null, double volume = 1, double fadeIn = 0, double fadeOut = 0) => new()
    {
        Name = file, File = file, Start = S(start), Length = length is { } l ? S(l) : null, Volume = volume, FadeIn = S(fadeIn), FadeOut = S(fadeOut),
    };

    void Load(params TimelineEvent[] events)
    {
        var timeline = Timeline(new TimelineTrack { Kind = TrackKind.Audio, Events = [.. events] }, Lights(Raw(0, 1, to: 200, fade: 60, from: 0)));
        _player.Load(Compile(timeline, Context(showFolder: _dir.Path)));
    }

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

    [Fact]
    public void A_cue_starts_once_when_due_and_is_not_restarted()
    {
        Load(Sound("thunder.wav", start: 2, length: 5));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away

        After(1.99);
        Assert.Empty(_audio.Calls);
        After(0.01);
        for (int i = 0; i < 100; i++) After(0.01);

        Assert.Equal(["start thunder.wav @0"], _audio.Calls);
    }

    [Fact]
    public void A_late_update_starts_the_cue_at_the_matching_file_offset()
    {
        Load(Sound("thunder.wav", start: 2, length: 5));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away

        After(2.35);   // the update that should have started it at 2.0 s ran 0.35 s late

        Assert.Equal(["start thunder.wav @0.35"], _audio.Calls);
    }

    [Fact]
    public void A_cue_stops_at_its_end()
    {
        Load(Sound("thunder.wav", start: 1, length: 2));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(1.5);

        After(2);

        Assert.Equal(["start thunder.wav @0.5", "stop"], _audio.Calls);   // first update was 0.5 s after its start
    }

    [Fact]
    public void Overlapping_cues_play_independently()
    {
        var thunder = Sound("thunder.wav", start: 0, length: 10);
        var scream = Sound("scream.mp3", start: 2, length: 1);
        Load(thunder, scream);
        _player.Play();
        _player.Tick();   // the playback thread updates straight away

        After(2.5);
        Assert.Equal(AudioInstanceState.Playing, _audio.GetState(thunder.Id));
        Assert.Equal(AudioInstanceState.Playing, _audio.GetState(scream.Id));
        After(1);

        Assert.Equal(AudioInstanceState.Playing, _audio.GetState(thunder.Id));
        Assert.Equal(AudioInstanceState.None, _audio.GetState(scream.Id));   // stopped on its own schedule only
    }

    [Fact]
    public void Pause_and_resume_pause_and_resume_the_instance_without_restarting_it()
    {
        Load(Sound("thunder.wav", start: 0, length: 10));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(1);

        _player.Pause();
        After(5);
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(1);

        Assert.Equal(["start thunder.wav @0", "pause", "resume"], _audio.Calls);
    }

    [Fact]
    public void Seeking_into_a_cue_moves_it_to_the_matching_offset()
    {
        var thunder = Sound("thunder.wav", start: 1, length: 10);
        Load(thunder);
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(2);

        _player.Seek(S(7.5));
        After(0);

        Assert.Equal(["start thunder.wav @1", "seek @6.5"], _audio.Calls);
    }

    [Fact]
    public void Seeking_into_a_cue_that_is_not_playing_starts_it_part_way()
    {
        Load(Sound("thunder.wav", start: 5, length: 10));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(1);

        _player.Seek(S(8));
        After(0);

        Assert.Equal(["start thunder.wav @3"], _audio.Calls);
    }

    [Fact]
    public void Seeking_out_of_a_cue_or_stopping_stops_it()
    {
        Load(Sound("thunder.wav", start: 1, length: 3));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(2);
        _player.Seek(S(10));
        After(0);
        Assert.Equal(["start thunder.wav @1", "stop"], _audio.Calls);

        _player.Seek(S(2));
        After(0);
        _player.Stop();
        _player.Tick();
        Assert.Equal(["start thunder.wav @1", "stop", "start thunder.wav @1", "stop"], _audio.Calls);
    }

    [Fact]
    public void Volume_follows_the_fades_on_the_timeline()
    {
        var thunder = Sound("thunder.wav", start: 0, length: 10, volume: 0.8, fadeIn: 2, fadeOut: 4);
        Load(thunder);
        _player.Play();
        _player.Tick();   // the playback thread updates straight away

        After(1);
        Assert.Equal(0.4, _audio.Volumes[thunder.Id], precision: 6);
        After(4);
        Assert.Equal(0.8, _audio.Volumes[thunder.Id], precision: 6);
        After(4);
        Assert.Equal(0.2, _audio.Volumes[thunder.Id], precision: 6);
    }

    [Fact]
    public void A_file_that_finished_on_its_own_is_not_played_again_until_a_seek()
    {
        var thunder = Sound("thunder.wav", start: 0);   // no length: plays until the file ends
        Load(thunder);
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(1);
        _audio.States[thunder.Id] = AudioInstanceState.Finished;

        for (int i = 0; i < 50; i++) After(0.1);
        Assert.Equal(["start thunder.wav @0"], _audio.Calls);

        _player.Seek(S(0.5));
        After(0);
        Assert.Equal(["start thunder.wav @0", "start thunder.wav @0.5"], _audio.Calls);
    }

    [Fact]
    public void An_edit_leaves_playing_sounds_that_did_not_change_alone()
    {
        var thunder = Sound("thunder.wav", start: 0, length: 10);
        Load(thunder);
        _player.Play();
        _player.Tick();
        After(1);

        Load(thunder);   // e.g. a lighting event elsewhere was edited
        After(0.01);

        Assert.Equal(["start thunder.wav @0"], _audio.Calls);   // no seek: re-seeking on every edit would make it skip
    }

    [Fact]
    public void An_edit_does_not_restart_a_sound_that_just_finished()
    {
        // Found by the playback soak test: the decoder can finish a few ms before the cue's computed end; an edit
        // landing in that gap started the sound again at its last moment.
        var thunder = Sound("thunder.wav", start: 0, length: 2);
        _audio.Lengths[_dir.File("thunder.wav")] = S(2);
        Load(thunder);
        _player.Play();
        _player.Tick();
        After(1.99);
        _audio.States[thunder.Id] = AudioInstanceState.Finished;

        Load(thunder);
        After(0.005);

        Assert.Equal(["start thunder.wav @0"], _audio.Calls);
    }

    [Fact]
    public void Moving_a_playing_sound_in_an_edit_puts_it_at_its_new_place()
    {
        var thunder = Sound("thunder.wav", start: 0, length: 10);
        Load(thunder);
        _player.Play();
        _player.Tick();
        After(3);

        thunder.Start = S(1);
        Load(thunder);
        After(0);

        Assert.Equal(["start thunder.wav @0", "seek @2"], _audio.Calls);
    }

    [Fact]
    public void A_failed_cue_is_not_retried_every_update_but_is_retried_after_a_seek()
    {
        _audio.FailStarts = true;
        Load(Sound("thunder.wav", start: 0, length: 30));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away

        for (int i = 0; i < 100; i++) After(0.01);
        Assert.Single(_audio.Calls, c => c.StartsWith("start"));

        _player.Seek(S(5));
        After(0.01);
        After(0.01);
        Assert.Equal(2, _audio.Calls.Count(c => c.StartsWith("start")));
    }

    [Fact]
    public void When_the_device_comes_back_playing_cues_restart_at_the_current_position()
    {
        Load(Sound("thunder.wav", start: 0, length: 30));
        _player.Play();
        _player.Tick();
        After(4);

        _audio.States.Clear();   // the device was lost and reopened: every instance is gone
        _audio.Generation++;
        After(0.5);

        Assert.Equal(["start thunder.wav @0", "start thunder.wav @4.5"], _audio.Calls);
    }

    [Fact]
    public void Audio_failures_do_not_stop_lighting()
    {
        _audio.FailStarts = true;
        Load(Sound("thunder.wav", start: 0, length: 30));
        _player.Play();
        _player.Tick();   // the playback thread updates straight away

        After(15);

        Assert.Equal(50, Output(1));   // the 60 s fade to 200 carries on
    }

    [Fact]
    public void An_edit_that_changes_a_playing_cue_re_seeks_or_restarts_it_and_an_unchanged_one_carries_on()
    {
        var thunder = Sound("thunder.wav", start: 0, length: 30);
        var scream = Sound("scream.mp3", start: 0, length: 30);
        Load(thunder, scream);
        _player.Play();
        _player.Tick();   // the playback thread updates straight away
        After(4);

        thunder.Start = S(1);   // moved: should now be 3 s in
        Load(thunder, scream);
        After(0);

        Assert.Equal(TimeSpan.FromSeconds(3), _audio.Offsets[thunder.Id]);
        Assert.Equal(2, _audio.Calls.Count(c => c.StartsWith("start")));   // nothing restarted
    }

    [Fact]
    public void Playback_loop_keeps_running_after_a_failing_update_and_reports_it()
    {
        _audio.ThrowOnStart = true;
        var log = new ListLogger();
        var player = new ShowPlayer(_engine, _audio, new StopwatchClock());
        var timeline = Timeline(new TimelineTrack { Kind = TrackKind.Audio, Events = [Sound("thunder.wav", start: 0, length: 30)] }, Lights(Raw(0, 1, to: 200)));
        player.Load(Compile(timeline, Context(showFolder: _dir.Path)));
        var loop = new PlaybackLoop(player, log);
        player.Play();

        loop.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (loop.ErrorCount < 20 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        bool stopped = loop.Stop(TimeSpan.FromSeconds(2));

        Assert.True(stopped);
        Assert.True(loop.ErrorCount >= 20, "the loop kept ticking after errors");
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error);   // reported once, not 20 times
        Assert.Equal(200, Output(1));                                 // levels were applied before audio failed
    }
}
