using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.App.Controls;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>One channel of a fixture in the picker: a logical control (Red, Intensity, ...) or a channel by number.</summary>
public sealed partial class ChannelChoiceViewModel(string label, LaneTarget target, int universe, int address, Action changed) : ObservableObject
{
    public string Label { get; } = label;
    public LaneTarget Target { get; } = target;

    /// <summary>Where it is in the patch now, for the picker's map.</summary>
    public int Universe { get; } = universe;
    public int Address { get; } = address;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>A patched fixture in the picker: tick it for all its channels, or expand it to tick single ones.</summary>
public sealed partial class FixtureChoiceViewModel : ObservableObject
{
    bool _updating;

    readonly Action _changed;

    public FixtureChoiceViewModel(PatchedFixture fixture, Action changed)
    {
        _changed = changed;
        Id = fixture.Id;
        Name = fixture.Name;
        Details = $"U{fixture.Universe} @{fixture.StartAddress} · {fixture.ChannelCount} ch";
        // Each channel of the mode once: as its logical control where it has one (so the lane follows the control
        // if the fixture's mode changes), otherwise by number.
        var seen = new HashSet<ChannelFunction>();
        for (int i = 0; i < fixture.Mode.Channels.Count; i++)
        {
            var channel = fixture.Mode.Channels[i];
            bool asControl = channel.Function != ChannelFunction.Generic && seen.Add(channel.Function) && fixture.HasControl(channel.Function);
            var target = asControl ? LaneTarget.FixtureControl(fixture.Id, channel.Function) : LaneTarget.FixtureChannel(fixture.Id, i + 1);
            Channels.Add(new ChannelChoiceViewModel($"{i + 1}: {channel.Name}", target, fixture.Universe, fixture.AddressOfChannel(i + 1), ChildChanged));
        }
    }

    public Guid Id { get; }
    public string Name { get; }
    public string Details { get; }
    public ObservableCollection<ChannelChoiceViewModel> Channels { get; } = new();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>All ticked: true; none: false; some: null (shown as a dash).</summary>
    public bool? IsChecked
    {
        get => Channels.All(c => c.IsChecked) ? true : Channels.Any(c => c.IsChecked) ? null : false;
        set
        {
            _updating = true;
            bool check = value != false;
            foreach (var c in Channels) c.IsChecked = check;
            _updating = false;
            OnPropertyChanged();
            _changed();
        }
    }

    void ChildChanged()
    {
        if (_updating) return;
        OnPropertyChanged(nameof(IsChecked));
        _changed();
    }
}

/// <summary>A raw DMX channel (or a lane whose fixture is gone) in the picker's list; remove it with ×.</summary>
public sealed record OtherChannelViewModel(string Label, LaneTarget Target);

/// <summary>
/// Picks the channels a lighting track controls: patched fixtures (whole, or single channels) and raw DMX addresses
/// for anything unpatched. OK applies the choice as one undoable edit; existing channels keep their points.
/// </summary>
public sealed partial class ChannelPickerViewModel : ObservableObject
{
    readonly TimelineTrack _track;
    readonly Action<IReadOnlyList<LaneTarget>> _apply;
    readonly Action _cancel;

    public ChannelPickerViewModel(TimelineTrack track, ShowSession session, TimelineEditorViewModel editor, Action? showFixtures,
                                  Action<IReadOnlyList<LaneTarget>> apply, Action cancel)
    {
        _showFixtures = showFixtures;
        _track = track;
        _apply = apply;
        _cancel = cancel;
        TrackName = track.Name;
        var current = track.Channels.Select(l => l.Target).ToHashSet();
        _patch = session.Patch.Fixtures;
        _fixtureOrder = session.Project.Fixtures.Select(f => f.Id).ToList();
        foreach (var fixture in _patch.OrderBy(f => f.Universe).ThenBy(f => f.StartAddress))
        {
            var choice = new FixtureChoiceViewModel(fixture, UpdateMap);
            foreach (var c in choice.Channels) c.IsChecked = current.Contains(c.Target);
            choice.IsExpanded = choice.IsChecked is null;   // partly picked: show which
            Fixtures.Add(choice);
        }
        var offered = Fixtures.SelectMany(f => f.Channels).Select(c => c.Target).ToHashSet();
        foreach (var target in track.Channels.Select(l => l.Target).Where(t => !offered.Contains(t)))
            Others.Add(new OtherChannelViewModel(target.IsRaw ? editor.LaneLabel(target) : $"{editor.LaneLabel(target)} (not patched)", target));
        RawUniverse = session.Project.Universes.FirstOrDefault()?.Number ?? 1;
        MapUniverses = session.Project.Universes.Select(u => u.Number).Order().ToList();
        Others.CollectionChanged += (_, _) => UpdateMap();
        // Open on the universe of the first channel the track already has, else the first.
        var picked = PickedAddresses().FirstOrDefault();
        MapUniverse = picked.Universe != 0 && MapUniverses.Contains(picked.Universe) ? picked.Universe : MapUniverses.FirstOrDefault(1);
    }

