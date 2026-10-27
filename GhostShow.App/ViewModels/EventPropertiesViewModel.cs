using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Edits one selected event with exact numbers, so dragging is never the only way to place something. Every change is
/// an undoable edit; typing into one field merges into a single undo step.
/// </summary>
public sealed partial class EventPropertiesViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly EditHistory _history;
    readonly Func<Task<string?>> _pickAudioFile;
    readonly PlaybackViewModel _playback;
    readonly Action<Guid, TimeSpan> _resize;

    /// <param name="resize">Changes a block's length the way dragging its right edge does (a lighting block stretches).</param>
    public EventPropertiesViewModel(TimelineEvent e, ShowSession session, EditHistory history, Func<Task<string?>> pickAudioFile,
                                    PlaybackViewModel playback, Action<Guid, TimeSpan> resize)
    {
        _resize = resize;
        Event = e;
        _session = session;
        _history = history;
        _pickAudioFile = pickAudioFile;
        _playback = playback;
    }

    public TimelineEvent Event { get; }

    public bool IsAudio => Event is AudioEvent;
    public bool IsMarker => Event is MarkerEvent;
    public bool IsLightingBlock => Event is LightingClip;

    public string Kind => Event switch { LightingClip => "Lighting block", AudioEvent => "Audio event", _ => "Marker" };

    /// <summary>A lighting block's length; changing it stretches the block's lines, as dragging its edge does.</summary>
    public decimal BlockLengthSeconds
    {
        get => Event is LightingClip clip ? Seconds(clip.Length) : 0;
        set
        {
            if (Event is LightingClip clip && value > 0) _resize(clip.Id, Time(value));
        }
    }

    // ---- common ----

    public string? Name
    {
        get => Event.Name;
        set => Change("Rename event", Event.Name, string.IsNullOrWhiteSpace(value) ? null : value, v => Event.Name = v, "name");
    }

    public decimal StartSeconds
    {
        get => Seconds(Event.Start);
        set => Change("Move event", Event.Start, Time(value), v => Event.Start = v, "start");
    }

    public bool Enabled
    {
        get => Event.Enabled;
        set => Change(value ? "Enable event" : "Disable event", Event.Enabled, value, v => Event.Enabled = v, "enabled");
    }

    // ---- audio ----

    AudioEvent Audio => (AudioEvent)Event;

    public string File => IsAudio ? Audio.File : "";

    public string FileLengthLabel => FileLength() is { } length ? $"File length {PlaybackViewModel.Format(length)}" : "File length not known yet";

    TimeSpan? FileLength() => IsAudio && !string.IsNullOrWhiteSpace(Audio.File)
        ? _playback.AudioLength(AudioPaths.Resolve(Audio.File, _session.ShowFolder))
        : null;

    /// <summary>Checked: play only Length; unchecked: to the end of the file.</summary>
    public bool HasLength
    {
        get => IsAudio && Audio.Length is not null;
        set => Change("Change audio length", Audio.Length, value ? Audio.Length ?? Time(LengthSeconds) : null, v => Audio.Length = v, "length");
    }

    /// <summary>Shows the file's playing time while no length is set; only editable (and only an edit) once one is.</summary>
    public decimal LengthSeconds
    {
        get => IsAudio ? Seconds(Audio.Length ?? FileLength() - Audio.Offset ?? TimeSpan.FromSeconds(5)) : 0;
        set
        {
            if (!HasLength) return;
            Change("Change audio length", Audio.Length, (TimeSpan?)Time(Math.Max(0.01m, value)), v => Audio.Length = v, "length");
        }
    }

    public decimal OffsetSeconds
    {
        get => IsAudio ? Seconds(Audio.Offset) : 0;
        set => Change("Change start offset", Audio.Offset, Time(value), v => Audio.Offset = v, "offset");
    }

    public decimal VolumePercent
    {
        get => IsAudio ? (decimal)Math.Round(Audio.Volume * 100) : 100;
        set => Change("Change volume", Audio.Volume, (double)Math.Clamp(value, 0, 100) / 100, v => Audio.Volume = v, "volume");
    }

    public decimal FadeInSeconds
    {
        get => IsAudio ? Seconds(Audio.FadeIn) : 0;
        set => Change("Change fade-in", Audio.FadeIn, Time(value), v => Audio.FadeIn = v, "fadeIn");
    }

    public decimal FadeOutSeconds
    {
        get => IsAudio ? Seconds(Audio.FadeOut) : 0;
        set => Change("Change fade-out", Audio.FadeOut, Time(value), v => Audio.FadeOut = v, "fadeOut");
    }

    public bool AudioLoop
    {
        get => IsAudio && Audio.Loop;
        set => Change(value ? "Loop audio" : "Stop looping audio", Audio.Loop, value, v => Audio.Loop = v, "loop");
    }

    [RelayCommand]
    async Task ChooseFile()
    {
        if (!IsAudio || await _pickAudioFile() is not { } path) return;
        var reference = AudioPaths.ToReference(path, _session.ShowFolder);
        Change("Change audio file", Audio.File, reference, v => Audio.File = v, "file");
    }

    // ---- helpers ----

    /// <summary>Called after any edit (including undo/redo) so every field shows the model's current values.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    void Change<T>(string description, T current, T value, Action<T> set, string field)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        _history.Execute(description, () => set(value), () => set(current), (Event.Id, field));
    }

    static decimal Seconds(TimeSpan time) => Math.Round((decimal)time.TotalSeconds, 3);
    static TimeSpan Time(decimal seconds) => TimeSpan.FromSeconds((double)Math.Max(0, seconds));
}
