using GhostShow.Core.Timeline;

namespace GhostShow.Core.Playback;

/// <summary>
/// What playback needs to know about sound files: how long they are and what they look like. Measuring a file takes
/// time, so neither call blocks: the first ask starts measuring in the background and returns null; ask again later.
/// (The desktop's AudioService implements it; tests use a fake.)
/// </summary>
public interface IAudioFiles
{
    /// <summary>The file's length, once measured; null until then.</summary>
    TimeSpan? GetLength(string path);

    /// <summary>Whether the file is still being read or opened (slow on a cloud or network drive), for "loading…" on screen.</summary>
    bool IsLoading(string path) => false;

    /// <summary>The file's waveform, for drawing on the timeline, once measured; null until then.</summary>
    Waveform? GetWaveform(string path);
}
