namespace GhostShow.Core.Playback;

/// <summary>A device audio tracks are routed to, the tracks that use it, and whether it's connected.</summary>
/// <param name="UsedBy">"Main: Thunder, Lobby: Music".</param>
/// <param name="Status">"Connected · 1 playing", "Not connected: plays on the default output".</param>
public sealed record RoutedOutputState(string Device, string UsedBy, string Status, bool Missing);

/// <summary>A sound file the show uses, and whether the show computer can play it.</summary>
/// <param name="Used">The timelines it's in: "Main, Lobby (3 events)".</param>
/// <param name="Length">"0:12.5", "…" while it's measured, or "" if it's missing.</param>
public sealed record ShowSoundRow(string File, string Used, string Length, string Status, bool Missing);

/// <summary>Everything the Audio screen shows about the show computer's sound.</summary>
/// <param name="Device">The default output chosen in its settings (null: the system's default).</param>
/// <param name="MasterVolume">0 to 1.</param>
/// <param name="Status">"Open: Speakers · 2 playing", "Not available (retrying every few seconds)".</param>
public sealed record AudioSetupState(string? Device, double MasterVolume, string Status, bool IsFaulted, string? LastError,
                                     IReadOnlyList<RoutedOutputState> RoutedOutputs, IReadOnlyList<ShowSoundRow> Sounds)
{
    public static readonly AudioSetupState Empty = new(null, 1, "", false, null, [], []);

    /// <summary>The same on screen? (A record's own comparison would only compare the lists by reference.)</summary>
    public bool SameAs(AudioSetupState other) =>
        this with { RoutedOutputs = other.RoutedOutputs, Sounds = other.Sounds } == other
        && RoutedOutputs.SequenceEqual(other.RoutedOutputs) && Sounds.SequenceEqual(other.Sounds);
}

/// <summary>
/// The show computer's sound, for the Audio screen: its default output and master volume (its settings, not the show's),
/// how its outputs stand, and the show's sound files there. The sound card is only on the show computer
/// (<c>LocalAudioSetup</c>); a device's screen asks over the connection (<c>RemoteAudioSetup</c> in GhostShow.Browser).
/// </summary>
public interface IAudioSetup
{
    AudioSetupState State { get; }

    /// <summary><see cref="State"/> changed. On the show's thread on the show computer; on the UI thread on a device.</summary>
    event Action? Changed;

    /// <summary>Makes this device the default output (null: the system's default) and opens it. Returns why it couldn't, or null.</summary>
    Task<string?> ChooseDeviceAsync(string? device);

    /// <summary>The master volume for all show audio, 0 to 1: at once, and saved once it settles.</summary>
    void SetVolume(double volume);

    /// <summary>A short tone on an output (null: the default output). Returns why it couldn't, or null.</summary>
    string? TestSound(string? device);
}
