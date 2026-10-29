using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Engine;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The buttons panel (beside the timeline): the show's buttons (VenueMagic's), each running its actions in order
/// when clicked or when its key is pressed anywhere in the app (except while typing in a field). A button lights up
/// while a timeline it runs is playing. In edit mode, buttons are added, named, coloured, given a key and actions,
/// reordered and removed; a click selects instead of pressing. Edits mark the show unsaved (no undo yet).
/// </summary>
public sealed partial class ButtonsViewModel : ObservableObject
{
    public static IReadOnlyList<string> Palette { get; } = ["#455A64", "#C62828", "#AD1457", "#6A1B9A", "#283593", "#1565C0", "#00838F", "#2E7D32", "#9E9D24", "#EF6C00", "#5D4037"];

    /// <summary>The keys a button can have: F1 and F3-F12 (F2 renames), the digits and the letters, all without modifiers.</summary>
    public static IReadOnlyList<KeyChoice> AllKeys { get; } =
    [
        new(null, "None"),
        .. new[] { 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }.Select(n => new KeyChoice($"F{n}", $"F{n}")),
        .. Enumerable.Range(0, 10).Select(n => new KeyChoice($"D{n}", n.ToString())),
        .. Enumerable.Range('A', 26).Select(c => new KeyChoice(((char)c).ToString(), ((char)c).ToString())),
    ];

    readonly ShowSession _session;
    readonly PlaybackViewModel _playback;
    readonly Action _blackout;
    readonly Action _markDirty;

    public ButtonsViewModel(ShowSession session, PlaybackViewModel playback, Action blackout, Action markDirty)
    {
        _session = session;
        _playback = playback;
        _blackout = blackout;
        _markDirty = markDirty;
        Reload();
    }

    public ObservableCollection<ShowButtonViewModel> Buttons { get; } = new();

    public bool HasButtons => Buttons.Count > 0;

    /// <summary>Whether the panel is showing (it folds away to a strip at the edge).</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; } = true;

    [RelayCommand]
    void ToggleOpen() => IsOpen = !IsOpen;

    /// <summary>Edit mode: a click selects a button to edit instead of pressing it.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial ShowButtonViewModel? Selected { get; set; }

    partial void OnSelectedChanged(ShowButtonViewModel? oldValue, ShowButtonViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        newValue?.IsSelected = true;
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (!value) Selected = null;
        else Selected ??= Buttons.FirstOrDefault();
    }

    /// <summary>What the last press did (or why it couldn't), for the panel's footer.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>The show's timelines (for the actions' pickers), by their place in the tree.</summary>
    public IReadOnlyList<TimelineChoice> Timelines =>
        _session.Project.Timelines.OrderBy(t => t.TreePath, StringComparer.OrdinalIgnoreCase).Select(t => new TimelineChoice(t.Id, t.TreePath)).ToList();

    /// <summary>A new or opened show: its buttons.</summary>
    public void Reload()
    {
        Buttons.Clear();
        foreach (var button in _session.Project.Buttons) Buttons.Add(new ShowButtonViewModel(button, this));
        Selected = null;
        IsOpen = Buttons.Count > 0;   // a show without buttons keeps the room for the timeline (the strip brings them up)
        OnPropertyChanged(nameof(HasButtons));
        OnPropertyChanged(nameof(Timelines));
    }

    // ---- pressing ----

    /// <summary>Runs a button's actions, in order. One that can't run (its timeline was deleted) is skipped and reported.</summary>
    public void Press(ShowButtonViewModel button)
    {
        var problems = new List<string>();
        foreach (var action in button.Button.Actions)
        {
            switch (action)
            {
                case RunTimelineAction run:
                    if (Find(run.TimelineId) is { } timeline) _playback.Launch(timeline, run.PlayOnceOnly);
                    else problems.Add("a timeline it runs is gone");
                    break;
                case StopTimelineAction stop:
                    if (Find(stop.TimelineId) is { } stopped) _playback.StopTimeline(stopped);
                    else problems.Add("a timeline it stops is gone");
                    break;
                case StopAllAction:
                    _playback.StopAllCommand.Execute(null);
                    break;
                case BlackoutAction:
                    _blackout();
                    break;
            }
        }
        Status = problems.Count > 0 ? $"{button.Name}: {string.Join("; ", problems)}." : button.Button.Actions.Count == 0 ? $"{button.Name} has no actions yet." : $"{button.Name}";
        Tick();
    }

