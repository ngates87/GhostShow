using GhostShow.Audio;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>An output device to choose; Name null means "whatever the system's default is".</summary>
public sealed record AudioDeviceChoice(string Label, string? Name)
{
    public override string ToString() => Label;
}

/// <summary>
/// The output devices this computer has, shared by the Audio screen and the output pickers on audio tracks, and
/// refreshed when either is opened (so a USB interface plugged in since shows up). The browser build has its own, which
/// asks the show computer.
/// </summary>
public sealed class AudioDeviceList(AudioService audio)
{
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Raised on the UI thread when <see cref="Names"/> changes.</summary>
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        var names = await audio.ListDevicesAsync();
        if (names.SequenceEqual(Names)) return;
        Names = names;
        Changed?.Invoke();
    }

    /// <summary>A short tone on a track's output (null: the default output). False if that device isn't open.</summary>
    public bool PlayTestSound(string? device, AudioSide side = AudioSide.Stereo) => audio.PlayTestSound(device, side);

    /// <summary>
    /// The choices for an audio track's output: the default output, every device, and the track's own device marked
    /// "not connected" if this computer doesn't have it (it stays chosen, and is used when it's plugged in).
    /// </summary>
    public static IReadOnlyList<AudioDeviceChoice> TrackChoices(IReadOnlyList<string> names, string? current)
    {
        var choices = new List<AudioDeviceChoice> { new("Default output", null) };
        choices.AddRange(names.Select(n => new AudioDeviceChoice(n, n)));
        if (current is not null && !names.Contains(current)) choices.Add(new AudioDeviceChoice($"{current} (not connected)", current));
        return choices;
    }
}
