using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>Edits one FixtureInstance in place. Every change writes through to the show and triggers re-validation.</summary>
public sealed partial class FixtureEditorViewModel : ObservableObject
{
    readonly Action _changed;
    readonly Func<string, FixtureProfile?> _findProfile;

    public FixtureEditorViewModel(FixtureInstance model, Func<string, FixtureProfile?> findProfile, Action changed)
    {
        Model = model;
        _findProfile = findProfile;
        _changed = changed;
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
        set => Set(Model.Name, value, v => Model.Name = v);
    }

    public string? Group
    {
        get => Model.Group;
        set => Set(Model.Group, string.IsNullOrWhiteSpace(value) ? null : value, v => Model.Group = v);
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set => Set(Model.Enabled, value, v => Model.Enabled = v);
    }

    public decimal Universe
    {
        get => Model.Universe;
        set => Set(Model.Universe, (int)value, v => Model.Universe = v);
    }

    public decimal StartAddress
    {
        get => Model.StartAddress;
        set => Set(Model.StartAddress, (int)value, v => Model.StartAddress = v);
    }

    public void SetProfile(FixtureProfile profile, FixtureMode mode)
    {
        Model.ProfileId = profile.Id;
        Model.ModeName = mode.Name;
        ModeChoice = CreateModeChoice();
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ProfileLabel));
        OnPropertyChanged(nameof(Summary));
        _changed();
    }

    ModeChoiceViewModel? CreateModeChoice() => Profile is { } profile
        ? new ModeChoiceViewModel(profile, Model.ModeName, mode => Set(Model.ModeName, mode.Name, v => Model.ModeName = v, nameof(Model.ModeName)))
        : null;

    void Set<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(Summary));
        _changed();
    }
}