    /// <summary>A key pressed anywhere (not while typing): presses the button with that key. Returns whether one had it.</summary>
    public bool PressKey(string key)
    {
        if (Buttons.FirstOrDefault(b => b.Button.Key == key) is not { } button) return false;
        Press(button);
        return true;
    }

    /// <summary>From the UI's refresh timer: which buttons are lit (a timeline they run is playing).</summary>
    public void Tick()
    {
        foreach (var button in Buttons)
            button.IsLit = button.Button.Actions.OfType<RunTimelineAction>().Any(r => _playback.IsRunning(r.TimelineId));
    }

    internal ShowTimeline? Find(Guid id) => _session.Project.Timelines.FirstOrDefault(t => t.Id == id);

    /// <summary>A click on a button: presses it, or in edit mode selects it.</summary>
    internal void Clicked(ShowButtonViewModel button)
    {
        if (IsEditing) Selected = button;
        else Press(button);
    }

    // ---- editing ----

    [RelayCommand]
    void Add()
    {
        var button = new ShowButton { Name = $"Button {Buttons.Count + 1}", Color = Palette[(Buttons.Count + 1) % Palette.Count] };
        _session.Project.Buttons.Add(button);
        var row = new ShowButtonViewModel(button, this);
        Buttons.Add(row);
        IsOpen = true;
        IsEditing = true;
        Selected = row;
        Changed();
    }

    internal void Remove(ShowButtonViewModel button)
    {
        int index = Buttons.IndexOf(button);
        _session.Project.Buttons.Remove(button.Button);
        Buttons.Remove(button);
        if (Selected == button) Selected = Buttons.Count == 0 ? null : Buttons[Math.Min(index, Buttons.Count - 1)];
        Changed();
    }

    internal void Duplicate(ShowButtonViewModel button)
    {
        var copy = button.Button.Clone();
        copy.Name = $"{button.Name} copy";
        int index = Buttons.IndexOf(button) + 1;
        _session.Project.Buttons.Insert(index, copy);
        var row = new ShowButtonViewModel(copy, this);
        Buttons.Insert(index, row);
        Selected = row;
        Changed();
    }

    internal void Move(ShowButtonViewModel button, int by)
    {
        int from = Buttons.IndexOf(button), to = from + by;
        if (from < 0 || to < 0 || to >= Buttons.Count) return;
        Buttons.Move(from, to);
        _session.Project.Buttons.RemoveAt(from);
        _session.Project.Buttons.Insert(to, button.Button);
        Changed();
    }

    /// <summary>The keys this button could take: its own and those no other button has.</summary>
    internal IReadOnlyList<KeyChoice> KeysFor(ShowButtonViewModel button) =>
        AllKeys.Where(k => k.Key is null || k.Key == button.Button.Key || Buttons.All(b => b == button || b.Button.Key != k.Key)).ToList();

    internal void Changed()
    {
        OnPropertyChanged(nameof(HasButtons));
        foreach (var button in Buttons) button.RefreshKeys();
        _markDirty();
    }
}

/// <summary>A key a button can have: its name (as Avalonia names it, e.g. "D1"), or null for none, and how it's shown.</summary>
public sealed record KeyChoice(string? Key, string Label)
{
    public override string ToString() => Label;
}

public sealed record TimelineChoice(Guid Id, string Path)
{
    public override string ToString() => Path;
}

/// <summary>One button: what it shows, and (in edit mode) its name, colour, key and actions.</summary>
public sealed partial class ShowButtonViewModel : ObservableObject
{
    readonly ButtonsViewModel _owner;

    public ShowButtonViewModel(ShowButton button, ButtonsViewModel owner)
    {
        Button = button;
        _owner = owner;
        foreach (var action in button.Actions) Actions.Add(new ButtonActionViewModel(action, this, owner));
    }

    public ShowButton Button { get; }

    public string Name
    {
        get => Button.Name;
        set
        {
            if (value == Button.Name || string.IsNullOrWhiteSpace(value)) return;
            Button.Name = value;
            OnPropertyChanged();
            _owner.Changed();
        }
    }

    public string Color
    {
        get => Button.Color ?? ButtonsViewModel.Palette[0];
        set
        {
            if (value == Button.Color) return;
            Button.Color = value;
            OnPropertyChanged();
            _owner.Changed();
        }
    }

    public IReadOnlyList<KeyChoice> KeyChoices => _owner.KeysFor(this);

