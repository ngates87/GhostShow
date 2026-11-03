using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Dmx;
using GhostShow.Core.Model;

namespace GhostShow.App.ViewModels;

/// <summary>
/// One universe: its name and number (part of the show) and the device it goes out on (part of the show computer's
/// settings), plus the live status of its output.
/// </summary>
public sealed partial class UniverseRowViewModel : ObservableObject
{
    /// <summary>Makes an edit of the show: its description, the change, and a key that merges quick edits of one field into one undo step.</summary>
    public delegate void EditUniverse(string description, Action change, object mergeKey);

    readonly EditUniverse _edit;
    readonly Action<UniverseRowViewModel, string> _devicePicked;
    bool _showingState;

    public UniverseRowViewModel(UniverseConfig model, EditUniverse edit, Action<UniverseRowViewModel, string> devicePicked)
    {
        Model = model;
        _edit = edit;
        _devicePicked = devicePicked;
    }

    public UniverseConfig Model { get; }

    public ObservableCollection<string> Choices { get; } = new();

    public decimal Number
    {
        get => Model.Number;
        set
        {
            if ((int)value == Model.Number) return;
            _edit("Renumber universe", () => Model.Number = (int)value, (Model, nameof(Number)));
        }
    }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (value == Model.Name) return;
            _edit("Rename universe", () => Model.Name = value, (Model, nameof(Name)));
        }
    }

    /// <summary>The show changed (an edit here, an undo, another device): its number and name follow it.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Number));
        OnPropertyChanged(nameof(Name));
    }

    [ObservableProperty]
    public partial string? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFaulted { get; set; }

    partial void OnSelectedDeviceChanged(string? value)
    {
        // The ComboBox clears its selection while Choices is rebuilt; that isn't the user picking something.
        if (_showingState || value is null) return;
        _devicePicked(this, value);
    }

    /// <summary>Shows what the show computer says about this universe's output (null: nothing yet).</summary>
    public void Show(UniverseOutput? output)
    {
        if (output is null) return;
        _showingState = true;
        try
        {
            Status = output.Status;
            IsFaulted = output.IsFaulted;
            if (!Choices.SequenceEqual(output.Choices))
            {
                Choices.Clear();
                foreach (var choice in output.Choices) Choices.Add(choice);
            }
            SelectedDevice = output.Selected;
        }
        finally { _showingState = false; }
    }
}
