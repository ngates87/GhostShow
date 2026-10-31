using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Tests.Playback;

/// <summary>
/// <see cref="ShowPlayback"/>: playback on the show computer, kept up to date with the show. What both the desktop
/// and the web remote rely on.
/// </summary>
public class ShowPlaybackTests
{
    sealed class NoAudioFiles : IAudioFiles
    {
        public TimeSpan? GetLength(string path) => null;
        public Waveform? GetWaveform(string path) => null;
    }

    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    readonly ShowSession _session = new(new ShowEngine(), new FixtureProfileLibrary(FixtureProfileLoader.LoadBuiltIn()), NullLogger<ShowSession>.Instance);
    readonly PlaybackManager _manager = new(new ShowEngine(), new FakeAudioOutput(), new ManualClock());
    readonly ShowPlayback _playback;
    readonly ShowTimeline _main = new() { Name = "Main", Length = S(30) };
    readonly ShowTimeline _scare = new() { Name = "Scare", Length = S(10) };

    public ShowPlaybackTests()
    {
        // Main's event track runs the scare at 5 s.
        _main.Tracks.Add(new TimelineTrack
        {
            Name = "Events", Kind = TrackKind.Event,
            Events = [new TimelineCommandEvent { Start = S(5), Command = TimelineCommand.Run, TimelineId = _scare.Id }],
        });
        _session.Use(new Project { Timelines = [_main, _scare] });
        _playback = new ShowPlayback(_session, _manager, new NoAudioFiles());
    }

    [Fact]
    public void Loading_a_timeline_loads_the_timelines_its_events_run()
    {
        _playback.Load(_main.Id);

        Assert.Empty(_playback.IssuesOf(_main.Id));
        Assert.True(_manager.IsLoaded(_main.Id));
        Assert.True(_manager.IsLoaded(_scare.Id));   // ready before the playhead gets to its event
    }

    [Fact]
    public void An_edit_to_a_loaded_timeline_recompiles_it_and_says_so()
    {
        _playback.Load(_main.Id);
        var compiled = new List<Guid>();
        _playback.Compiled += compiled.Add;

        _session.Document.Apply(new Transaction("Change length", new SetOp(ShowPath.Timeline(_main.Id).Then("length"), S(12))));

        Assert.Equal(S(12), _manager.StatusOf(_main.Id).Length);
        Assert.Contains(_main.Id, compiled);
    }

    [Fact]
    public void A_timeline_that_isn_t_loaded_isn_t_compiled_on_an_edit()
    {
        _session.Document.Apply(new Transaction("Change length", new SetOp(ShowPath.Timeline(_scare.Id).Then("length"), S(3))));

        Assert.False(_manager.IsLoaded(_scare.Id));
    }

    [Fact]
    public void A_deleted_timeline_stops_and_is_unloaded()
    {
        _playback.Play(_scare.Id);
        Assert.True(_playback.IsRunning(_scare.Id));

        _session.Document.Apply(new Transaction("Delete", new RemoveOp(ShowPath.Timeline(_scare.Id))));

        Assert.False(_playback.IsRunning(_scare.Id));   // stopped at once
        _manager.Tick();                                // and forgotten on the next tick, once its lights are released
        Assert.False(_manager.IsLoaded(_scare.Id));
    }

    [Fact]
    public void Play_loads_what_it_needs_and_launch_makes_copies_unless_play_once_only()
    {
        _playback.Play(_main.Id);
        Assert.Equal(TransportState.Playing, _playback.StatusOf(_main.Id).State);

        Assert.True(_playback.Launch(_scare.Id));
        Assert.True(_playback.Launch(_scare.Id));                       // a second copy, as VenueMagic does
        Assert.False(_playback.Launch(_scare.Id, playOnceOnly: true));   // not while one is running
        Assert.Equal(2, _manager.Running.Count(r => r.TimelineId == _scare.Id));

        _playback.StopTimeline(_scare.Id);
        Assert.False(_playback.IsRunning(_scare.Id));
    }

    [Fact]
    public void A_timeline_s_problems_are_kept_from_its_last_compile()
    {
        var gone = Guid.NewGuid();
        _main.Tracks[0].Events.Add(new TimelineCommandEvent { Start = S(8), Command = TimelineCommand.Run, TimelineId = gone });

        _playback.Load(_main.Id);

        var issue = Assert.Single(_playback.IssuesOf(_main.Id));
        Assert.Contains("isn't in the show", issue.Message);
        _playback.Reset();
        Assert.Empty(_playback.IssuesOf(_main.Id));
    }
}
