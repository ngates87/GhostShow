namespace GhostShow.Core.Playback;

/// <summary>Playback in words, the same on every screen and in Diagnostics.</summary>
public static class PlaybackText
{
    /// <summary>"01:23.4", or "1:02:03.4" from an hour up.</summary>
    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss\.f") : time.ToString(@"mm\:ss\.f");

    /// <summary>"Playing", "Playing (looping)", "Paused", "Stopped", or "Ended" once it has reached its end.</summary>
    public static string StateLabel(PlaybackStatus status) => status switch
    {
        { AtEnd: true } => "Ended",
        { State: TransportState.Playing, Loop: true } => "Playing (looping)",
        { State: var state } => state.ToString(),
    };
}
