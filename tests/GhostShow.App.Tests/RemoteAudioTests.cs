using GhostShow.App.ViewModels;
using GhostShow.Audio;
using GhostShow.Core.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.App.Tests;

/// <summary>
/// The Audio screen on whatever runs it: the sound is the show computer's (<see cref="IAudioSetup"/>), so what it shows
/// comes from there, and choosing an output, the volume and test tones ask it.
/// </summary>
public class RemoteAudioTests : IDisposable
{
    /// <summary>The show computer's sound, as the screen hears of it: records what it was asked.</summary>
    sealed class ShowComputer : IAudioSetup
    {
        public readonly List<string> Calls = [];
        public AudioSetupState State { get; private set; } = AudioSetupState.Empty;
        public event Action? Changed;

        public void Says(AudioSetupState state)
        {
            State = state;
            Changed?.Invoke();
        }

        public Task<string?> ChooseDeviceAsync(string? device)
        {
            Calls.Add($"device {device}");
            return Task.FromResult<string?>(null);
        }

        public void SetVolume(double volume) => Calls.Add($"volume {volume}");

        public string? TestSound(string? device)
        {
            Calls.Add($"test {device}");
            return device == "Gone" ? "Gone isn't connected." : null;
        }
    }

    readonly ShowComputer _showComputer = new();
    readonly AudioService _audio = new(NullLogger.Instance, useNullDevice: true);

    public void Dispose() => _audio.Dispose();

    AudioViewModel Screen() => new(_showComputer, LocalAudioDevices.For(_audio));

    [Fact]
    public void The_screen_shows_the_show_computer_s_sound_without_sending_it_back()
    {
        var screen = Screen();
        _showComputer.Says(new AudioSetupState("USB Interface", 0.75, "Open: USB Interface · 1 playing", false, null,
            [new RoutedOutputState("Headset", "Main: Whisper", "Not connected: plays on the default output", true)],
            [new ShowSoundRow("thunder.wav", "Main", "0:04.0", "Found", false)]));

        Assert.Equal(("Open: USB Interface · 1 playing", 75.0), (screen.StatusLabel, screen.VolumePercent));
        Assert.Equal("USB Interface", screen.SelectedDevice?.Name);   // chosen there, though this list hasn't been read yet
        Assert.Equal("Headset", screen.RoutedOutputs.Single().Device);
        Assert.Equal("thunder.wav", screen.Sounds.Single().File);
        Assert.Empty(_showComputer.Calls);
    }

    [Fact]
    public void Choosing_the_volume_an_output_or_a_test_tone_asks_the_show_computer()
    {
        var screen = Screen();
        _showComputer.Says(AudioSetupState.Empty with { RoutedOutputs = [new RoutedOutputState("Gone", "Main: Music", "Not connected", true)] });

        screen.VolumePercent = 40;
        screen.SelectedDevice = screen.Devices[0] with { Label = "Speakers", Name = "Speakers" };
        screen.TestSoundCommand.Execute(null);
        Assert.Equal("Playing a test tone (A440, half a second).", screen.Message);
        screen.RoutedOutputs.Single().TestCommand.Execute(null);
        Assert.Equal("Gone isn't connected.", screen.Message);

        Assert.Equal(["volume 0.4", "device Speakers", "test ", "test Gone"], _showComputer.Calls);
    }
}
