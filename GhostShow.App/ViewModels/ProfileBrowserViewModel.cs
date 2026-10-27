using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

public enum ProfileGrouping { Manufacturer, Type }

/// <summary>
/// One button in the browser grid: either a group to open (Group set) or a profile to choose (Profile set).
/// IsCurrent: the fixture uses this profile (or, for a group, a profile in it). IsPending: picked, awaiting Apply.
/// </summary>
public sealed record ProfileTile(string Title, string Detail, string? Group, FixtureProfile? Profile, bool IsCurrent, bool IsPending);

/// <summary>
/// Drill-down picker for fixture profiles, in the style of lighting consoles: a grid of groups (manufacturers, or
/// GhostShow's fixture categories such as fog, dimmer or moving head), then a grid of the profiles in the chosen
/// group. Typing a search shows matching profiles from every group instead.
///
/// Picking a profile opens a confirm bar showing it and its modes; only the confirm button passes the choice on.
/// What happens next (place new fixtures, or change an existing one) is up to the caller.
/// </summary>
public sealed partial class ProfileBrowserViewModel : ObservableObject
{
    const int MaxSearchResults = 300;

    readonly Action<FixtureProfile, FixtureMode> _apply;
    FixtureProfileLibrary _library = FixtureProfileLibrary.Empty;
    Dictionary<string, string> _manufacturerNames = new();
    FixtureProfile? _current;
    string? _currentModeName;

    public ProfileBrowserViewModel(Action<FixtureProfile, FixtureMode> apply) => _apply = apply;

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

    /// <summary>The profile picked in the grid, with its modes, waiting for Apply or Cancel. null when nothing is picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(PendingTitle), nameof(PendingDetail))]
    public partial ModeChoiceViewModel? Pending { get; private set; }

    public bool HasPending => Pending is not null;

    public string PendingTitle => Pending?.Profile.ToString() ?? "";

    public string PendingDetail => Pending is null ? ""
        : $"{Pending.Profile.EffectiveCategory.Label()}{(Pending.Profile == _current ? " · the current type" : "")} · pick a mode";

    /// <summary>Text of the confirm button, e.g. "Next: place them ▶" or "Use this type".</summary>
    [ObservableProperty]
    public partial string ApplyLabel { get; private set; } = "Apply";

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
        _manufacturerNames = library.Profiles
            .GroupBy(p => FixtureProfileLibrary.NormalizeName(p.Manufacturer))
            .ToDictionary(g => g.Key, g => g.GroupBy(p => p.Manufacturer).OrderByDescending(s => s.Count()).First().Key);
        Refresh();
    }

    /// <summary>
    /// Starts a fresh pick. current (if any) is highlighted and its group opened, so browsing starts where the
    /// fixture, or the last type used, is.
    /// </summary>
    public void Start(FixtureProfile? current, string? currentModeName, string applyLabel)
    {
        _current = current;
        _currentModeName = currentModeName;
        ApplyLabel = applyLabel;
        Pending = null;
        Search = "";
        var group = current is null ? null : GroupOf(current);
        if (group == OpenGroup) Refresh();
        else OpenGroup = group;
    }

    [RelayCommand]
    void Open(ProfileTile tile)
    {
        if (tile.Profile is { } profile)
        {
            Pending = new ModeChoiceViewModel(profile, profile == _current ? _currentModeName : null);
            Refresh();
        }
        else
        {
            Search = "";
            OpenGroup = tile.Group;
        }
    }

    [RelayCommand]
    void Apply()
    {
        if (Pending is not { } pending) return;
        _current = pending.Profile;
        _currentModeName = pending.Selected.Name;
        Pending = null;
        _apply(pending.Profile, pending.Selected);
        Refresh();
    }

    [RelayCommand]
    void Cancel()
    {
        Pending = null;
        Refresh();
    }

    [RelayCommand]
    void Back()
    {
        Search = "";
        OpenGroup = null;
    }

    /// <summary>
    /// The group a profile is listed under. Manufacturer spellings that differ only in case or punctuation
    /// ("Pro-Lights", "Prolights") are one group, shown under the spelling most of its profiles use.
    /// </summary>
    string GroupOf(FixtureProfile profile) =>
        Grouping == ProfileGrouping.Manufacturer
            ? _manufacturerNames.GetValueOrDefault(FixtureProfileLibrary.NormalizeName(profile.Manufacturer), profile.Manufacturer)
            : profile.EffectiveCategory.Label();

    /// <summary>(group name, profile count): manufacturers alphabetically, categories in their defined order.</summary>
    IEnumerable<(string Name, int Count)> Groups() => Grouping == ProfileGrouping.Manufacturer
        ? _library.Profiles.GroupBy(GroupOf)
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
            Tiles.Add(new ProfileTile(name, count == 1 ? "1 fixture" : $"{count} fixtures", name, null, name == currentGroup, false));
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
        bool inUse = profile == _current;
        return new ProfileTile(profile.Model, $"{(inUse ? "✓ In use · " : "")}{context} · {modes}", null, profile,
                               inUse, profile == Pending?.Profile);
    }
}
