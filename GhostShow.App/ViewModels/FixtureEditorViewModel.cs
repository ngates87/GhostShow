using System.Collections.ObjectModel;
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
        RefreshModeNames();
    }

    public FixtureInstance Model { get; }

    public FixtureProfile? Profile => _findProfile(Model.ProfileId);

    public ObservableCollection<string> ModeNames { get; } = new();

    public string ProfileLabel => Profile?.ToString() ?? $"Missing profile '{Model.ProfileId}'";

    public string AddressLabel => Profile?.FindMode(Model.ModeName) is { } mode
        ? $"U{Model.Universe} · {Model.StartAddress}-{Model.StartAddress + mode.Channels.Count - 1}"
        : $"U{Model.Universe} · {Model.StartAddress}";

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

    public string? ModeName
    {
        get => Model.ModeName;
        set
        {
            if (value is null) return;   // the ComboBox clears its selection while its items are replaced
            Set(Model.ModeName, value, v => Model.ModeName = v);
        }
    }

    /// <summary>Switching profile picks its first mode, since mode names differ between profiles.</summary>
    public void SetProfile(FixtureProfile profile)
    {
        Model.ProfileId = profile.Id;
        Model.ModeName = profile.Modes[0].Name;
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(ProfileLabel));
        RefreshModeNames();
        OnPropertyChanged(nameof(AddressLabel));
        _changed();
    }

    /// <summary>
    /// Adds the new mode names before removing the old ones. Clearing first would make the mode ComboBox write an
    /// empty selection back, re-read the new mode while the list is still empty, fail to select it, and then ignore
    /// later notifications for a value it believes is current: the box would stay blank.
    /// </summary>
    void RefreshModeNames()
    {
        var names = Profile?.Modes.Select(m => m.Name).ToList() ?? [];
        for (int i = 0; i < names.Count; i++) ModeNames.Insert(i, names[i]);
        while (ModeNames.Count > names.Count) ModeNames.RemoveAt(ModeNames.Count - 1);
    }

    void Set<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(AddressLabel));
        _changed();
    }
}
