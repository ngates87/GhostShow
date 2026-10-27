using GhostShow.Audio;
using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging;

namespace GhostShow.Tests.Audio;

/// <summary>
/// The real SoundFlow service, on miniaudio's null backend: a device that consumes audio in real time and outputs
/// nothing. So these tests decode and play for real, silently, on any machine.
/// </summary>
public class AudioServiceTests : IAsyncLifetime
{
    static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", name);

    readonly ListLogger _log = new();
    readonly AudioService _audio;

    public AudioServiceTests() => _audio = new AudioService(_log, useNullDevice: true);

    public async ValueTask InitializeAsync() => Assert.True(await _audio.OpenAsync(null), "the null device opens");

    public ValueTask DisposeAsync()
    {
        _audio.Dispose();
        return ValueTask.CompletedTask;
    }

    static AudioCue Cue(string file, bool loop = false) =>
        new(Guid.NewGuid(), file, Asset(file), Start: 0, End: null, Offset: 0, Volume: 1, FadeIn: 0, FadeOut: 0, Loop: loop);

    [Fact]
    public void Device_opens_and_reports_its_status()
    {
        Assert.Equal(AudioDeviceState.Open, _audio.Status.State);
        Assert.False(string.IsNullOrEmpty(_audio.Status.DeviceName));
    }

    [Theory]
    [InlineData("silence-3s.wav")]
    [InlineData("silence-1s.mp3")]
    public void Wav_and_mp3_files_play(string file)
    {
        var cue = Cue(file);

        _audio.Start(cue, TimeSpan.Zero, volume: 0.5);

        Assert.Equal(AudioInstanceState.Playing, _audio.GetState(cue.EventId));
    }

    [Fact]
    public async Task A_short_file_reports_finished_when_it_ends()
    {
        var cue = Cue("silence-0.5s.wav");
        _audio.Start(cue, TimeSpan.Zero, 1);

        await Eventually.True(() => _audio.GetState(cue.EventId) == AudioInstanceState.Finished, "the file plays out", TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("silence-0.5s.wav", 0.5)]   // 16 kHz mono: must be converted to the device's 48 kHz stereo
    [InlineData("silence-1s.mp3", 1.0)]     // 44.1 kHz mono MP3
    public async Task Files_play_in_real_time_whatever_their_sample_rate_and_channels(string file, double seconds)
    {
        var cue = Cue(file);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _audio.Start(cue, TimeSpan.Zero, 1);

        await Eventually.True(() => _audio.GetState(cue.EventId) == AudioInstanceState.Finished, "the file plays out", TimeSpan.FromSeconds(10));

        Assert.InRange(clock.Elapsed.TotalSeconds, seconds * 0.85, seconds + 0.5);
    }

    [Fact]
    public async Task Starting_at_an_offset_skips_into_the_file()
    {
        var cue = Cue("silence-3s.wav");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        _audio.Start(cue, TimeSpan.FromSeconds(2.5), 1);

        await Eventually.True(() => _audio.GetState(cue.EventId) == AudioInstanceState.Finished, "the last half second plays", TimeSpan.FromSeconds(10));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.3, 1.2);
    }

    [Fact]
    public void Overlapping_instances_are_controlled_independently()
    {
        var a = Cue("silence-3s.wav");
        var b = Cue("silence-3s.wav");
        _audio.Start(a, TimeSpan.Zero, 1);
        _audio.Start(b, TimeSpan.FromSeconds(1), 1);

        _audio.Pause(a.EventId);
        Assert.Equal((AudioInstanceState.Paused, AudioInstanceState.Playing), (_audio.GetState(a.EventId), _audio.GetState(b.EventId)));
        Assert.Equal(2, _audio.Status.Playing);

        _audio.Stop(b.EventId);
        _audio.Resume(a.EventId);
        Assert.Equal((AudioInstanceState.Playing, AudioInstanceState.None), (_audio.GetState(a.EventId), _audio.GetState(b.EventId)));
        Assert.Equal(1, _audio.Status.Playing);
    }

    [Fact]
    public void Stop_all_releases_every_instance()
    {
        for (int i = 0; i < 8; i++) _audio.Start(Cue("silence-3s.wav"), TimeSpan.Zero, 1);

        _audio.StopAll();

        Assert.Equal(0, _audio.Status.Playing);
    }

