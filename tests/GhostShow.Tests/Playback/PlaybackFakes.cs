using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Playback;

/// <summary>A clock tests move by hand, so timing tests are exact and never sleep.</summary>
sealed class ManualClock : IPlaybackClock
{
    public long Now { get; private set; }
    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds).Ticks;
}

/// <summary>Records what the player asked of audio, and lets tests make instances finish or fail.</summary>
sealed class FakeAudioOutput : IAudioOutput
{
    public readonly List<string> Calls = [];
    public readonly Dictionary<Guid, AudioInstanceState> States = [];
    public readonly Dictionary<Guid, TimeSpan> Offsets = [];
    public readonly Dictionary<Guid, double> Volumes = [];
    public readonly Dictionary<string, TimeSpan> Lengths = [];
    public bool FailStarts;
    public bool ThrowOnStart;

    public void Start(AudioCue cue, TimeSpan fileOffset, double volume)
    {
        if (ThrowOnStart) throw new InvalidOperationException("audio output exploded");
        Calls.Add($"start {cue.Name} @{fileOffset.TotalSeconds:0.###}");
        States[cue.EventId] = FailStarts ? AudioInstanceState.Failed : AudioInstanceState.Playing;
        Offsets[cue.EventId] = fileOffset;
        Volumes[cue.EventId] = volume;
    }

    public void Seek(Guid eventId, TimeSpan fileOffset) { Calls.Add($"seek @{fileOffset.TotalSeconds:0.###}"); Offsets[eventId] = fileOffset; }
    public void Pause(Guid eventId) { Calls.Add("pause"); States[eventId] = AudioInstanceState.Paused; }
    public void Resume(Guid eventId) { Calls.Add("resume"); States[eventId] = AudioInstanceState.Playing; }
    public void SetVolume(Guid eventId, double volume) => Volumes[eventId] = volume;
    public void Stop(Guid eventId) { Calls.Add("stop"); States.Remove(eventId); }
    public void StopAll() { Calls.Add("stop all"); States.Clear(); }
    public AudioInstanceState GetState(Guid eventId) => States.GetValueOrDefault(eventId);
    public TimeSpan? GetLength(string path) => Lengths.TryGetValue(path, out var length) ? length : null;

    /// <summary>Tests bump this to simulate the device being reopened (every instance lost).</summary>
    public int Generation { get; set; }

    /// <summary>The calls that start, stop, seek, pause or resume: what changes what's heard.</summary>
    public IEnumerable<string> Transport => Calls;
}
