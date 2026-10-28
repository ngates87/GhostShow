using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;

namespace GhostShow.App.ViewModels;

/// <summary>A channel function as offered in the editor, with a friendly name.</summary>
public sealed record FunctionOption(ChannelFunction Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A category as offered in the editor; null Value: worked out from the channels, as for library profiles.</summary>
public sealed record CategoryOption(FixtureCategory? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Makes or edits a custom fixture type, for a fixture the libraries don't have: manufacturer, model, category, and
/// its modes, each a list of channels in DMX order (a name, what it does, and the level it sits at when patched).
/// Starts blank, from a copy of a library profile ("copy and edit": usually the quickest way), or from an existing
/// custom type. Nothing is written until Save; problems are listed as you go and Save waits until there are none.
/// </summary>
public sealed partial class CustomProfileEditorViewModel : ObservableObject
{
    public static IReadOnlyList<FunctionOption> Functions { get; } =
    [
        new(ChannelFunction.Intensity, "Dimmer (intensity)"), new(ChannelFunction.Red, "Red"), new(ChannelFunction.Green, "Green"),
        new(ChannelFunction.Blue, "Blue"), new(ChannelFunction.White, "White"), new(ChannelFunction.Amber, "Amber"), new(ChannelFunction.UV, "UV"),
        new(ChannelFunction.Strobe, "Strobe"), new(ChannelFunction.Shutter, "Shutter"), new(ChannelFunction.Pan, "Pan"), new(ChannelFunction.Tilt, "Tilt"),
        new(ChannelFunction.ColorWheel, "Color wheel"), new(ChannelFunction.Gobo, "Gobo"), new(ChannelFunction.Speed, "Speed"),
        new(ChannelFunction.Effect, "Effect / program"), new(ChannelFunction.Maintenance, "Maintenance / reset"), new(ChannelFunction.Generic, "Other"),
    ];

    /// <summary>The quick-add buttons: the channels most fixtures are made of.</summary>
    public static IReadOnlyList<FunctionOption> QuickChannels { get; } = Functions.Where(f => f.Value is
        ChannelFunction.Intensity or ChannelFunction.Red or ChannelFunction.Green or ChannelFunction.Blue or ChannelFunction.White or
        ChannelFunction.Amber or ChannelFunction.UV or ChannelFunction.Strobe or ChannelFunction.Generic).ToList();

    public static IReadOnlyList<CategoryOption> Categories { get; } =
        [new(null, "Work it out from the channels"), .. Enum.GetValues<FixtureCategory>().Select(c => new CategoryOption(c, c.Label()))];

    readonly FixtureProfile? _editing;
    readonly Func<FixtureProfile, IReadOnlyDictionary<string, string>, string?> _save;
    readonly Action _cancel;
    readonly Action<FixtureProfile>? _delete;

    /// <param name="source">Where to start: null for blank, a profile to copy, or (with editInPlace) the custom profile to edit.</param>
    /// <param name="save">Saves the profile, given which modes were renamed (old name → new); returns why it couldn't, or null.</param>
    /// <param name="usedBy">How many of this show's fixtures use the profile being edited (deleting is only offered at 0).</param>
    public CustomProfileEditorViewModel(FixtureProfile? source, bool editInPlace, Func<string, string, string> newId,
                                        Func<FixtureProfile, IReadOnlyDictionary<string, string>, string?> save, Action cancel,
                                        Action<FixtureProfile>? delete = null, int usedBy = 0)
    {
        _editing = editInPlace ? source : null;
        _save = save;
        _cancel = cancel;
        _delete = delete;
        _newId = newId;
        UsedBy = usedBy;
        Heading = source is null ? "New fixture type"
                : editInPlace ? $"Edit '{source}'"
                : $"New fixture type, starting from {source}";
        Manufacturer = source?.Manufacturer ?? "";
        Model = source is null ? "" : editInPlace ? source.Model : $"{source.Model} (custom)";
        Category = Categories.FirstOrDefault(c => c.Value == source?.Category) ?? Categories[0];
        foreach (var mode in source?.Modes ?? [])
            Modes.Add(new ModeDraftViewModel(this, mode.Name, editInPlace ? mode.Name : null,
                                             mode.Channels.Select(c => new ChannelDraft(c.Name, c.Function, c.DefaultValue))));
        if (Modes.Count == 0) Modes.Add(new ModeDraftViewModel(this, "Standard", null, []));
        SelectedMode = Modes[0];
        Validate();
    }

    readonly Func<string, string, string> _newId;

    public string Heading { get; }

    public bool IsEditing => _editing is not null;

    public int UsedBy { get; }

    /// <summary>Delete is for a custom type no fixture in this show uses (other shows using it would lose it too, so it asks first).</summary>
    public bool CanDelete => _editing is not null && _delete is not null && UsedBy == 0;

    public string DeleteHint => UsedBy == 0 ? "" : $"{UsedBy} fixture{(UsedBy == 1 ? "" : "s")} in this show use{(UsedBy == 1 ? "s" : "")} it, so it can't be deleted.";

    [ObservableProperty]
    public partial string Manufacturer { get; set; }

    [ObservableProperty]
    public partial string Model { get; set; }

    [ObservableProperty]
    public partial CategoryOption Category { get; set; }

    public ObservableCollection<ModeDraftViewModel> Modes { get; } = new();

    [ObservableProperty]
    public partial ModeDraftViewModel? SelectedMode { get; set; }

    /// <summary>What's stopping Save, one per line; empty when it's ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Problems { get; private set; } = "";

    public bool HasProblems => Problems.Length > 0;

    /// <summary>Asking "really delete?" (the Delete button was pressed once).</summary>
    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; set; }

    partial void OnManufacturerChanged(string value) => Validate();
    partial void OnModelChanged(string value) => Validate();

    // ---- modes ----

    [RelayCommand]
    void AddMode()
    {
        var mode = new ModeDraftViewModel(this, UniqueModeName("New mode"), null, []);
        Modes.Add(mode);
        SelectedMode = mode;
        Validate();
    }

    /// <summary>A copy of the selected mode, to make a variant (e.g. the same channels with a dimmer added).</summary>
    [RelayCommand]
    void DuplicateMode()
    {
        if (SelectedMode is not { } source) return;
        var mode = new ModeDraftViewModel(this, UniqueModeName($"{source.Name} copy"), null, source.Channels.Select(c => c.ToDraft()));
        Modes.Insert(Modes.IndexOf(source) + 1, mode);
        SelectedMode = mode;
        Validate();
    }

    [RelayCommand]
    void RemoveMode()
    {
        if (SelectedMode is not { } mode || Modes.Count == 1) return;
        int index = Modes.IndexOf(mode);
        Modes.Remove(mode);
        SelectedMode = Modes[Math.Min(index, Modes.Count - 1)];
        Validate();
    }

    string UniqueModeName(string stem)
    {
        var taken = Modes.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(stem)) return stem;
        for (int n = 2; ; n++) if (!taken.Contains($"{stem} {n}")) return $"{stem} {n}";
    }

    // ---- checking and saving ----

    internal void Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Model)) problems.Add("Give it a model name.");
        foreach (var mode in Modes)
        {
            if (string.IsNullOrWhiteSpace(mode.Name)) problems.Add("Every mode needs a name.");
            else if (mode.Channels.Count == 0) problems.Add($"Mode '{mode.Name}' has no channels yet: add them below.");
            else if (mode.Channels.Count > 512) problems.Add($"Mode '{mode.Name}' has more than 512 channels.");
            foreach (var channel in mode.Channels.Where(c => string.IsNullOrWhiteSpace(c.Name)))
                problems.Add($"Channel {channel.Number} of mode '{mode.Name}' needs a name.");
        }
        foreach (var duplicate in Modes.Where(m => !string.IsNullOrWhiteSpace(m.Name)).GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Two modes are called '{duplicate.Key}'.");
        Problems = string.Join(Environment.NewLine, problems.Distinct());
    }

    /// <summary>The profile as it stands (a new ID for a new type; the same one when editing).</summary>
    public FixtureProfile Build()
    {
        string manufacturer = string.IsNullOrWhiteSpace(Manufacturer) ? "Custom" : Manufacturer.Trim();
        string model = Model.Trim();
        return new FixtureProfile
        {
            Id = _editing?.Id ?? _newId(manufacturer, model),
            Manufacturer = manufacturer,
            Model = model,
            Type = Category.Value?.Label() ?? _editing?.Type ?? "Other",
            Category = Category.Value,
            Modes = Modes.Select(m => new FixtureMode
            {
                Name = m.Name.Trim(),
                Channels = m.Channels.Select(c => new FixtureChannel { Name = c.Name.Trim(), Function = c.Function.Value, DefaultValue = (byte)Math.Clamp(c.DefaultValue, 0, 255) }).ToList(),
            }).ToList(),
        };
    }

    bool CanSave() => !HasProblems;

    [RelayCommand(CanExecute = nameof(CanSave))]
    void Save()
    {
        Error = null;
        var renamed = Modes.Where(m => m.OriginalName is not null && m.OriginalName != m.Name.Trim()).ToDictionary(m => m.OriginalName!, m => m.Name.Trim());
        if (_save(Build(), renamed) is { } error) ShowError(error);
    }

    [RelayCommand]
    void Cancel() => _cancel();

    [RelayCommand]
    void Delete()
    {
        if (!CanDelete) return;
        if (!IsConfirmingDelete)
        {
            IsConfirmingDelete = true;
            return;
        }
        _delete!(_editing!);
    }

    [RelayCommand]
    void CancelDelete() => IsConfirmingDelete = false;

    /// <summary>Why saving or deleting just failed (a disk error, say); Save can be tried again.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    public void ShowError(string message)
    {
        IsConfirmingDelete = false;
        Error = message;
    }
}

