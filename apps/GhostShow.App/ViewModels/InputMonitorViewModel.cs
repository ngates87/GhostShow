using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Inputs → Input monitor: every input event, newest first, and what it did, for finding out what a key or sensor sends
/// and why a button didn't fire. It keeps listening on every screen, so a sensor can be tripped in the room and looked at
/// after. It can show one device's only (each input's monitor button). Only the latest events are kept, and none are saved.
/// </summary>
public sealed partial class InputMonitorViewModel : ObservableObject
{
    public const int Capacity = 200;

    readonly ButtonsViewModel _buttons;
    readonly List<InputMonitorRow> _all = [];   // newest first, whatever is shown

    public InputMonitorViewModel(ButtonsViewModel buttons)
    {
        _buttons = buttons;
        buttons.InputReceived += Add;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>The events shown, newest first.</summary>
    public ObservableCollection<InputMonitorRow> Rows { get; } = new();

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The one device whose events are shown (<see cref="InputMonitorRow.DeviceKey"/>); null shows every input's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered), nameof(FilterLabel), nameof(EmptyLabel))]
    public partial string? ShowingOnly { get; private set; }

    public bool IsFiltered => ShowingOnly is not null;

    /// <summary>"Showing Ethereal FX only".</summary>
    public string FilterLabel => ShowingOnly is null ? "" : $"Showing {_filterName} only";

    public string EmptyLabel => ShowingOnly is null
        ? "Nothing yet: press a button's key, or send a serial command, and it shows here."
        : $"Nothing from {_filterName} yet.";

    string _filterName = "";

    /// <summary>Shows only this device's events (or every input's, if it's the one already shown).</summary>
    public void ShowOnly(string deviceKey, string name)
    {
        _filterName = name;
        ShowingOnly = ShowingOnly == deviceKey ? null : deviceKey;
        Rebuild();
    }

    [RelayCommand]
    void ShowAll()
    {
        ShowingOnly = null;
        Rebuild();
    }

    void Rebuild()
    {
        Rows.Clear();
        foreach (var row in _all.Where(Shows)) Rows.Add(row);
    }

    bool Shows(InputMonitorRow row) => ShowingOnly is null || row.DeviceKey == ShowingOnly;

    void Add(InputEvent input, string outcome)
    {
        var row = new InputMonitorRow(input, _buttons.InputName(input.Input), outcome);
        _all.Insert(0, row);
        if (_all.Count > Capacity)
        {
            var dropped = _all[^1];
            _all.RemoveAt(_all.Count - 1);
            Rows.Remove(dropped);
        }
        if (Shows(row)) Rows.Insert(0, row);
    }

    [RelayCommand]
    void Clear()
    {
        _all.Clear();
        Rows.Clear();
    }
}

/// <summary>One event in the input monitor.</summary>
/// <param name="source">The show's name for the device it came from: "Ethereal FX", "Keyboard".</param>
public sealed class InputMonitorRow(InputEvent input, string source, string outcome)
{
    /// <summary>Which device's monitor shows it: the device's id, or "keyboard".</summary>
    public static string KeyOf(Guid? device) => device?.ToString() ?? InputTrigger.Keyboard;

    public InputEvent Input { get; } = input;
    public string DeviceKey => KeyOf(Input.Input);
    public string Time => Input.Time.ToString("HH:mm:ss.ff");
    public string Source { get; } = source;

    /// <summary>"F5", "BTN24", "BTN24=On": a command as it was sent, with its value.</summary>
    public string Address => Input.Source == InputTrigger.Keyboard ? TriggerKeys.Label(Input.Address) : InputText.Visible(Input.Command);

    /// <summary>Keys are pressed and released; a serial command just arrives.</summary>
    public string Change => Input.Source == InputTrigger.Serial ? "received" : Input.IsPressed ? "pressed" : "released";

    /// <summary>"Pressed Scare 1", "Ran Hallway", or why nothing happened: "Nothing uses it", "Scare 1: re-arms in 2.0 s".</summary>
    public string Outcome { get; } = outcome;

    /// <summary>It set something off: pressed a button or ran a timeline.</summary>
    public bool PressedAButton => Outcome.StartsWith("Pressed ", StringComparison.Ordinal) || Outcome.StartsWith("Ran ", StringComparison.Ordinal);
}
