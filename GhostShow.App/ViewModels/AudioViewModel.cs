using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Audio;
using GhostShow.Core.Engine;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>An output device to choose; Name null means "whatever the system's default is".</summary>
public sealed record AudioDeviceChoice(string Label, string? Name)
{
    public override string ToString() => Label;
}

/// <summary>A sound file the show uses, and whether this computer can play it.</summary>
public sealed record ShowSoundRow(string File, string Used, string Length, string Status, bool Missing);

/// <summary>
/// The Audio screen: which output device and master volume this computer uses (machine settings, not the show, like
/// DMX adapters), the device's status, a test sound, and the sound files the show uses.
/// </summary>
public sealed partial class AudioViewModel : ObservableObject
{
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    readonly AudioService _audio;
    readonly ShowSession _session;
    readonly MachineSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly DispatcherTimer _saveTimer;
    bool _updating;

    public AudioViewModel(AudioService audio, ShowSession session, MachineSettings settings, Func<Task> saveSettings)
    {
        _audio = audio;
        _session = session;
        _settings = settings;
        _saveSettings = saveSettings;
        // Volume drags change it many times a second: save once it settles.
        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = _saveSettings();
        };
        audio.StatusChanged += () => Dispatcher.UIThread.Post(Tick);
        _updating = true;
        Devices.Add(new AudioDeviceChoice("System default", null));
        if (settings.Audio.DeviceName is { } saved) Devices.Add(new AudioDeviceChoice(saved, saved));
        SelectedDevice = Devices[^1];
        VolumePercent = Math.Round(settings.Audio.MasterVolume * 100);
        _updating = false;
    }

    public ObservableCollection<AudioDeviceChoice> Devices { get; } = new();

    public ObservableCollection<ShowSoundRow> Sounds { get; } = new();

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
        if (_updating || value is null || value.Name == _settings.Audio.DeviceName) return;
        _settings.Audio = _settings.Audio with { DeviceName = value.Name };
        _ = _saveSettings();
        Message = $"Switching to {value.Label}…";
        _ = SwitchDeviceAsync(value);
    }

    async Task SwitchDeviceAsync(AudioDeviceChoice choice)
    {
        bool opened = await _audio.OpenAsync(choice.Name);
        Message = opened ? null : $"Couldn't open {choice.Label}; it will be retried every few seconds.";
        Tick();
    }

    partial void OnVolumePercentChanged(double value)
    {
        double volume = Math.Clamp(value, 0, 100) / 100;
        _audio.MasterVolume = volume;
        if (_updating || volume == _settings.Audio.MasterVolume) return;
        _settings.Audio = _settings.Audio with { MasterVolume = volume };
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Lists the output devices again (e.g. after plugging in a USB interface).</summary>
    [RelayCommand]
    async Task RefreshDevices()
    {
        var names = await _audio.ListDevicesAsync();
        _updating = true;
        try
        {
            string? selected = SelectedDevice?.Name;
            Devices.Clear();
            Devices.Add(new AudioDeviceChoice("System default", null));
            foreach (var name in names) Devices.Add(new AudioDeviceChoice(name, name));
            // A saved device that isn't plugged in stays chosen (and is used when it comes back), marked as missing.
            if (selected is not null && !names.Contains(selected)) Devices.Add(new AudioDeviceChoice($"{selected} (not connected)", selected));
            SelectedDevice = Devices.FirstOrDefault(d => d.Name == selected) ?? Devices[0];
        }
        finally
        {
            _updating = false;
        }
    }

    [RelayCommand]
    void TestSound() => Message = _audio.PlayTestSound() ? "Playing a test tone (A440, half a second)." : "No audio device is open.";

    /// <summary>Called when the screen opens and then once a second while it's visible.</summary>
    public void Tick()
    {
        var status = _audio.Status;
        IsFaulted = status.State != AudioDeviceState.Open;
        StatusLabel = status.State switch
        {
            AudioDeviceState.Open => $"Open: {status.DeviceName} · {status.Playing} playing",
            AudioDeviceState.Failed => "Not available (retrying every few seconds)",
            _ => "Closed",
        };
        LastError = status.LastError;
        RefreshSounds();
    }

    /// <summary>Every audio event in every timeline, grouped by file.</summary>
    void RefreshSounds()
    {
        var rows = _session.Project.Timelines
            .SelectMany(t => t.Tracks.SelectMany(track => track.Events.OfType<AudioEvent>()).Select(e => (Timeline: t.Name, Event: e)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Event.File))
            .GroupBy(x => x.Event.File)
            .Select(g =>
            {
                string path = AudioPaths.Resolve(g.Key, _session.ShowFolder);
                bool exists = File.Exists(path);
                var length = exists ? _audio.GetLength(path) : null;
                string used = string.Join(", ", g.Select(x => x.Timeline).Distinct()) + (g.Count() > 1 ? $" ({g.Count()} events)" : "");
                return new ShowSoundRow(g.Key, used, length is { } l ? PlaybackViewModel.Format(l) : exists ? "…" : "",
                                        exists ? "Found" : $"Missing: {path}", !exists);
            })
            .ToList();
        if (rows.SequenceEqual(Sounds)) return;
        Sounds.Clear();
        foreach (var row in rows) Sounds.Add(row);
    }
}
