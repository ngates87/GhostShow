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

/// <summary>
/// The output devices this computer has, shared by the Audio screen and the output pickers on audio tracks, and
/// refreshed when either is opened (so a USB interface plugged in since shows up).
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
    public bool PlayTestSound(string? device) => audio.PlayTestSound(device);

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

/// <summary>A device audio tracks are routed to, the tracks that use it, and whether it's connected.</summary>
public sealed partial class RoutedOutputRow(AudioViewModel screen, string device, string usedBy, string status, bool missing)
{
    public string Device { get; } = device;
    public string UsedBy { get; } = usedBy;
    public string Status { get; } = status;
    public bool Missing { get; } = missing;

    [RelayCommand]
    void Test() => screen.TestOutput(Device);
}

/// <summary>A sound file the show uses, and whether this computer can play it.</summary>
public sealed record ShowSoundRow(string File, string Used, string Length, string Status, bool Missing);

/// <summary>
/// The Audio screen: the default output device and master volume of this computer (machine settings, not the show,
/// like DMX adapters), the other outputs the show's tracks are routed to and whether they're connected, test sounds,
/// and the sound files the show uses.
/// </summary>
public sealed partial class AudioViewModel : ObservableObject
{
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    readonly AudioService _audio;
    readonly AudioDeviceList _deviceList;
    readonly ShowSession _session;
    readonly MachineSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly DispatcherTimer _saveTimer;
    bool _updating;

    public AudioViewModel(AudioService audio, AudioDeviceList deviceList, ShowSession session, MachineSettings settings, Func<Task> saveSettings)
    {
        _audio = audio;
        _deviceList = deviceList;
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
        await _deviceList.RefreshAsync();
        var names = _deviceList.Names;
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
        RefreshRoutedOutputs(status);
        RefreshSounds();
    }

    void RefreshRoutedOutputs(AudioStatus status)
    {
        var tracks = _session.Project.Timelines
            .SelectMany(t => t.Tracks.Where(track => track.Kind == TrackKind.Audio && !string.IsNullOrWhiteSpace(track.AudioDevice))
                                     .Select(track => (Device: track.AudioDevice!, Track: $"{t.Name}: {track.Name}")))
            .ToLookup(x => x.Device, x => x.Track);
        var rows = tracks.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g =>
        {
            var output = status.Outputs.FirstOrDefault(o => o.Name == g.Key);
            bool open = output?.Open ?? g.Key == status.DeviceName;   // routed to the default output's own device
            return new RoutedOutputRow(this, g.Key, string.Join(", ", g), open
                ? $"Connected · {output?.Playing ?? 0} playing"
                : "Not connected: plays on the default output", !open);
        }).ToList();
        if (rows.Select(r => (r.Device, r.UsedBy, r.Status)).SequenceEqual(RoutedOutputs.Select(r => (r.Device, r.UsedBy, r.Status)))) return;
        RoutedOutputs.Clear();
        foreach (var row in rows) RoutedOutputs.Add(row);
    }

    internal void TestOutput(string device) =>
        Message = _audio.PlayTestSound(device) ? $"Playing a test tone on {device}." : $"{device} isn't connected.";

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
