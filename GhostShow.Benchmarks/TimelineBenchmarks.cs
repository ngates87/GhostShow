using BenchmarkDotNet.Attributes;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.Benchmarks;

/// <summary>A show for the timeline benchmarks: 100 RGBW fixtures and lighting events spread over 10 minutes.</summary>
static class BenchmarkShow
{
    public static (Project Project, PatchResult Patch) Create(int eventCount)
    {
        var fixtures = Enumerable.Range(0, 100)
            .Select(i => new FixtureInstance { Name = $"Par {i}", ProfileId = "bench/rgbw", ModeName = "5ch", StartAddress = 1 + i * 5 })
            .ToList();
        var random = new Random(42);
        ChannelFunction[] controls = [ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue, ChannelFunction.White];
        // Four lighting tracks; each event sets 1-5 controls of one fixture with a 0-5 s fade.
        var tracks = Enumerable.Range(0, 4).Select(i => new TimelineTrack { Name = $"Lighting {i + 1}", Kind = TrackKind.Lighting }).ToList();
        for (int i = 0; i < eventCount; i++)
        {
            tracks[i % tracks.Count].Events.Add(new LevelsEvent
            {
                FixtureId = fixtures[random.Next(fixtures.Count)].Id,
                Start = TimeSpan.FromSeconds(random.NextDouble() * 600),
                Fade = TimeSpan.FromSeconds(random.Next(6)),
                Levels = controls.Take(random.Next(1, 6)).Select(c => new Level((byte)random.Next(256), Control: c)).ToList(),
            });
        }
        var timeline = new ShowTimeline { Name = "Bench", Tracks = tracks };
        var project = new Project { Name = "Bench", Fixtures = fixtures, Timelines = [timeline] };
        return (project, BenchmarkProfiles.PatchOrThrow(fixtures));
    }

    public static TimelineContext Context(Project project, PatchResult patch) => new(
        patch.Fixtures.ToDictionary(f => f.Id),
        project.Fixtures.Select(f => f.Id).ToHashSet(),
        [1],
        path => path,
        _ => null);
}

/// <summary>
/// The playback thread's work per update (100 times a second), and recompiling, which happens on every edit.
/// Evaluating must not allocate and must stay far below the 10 ms update interval even for big shows.
/// </summary>
[MemoryDiagnoser]
public class TimelineBenchmarks
{
    CompiledTimeline _compiled = null!;
    LevelsFrame _frame = null!;
    ShowEngine _engine = null!;
    ShowTimeline _timeline = null!;
    TimelineContext _context = null!;
    long _position;
    static readonly long Step = TimeSpan.FromMilliseconds(10).Ticks;

    [Params(100, 10_000)]
    public int EventCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var (project, patch) = BenchmarkShow.Create(EventCount);
        _timeline = project.Timelines[0];
        _context = BenchmarkShow.Context(project, patch);
        _compiled = TimelineCompiler.Compile(_timeline, _context);
        if (_compiled.Issues.Count > 0) throw new InvalidOperationException($"Benchmark timeline is invalid: {_compiled.Issues[0]}");
        _frame = _compiled.CreateFrame();
        _engine = new ShowEngine([1], patch.Fixtures);
    }

    /// <summary>Advances like playback does, wrapping at the end, so every part of the show is evaluated.</summary>
    long NextPosition() => _position = (_position + Step) % _compiled.Length;

    [Benchmark(Description = "Evaluate levels at the next position (per playback update)")]
    public void Evaluate() => _compiled.EvaluateLevels(NextPosition(), _frame);

    [Benchmark(Description = "Evaluate + hand to the engine (per playback update)")]
    public void EvaluateAndApply()
    {
        _compiled.EvaluateLevels(NextPosition(), _frame);
        _engine.SetTimelineLevels(_frame);
    }

    [Benchmark(Description = "Compile the timeline (on every edit)")]
    public CompiledTimeline Compile() => TimelineCompiler.Compile(_timeline, _context);
}

/// <summary>Saving and opening a large show: the user waits for these, so they should take well under a second.</summary>
[MemoryDiagnoser]
public class ShowFileBenchmarks
{
    Project _project = null!;
    string _saved = null!;
    string _scratch = null!;

    [Params(10_000)]
    public int EventCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _project = BenchmarkShow.Create(EventCount).Project;
        _saved = Path.Combine(Path.GetTempPath(), $"ghostshow-bench-{Guid.NewGuid():N}");
        _scratch = _saved + "-save";
        ShowFolderStore.SaveAsync(_project, _saved, null).GetAwaiter().GetResult();
        long bytes = Directory.EnumerateFiles(_saved, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"// Show folder with {EventCount} events: {bytes / 1024.0 / 1024.0:0.0} MB of JSON");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var folder in new[] { _saved, _scratch })
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>Everything written (a new folder), the worst case: saving back to the same folder writes only changed timelines.</summary>
    [Benchmark(Description = "Save show folder (serialize + atomic writes)")]
    public Task Save() => ShowFolderStore.SaveAsync(_project, _scratch, null);

    [Benchmark(Description = "Open show folder (read + parse + migrate)")]
    public Task<ProjectLoadResult> Load() => ShowFolderStore.LoadAsync(_saved);
}
