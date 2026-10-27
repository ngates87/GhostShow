using System.Diagnostics;

namespace GhostShow.Core.Playback;

/// <summary>
/// A monotonic time source, in TimeSpan ticks. The player derives the show position from it, so position never drifts
/// with late or missed updates. Tests substitute a clock they advance by hand.
/// </summary>
public interface IPlaybackClock
{
    long Now { get; }
}

/// <summary>Stopwatch time: monotonic, high resolution, unaffected by wall-clock changes.</summary>
public sealed class StopwatchClock : IPlaybackClock
{
    readonly long _origin = Stopwatch.GetTimestamp();

    public long Now => Stopwatch.GetElapsedTime(_origin).Ticks;
}
