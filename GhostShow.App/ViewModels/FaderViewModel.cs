using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;

namespace GhostShow.App.ViewModels;

/// <summary>
/// One slider driving one DMX channel through an engine command. The UI sets <see cref="Value"/>; the screen's
/// refresh tick calls <see cref="ShowValue"/> with what the engine currently holds, so changes made elsewhere
/// (another screen, later OSC or a timeline) appear here too. Both run on the UI thread, so a drag never fights
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
            var result = _send(clamped);
            if (!result.Succeeded) _reportError(result.Error!);
        }
    }

    public string Percent => $"{_value * 100 / 255}%";

    public void ShowValue(byte engineValue)
    {
        if (engineValue == _value) return;
        _value = engineValue;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Percent));
    }

    [RelayCommand]
    void Zero() => Value = 0;

    [RelayCommand]
    void Full() => Value = 255;
}
