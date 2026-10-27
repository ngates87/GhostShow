using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.Controls;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The address map of one universe: a block per fixture (including disabled and colliding ones, so problems are
/// visible), plus usage figures and a readout of whatever the pointer is over.
/// </summary>
public sealed partial class PatchMapViewModel : ObservableObject
{
    readonly Action<Guid> _select;
    readonly Action<Guid, int> _move;
    readonly Action<int> _place;
    IReadOnlyList<FixtureEditorViewModel> _fixtures = [];
    PatchResult _patch = new([], []);

    public PatchMapViewModel(Action<Guid> select, Action<Guid, int> move, Action<int> place)
    {
        _select = select;
        _move = move;
        _place = place;
    }

    public ObservableCollection<int> Universes { get; } = new();

    [ObservableProperty]
    public partial int Universe { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PatchMapBlock> Blocks { get; private set; } = [];

    [ObservableProperty]
    public partial Guid? SelectedId { get; set; }

    [ObservableProperty]
    public partial string Usage { get; private set; } = "";

    /// <summary>One line for the heat map, e.g. "69 of 512 used · 2 collisions".</summary>
    [ObservableProperty]
    public partial string UsageShort { get; private set; } = "";

    [ObservableProperty]
    public partial string Hover { get; set; } = "";

    /// <summary>Channel count of the fixture being placed (the picked profile's, else the selected fixture's); 0 = none.</summary>
    [ObservableProperty]
    public partial int Footprint { get; private set; }

    /// <summary>Where a fixture of that footprint fits on this universe, as buttons above the map.</summary>
    public ObservableCollection<AddressRange> Openings { get; } = new();

    [ObservableProperty]
    public partial string OpeningsLabel { get; private set; } = "";

    /// <summary>Sets what is being placed. Call when the selection, the picked profile or its mode changes.</summary>
    public void SetFootprint(int channelCount)
    {
        Footprint = channelCount;
        Rebuild();
    }

    [RelayCommand]
    void PlaceAt(AddressRange opening) => _place(opening.Start);

    public void Place(int startAddress) => _place(startAddress);

    partial void OnUniverseChanged(int value) => Rebuild();

    /// <summary>Called after every patch so the map always shows the current state.</summary>
    public void Refresh(IReadOnlyList<FixtureEditorViewModel> fixtures, PatchResult patch, IEnumerable<int> universes)
    {
        _fixtures = fixtures;
        _patch = patch;
        var numbers = universes.Order().ToList();
        if (!numbers.SequenceEqual(Universes))
        {
            Universes.Clear();
            foreach (var n in numbers) Universes.Add(n);
        }
        Rebuild();
    }

    /// <summary>Highlights a fixture and switches to its universe.</summary>
    public void ShowFixture(FixtureEditorViewModel? fixture)
    {
        SelectedId = fixture?.Model.Id;
        Rebuild();
        if (fixture is not null && fixture.Model.Universe != Universe && Universes.Contains(fixture.Model.Universe))
            Universe = fixture.Model.Universe;
    }

    public void Select(Guid id) => _select(id);

    public void Move(Guid id, int startAddress) => _move(id, startAddress);

    void Rebuild()
    {
        var blocks = new List<PatchMapBlock>();
        for (int i = 0; i < _fixtures.Count; i++)
        {
            var fixture = _fixtures[i];
            if (fixture.Model.Universe != Universe || fixture.Profile?.FindMode(fixture.Model.ModeName) is not { } mode) continue;
            if (fixture.Model.StartAddress is < 1 or > 512) continue;
            var labels = mode.Channels.Select(c => c.Name).ToArray();
            blocks.Add(new PatchMapBlock(fixture.Model.Id, fixture.Name, fixture.Model.StartAddress, labels, i, fixture.Model.Enabled));
        }
        Blocks = blocks;

        var usage = new UniverseUsage(_patch.Fixtures.Where(f => f.Universe == Universe));
        string free = usage.FreeRanges.Count == 0 ? "none" : string.Join(", ", usage.FreeRanges.Take(6)) + (usage.FreeRanges.Count > 6 ? ", …" : "");
        string overlaps = usage.OverlapRanges.Count == 0 ? "" : $" · overlapping: {string.Join(", ", usage.OverlapRanges)}";
        Usage = $"{usage.UsedChannels} of 512 channels used · free: {free}{overlaps}";
        int collisions = usage.OverlapRanges.Sum(r => r.Length);
        UsageShort = $"Universe {Universe}: {usage.UsedChannels} of 512 used · {blocks.Count(b => b.Enabled)} fixtures" +
                     (collisions > 0 ? $" · {collisions} channels collide" : "");

        // Openings count the selected fixture's own addresses as free: it's the one being moved.
        var others = _patch.Fixtures.Where(f => f.Universe == Universe && f.Id != SelectedId);
        var openings = Footprint > 0 ? new UniverseUsage(others).OpeningsFor(Footprint) : [];
        Openings.Clear();
        foreach (var opening in openings.Take(12)) Openings.Add(opening);
        OpeningsLabel = Footprint == 0 ? ""
            : openings.Count == 0 ? $"No opening on universe {Universe} fits {Footprint} channels."
            : $"Fits {Footprint} ch at:{(openings.Count > 12 ? $" (first 12 of {openings.Count})" : "")}";
    }
}
