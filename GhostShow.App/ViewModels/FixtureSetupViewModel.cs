using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;

namespace GhostShow.App.ViewModels;

/// <summary>What the centre of the Fixtures screen is showing.</summary>
public enum SetupStep
{
    /// <summary>The selected fixture's settings and the address map (drag to re-address).</summary>
    Overview,
    /// <summary>Wizard step 1: choose a fixture type, for new fixtures or to change the selected one.</summary>
    ChooseType,
    /// <summary>Wizard step 2: click openings on the map to add fixtures of the chosen type, then Done.</summary>
    Place,
    /// <summary>From step 1: make (or edit) a custom fixture type, for a fixture the libraries don't have.</summary>
    EditProfile,
}

/// <summary>
/// Creating, editing and patching fixtures. Every edit is applied straight away, so address conflicts and other
/// problems show up immediately and a fixture is controllable as soon as it's valid.
///
/// Adding is an explicit mode: Add fixtures → choose the type → click openings on the map (each click adds one) →
/// Done, back to the overview. Changing an existing fixture's type goes through the same type picker.
/// </summary>
public sealed partial class FixtureSetupViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly Action _markDirty;
    bool _changingType;
    FixtureProfile? _lastAddedProfile;
    string? _lastAddedMode;

    public FixtureSetupViewModel(ShowSession session, ProfileLibraryViewModel library, Action markDirty)
    {
        _session = session;
        _markDirty = markDirty;
        Library = library;
        library.Loaded += Reload;
        _session.Applied += ShowIssues;
        Browser = new ProfileBrowserViewModel(OnTypeChosen);
        Placement = new FixturePlacementViewModel(AddFixture, RemoveFixture, FreeRanges);
        Map = new PatchMapViewModel(
            select: id => Selected = Fixtures.FirstOrDefault(f => f.Model.Id == id),
            move: (id, start) =>
            {
                var fixture = Fixtures.FirstOrDefault(f => f.Model.Id == id);
                if (fixture is null) return;
                Selected = fixture;
                fixture.StartAddress = start;
            },
            place: start => Placement.PlaceAt(Map!.Universe, start));
        Reload();
    }

    public ObservableCollection<FixtureEditorViewModel> Fixtures { get; } = new();
    public ObservableCollection<PatchIssue> Issues { get; } = new();

    [ObservableProperty]
    public partial FixtureEditorViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string IssueSummary { get; set; } = "";

    /// <summary>Profile count and the download/update button for the online library.</summary>
    public ProfileLibraryViewModel Library { get; }

    public ProfileBrowserViewModel Browser { get; }

    public FixturePlacementViewModel Placement { get; }

    /// <summary>The universe's address map, shared by the overview map, the placing map and the heat map.</summary>
    public PatchMapViewModel Map { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsChoosingType), nameof(IsPlacing), nameof(IsEditingProfile), nameof(WizardTitle))]
    public partial SetupStep Step { get; private set; }

    public bool IsOverview => Step == SetupStep.Overview;
    public bool IsChoosingType => Step == SetupStep.ChooseType;
    public bool IsPlacing => Step == SetupStep.Place;
    public bool IsEditingProfile => Step == SetupStep.EditProfile;

    /// <summary>The custom fixture type being made or edited (the EditProfile step).</summary>
    [ObservableProperty]
    public partial CustomProfileEditorViewModel? ProfileEditor { get; private set; }

    public string WizardTitle => Step switch
    {
        SetupStep.ChooseType when _changingType => $"Change the type of '{Selected?.Name}'",
        SetupStep.ChooseType => "Add fixtures · step 1 of 2 · choose the fixture type",
        SetupStep.Place => "Add fixtures · step 2 of 2 · click green openings to place them",
        SetupStep.EditProfile => ProfileEditor?.Heading ?? "",
        _ => "",
    };

    partial void OnSelectedChanged(FixtureEditorViewModel? value)
    {
        if (Step != SetupStep.Place) Map.ShowFixture(value);
    }

    partial void OnStepChanged(SetupStep value)
    {
        if (value != SetupStep.ChooseType) Browser.Stop();
        // Placing treats every existing fixture as occupied and shows openings for the new type; the overview shows
        // the selected fixture and no openings.
        if (value == SetupStep.Place)
        {
            Map.ShowFixture(null);
            Map.SetFootprint(Placement.Footprint);
        }
        else
        {
            Map.ShowFixture(Selected);
            Map.SetFootprint(0);
        }
    }

    [RelayCommand]
    void StartAdd()
    {
        _changingType = false;
        Browser.Start(_lastAddedProfile, _lastAddedMode, "Next: place them ▶");
        Step = SetupStep.ChooseType;
    }

    [RelayCommand]
    void ChangeType()
    {
        if (Selected is null) return;
        _changingType = true;
        Browser.Start(Selected.Profile, Selected.Model.ModeName, $"Use this type for '{Selected.Name}'");
        Step = SetupStep.ChooseType;
    }

    [RelayCommand]
    void CancelWizard() => Step = SetupStep.Overview;

    // ---- custom fixture types ----

    /// <summary>"Can't find it? New fixture type": a blank custom type.</summary>
    [RelayCommand]
    void NewProfile() => EditProfile(null, editInPlace: false);

    /// <summary>"Copy and edit": a custom type starting from the picked profile (usually the quickest way).</summary>
    [RelayCommand]
    void CopyProfile()
    {
        if (Browser.Pending?.Profile is { } profile) EditProfile(profile, editInPlace: false);
    }

    /// <summary>Edit the picked profile, if it's a custom one.</summary>
    [RelayCommand]
    void EditCustomProfile()
    {
        if (Browser.Pending?.Profile is { } profile && CustomProfiles.IsCustom(profile)) EditProfile(profile, editInPlace: true);
    }

    void EditProfile(FixtureProfile? source, bool editInPlace)
    {
        int usedBy = source is null ? 0 : _session.Project.Fixtures.Count(f => string.Equals(f.ProfileId, source.Id, StringComparison.OrdinalIgnoreCase));
        ProfileEditor = new CustomProfileEditorViewModel(source, editInPlace,
            newId: (manufacturer, model) => CustomProfiles.NewId(manufacturer, model, _session.Profiles),
            save: SaveProfile, cancel: () => BackToBrowser(null), delete: DeleteProfile, usedBy: usedBy);
        Step = SetupStep.EditProfile;
    }

    /// <summary>
    /// Saves it, moves this show's fixtures off any mode that was renamed, and goes back to the type picker with the
    /// saved type picked (so Next places it, or, when changing a fixture's type, uses it).
    /// </summary>
    string? SaveProfile(FixtureProfile profile, IReadOnlyDictionary<string, string> renamedModes)
    {
        if (Library.SaveCustom(profile) is { } error) return error;
        bool changed = false;
        foreach (var fixture in _session.Project.Fixtures.Where(f => string.Equals(f.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase)))
            if (renamedModes.TryGetValue(fixture.ModeName, out var renamed))
            {
                fixture.ModeName = renamed;
                changed = true;
            }
        if (changed) _markDirty();
        RefreshFixtures();
        BackToBrowser(profile);
        return null;
    }

    void DeleteProfile(FixtureProfile profile)
    {
        if (Library.DeleteCustom(profile) is { } error)
        {
            ProfileEditor?.ShowError(error);
            return;
        }
        RefreshFixtures();
        BackToBrowser(null);
    }

    /// <summary>Back to choosing a type (for new fixtures, or the selected one's), with a profile picked if given.</summary>
    void BackToBrowser(FixtureProfile? pick)
    {
        ProfileEditor = null;
        Browser.SetLibrary(_session.Profiles);
        if (_changingType && Selected is { } selected) Browser.Start(selected.Profile, selected.Model.ModeName, $"Use this type for '{selected.Name}'");
        else Browser.Start(_lastAddedProfile, _lastAddedMode, "Next: place them ▶");
        Step = SetupStep.ChooseType;
        if (pick is not null) Browser.Pick(pick);
    }

    /// <summary>The fixture list again (they look their profiles up when made), keeping the selection and the step.</summary>
    void RefreshFixtures()
    {
        var selected = Selected?.Model;
        Fixtures.Clear();
        foreach (var fixture in _session.Project.Fixtures)
            Fixtures.Add(new FixtureEditorViewModel(fixture, _session.Profiles.Find, OnFixtureChanged));
        Selected = Fixtures.FirstOrDefault(f => f.Model == selected) ?? Fixtures.FirstOrDefault();
        _session.Apply();
    }

    /// <summary>From placing back to the type picker, keeping what was already added.</summary>
    [RelayCommand]
    void BackToChooseType()
    {
        _changingType = false;
        Browser.Start(Placement.Profile, Placement.Mode?.Name, "Next: place them ▶");
        Step = SetupStep.ChooseType;
    }

    /// <summary>Ends placing; selects the last fixture added so its settings are on screen.</summary>
    [RelayCommand]
    void Done()
    {
        var last = Placement.Added.LastOrDefault();
        Step = SetupStep.Overview;
        if (last is not null) Selected = last;
    }

    [RelayCommand]
    void AddInARow() => Placement.AddInARow(Map.Universe);

    /// <summary>A copy of the selected fixture at the first gap it fits in.</summary>
    [RelayCommand]
    void Duplicate()
    {
        if (Selected is not { Profile: { } profile } source || profile.FindMode(source.Model.ModeName) is not { } mode) return;
        int universe = source.Model.Universe;
        int start = new UniverseUsage(_session.Patch.Fixtures.Where(f => f.Universe == universe)).FirstFreeStart(mode.Channels.Count)
                    ?? source.Model.StartAddress;   // no room: same address, and the overlap is flagged
        var copy = AddFixture(profile, mode, NameStem(source.Name), universe, start);
        copy.Group = source.Group;
        Selected = copy;
    }

    [RelayCommand]
    void Remove(FixtureEditorViewModel fixture)
    {
        int index = Fixtures.IndexOf(fixture);
        RemoveFixture(fixture);
        Selected = Fixtures.Count == 0 ? null : Fixtures[Math.Min(index, Fixtures.Count - 1)];
    }

    [RelayCommand]
    void SelectIssue(PatchIssue issue)
    {
        if (issue.FixtureId is { } id) Selected = Fixtures.FirstOrDefault(f => f.Model.Id == id);
    }

    /// <summary>Rebuilds the list from the show, e.g. after New or Open, or once the full profile library has loaded.</summary>
    public void Reload()
    {
        Fixtures.Clear();
        foreach (var fixture in _session.Project.Fixtures)
            Fixtures.Add(new FixtureEditorViewModel(fixture, _session.Profiles.Find, OnFixtureChanged));
        Browser.SetLibrary(_session.Profiles);
        Step = SetupStep.Overview;
        Selected = Fixtures.FirstOrDefault();
        ShowIssues();
    }

    void OnTypeChosen(FixtureProfile profile, FixtureMode mode)
    {
        if (_changingType)
        {
            Selected?.SetProfile(profile, mode);
            Step = SetupStep.Overview;
            return;
        }
        _lastAddedProfile = profile;
        _lastAddedMode = mode.Name;
        if (Placement.Profile != profile || Placement.Mode != mode) Placement.Begin(profile, mode);
        Step = SetupStep.Place;
        Map.SetFootprint(Placement.Footprint);
    }

    FixtureEditorViewModel AddFixture(FixtureProfile profile, FixtureMode mode, string nameStem, int universe, int startAddress)
    {
        var fixture = new FixtureInstance
        {
            Name = NextName(nameStem),
            ProfileId = profile.Id,
            ModeName = mode.Name,
            Universe = universe,
            StartAddress = startAddress,
        };
        _session.Project.Fixtures.Add(fixture);
        var editor = new FixtureEditorViewModel(fixture, _session.Profiles.Find, OnFixtureChanged);
        Fixtures.Add(editor);
        OnFixtureChanged();
        return editor;
    }

    void RemoveFixture(FixtureEditorViewModel fixture)
    {
        _session.Project.Fixtures.Remove(fixture.Model);
        Fixtures.Remove(fixture);
        OnFixtureChanged();
    }

    IReadOnlyList<AddressRange> FreeRanges(int universe) =>
        new UniverseUsage(_session.Patch.Fixtures.Where(f => f.Universe == universe)).FreeRanges;

    void OnFixtureChanged()
    {
        _markDirty();
        _session.Apply();
    }

    void ShowIssues()
    {
        Issues.Clear();
        foreach (var issue in _session.Patch.Issues.OrderByDescending(i => i.Severity)) Issues.Add(issue);

        var byFixture = _session.Patch.Issues.Where(i => i.FixtureId is not null).ToLookup(i => i.FixtureId!.Value);
        foreach (var fixture in Fixtures)
        {
            var issues = byFixture[fixture.Model.Id].ToList();
            fixture.Issue = issues.Count == 0 ? null : string.Join(Environment.NewLine, issues.Select(i => i.Message));
            fixture.HasError = issues.Any(i => i.Severity == PatchIssueSeverity.Error);
        }

        int errors = Issues.Count(i => i.Severity == PatchIssueSeverity.Error);
        int warnings = Issues.Count - errors;
        IssueSummary = (errors, warnings) switch
        {
            (0, 0) => $"{_session.Patch.Fixtures.Count} fixtures patched, no problems.",
            _ => $"{_session.Patch.Fixtures.Count} fixtures patched · {errors} errors · {warnings} warnings",
        };
        Map.Refresh(Fixtures, _session.Patch, _session.Project.Universes.Select(u => u.Number));
    }

    /// <summary>"Par 3" → "Par"; "Par" → "Par".</summary>
    static string NameStem(string name)
    {
        int space = name.LastIndexOf(' ');
        return space > 0 && int.TryParse(name[(space + 1)..], out _) ? name[..space] : name;
    }

    /// <summary>
    /// "{stem} N" with the lowest N from 1 that no fixture uses yet. The stem is used as given (a model name such as
    /// "Hurricane 1000" keeps its number); Duplicate passes the copied fixture's name without its own number.
    /// </summary>
    string NextName(string stem)
    {
        stem = stem.Trim();
        if (stem.Length == 0) stem = "Fixture";
        var taken = Fixtures.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (taken.Contains($"{stem} {n}")) n++;
        return $"{stem} {n}";
    }
}
