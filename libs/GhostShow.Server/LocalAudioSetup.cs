using GhostShow.Audio;
using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.Server;

/// <summary>
/// The show computer's sound, for the Audio screen (here and on devices, see <see cref="IAudioSetup"/>): the default
/// output and master volume (its settings), how its outputs stand, and the show's sound files. Called on the show's thread.
/// </summary>
public sealed class LocalAudioSetup : IAudioSetup
{
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    readonly AudioService _audio;
    readonly ShowSession _session;
    readonly MachineSettings _settings;
    readonly Func<Task> _saveSettings;
    readonly Debouncer _save;
    readonly HashSet<object> _watching = [];

    public LocalAudioSetup(AudioService audio, ShowSession session, MachineSettings settings, Func<Task> saveSettings, IShowThread thread)
    {
        _audio = audio;
        _session = session;
        _settings = settings;
        _saveSettings = saveSettings;
        // Volume drags change it many times a second: save once it settles.
        _save = new Debouncer(thread, SaveDelay, () => _ = _saveSettings());
        audio.StatusChanged += () => thread.Post(Refresh);
        State = AudioSetupState.Empty with { Device = settings.Audio.DeviceName, MasterVolume = settings.Audio.MasterVolume };
    }

    public AudioSetupState State { get; private set; }

    public event Action? Changed;

    /// <summary>A device's Audio screen opened or closed: while one is, the state is kept up to date for it.</summary>
    public void SetWatching(object screen, bool watching)
    {
        if (watching) _watching.Add(screen);
        else _watching.Remove(screen);
        if (watching) Refresh();
    }

    /// <summary>A device shows the Audio screen (the show computer's own refreshes it while it's on screen).</summary>
    public bool IsWatched => _watching.Count > 0;

    public async Task<string?> ChooseDeviceAsync(string? device)
    {
        if (device != _settings.Audio.DeviceName)
        {
            _settings.Audio = _settings.Audio with { DeviceName = device };
            _ = _saveSettings();
        }
        Refresh();
        bool opened = await _audio.OpenAsync(device);
        Refresh();
        return opened ? null : $"Couldn't open {device ?? "the system default"}; it will be retried every few seconds.";
    }

    public void SetVolume(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        _audio.MasterVolume = volume;
        if (volume == _settings.Audio.MasterVolume) return;
        _settings.Audio = _settings.Audio with { MasterVolume = volume };
        _save.Poke();
        Refresh();
    }

    public string? TestSound(string? device) =>
        _audio.PlayTestSound(device) ? null : device is null ? "No audio device is open." : $"{device} isn't connected.";

    /// <summary>When the screen opens, then once a second while it's on screen here or on a device.</summary>
    public void Refresh()
    {
        var status = _audio.Status;
        var state = new AudioSetupState(_settings.Audio.DeviceName, _settings.Audio.MasterVolume,
            status.State switch
            {
                AudioDeviceState.Open => $"Open: {status.DeviceName} · {status.Playing} playing",
                AudioDeviceState.Failed => "Not available (retrying every few seconds)",
                _ => "Closed",
            },
            status.State != AudioDeviceState.Open, status.LastError, RoutedOutputs(status), Sounds());
        if (state.SameAs(State)) return;
        State = state;
        Changed?.Invoke();
    }

    List<RoutedOutputState> RoutedOutputs(AudioStatus status)
    {
        var tracks = _session.Project.Timelines
            .SelectMany(t => t.Tracks.Where(track => track.Kind == TrackKind.Audio && !string.IsNullOrWhiteSpace(track.AudioDevice))
                                     .Select(track => (Device: track.AudioDevice!, Track: $"{t.Name}: {track.Name}")))
            .ToLookup(x => x.Device, x => x.Track);
        return [.. tracks.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).Select(g =>
        {
            var output = status.Outputs.FirstOrDefault(o => o.Name == g.Key);
            bool open = output?.Open ?? g.Key == status.DeviceName;   // routed to the default output's own device
            return new RoutedOutputState(g.Key, string.Join(", ", g), open
                ? $"Connected · {output?.Playing ?? 0} playing"
                : "Not connected: plays on the default output", !open);
        })];
    }

    /// <summary>Every audio event in every timeline, grouped by file.</summary>
    List<ShowSoundRow> Sounds() =>
    [
        .. _session.Project.Timelines
            .SelectMany(t => t.Tracks.SelectMany(track => track.Events.OfType<AudioEvent>()).Select(e => (Timeline: t.Name, Event: e)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Event.File))
            .GroupBy(x => x.Event.File)
            .Select(g =>
            {
                string path = AudioPaths.Resolve(g.Key, _session.ShowFolder);
                bool exists = File.Exists(path);
                var length = exists ? _audio.GetLength(path) : null;
                string used = string.Join(", ", g.Select(x => x.Timeline).Distinct()) + (g.Count() > 1 ? $" ({g.Count()} events)" : "");
                return new ShowSoundRow(g.Key, used, length is { } l ? PlaybackText.Format(l) : exists ? "…" : "",
                                        exists ? "Found" : $"Missing: {path}", !exists);
            }),
    ];
}
