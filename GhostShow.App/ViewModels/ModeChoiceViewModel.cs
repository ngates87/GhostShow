using CommunityToolkit.Mvvm.ComponentModel;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.ViewModels;

/// <summary>One mode shown as a button, e.g. "6 Channel · 6 ch".</summary>
public sealed partial class ModeOption(FixtureMode mode, Action<ModeOption> selected) : ObservableObject
{
    public FixtureMode Mode { get; } = mode;

    public string Label => $"{Mode.Name} · {Mode.Channels.Count} ch";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) selected(this);
    }
}

/// <summary>
/// The modes of one profile with one selected, shown as a row of toggle buttons. Each option carries its own
/// IsSelected, rather than a ComboBox or ListBox binding a selection: swapping such a control's items to another
/// profile's modes left Avalonia's selection blank. A new instance is made per profile, so options never change.
/// </summary>
public sealed class ModeChoiceViewModel
{
    readonly Action<FixtureMode>? _changed;
    bool _updating;

    public ModeChoiceViewModel(FixtureProfile profile, string? selectedModeName, Action<FixtureMode>? changed = null)
    {
        Profile = profile;
        Options = profile.Modes.Select(m => new ModeOption(m, OnSelected)).ToArray();
        var initial = Options.FirstOrDefault(o => string.Equals(o.Mode.Name, selectedModeName, StringComparison.OrdinalIgnoreCase))
                      ?? Options[0];
        _updating = true;
        initial.IsSelected = true;
        _updating = false;
        Selected = initial.Mode;
        _changed = changed;
    }

    public FixtureProfile Profile { get; }
    public IReadOnlyList<ModeOption> Options { get; }
    public FixtureMode Selected { get; private set; }

    /// <summary>Keeps exactly one option selected. Clicking the selected one again re-checks it rather than leaving none.</summary>
    void OnSelected(ModeOption option)
    {
        if (_updating) return;
        _updating = true;
        foreach (var other in Options.Where(o => o != option)) other.IsSelected = false;
        _updating = false;
        if (option.Mode == Selected) return;
        Selected = option.Mode;
        _changed?.Invoke(option.Mode);
    }
}