    public KeyChoice SelectedKey
    {
        get => ButtonsViewModel.AllKeys.First(k => k.Key == Button.Key);
        set
        {
            if (value is null || value.Key == Button.Key) return;
            Button.Key = value.Key;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyLabel));
            _owner.Changed();
        }
    }

    /// <summary>The key, shown in the button's corner ("F5", "1"); empty for none.</summary>
    public string KeyLabel => Button.Key is null ? "" : SelectedKey.Label;

    internal void RefreshKeys() => OnPropertyChanged(nameof(KeyChoices));

    public ObservableCollection<ButtonActionViewModel> Actions { get; } = new();

    /// <summary>A timeline it runs is playing.</summary>
    [ObservableProperty]
    public partial bool IsLit { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    void Click() => _owner.Clicked(this);

    [RelayCommand]
    void Remove() => _owner.Remove(this);

    [RelayCommand]
    void Duplicate() => _owner.Duplicate(this);

    [RelayCommand]
    void MoveEarlier() => _owner.Move(this, -1);

    [RelayCommand]
    void MoveLater() => _owner.Move(this, +1);

    [RelayCommand]
    void PickColor(string color) => Color = color;

    /// <summary>Adds an action: "run", "stop", "stopAll" or "blackout". A timeline action starts on the first timeline.</summary>
    [RelayCommand]
    void AddAction(string kind)
    {
        var first = _owner.Timelines.FirstOrDefault()?.Id ?? Guid.Empty;
        ButtonAction action = kind switch
        {
            "stop" => new StopTimelineAction { TimelineId = first },
            "stopAll" => new StopAllAction(),
            "blackout" => new BlackoutAction(),
            _ => new RunTimelineAction { TimelineId = first },
        };
        Button.Actions.Add(action);
        Actions.Add(new ButtonActionViewModel(action, this, _owner));
        _owner.Changed();
    }

    internal void RemoveAction(ButtonActionViewModel action)
    {
        Button.Actions.Remove(action.Action);
        Actions.Remove(action);
        _owner.Changed();
    }

    internal void MoveAction(ButtonActionViewModel action, int by)
    {
        int from = Actions.IndexOf(action), to = from + by;
        if (from < 0 || to < 0 || to >= Actions.Count) return;
        Actions.Move(from, to);
        Button.Actions.RemoveAt(from);
        Button.Actions.Insert(to, action.Action);
        _owner.Changed();
    }

    internal void ActionChanged() => _owner.Changed();
}

/// <summary>One action of a button, in the editor.</summary>
public sealed partial class ButtonActionViewModel(ButtonAction action, ShowButtonViewModel button, ButtonsViewModel owner) : ObservableObject
{
    public ButtonAction Action { get; } = action;

    public string Kind => Action switch
    {
        RunTimelineAction => "Run timeline",
        StopTimelineAction => "Stop timeline",
        StopAllAction => "Stop all timelines",
        BlackoutAction => "Blackout",
        _ => "?",
    };

    public bool HasTimeline => Action is RunTimelineAction or StopTimelineAction;
    public bool IsRun => Action is RunTimelineAction;

    public IReadOnlyList<TimelineChoice> Timelines => owner.Timelines;

    Guid TimelineId
    {
        get => Action switch { RunTimelineAction r => r.TimelineId, StopTimelineAction s => s.TimelineId, _ => Guid.Empty };
        set
        {
            if (Action is RunTimelineAction r) r.TimelineId = value;
            else if (Action is StopTimelineAction s) s.TimelineId = value;
        }
    }

    /// <summary>The timeline it runs or stops; null if that timeline was deleted (shown as missing).</summary>
    public TimelineChoice? Timeline
    {
        get => Timelines.FirstOrDefault(t => t.Id == TimelineId);
        set
        {
            if (value is null || value.Id == TimelineId) return;
            TimelineId = value.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMissing));
            button.ActionChanged();
        }
    }

    public bool IsMissing => HasTimeline && owner.Find(TimelineId) is null;

    /// <summary>Run timeline: do nothing while it's still running (otherwise each press plays another copy).</summary>
    public bool PlayOnceOnly
    {
        get => Action is RunTimelineAction { PlayOnceOnly: true };
        set
        {
            if (Action is not RunTimelineAction run || run.PlayOnceOnly == value) return;
            run.PlayOnceOnly = value;
            OnPropertyChanged();
            button.ActionChanged();
        }
    }

    [RelayCommand]
    void Remove() => button.RemoveAction(this);

    [RelayCommand]
    void MoveEarlier() => button.MoveAction(this, -1);

    [RelayCommand]
    void MoveLater() => button.MoveAction(this, +1);
}
