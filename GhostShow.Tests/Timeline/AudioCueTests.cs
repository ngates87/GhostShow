using GhostShow.Core.Timeline;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Timelines;

public class AudioCueTests : IDisposable
{
    readonly TempDirectory _dir = new();
    readonly string _wav;

    public AudioCueTests()
    {
        _wav = _dir.File("thunder.wav");
        File.WriteAllBytes(_wav, [0]);   // contents don't matter to the compiler; existence does
    }

    public void Dispose() => _dir.Dispose();

    AudioCue CompileOne(AudioEvent e, Func<string, TimeSpan?>? length = null)
    {
        var compiled = Compile(Timeline(new TimelineTrack { Kind = TrackKind.Audio, Events = [e] }), Context(showFolder: _dir.Path, audioLength: length));
        Assert.Empty(compiled.Issues);
        return Assert.Single(compiled.AudioCues);
    }

    [Fact]
    public void Relative_files_resolve_against_the_show_folder()
    {
        var cue = CompileOne(new AudioEvent { File = "thunder.wav" });

        Assert.Equal(_wav, cue.Path);
    }

    [Fact]
    public void The_track_volume_scales_every_clip_volume()
    {
        var loud = new AudioEvent { File = "thunder.wav", Volume = 0.8 };
        var full = new AudioEvent { File = "thunder.wav", Start = S(5) };
        var track = new TimelineTrack { Kind = TrackKind.Audio, Volume = 0.5, Events = [loud, full] };

        var compiled = Compile(Timeline(track), Context(showFolder: _dir.Path));

        Assert.Equal([0.4, 0.5], compiled.AudioCues.Select(c => Math.Round(c.Volume, 6)));
        Assert.Equal(1.0, new TimelineTrack().Volume);   // a new track (or one saved before track volumes) is at full volume
    }

    [Fact]
    public void Missing_files_are_reported_with_the_path_looked_for()
    {
        var e = new AudioEvent { File = "nope.wav" };

        var compiled = Compile(Timeline(new TimelineTrack { Kind = TrackKind.Audio, Events = [e] }), Context(showFolder: _dir.Path));

        Assert.Contains(compiled.Issues, i => i.EventId == e.Id && i.Message.Contains(Path.Combine(_dir.Path, "nope.wav")));
        Assert.Empty(compiled.AudioCues);
    }

    [Fact]
    public void End_comes_from_the_length_or_else_the_file_length_minus_the_offset()
    {
        Assert.Equal(T(5), CompileOne(new AudioEvent { File = _wav, Start = S(2), Length = S(3) }).End);
        Assert.Equal(T(9), CompileOne(new AudioEvent { File = _wav, Start = S(2), Offset = S(1) }, _ => S(8)).End);
        Assert.Null(CompileOne(new AudioEvent { File = _wav }).End);   // length not known yet: plays until it finishes
    }

    [Theory]
    [InlineData(10.0, 0.0)]
    [InlineData(10.5, 0.25)]
    [InlineData(11.0, 0.5)]
    [InlineData(15.0, 0.5)]
    [InlineData(19.0, 0.25)]
    [InlineData(20.0, 0.0)]
    public void Fades_are_computed_from_timeline_position(double at, double expected)
    {
        var cue = CompileOne(new AudioEvent { File = _wav, Start = S(10), Length = S(10), Volume = 0.5, FadeIn = S(1), FadeOut = S(2) });

        Assert.Equal(expected, cue.VolumeAt(T(at)), precision: 6);
    }

    [Fact]
    public void File_offset_follows_the_timeline_and_wraps_when_looping()
    {
        var once = CompileOne(new AudioEvent { File = _wav, Start = S(10), Offset = S(1) });
        var looped = CompileOne(new AudioEvent { File = _wav, Start = S(10), Offset = S(1), Loop = true });

        Assert.Equal(T(3.5), once.FileOffsetAt(T(12.5), fileLength: T(4)));
        Assert.Equal(T(1.5), looped.FileOffsetAt(T(13.5), fileLength: T(4)));   // 3.5 s played of a 3 s loop -> 0.5 s in
    }

    [Fact]
    public void Due_from_start_until_end()
    {
        var cue = CompileOne(new AudioEvent { File = _wav, Start = S(1), Length = S(2) });

        Assert.Equal([false, true, true, false], new[] { 0.9, 1.0, 2.9, 3.0 }.Select(t => cue.IsDueAt(T(t))));
    }
}

public class AudioPathsTests
{
    [Fact]
    public void Files_inside_the_show_folder_are_stored_relative_and_others_absolute()
    {
        string show = Path.Combine(Path.GetTempPath(), "haunt");

        Assert.Equal("audio/thunder.wav", AudioPaths.ToReference(Path.Combine(show, "audio", "thunder.wav"), show));
        Assert.Equal(Path.GetFullPath("/elsewhere/scream.mp3"), AudioPaths.ToReference("/elsewhere/scream.mp3", show));
        Assert.Equal(Path.GetFullPath("/elsewhere/scream.mp3"), AudioPaths.ToReference("/elsewhere/scream.mp3", showFolder: null));
    }

    [Fact]
    public void Saving_to_a_new_folder_rebases_references_without_changing_which_file_is_meant()
    {
        string oldShow = Path.Combine(Path.GetTempPath(), "old"), newShow = Path.Combine(Path.GetTempPath(), "new");
        var timeline = new ShowTimeline { Tracks = [new TimelineTrack { Events = [new AudioEvent { File = "audio/a.wav" }, new AudioEvent { File = Path.Combine(newShow, "b.wav") }] }] };

        AudioPaths.Rebase([timeline], oldShow, newShow);

        var files = timeline.Tracks[0].Events.Cast<AudioEvent>().Select(e => e.File).ToList();
        Assert.Equal(Path.Combine(oldShow, "audio", "a.wav"), files[0]);   // outside the new folder: absolute
        Assert.Equal("b.wav", files[1]);                                    // inside it: relative
    }
}
