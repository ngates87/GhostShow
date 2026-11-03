using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Step 2 of adding fixtures: one fixture type has been chosen, and every click on an opening adds another of it
/// there, named "Prefix 1", "Prefix 2", ... until Done. "Add N in a row" places a batch back to back in the first gap
/// big enough. Undo removes the most recent addition.
/// </summary>
public sealed partial class FixturePlacementViewModel : ObservableObject
{
    readonly Func<FixtureProfile, FixtureMode, string, int, int, FixtureEditorViewModel> _add;
    readonly Action<FixtureEditorViewModel> _remove;
    readonly Func<int, IReadOnlyList<AddressRange>> _freeRanges;

    /// <param name="add">Adds a fixture (profile, mode, name, universe, start address) to the show and returns it.</param>
    /// <param name="remove">Removes a fixture from the show (for Undo).</param>
    /// <param name="freeRanges">The free address ranges of a universe right now.</param>
    public FixturePlacementViewModel(Func<FixtureProfile, FixtureMode, string, int, int, FixtureEditorViewModel> add,
                                     Action<FixtureEditorViewModel> remove,
                                     Func<int, IReadOnlyList<AddressRange>> freeRanges)
    {
        _add = add;
        _remove = remove;
        _freeRanges = freeRanges;
    }

    public FixtureProfile? Profile { get; private set; }
    public FixtureMode? Mode { get; private set; }

    public int Footprint => Mode?.Channels.Count ?? 0;

    public string Title => Profile is null ? "" : $"{Profile} · {Mode!.Name} · {Footprint} ch";

    /// <summary>New fixtures are named "{NamePrefix} 1", "{NamePrefix} 2", ...; editable before placing.</summary>
    [ObservableProperty]
    public partial string NamePrefix { get; set; } = "";

    [ObservableProperty]
    public partial decimal Quantity { get; set; } = 4;

    /// <summary>Fixtures added since this placing session began, oldest first.</summary>
    public ObservableCollection<FixtureEditorViewModel> Added { get; } = new();

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    public void Begin(FixtureProfile profile, FixtureMode mode)
    {
        Profile = profile;
        Mode = mode;
        NamePrefix = profile.Model;
        Added.Clear();
        Status = "Click a green opening to add one there. Keep clicking to add more.";
        OnPropertyChanged(nameof(Footprint));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>Adds one fixture starting at the address (the map has already checked it fits).</summary>
    public void PlaceAt(int universe, int startAddress)
    {
        if (Profile is null || Mode is null) return;
        var fixture = _add(Profile, Mode, NamePrefix, universe, startAddress);
        Added.Add(fixture);
        Status = $"Added {fixture.Name} at {startAddress}-{startAddress + Footprint - 1}. Click another opening to add more, or Done.";
    }

    /// <summary>Places Quantity fixtures back to back in the first gap with room for all of them.</summary>
    public void AddInARow(int universe)
    {
        int count = (int)Quantity;
        if (Profile is null || count < 1) return;
        int needed = count * Footprint;
        var gap = _freeRanges(universe).FirstOrDefault(r => r.Length >= needed);
        if (gap.Length < needed)
        {
            Status = $"No gap has {needed} free channels in a row ({count} × {Footprint}). Place them one at a time, or fewer at once.";
            return;
        }
        for (int i = 0; i < count; i++) PlaceAt(universe, gap.Start + i * Footprint);
        Status = $"Added {count} at {gap.Start}-{gap.Start + needed - 1}. Add more, or Done.";
    }

    [RelayCommand]
    void UndoLast()
    {
        if (Added.Count == 0) return;
        var last = Added[^1];
        Added.RemoveAt(Added.Count - 1);
        _remove(last);
        Status = $"Removed {last.Name}.";
    }
}
