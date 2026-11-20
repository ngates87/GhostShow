using System.Text.Json.Nodes;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;

namespace GhostShow.Browser.Remote;

/// <summary>
/// The show computer's sound, from a device's Audio screen: its state arrives as "audioSetup" messages while the screen
/// is open (<see cref="SetWatching"/>), and choosing the output, the volume and test tones are asked of the show computer.
/// </summary>
sealed class RemoteAudioSetup(RemoteLink link) : IAudioSetup
{
    bool _watching;

    public AudioSetupState State { get; private set; } = AudioSetupState.Empty;

    public event Action? Changed;

    /// <summary>The Audio screen opened or closed here: the show computer keeps it up to date while it's open.</summary>
    public void SetWatching(bool watching)
    {
        if (watching == _watching) return;
        _watching = watching;
        link.Send("watchAudio", ("on", watching));
    }

    /// <summary>After each hello (a new connection): watching again, if the screen is open.</summary>
    public void OnHello()
    {
        if (_watching) link.Send("watchAudio", ("on", true));
    }

    public void OnAudio(JsonObject message)
    {
        if (message["state"] is not { } node || ShowJson.FromNode<AudioSetupState>(node) is not { } state) return;
        if (state.SameAs(State)) return;
        State = state;
        Changed?.Invoke();
    }

    public Task<string?> ChooseDeviceAsync(string? device) => link.RequestAsync("chooseAudioDevice", ("device", device));

    public void SetVolume(double volume) => link.Send("setVolume", ("volume", volume));

    public string? TestSound(string? device)
    {
        link.Send("testSound", ("device", device));
        return null;
    }
}
