using GhostShow.Audio;
using GhostShow.Server;

namespace GhostShow.App.ViewModels;

/// <summary>The show computer's own sound devices (the server's <see cref="AudioOutputs"/>) for its screens. Not in the browser build.</summary>
public static class LocalAudioDevices
{
    public static AudioDeviceList For(AudioOutputs outputs)
    {
        var list = new AudioDeviceList(outputs.PlayTestSound, outputs.RefreshAsync);
        outputs.Changed += () => list.Update(outputs.Names);
        list.Update(outputs.Names);
        return list;
    }

    public static AudioDeviceList For(AudioService audio) => For(new AudioOutputs(audio));
}
