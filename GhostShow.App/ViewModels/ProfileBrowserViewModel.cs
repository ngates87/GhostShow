using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

public enum ProfileGrouping { Manufacturer, Type }

/// <summary>
/// One button in the browser grid: either a group to open (Group set) or a profile to choose (Profile set).
/// </summary>
public sealed record ProfileTile(string Title, string Detail, string? Group, FixtureProfile? Profile, bool IsCurrent);

/// <summary>
/// Drill-down picker for fixture profiles, in the style of lighting consoles: a grid of groups (manufacturers, or
/// GhostShow's fixture categories such as fog, dimmer or moving head), then a grid of the profiles in the chosen group. Typing a search shows matching profiles from every
/// group instead. Choosing a profile calls the callback; the browser itself changes nothing.
/// </summary>
public sealed partial class ProfileBrowserViewModel : ObservableObject
{
    const int MaxSearchResults = 300;

    readonly Action<FixtureProfile> _choose;
    FixtureProfileLibrary _library = FixtureProfileLibrary.Empty;
    FixtureProfile? _current;

    public ProfileBrowserViewModel(Action<FixtureProfile> choose) => _choose = choose;

    public ObservableCollection<ProfileTile> Tiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ByManufacturer), nameof(ByType))]
    public partial ProfileGrouping Grouping { get; set; }

    /// <summary>Null at the top level, otherwise the open manufacturer or type.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInsideGroup))]
    public partial string? OpenGroup { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string Breadcrumb { get; set; } = "";

    public bool IsInsideGroup => OpenGroup is not null;

    // Two-way bound to the toggle buttons; setting one selects that grouping.
    public bool ByManufacturer
    {
        get => Grouping == ProfileGrouping.Manufacturer;
        set { if (value) Grouping = ProfileGrouping.Manufacturer; }
    }

    public bool ByType
    {
        get => Grouping == ProfileGrouping.Type;
        set { if (value) Grouping = ProfileGrouping.Type; }
    }

    partial void OnGroupingChanged(ProfileGrouping value)
    {
        // Stay with the current fixture's group in the new grouping, so switching views doesn't lose your place.
        OpenGroup = _current is null ? null : GroupOf(_current);
        Refresh();
    }

    partial void OnOpenGroupChanged(string? value) => Refresh();

    partial void OnSearchChanged(string value) => Refresh();

    public void SetLibrary(FixtureProfileLibrary library)
    {
        _library = library;
        Refresh();
    }

    /// <summary>Highlights the fixture's current profile and opens its group, so the browser starts where it is.</summary>
    public void ShowProfile(FixtureProfile? profile)
    {
        _current = profile;
        var group = profile is null ? null : GroupOf(profile);
        if (group == OpenGroup) Refresh();
        else OpenGroup = group;
    }

    [RelayCommand]
    void Open(ProfileTile tile)
    {
        if (tile.Profile is { } profile)
        {
            _current = profile;
            _choose(profile);
            Refresh();
        }
        else
        {
            Search = "";
            OpenGroup = tile.Group;
        }
    }

    [RelayCommand]
    void Back()
    {
        Search = "";
        OpenGroup = null;
    }

    string GroupOf(FixtureProfile profile) =>
        Grouping == ProfileGrouping.Manufacturer ? profile.Manufacturer : profile.EffectiveCategory.Label();

    /// <summary>(group name, profile count): manufacturers alphabetically, categories in their defined order.</summary>
    IEnumerable<(string Name, int Count)> Groups() => Grouping == ProfileGrouping.Manufacturer
        ? _library.Profiles.GroupBy(p => p.Manufacturer)
                           .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                           .Select(g => (g.Key, g.Count()))
        : _library.Profiles.GroupBy(p => p.EffectiveCategory)
                           .OrderBy(g => g.Key)
                           .Select(g => (g.Key.Label(), g.Count()));

    string AllGroupsLabel => Grouping == ProfileGrouping.Manufacturer ? "All manufacturers" : "All fixture types";

    void Refresh()
    {
        Tiles.Clear();
        if (!string.IsNullOrWhiteSpace(Search)) ShowSearchResults();
        else if (OpenGroup is null) ShowGroups();
        else ShowGroup(OpenGroup);
    }

    void ShowGroups()
    {
        string? currentGroup = _current is null ? null : GroupOf(_current);
        foreach (var (name, count) in Groups())
            Tiles.Add(new ProfileTile(name, count == 1 ? "1 fixture" : $"{count} fixtures", name, null, name == currentGroup));
        Breadcrumb = AllGroupsLabel;
    }

    void ShowGroup(string group)
    {
        var profiles = _library.Profiles.Where(p => GroupOf(p) == group)
                                        .OrderBy(p => p.Manufacturer, StringComparer.OrdinalIgnoreCase)
                                        .ThenBy(p => p.Model, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles) Tiles.Add(ProfileTileFor(profile));
        Breadcrumb = $"{AllGroupsLabel}  ›  {group}";
    }

    void ShowSearchResults()
    {
        var terms = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _library.Profiles
            .Where(p => terms.All(t => p.Manufacturer.Contains(t, StringComparison.OrdinalIgnoreCase)
                                       || p.Model.Contains(t, StringComparison.OrdinalIgnoreCase)
                                       || p.EffectiveCategory.Label().Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var profile in matches.Take(MaxSearchResults)) Tiles.Add(ProfileTileFor(profile));
        Breadcrumb = matches.Count > MaxSearchResults
            ? $"Search: first {MaxSearchResults} of {matches.Count} matches; type more to narrow it down"
            : $"Search: {matches.Count} matches";
    }

    /// <summary>The detail line shows what the grouping doesn't: the type when browsing by manufacturer, and vice versa.</summary>
    ProfileTile ProfileTileFor(FixtureProfile profile)
    {
        string context = Grouping == ProfileGrouping.Manufacturer && string.IsNullOrWhiteSpace(Search)
            ? profile.EffectiveCategory.Label()
            : profile.Manufacturer;
        string modes = profile.Modes.Count == 1 ? "1 mode" : $"{profile.Modes.Count} modes";
        return new ProfileTile(profile.Model, $"{context} · {modes}", null, profile, profile == _current);
    }
}
