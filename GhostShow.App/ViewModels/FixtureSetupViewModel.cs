using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Dmx;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>
/// Creating, editing and patching fixtures. Each edit is applied straight away, so address conflicts and other
/// problems show up while typing, and the fixture is controllable as soon as it's valid.
/// </summary>
public sealed partial class FixtureSetupViewModel : ObservableObject
{
    readonly ShowSession _session;
    readonly Action _markDirty;

    public FixtureSetupViewModel(ShowSession session, Action markDirty)
    {
        _session = session;
        _markDirty = markDirty;
        _session.Applied += ShowIssues;
        Browser = new ProfileBrowserViewModel(profile => Selected?.SetProfile(profile));
        Reload();
    }

    public ObservableCollection<FixtureEditorViewModel> Fixtures { get; } = new();
    public ObservableCollection<PatchIssue> Issues { get; } = new();

    [ObservableProperty]
    public partial FixtureEditorViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string IssueSummary { get; set; } = "";

    public string LibrarySummary => $"{_session.Profiles.Profiles.Count} profiles available" +
        (_session.Profiles.LoadErrors.Count > 0 ? $" ({_session.Profiles.LoadErrors.Count} files couldn't be read; see Diagnostics)" : "");

    public ProfileBrowserViewModel Browser { get; }

    partial void OnSelectedChanged(FixtureEditorViewModel? value) => Browser.ShowProfile(value?.Profile);

    /// <summary>Rebuilds the list from the show, e.g. after New or Open, or once the full profile library has loaded.</summary>
    public void Reload()
    {
        Fixtures.Clear();
        foreach (var fixture in _session.Project.Fixtures)
            Fixtures.Add(new FixtureEditorViewModel(fixture, _session.Profiles.Find, OnFixtureChanged));
        Browser.SetLibrary(_session.Profiles);
        Selected = Fixtures.FirstOrDefault();
        Browser.ShowProfile(Selected?.Profile);
        OnPropertyChanged(nameof(LibrarySummary));
        ShowIssues();
    }

    /// <summary>Adds a fixture like the selected one (or a generic dimmer) at the next free address.</summary>
    [RelayCommand]
    void Add()
    {
        var template = Selected?.Model;
        var profile = (template is null ? null : _session.Profiles.Find(template.ProfileId))
                      ?? _session.Profiles.Find("generic/dimmer")
                      ?? _session.Profiles.Profiles.First();
        string mode = template is not null && profile.FindMode(template.ModeName) is not null ? template.ModeName : profile.Modes[0].Name;
        int universe = template?.Universe ?? _session.Project.Universes.FirstOrDefault()?.Number ?? 0;

        var fixture = new GhostShow.Core.Fixtures.FixtureInstance
        {
            Name = template is null ? "Fixture 1" : NextName(template.Name),
            ProfileId = profile.Id,
            ModeName = mode,
            Universe = universe,
            StartAddress = NextFreeAddress(universe),
            Group = template?.Group,
        };
        _session.Project.Fixtures.Add(fixture);
        var editor = new FixtureEditorViewModel(fixture, _session.Profiles.Find, OnFixtureChanged);
        Fixtures.Add(editor);
        Selected = editor;
        OnFixtureChanged();
    }

    [RelayCommand]
    void Remove(FixtureEditorViewModel fixture)
    {
        int index = Fixtures.IndexOf(fixture);
        _session.Project.Fixtures.Remove(fixture.Model);
        Fixtures.Remove(fixture);
        Selected = Fixtures.Count == 0 ? null : Fixtures[Math.Min(index, Fixtures.Count - 1)];
        OnFixtureChanged();
    }

    [RelayCommand]
    void SelectIssue(PatchIssue issue)
    {
        if (issue.FixtureId is { } id) Selected = Fixtures.FirstOrDefault(f => f.Model.Id == id);
    }

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
    }

    int NextFreeAddress(int universe)
    {
        int end = _session.Patch.Fixtures.Where(f => f.Universe == universe).Select(f => f.EndAddress).DefaultIfEmpty(0).Max();
        return Math.Min(end + 1, DmxUniverse.LastAddress);
    }

    /// <summary>"Par 3" → "Par 4"; "Par" → "Par 2".</summary>
    static string NextName(string name)
    {
        int space = name.LastIndexOf(' ');
        if (space > 0 && int.TryParse(name[(space + 1)..], out int n)) return $"{name[..space]} {n + 1}";
        return $"{name} 2";
    }
}
