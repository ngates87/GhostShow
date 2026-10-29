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
    readonly TimelineEditorViewModel? _editor;

    /// <param name="resize">Changes a clip's length the way dragging its right edge does (a lighting clip scales or crops).</param>
    /// <param name="editor">For effect clips' settings, new pattern and converting (null: those do nothing).</param>
    public EventPropertiesViewModel(TimelineEvent e, ShowSession session, EditHistory history, Func<Task<string?>> pickAudioFile,
                                    PlaybackViewModel playback, Action<Guid, TimeSpan> resize, TimelineEditorViewModel? editor = null)
    {
        _resize = resize;
        _editor = editor;
        Event = e;
        _session = session;
        _history = history;
        _pickAudioFile = pickAudioFile;
        _playback = playback;
    }

    public TimelineEvent Event { get; }

    public bool IsAudio => Event is AudioEvent;
    public bool IsLightingClip => Event is LightingClip;

    public string Kind => Event switch
    {
        LightingClip { Effect: not null } clip => $"{clip.KindName} effect",
        LightingClip clip => clip.KindName,
        TimelineCommandEvent => "Event",
        _ => "Audio clip",
    };

    /// <summary>A Level Control clip (handles of its own), not an effect.</summary>
    public bool IsLevelControl => Event is LightingClip { Effect: null };

    /// <summary>A lighting clip's length; changing it scales or crops its envelopes, as dragging its edge does.</summary>
    public decimal ClipLengthSeconds
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
        set => Change("Rename clip", Event.Name, string.IsNullOrWhiteSpace(value) ? null : value, v => Event.Name = v, "name");
    }

    public decimal StartSeconds
    {
        get => Seconds(Event.Start);
        set
        {
            if (!Event.Locked) Change("Move clip", Event.Start, Time(value), v => Event.Start = v, "start");
        }
    }

    /// <summary>VenueMagic's "Enable playback": a disabled clip is skipped (the next one carries on from the one before).</summary>
    public bool Enabled
    {
        get => Event.Enabled;
        set => Change(value ? "Enable clip" : "Disable clip", Event.Enabled, value, v => Event.Enabled = v, "enabled");
    }

    /// <summary>Locked: can't be moved, resized, split, deleted or have its envelopes edited.</summary>
    public bool Locked
    {
        get => Event.Locked;
        set => Change(value ? "Lock clip" : "Unlock clip", Event.Locked, value, v => Event.Locked = v, "locked");
    }

    public bool IsUnlocked => !Event.Locked;

    /// <summary>On: resizing scales the envelopes with the clip. Off: it crops them (handles keep their times).</summary>
    public bool ScaleOnResize
    {
        get => Event is LightingClip { ScaleOnResize: true };
        set
        {
            if (Event is LightingClip clip) Change(value ? "Scale envelopes on resize" : "Crop envelopes on resize", clip.ScaleOnResize, value, v => clip.ScaleOnResize = v, "scale");
        }
    }

    // ---- event track: run or stop a timeline ----

    TimelineCommandEvent? Command => Event as TimelineCommandEvent;

    public bool IsCommand => Command is not null;

    public static IReadOnlyList<string> Commands { get; } = ["Run timeline", "Stop timeline"];

    /// <summary>0: run, 1: stop.</summary>
    public int CommandIndex
    {
        get => Command?.Command == TimelineCommand.Stop ? 1 : 0;
        set
        {
            if (Command is { } e) Change(value == 1 ? "Make it Stop timeline" : "Make it Run timeline", e.Command, value == 1 ? TimelineCommand.Stop : TimelineCommand.Run, v => e.Command = v, "command");
            OnPropertyChanged(nameof(IsRun));
        }
    }

    public bool IsRun => Command?.Command == TimelineCommand.Run;

    /// <summary>Every timeline in the show, by its place in the tree.</summary>
    public IReadOnlyList<TimelineChoice> Timelines =>
        _session.Project.Timelines.OrderBy(t => t.TreePath, StringComparer.OrdinalIgnoreCase).Select(t => new TimelineChoice(t.Id, t.TreePath)).ToList();

    /// <summary>The timeline it runs or stops; null if none is chosen (or it was deleted).</summary>
    public TimelineChoice? Target
    {
        get => Command is { } e ? Timelines.FirstOrDefault(t => t.Id == e.TimelineId) : null;
        set
        {
            if (Command is { } e && value is not null) Change("Change the event's timeline", e.TimelineId, value.Id, v => e.TimelineId = v, "target");
        }
    }

    public bool PlayOnceOnly
    {
        get => Command?.PlayOnceOnly == true;
        set
        {
            if (Command is { } e) Change(value ? "Play once only" : "Play every time", e.PlayOnceOnly, value, v => e.PlayOnceOnly = v, "once");
        }
    }

    public static IReadOnlyList<EventSymbol> Symbols { get; } = Enum.GetValues<EventSymbol>();

    /// <summary>The marker it's drawn with on its lane.</summary>
    public EventSymbol Symbol
    {
        get => Command?.Symbol ?? EventSymbol.Diamond;
        set
        {
            if (Command is { } e) Change($"Mark it with a {value.ToString().ToLowerInvariant()}", e.Symbol, value, v => e.Symbol = v, "symbol");
        }
    }

    // ---- built-in effect ----

    LightingClip? EffectClip => Event as LightingClip is { Effect: not null } clip ? clip : null;
    LightingEffect? Effect => EffectClip?.Effect;
    EffectInfo? Info => Effect is { } effect ? EffectInfo.Of(effect.Kind) : null;

    public bool IsEffect => Effect is not null;

    public static IReadOnlyList<EffectInfo> EffectKinds => EffectInfo.All;

    /// <summary>Changing the kind keeps the strength and pattern, and takes the new kind's own rate and colour.</summary>
    public EffectInfo? SelectedEffect
    {
        get => Info;
        set
        {
            if (value is null || value.Kind == Effect?.Kind) return;
            ChangeEffect($"Make it {value.Name}", e => { e.Kind = value.Kind; e.Rate = value.DefaultRate; e.Color = null; }, "kind");
        }
    }

    public string EffectDescription => Info?.Description ?? "";
    public string StrengthLabel => $"{Info?.StrengthLabel ?? "Strength"} (%)";

    public decimal StrengthPercent
    {
        get => (decimal)Math.Round((Effect?.Strength ?? 0) * 100);
        set => ChangeEffect($"Change {StrengthLabel.Replace(" (%)", "").ToLowerInvariant()}", e => e.Strength = (double)Math.Clamp(value, 0, 100) / 100, "strength");
    }

    public bool HasRate => Info?.RateLabel is not null;
    public string RateLabel => Info?.RateLabel ?? "";
    public decimal RateMinimum => (decimal)(Info?.MinRate ?? 0);
    public decimal RateMaximum => (decimal)(Info?.MaxRate ?? 1);

    public decimal Rate
    {
        get => (decimal)Math.Round(Effect?.Rate ?? 0, 2);
        set => ChangeEffect($"Change {RateLabel.ToLowerInvariant()}", e => e.Rate = (double)value, "rate");
    }

    public bool HasColor => Info?.DefaultColor is not null;

    /// <summary>"#RRGGBB"; the kind's own colour when none is set. A value that isn't a colour is ignored.</summary>
    public string EffectColor
    {
        get => Effect?.Color ?? Info?.DefaultColor ?? "";
        set
        {
            if (value is not { Length: 7 } || !Avalonia.Media.Color.TryParse(value, out _)) return;
            string? color = string.Equals(value, Info?.DefaultColor, StringComparison.OrdinalIgnoreCase) ? null : value.ToUpperInvariant();
            ChangeEffect("Change effect colour", e => e.Color = color, "color");
        }
    }

    /// <summary>Quick picks for an effect's colour.</summary>
    public static IReadOnlyList<string> Swatches { get; } = ["#DCE6FF", "#FFFFFF", "#FFD08A", "#FF0000", "#FF5A00", "#FFC400", "#20FF40", "#2050FF", "#9B30FF"];

    [RelayCommand]
    void PickColor(string color) => EffectColor = color;

    /// <summary>On: the lights are left out when it ends; off: back where they were.</summary>
    public bool EndsDark
    {
        get => Effect?.Ending == EffectEnding.Dark;
        set => ChangeEffect(value ? "End dark" : "End back where it was", e => e.Ending = value ? EffectEnding.Dark : EffectEnding.BackToBefore, "ending");
    }

    [RelayCommand]
    void NewPattern()
    {
        if (EffectClip is { } clip) _editor?.NewEffectPattern(clip);
    }

    [RelayCommand]
    void ConvertToLevelControl()
    {
        if (EffectClip is { } clip) _editor?.ConvertToLevelControl(clip);
    }

    void ChangeEffect(string description, Action<LightingEffect> change, string field)
    {
        if (EffectClip is { } clip) _editor?.ChangeEffect(clip, description, change, field);
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
