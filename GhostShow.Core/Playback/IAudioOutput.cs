using GhostShow.Core.Timeline;

namespace GhostShow.Core.Playback;

public enum AudioInstanceState
{
    /// <summary>No instance for this event.</summary>
    None,
    Playing,
    Paused,
    /// <summary>The file played to its end on its own.</summary>
    Finished,
    /// <summary>It couldn't be started (missing or unreadable file, no device). Already reported by the output.</summary>
    Failed,
}

/// <summary>
/// What the player needs from audio: one playing instance per audio event, controlled by event ID. Implemented by the
/// SoundFlow-based service in GhostShow.Audio; tests use a fake.
///
/// Every method is called from the playback thread and must return quickly: no waiting on files or devices. Failures
/// are reported by the output itself (logs, status) and show up here as <see cref="AudioInstanceState.Failed"/>;
/// they never throw into the player.
/// </summary>
public interface IAudioOutput
{
    /// <summary>Starts the cue's file at the offset, or restarts it if the event already has an instance.</summary>
    void Start(AudioCue cue, TimeSpan fileOffset, double volume);

    void Seek(Guid eventId, TimeSpan fileOffset);
    void Pause(Guid eventId);
    void Resume(Guid eventId);

    /// <summary>0-1, before the master volume.</summary>
    void SetVolume(Guid eventId, double volume);

    void Stop(Guid eventId);
    void StopAll();

    AudioInstanceState GetState(Guid eventId);

    /// <summary>The length of a file, if it has been measured; null if not (yet). Never blocks to measure it.</summary>
    TimeSpan? GetLength(string path);

    /// <summary>
    /// Goes up whenever the output device is (re)opened, e.g. after a lost device comes back or the user picks
    /// another. Every instance is gone at that point; the player starts what should be playing again.
    /// </summary>
    int Generation { get; }
}

/// <summary>For running without audio (tests, the CLI's --silent): nothing plays, nothing fails.</summary>
public sealed class SilentAudioOutput : IAudioOutput
{
    public void Start(AudioCue cue, TimeSpan fileOffset, double volume) { }
    public void Seek(Guid eventId, TimeSpan fileOffset) { }
    public void Pause(Guid eventId) { }
    public void Resume(Guid eventId) { }
    public void SetVolume(Guid eventId, double volume) { }
    public void Stop(Guid eventId) { }
    public void StopAll() { }
    public AudioInstanceState GetState(Guid eventId) => AudioInstanceState.None;
    public TimeSpan? GetLength(string path) => null;
    public int Generation => 0;
}
