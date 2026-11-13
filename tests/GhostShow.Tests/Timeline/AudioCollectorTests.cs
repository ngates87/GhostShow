using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Timelines;

/// <summary>Copying a show's sounds from elsewhere into its folder, so it's self-contained.</summary>
public class AudioCollectorTests : IDisposable
{
    readonly TempDirectory _show = new(), _elsewhere = new();

    public void Dispose()
    {
        _show.Dispose();
        _elsewhere.Dispose();
    }

    static ShowTimeline With(params string[] files) => new()
    {
        Name = "T",
        Tracks = [new TimelineTrack { Kind = TrackKind.Audio, Events = [.. files.Select(f => new AudioEvent { File = f })] }],
    };

    [Fact]
    public async Task Sounds_from_elsewhere_are_copied_into_the_audio_folder_and_the_clips_point_at_the_copies()
    {
        string outside = _elsewhere.File("thunder.wav");
        File.WriteAllBytes(outside, [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(_show.Path, "audio"));
        File.WriteAllBytes(Path.Combine(_show.Path, "audio", "inside.wav"), [9]);
        var timelines = new[] { With(outside, "audio/inside.wav"), With(outside) };

        var plan = AudioCollector.Plan(timelines, _show.Path);
        Assert.Equal([("thunder.wav", "audio/thunder.wav")], plan.Select(p => (Path.GetFileName(p.Source), p.Reference)));

        await AudioCollector.CopyAsync(plan, _show.Path, cancellationToken: TestContext.Current.CancellationToken);
        var changed = AudioCollector.Repoint(timelines, plan, _show.Path);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_show.Path, "audio", "thunder.wav")));
        Assert.Equal(2, changed.Count);
        Assert.Equal(["audio/thunder.wav", "audio/inside.wav", "audio/thunder.wav"],
                     timelines.SelectMany(t => t.Tracks[0].Events).Cast<AudioEvent>().Select(e => e.File));
        Assert.Empty(AudioCollector.Plan(timelines, _show.Path));   // nothing left to copy
        Assert.True(File.Exists(outside));                          // the original stays where it was
    }

    [Fact]
    public void A_copy_already_there_is_reused_and_another_file_of_the_same_name_gets_a_number()
    {
        Directory.CreateDirectory(Path.Combine(_show.Path, "audio"));
        File.WriteAllBytes(Path.Combine(_show.Path, "audio", "scream.wav"), [1, 2]);
        string same = _elsewhere.File("scream.wav");
        File.WriteAllBytes(same, [1, 2]);
        Directory.CreateDirectory(Path.Combine(_elsewhere.Path, "other"));
        string different = Path.Combine(_elsewhere.Path, "other", "scream.wav");
        File.WriteAllBytes(different, [1, 2, 3, 4]);

        var plan = AudioCollector.Plan([With(same, different)], _show.Path);

        Assert.Equal(["audio/scream.wav", "audio/scream (2).wav"], plan.Select(p => p.Reference));
    }

    [Fact]
    public void Missing_files_are_left_for_the_compiler_to_report()
    {
        Assert.Empty(AudioCollector.Plan([With(_elsewhere.File("gone.wav"))], _show.Path));
    }
}
