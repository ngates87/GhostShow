using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Playback;

namespace GhostShow.App.ViewModels;

/// <summary>A device audio tracks are routed to, the tracks that use it, and whether it's connected.</summary>
public sealed partial class RoutedOutputRow(AudioViewModel screen, RoutedOutputState state)
{
    public RoutedOutputState State => state;
    public string Device => state.Device;
    public string UsedBy => state.UsedBy;
    public string Status => state.Status;
    public bool Missing => state.Missing;

    [RelayCommand]
    void Test() => screen.TestOutput(Device);
}

/// <summary>
/// The Audio screen: the show computer's default output device and master volume (its settings, not the show's, like
/// DMX adapters), the other outputs the show's tracks are routed to and whether they're connected, test sounds, and the
/// sound files the show uses. The sound is the show computer's (<see cref="IAudioSetup"/>), on its screen and a
/// device's alike.
/// </summary>
public sealed partial class AudioViewModel : ObservableObject
{
    readonly IAudioSetup _setup;
    readonly AudioDeviceList _deviceList;
    bool _updating;

    public AudioViewModel(IAudioSetup setup, AudioDeviceList deviceList)
    {
        _setup = setup;
        _deviceList = deviceList;
        setup.Changed += ShowSetup;
        deviceList.Changed += ShowDevices;
        ShowDevices();
        ShowSetup();
    }

    public ObservableCollection<AudioDeviceChoice> Devices { get; } = new();

    public ObservableCollection<ShowSoundRow> Sounds { get; } = new();

    /// <summary>The devices the show's audio tracks are routed to (other than the default output).</summary>
    public ObservableCollection<RoutedOutputRow> RoutedOutputs { get; } = new();

    [ObservableProperty]
    public partial AudioDeviceChoice? SelectedDevice { get; set; }

    /// <summary>Master volume for all show audio, 0-100 %.</summary>
    [ObservableProperty]
    public partial double VolumePercent { get; set; }

    [ObservableProperty]
    public partial string StatusLabel { get; private set; } = "Opening…";

    [ObservableProperty]
    public partial bool IsFaulted { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    [ObservableProperty]
    public partial string? Message { get; private set; }

    partial void OnSelectedDeviceChanged(AudioDeviceChoice? value)
    {
        if (_updating || value is null || value.Name == _setup.State.Device) return;
        Message = $"Switching to {value.Label}…";
        _ = SwitchDeviceAsync(value);
    }

    async Task SwitchDeviceAsync(AudioDeviceChoice choice) => Message = await _setup.ChooseDeviceAsync(choice.Name);

    partial void OnVolumePercentChanged(double value)
    {
        if (!_updating) _setup.SetVolume(Math.Clamp(value, 0, 100) / 100);
    }

    /// <summary>Lists the output devices again (e.g. after plugging in a USB interface).</summary>
    [RelayCommand]
    Task RefreshDevices() => _deviceList.RefreshAsync();

    /// <summary>
    /// The choices: the system's default, every device, and the chosen one marked "not connected" if it isn't plugged in
    /// (it stays chosen, and is used when it comes back).
    /// </summary>
    void ShowDevices()
    {
        string? chosen = _setup.State.Device;
        var names = _deviceList.Names;
        var choices = new List<AudioDeviceChoice> { new("System default", null) };
        choices.AddRange(names.Select(n => new AudioDeviceChoice(n, n)));
        if (chosen is not null && !names.Contains(chosen))
            choices.Add(new AudioDeviceChoice(names.Count == 0 ? chosen : $"{chosen} (not connected)", chosen));
        _updating = true;
        try
        {
            if (!choices.SequenceEqual(Devices))
            {
                Devices.Clear();
                foreach (var choice in choices) Devices.Add(choice);
            }
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == chosen) ?? Devices[0];
        }
        finally
        {
            _updating = false;
        }
    }

    [RelayCommand]
    void TestSound() => Message = _setup.TestSound(null) ?? "Playing a test tone (A440, half a second).";

    internal void TestOutput(string device) => Message = _setup.TestSound(device) ?? $"Playing a test tone on {device}.";

    /// <summary>How the show computer's sound stands now.</summary>
    void ShowSetup()
    {
        var state = _setup.State;
        StatusLabel = state.Status.Length > 0 ? state.Status : "Opening…";
        IsFaulted = state.IsFaulted;
        LastError = state.LastError;
        _updating = true;
        try
        {
            if (Math.Abs(VolumePercent / 100 - state.MasterVolume) > 0.001) VolumePercent = Math.Round(state.MasterVolume * 100);
        }
        finally
        {
            _updating = false;
        }
        if (SelectedDevice?.Name != state.Device) ShowDevices();   // chosen elsewhere (the show computer, or another device)
        if (!state.RoutedOutputs.SequenceEqual(RoutedOutputs.Select(r => r.State)))
        {
            RoutedOutputs.Clear();
            foreach (var output in state.RoutedOutputs) RoutedOutputs.Add(new RoutedOutputRow(this, output));
        }
        if (!state.Sounds.SequenceEqual(Sounds))
        {
            Sounds.Clear();
            foreach (var sound in state.Sounds) Sounds.Add(sound);
        }
    }
}
