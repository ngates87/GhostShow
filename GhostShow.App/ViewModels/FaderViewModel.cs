using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;

namespace GhostShow.App.ViewModels;

/// <summary>
/// One slider driving one DMX channel through an engine command. The UI sets <see cref="Value"/>; the screen's
/// refresh tick calls <see cref="ShowValue"/> with what the engine currently outputs, so changes made elsewhere
/// (another screen, the playing timeline) appear here too. Both run on the UI thread, so a drag never fights
/// the refresh: the engine already holds the value the slider just sent.
/// </summary>
public sealed partial class FaderViewModel : ObservableObject
{
    readonly Func<int, CommandResult> _send;
    readonly Action<string> _reportError;
    int _value;

    public FaderViewModel(int address, string label, string? caption, Func<int, CommandResult> send, Action<string> reportError)
    {
        Address = address;
        Label = label;
        Caption = caption;
        _send = send;
        _reportError = reportError;
    }

    public int Address { get; }

    /// <summary>Main label, e.g. "Red" or "37".</summary>
    public string Label { get; }

    /// <summary>Secondary line, e.g. the DMX address or the channel's function.</summary>
    public string? Caption { get; }

    public double Value
    {
        get => _value;
        set
        {
            int clamped = (int)Math.Round(Math.Clamp(value, 0, 255));
            if (clamped == _value) return;
            _value = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Percent));
            OnPropertyChanged(nameof(Level));
            OnPropertyChanged(nameof(DmxValue));
            var result = _send(clamped);
            if (!result.Succeeded) _reportError(result.Error!);
        }
    }

    public string Percent => $"{_value * 100 / 255}%";

    /// <summary>The level the way a console shows it: "FL" at full, otherwise the percentage (0-99).</summary>
    public string Level => _value == 255 ? "FL" : (_value * 100 / 255).ToString();

    /// <summary>The raw DMX value, for the smaller line under the level.</summary>
    public string DmxValue => $"DMX {_value}";

    /// <summary>
    /// The playing timeline controls this channel: the fader shows the timeline's level, and moving it changes the
    /// manual level underneath, which takes effect when playback stops.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineTip))]
    public partial bool IsTimelineControlled { get; private set; }

    public string? TimelineTip => IsTimelineControlled
        ? "The playing timeline controls this channel. Moving the fader changes the manual level, which applies when playback stops."
        : null;

    public void ShowValue(byte engineValue, bool timelineControlled = false)
    {
        IsTimelineControlled = timelineControlled;
        if (engineValue == _value) return;
        _value = engineValue;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(DmxValue));
    }

    [RelayCommand]
    void Zero() => Value = 0;

    [RelayCommand]
    void Full() => Value = 255;
}