    readonly IReadOnlyList<PatchedFixture> _patch;
    readonly Action? _showFixtures;

    /// <summary>"Patch fixtures first": closes the picker and goes to Lighting → Fixtures.</summary>
    [RelayCommand]
    void PatchFixtures()
    {
        _cancel();
        _showFixtures?.Invoke();
    }
    readonly List<Guid> _fixtureOrder;

    // ---- the map: where the picked channels are ----

    public IReadOnlyList<int> MapUniverses { get; }

    public bool HasSeveralUniverses => MapUniverses.Count > 1;

    /// <summary>The universe the map shows.</summary>
    [ObservableProperty]
    public partial int MapUniverse { get; set; }

    partial void OnMapUniverseChanged(int value) => UpdateMap();

    [ObservableProperty]
    public partial IReadOnlyList<PatchMapBlock> MapBlocks { get; private set; } = [];

    /// <summary>The picked channels' addresses on the map's universe.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<int> MapHighlights { get; private set; } = new HashSet<int>();

    /// <summary>"Picked on universe 1: 21–23, 100" (and how many are on other universes).</summary>
    [ObservableProperty]
    public partial string MapSummary { get; private set; } = "";

    IEnumerable<(int Universe, int Address)> PickedAddresses() =>
        Fixtures.SelectMany(f => f.Channels).Where(c => c.IsChecked).Select(c => (c.Universe, c.Address))
            .Concat(Others.Where(o => o.Target.IsRaw && o.Target.Channel is not null).Select(o => (o.Target.Universe, o.Target.Channel!.Value)));

    void UpdateMap()
    {
        if (_patch is null) return;   // still constructing
        MapBlocks = _patch.Where(f => f.Universe == MapUniverse)
            .Select(f => new PatchMapBlock(f.Id, f.Name, f.StartAddress, f.Mode.Channels.Select(c => c.Name).ToArray(),
                                           Math.Max(0, _fixtureOrder.IndexOf(f.Id)), Enabled: true))
            .ToList();
        var picked = PickedAddresses().ToList();
        var here = picked.Where(p => p.Universe == MapUniverse).Select(p => p.Address).ToHashSet();
        MapHighlights = here;
        int elsewhere = picked.Count(p => p.Universe != MapUniverse);
        MapSummary = here.Count == 0 && elsewhere == 0 ? "Nothing picked yet."
            : $"Picked on universe {MapUniverse}: {(here.Count == 0 ? "none" : Ranges(here))}" + (elsewhere > 0 ? $" · {elsewhere} on other universes" : "");
    }

    /// <summary>"21–23, 100": consecutive addresses as ranges.</summary>
    static string Ranges(IEnumerable<int> addresses)
    {
        var parts = new List<string>();
        var sorted = addresses.Order().ToList();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? $"{sorted[i]}" : $"{sorted[i]}–{sorted[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>Clicking a fixture on the map ticks all its channels, or unticks them if they all were.</summary>
    [RelayCommand]
    void ToggleFixture(Guid id)
    {
        if (Fixtures.FirstOrDefault(f => f.Id == id) is { } fixture) fixture.IsChecked = fixture.IsChecked != true;
    }

    public string TrackName { get; }

    public ObservableCollection<FixtureChoiceViewModel> Fixtures { get; } = new();

    /// <summary>Raw DMX channels, and channels of fixtures that aren't patched (kept unless removed).</summary>
    public ObservableCollection<OtherChannelViewModel> Others { get; } = new();

    public bool HasFixtures => Fixtures.Count > 0;

    [ObservableProperty]
    public partial decimal RawUniverse { get; set; }

    [ObservableProperty]
    public partial decimal RawFrom { get; set; } = 1;

    [ObservableProperty]
    public partial decimal RawTo { get; set; } = 1;

    /// <summary>Adds raw addresses From..To on the universe (e.g. 1-3 adds three channels).</summary>
    [RelayCommand]
    void AddRaw()
    {
        int from = (int)Math.Clamp(RawFrom, 1, 512), to = (int)Math.Clamp(Math.Max(RawFrom, RawTo), 1, 512);
        for (int address = from; address <= to; address++)
        {
            var target = LaneTarget.Raw((int)RawUniverse, address);
            if (Others.All(o => o.Target != target)) Others.Add(new OtherChannelViewModel($"U{target.Universe} · {address}", target));
        }
    }

    [RelayCommand]
    void RemoveOther(OtherChannelViewModel? other)
    {
        if (other is not null) Others.Remove(other);
    }

    /// <summary>The picked channels: ones the track already had keep their order, new ones follow in picker order.</summary>
    public IReadOnlyList<LaneTarget> Picked()
    {
        var picked = Fixtures.SelectMany(f => f.Channels).Where(c => c.IsChecked).Select(c => c.Target).Concat(Others.Select(o => o.Target)).ToList();
        var kept = _track.Channels.Select(l => l.Target).Where(picked.Contains);
        return kept.Concat(picked.Where(t => !kept.Contains(t))).ToList();
    }

    [RelayCommand]
    void Ok() => _apply(Picked());

    [RelayCommand]
    void Cancel() => _cancel();
}
