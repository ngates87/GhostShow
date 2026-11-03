using GhostShow.Core.Timeline;

namespace GhostShow.Core.Playback;

/// <summary>
/// The audio output for a second (third, …) copy of a timeline playing at once: the same device, but each sound gets
/// an ID of its own (its event's ID mixed with the copy's), so two copies of a scare play two overlapping screams
/// instead of restarting one.
/// </summary>
sealed class InstanceAudioOutput(IAudioOutput inner, Guid instance) : IAudioOutput
{
    readonly byte[] _salt = instance.ToByteArray();

    Guid Map(Guid eventId)
    {
        Span<byte> bytes = stackalloc byte[16];
        eventId.TryWriteBytes(bytes);
        for (int i = 0; i < 16; i++) bytes[i] ^= _salt[i];
        return new Guid(bytes);
    }

    public void Start(AudioCue cue, TimeSpan fileOffset, double volume) => inner.Start(cue with { EventId = Map(cue.EventId) }, fileOffset, volume);
    public void Seek(Guid eventId, TimeSpan fileOffset) => inner.Seek(Map(eventId), fileOffset);
    public void Pause(Guid eventId) => inner.Pause(Map(eventId));
    public void Resume(Guid eventId) => inner.Resume(Map(eventId));
    public void SetVolume(Guid eventId, double volume) => inner.SetVolume(Map(eventId), volume);
    public void Stop(Guid eventId) => inner.Stop(Map(eventId));

    /// <summary>Does nothing: one copy must never silence the others (the player stops its own sounds one by one).</summary>
    public void StopAll() { }

    public AudioInstanceState GetState(Guid eventId) => inner.GetState(Map(eventId));
    public TimeSpan? GetLength(string path) => inner.GetLength(path);
    public int Generation => inner.Generation;
}
