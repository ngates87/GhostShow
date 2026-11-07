using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Inputs → Monitor: every input event, newest first, and what it did, for finding out what a key or sensor sends and why
/// a button didn't fire. It keeps listening on every screen, so a sensor can be tripped in the room and looked at after.
/// Only the latest events are kept, and none are saved.
/// </summary>
public sealed partial class InputMonitorViewModel : ObservableObject
{
    public const int Capacity = 200;

    public InputMonitorViewModel(ButtonsViewModel buttons)
    {
        buttons.InputReceived += Add;
        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Newest first.</summary>
    public ObservableCollection<InputMonitorRow> Rows { get; } = new();

    public bool IsEmpty => Rows.Count == 0;

    void Add(InputEvent input, string outcome)
    {
        Rows.Insert(0, new InputMonitorRow(input, outcome));
        if (Rows.Count > Capacity) Rows.RemoveAt(Rows.Count - 1);
    }

    [RelayCommand]
    void Clear() => Rows.Clear();
}

/// <summary>One event in the input monitor.</summary>
public sealed class InputMonitorRow(InputEvent input, string outcome)
{
    public InputEvent Input { get; } = input;
    public string Time => Input.Time.ToString("HH:mm:ss.ff");
    public string Source => Input.Source switch
    {
        ButtonTrigger.Keyboard => "Keyboard",
        ButtonTrigger.Serial => "Serial",
        var other => other,
    };

    public string Address => Input.Source == ButtonTrigger.Keyboard ? TriggerKeys.Label(Input.Address) : InputText.Visible(Input.Address);

    /// <summary>Keys are pressed and released; a serial command just arrives.</summary>
    public string Change => Input.Source == ButtonTrigger.Serial ? "received" : Input.IsPressed ? "pressed" : "released";

    /// <summary>"Pressed Scare 1", or why nothing happened: "No button uses it", "Scare 1: re-arms in 2.0 s".</summary>
    public string Outcome { get; } = outcome;

    public bool PressedAButton => Outcome.StartsWith("Pressed ", StringComparison.Ordinal);
}