/// <summary>A channel's starting values, when building a mode.</summary>
public sealed record ChannelDraft(string Name, ChannelFunction Function, byte DefaultValue);

/// <summary>One mode being edited: its name and channels in DMX order.</summary>
public sealed partial class ModeDraftViewModel : ObservableObject
{
    readonly CustomProfileEditorViewModel _owner;

    public ModeDraftViewModel(CustomProfileEditorViewModel owner, string name, string? originalName, IEnumerable<ChannelDraft> channels)
    {
        _owner = owner;
        Name = name;
        OriginalName = originalName;
        foreach (var channel in channels) Channels.Add(new ChannelDraftViewModel(this, channel));
        Renumber();
    }

    /// <summary>Its name when editing started (fixtures using it are moved to the new name on save); null for a new mode.</summary>
    public string? OriginalName { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value) => _owner?.Validate();

    public ObservableCollection<ChannelDraftViewModel> Channels { get; } = new();

    public string Summary => Channels.Count == 1 ? "1 channel" : $"{Channels.Count} channels";

    /// <summary>Quick add: a channel doing that, named after it ("Dimmer", "Red", "Channel 5" for other).</summary>
    [RelayCommand]
    void AddChannel(FunctionOption? function)
    {
        var value = function?.Value ?? ChannelFunction.Generic;
        string name = value switch
        {
            ChannelFunction.Generic => $"Channel {Channels.Count + 1}",
            ChannelFunction.Intensity => "Dimmer",
            _ => function!.Label,
        };
        Channels.Add(new ChannelDraftViewModel(this, new ChannelDraft(name, value, 0)));
        Changed();
    }

