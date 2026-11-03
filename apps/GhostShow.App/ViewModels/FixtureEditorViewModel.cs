using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Editing;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>
/// One fixture on the Fixtures screen. Every change is an edit of the show (see <see cref="ShowDocument"/>); what's on
/// screen follows the show through <see cref="Refresh"/>, so undo and other devices' edits show up the same way.
/// </summary>
public sealed partial class FixtureEditorViewModel : ObservableObject
{
    /// <summary>Makes an edit: its description, the change, and a key that merges quick edits of one field into one undo step.</summary>
    public delegate void EditFixture(string description, Action change, object mergeKey);

    readonly EditFixture _edit;
    readonly Func<string, FixtureProfile?> _findProfile;

    public FixtureEditorViewModel(FixtureInstance model, Func<string, FixtureProfile?> findProfile, EditFixture edit)
    {
        Model = model;
        _findProfile = findProfile;
        _edit = edit;
        ModeChoice = CreateModeChoice();
    }

    public FixtureInstance Model { get; }

    public FixtureProfile? Profile => _findProfile(Model.ProfileId);

    FixtureMode? Mode => Profile?.FindMode(Model.ModeName);

    /// <summary>The profile's modes as buttons; null if the profile is missing. Replaced whenever the profile changes.</summary>
    [ObservableProperty]
    public partial ModeChoiceViewModel? ModeChoice { get; private set; }

    public string ProfileLabel => Profile?.ToString() ?? $"Missing profile '{Model.ProfileId}'";

    /// <summary>e.g. "Color / RGB · 10 ch · U0 · 1-10", for the fixture list.</summary>
    public string Summary
    {
        get
        {
            string category = Profile?.EffectiveCategory.Label() ?? "Unknown profile";
            return Mode is { } mode
                ? $"{category} · {mode.Channels.Count} ch · U{Model.Universe} · {Model.StartAddress}-{Model.StartAddress + mode.Channels.Count - 1}"
                : $"{category} · U{Model.Universe} · {Model.StartAddress}";
        }
    }

    /// <summary>Error or warning text for this fixture from the last validation; null if it's fine.</summary>
    [ObservableProperty]
    public partial string? Issue { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    public string Name
    {
        get => Model.Name;
        set => Set("Rename fixture", Model.Name, value, v => Model.Name = v);
    }

    public string? Group
    {
        get => Model.Group;
        set => Set("Change fixture group", Model.Group, string.IsNullOrWhiteSpace(value) ? null : value, v => Model.Group = v);
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set => Set(value ? "Enable fixture" : "Disable fixture", Model.Enabled, value, v => Model.Enabled = v);
    }

    public decimal Universe
    {
        get => Model.Universe;
        set => Set("Move fixture to another universe", Model.Universe, (int)value, v => Model.Universe = v);
    }

    public decimal StartAddress
    {
        get => Model.StartAddress;
        set => Set("Readdress fixture", Model.StartAddress, (int)value, v => Model.StartAddress = v);
    }

    public void SetProfile(FixtureProfile profile, FixtureMode mode) =>
        _edit("Change fixture type", () =>
        {
            Model.ProfileId = profile.Id;
            Model.ModeName = mode.Name;
        }, (Model.Id, nameof(SetProfile)));

    /// <summary>The show changed (an edit here, an undo, another device): everything on screen follows it.</summary>
    public void Refresh()
    {
        if (ModeChoice?.Profile != Profile || !string.Equals(ModeChoice?.Selected.Name, Model.ModeName, StringComparison.OrdinalIgnoreCase))
            ModeChoice = CreateModeChoice();
        OnPropertyChanged(string.Empty);
    }

    ModeChoiceViewModel? CreateModeChoice() => Profile is { } profile
        ? new ModeChoiceViewModel(profile, Model.ModeName, mode => Set("Change fixture mode", Model.ModeName, mode.Name, v => Model.ModeName = v))
        : null;

    void Set<T>(string description, T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        _edit(description, () => assign(value), (Model.Id, property));
    }
}