    [Theory]
    [InlineData("does-not-exist.wav")]
    [InlineData("not-audio.mp3")]
    public void Missing_or_unreadable_files_fail_that_instance_and_are_reported_once(string file)
    {
        var cue = Cue(file);

        for (int i = 0; i < 10; i++) _audio.Start(cue, TimeSpan.Zero, 1);

        Assert.Equal(AudioInstanceState.Failed, _audio.GetState(cue.EventId));
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(AudioDeviceState.Open, _audio.Status.State);   // the service itself is fine
    }

    [Fact]
    public void Without_a_device_starts_fail_instead_of_throwing()
    {
        using var closed = new AudioService(_log, useNullDevice: true);   // never opened
        var cue = Cue("silence-3s.wav");

        closed.Start(cue, TimeSpan.Zero, 1);

        Assert.Equal(AudioInstanceState.Failed, closed.GetState(cue.EventId));
    }

    [Fact]
    public async Task A_lost_device_drops_instances_then_recovers_with_a_new_generation()
    {
        var cue = Cue("silence-3s.wav");
        _audio.Start(cue, TimeSpan.Zero, 1);
        int generation = _audio.Generation;

        _audio.HandleDeviceLost("test: unplugged");

        Assert.Equal(AudioDeviceState.Failed, _audio.Status.State);
        Assert.Equal(AudioInstanceState.None, _audio.GetState(cue.EventId));
        await Eventually.True(() => _audio.Status.State == AudioDeviceState.Open, "the device is reopened", TimeSpan.FromSeconds(8));
        Assert.True(_audio.Generation > generation);
    }

    [Theory]
    [InlineData("silence-0.5s.wav", 0.5)]
    [InlineData("silence-3s.wav", 3.0)]
    [InlineData("silence-1s.mp3", 1.0)]
    public async Task File_lengths_are_measured_in_the_background_accurately(string file, double seconds)
    {
        Assert.Null(_audio.GetLength(Asset(file)));   // never blocks to measure

        await Eventually.True(() => _audio.GetLength(Asset(file)) is not null, "the length is measured");
        Assert.Equal(seconds, _audio.GetLength(Asset(file))!.Value.TotalSeconds, tolerance: 0.1);
    }

    [Fact]
    public async Task Disposing_while_playing_is_clean()
    {
        using var service = new AudioService(_log, useNullDevice: true);
        Assert.True(await service.OpenAsync(null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        for (int i = 0; i < 4; i++) service.Start(Cue("silence-3s.wav"), TimeSpan.Zero, 1);

        service.Dispose();

        Assert.Equal(AudioDeviceState.Closed, service.Status.State);
        Assert.Equal(0, service.Status.Playing);
    }

    [Fact]
    public async Task The_player_drives_the_real_service_through_play_pause_seek_and_stop()
    {
        var engine = new ShowEngine();
        var player = new ShowPlayer(engine, _audio, new StopwatchClock());
        var thunder = new AudioEvent { File = Asset("silence-3s.wav"), Start = TimeSpan.Zero };
        var timeline = new ShowTimeline { Length = TimeSpan.FromSeconds(10), Tracks = [new TimelineTrack { Kind = TrackKind.Audio, Events = [thunder] }] };
        player.Load(TimelineCompiler.Compile(timeline, new TimelineContext(new Dictionary<Guid, Core.Fixtures.PatchedFixture>(), new HashSet<Guid>(), [1], p => p, _audio.GetLength)));

        player.Play();
        player.Tick();
        Assert.Equal(AudioInstanceState.Playing, _audio.GetState(thunder.Id));

        player.Pause();
        player.Tick();
        Assert.Equal(AudioInstanceState.Paused, _audio.GetState(thunder.Id));

        player.Play();
        player.Seek(TimeSpan.FromSeconds(1));
        player.Tick();
        Assert.Equal(AudioInstanceState.Playing, _audio.GetState(thunder.Id));

        player.Stop();
        player.Tick();
        Assert.Equal(AudioInstanceState.None, _audio.GetState(thunder.Id));
        await Task.CompletedTask;
    }
}