    internal void Remove(ChannelDraftViewModel channel)
    {
        Channels.Remove(channel);
        Changed();
    }

    internal void Move(ChannelDraftViewModel channel, int by)
    {
        int from = Channels.IndexOf(channel), to = from + by;
        if (to < 0 || to >= Channels.Count) return;
        Channels.Move(from, to);
        Changed();
    }

    internal void Changed()
    {
        Renumber();
        OnPropertyChanged(nameof(Summary));
        _owner.Validate();
    }

    void Renumber()
    {
        for (int i = 0; i < Channels.Count; i++) Channels[i].Number = i + 1;
    }
}

/// <summary>One channel being edited.</summary>
public sealed partial class ChannelDraftViewModel : ObservableObject
{
    readonly ModeDraftViewModel _mode;

    public ChannelDraftViewModel(ModeDraftViewModel mode, ChannelDraft draft)
    {
        _mode = mode;
        Name = draft.Name;
        Function = CustomProfileEditorViewModel.Functions.First(f => f.Value == draft.Function);
        DefaultValue = draft.DefaultValue;
    }

    /// <summary>Its place in the mode: 1 is at the fixture's start address.</summary>
    [ObservableProperty]
    public partial int Number { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial FunctionOption Function { get; set; }

    /// <summary>The level it sits at when patched (e.g. a shutter's "open" value, or the fixture shows nothing).</summary>
    [ObservableProperty]
    public partial decimal DefaultValue { get; set; }

    partial void OnNameChanged(string value) => _mode?.Changed();

    /// <summary>Choosing a function renames a channel that still has an automatic name ("Channel 3" → "Red").</summary>
    partial void OnFunctionChanged(FunctionOption oldValue, FunctionOption newValue)
    {
        if (oldValue is null) return;
        bool automatic = Name.StartsWith("Channel ", StringComparison.Ordinal) || Name == oldValue.Label || (oldValue.Value == ChannelFunction.Intensity && Name == "Dimmer");
        if (automatic && newValue.Value != ChannelFunction.Generic) Name = newValue.Value == ChannelFunction.Intensity ? "Dimmer" : newValue.Label;
    }

    public ChannelDraft ToDraft() => new(Name, Function.Value, (byte)Math.Clamp(DefaultValue, 0, 255));

    [RelayCommand]
    void Remove() => _mode.Remove(this);

    [RelayCommand]
    void MoveUp() => _mode.Move(this, -1);

    [RelayCommand]
    void MoveDown() => _mode.Move(this, +1);
}
