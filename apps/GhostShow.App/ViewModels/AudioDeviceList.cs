using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>An output device to choose; Name null means "whatever the system's default is".</summary>
public sealed record AudioDeviceChoice(string Label, string? Name)
{
    public override string ToString() => Label;
}

/// <summary>
/// The show computer's sound output devices, shared by the Audio screen and the output pickers on audio tracks. On the
/// show computer they're its own (<see cref="LocalAudioDevices"/>), refreshed when either screen opens (so a USB
/// interface plugged in since shows up); over the connection the show computer sends them when they change.
/// </summary>
/// <param name="testSound">Plays a short tone on a device (null: the default output); false if that device isn't open.</param>
/// <param name="refresh">Looks again for devices; null where the show computer says when they change.</param>
public sealed class AudioDeviceList(Func<string?, AudioSide, bool> testSound, Func<Task>? refresh = null)
{
    public IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Raised on the UI thread when <see cref="Names"/> changes.</summary>
    public event Action? Changed;

    public Task RefreshAsync() => refresh?.Invoke() ?? Task.CompletedTask;

    /// <summary>A short tone on a track's output (null: the default output), on the show computer. False if that device isn't open.</summary>
    public bool PlayTestSound(string? device, AudioSide side = AudioSide.Stereo) => testSound(device, side);

    /// <summary>The devices there are now (on the UI thread).</summary>
    public void Update(IReadOnlyList<string> names)
    {
        if (names.SequenceEqual(Names)) return;
        Names = [.. names];
        Changed?.Invoke();
    }

    /// <summary>
    /// The choices for an audio track's output: the default output, every device, and the track's own device marked
    /// "not connected" if the show computer doesn't have it (it stays chosen, and is used when it's plugged in).
    /// </summary>
    public static IReadOnlyList<AudioDeviceChoice> TrackChoices(IReadOnlyList<string> names, string? current)
    {
        var choices = new List<AudioDeviceChoice> { new("Default output", null) };
        choices.AddRange(names.Select(n => new AudioDeviceChoice(n, n)));
        if (current is not null && !names.Contains(current)) choices.Add(new AudioDeviceChoice($"{current} (not connected)", current));
        return choices;
    }
}
