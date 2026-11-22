using GhostShow.Audio;
using GhostShow.Core.Timeline;

namespace GhostShow.Server;

/// <summary>
/// The show computer's sound output devices, by name: what audio tracks can play on, here and on every device.
/// Refreshed when a screen showing them opens, so a USB interface plugged in since shows up.
/// </summary>
public sealed class AudioOutputs(AudioService audio)
{
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Raised when <see cref="Names"/> changes, on the thread <see cref="RefreshAsync"/> was called on (the show's).</summary>
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        var names = await audio.ListDevicesAsync();
        if (names.SequenceEqual(Names)) return;
        Names = names;
        Changed?.Invoke();
    }

    /// <summary>A short tone on an output (null: the default output). False if that device isn't open.</summary>
    public bool PlayTestSound(string? device, AudioSide side = AudioSide.Stereo) => audio.PlayTestSound(device, side);
}
