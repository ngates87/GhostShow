using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Timelines;

/// <summary>Builds small timelines and compiles them against a test patch, in seconds for readability.</summary>
static class TimelineTestKit
{
    public static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
    public static long T(double seconds) => TimeSpan.FromSeconds(seconds).Ticks;

    public static LevelsEvent Raw(double start, int address, byte to, double fade = 0, byte? from = null, int universe = 1) => new()
    {
        Universe = universe, Start = S(start), Fade = S(fade), Levels = [new Level(to, Channel: address, From: from)],
    };

    public static TimelineTrack Lights(params TimelineEvent[] events) => new() { Name = "Lights", Kind = TrackKind.Lighting, Events = [.. events] };

    public static ShowTimeline Timeline(params TimelineTrack[] tracks) => new() { Tracks = [.. tracks] };

    public static TimelineContext Context(IEnumerable<PatchedFixture>? fixtures = null, IEnumerable<Guid>? allFixtureIds = null,
                                          string? showFolder = null, Func<string, TimeSpan?>? audioLength = null)
    {
        var patched = (fixtures ?? []).ToDictionary(f => f.Id);
        return new TimelineContext(patched, (allFixtureIds ?? patched.Keys).ToHashSet(), [1, 2],
                                   reference => AudioPaths.Resolve(reference, showFolder), audioLength ?? (_ => null));
    }

    public static CompiledTimeline Compile(ShowTimeline timeline, TimelineContext? context = null) =>
        TimelineCompiler.Compile(timeline, context ?? Context());

    /// <summary>Universe 1's levels at a moment, and which channels the timeline controls.</summary>
    public static (byte[] Values, bool[] Controlled) LevelsAt(CompiledTimeline compiled, double seconds, int universe = 1)
    {
        var frame = compiled.CreateFrame();
        compiled.EvaluateLevels(T(seconds), frame);
        int slot = compiled.Universes.ToList().IndexOf(universe);
        return slot < 0 ? (new byte[512], new bool[512]) : (frame.Values[slot], frame.Controlled[slot]);
    }
}
