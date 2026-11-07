using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The buttons panel (beside the timeline): the show's buttons (VenueMagic's), each running its actions in order
/// when clicked or when one of its keys is pressed anywhere in the app (except while typing in a field). A button lights
/// up while a timeline it runs is playing. In edit mode, buttons are added, named, coloured, given keys and actions,
/// reordered and removed; a click selects instead of pressing. Edits mark the show unsaved (no undo yet).
/// </summary>
public sealed partial class ButtonsViewModel : ObservableObject
{
    public static IReadOnlyList<string> Palette { get; } = ["#455A64", "#C62828", "#AD1457", "#6A1B9A", "#283593", "#1565C0", "#00838F", "#2E7D32", "#9E9D24", "#EF6C00", "#5D4037"];

    readonly ShowSession _session;
    readonly PlaybackViewModel _playback;
    readonly Action _blackout;
    readonly Action _markDirty;
    readonly TimeProvider _time;

    /// <param name="time">The clock for re-arm times and "last fired"; tests pass their own.</param>
    public ButtonsViewModel(ShowSession session, PlaybackViewModel playback, Action blackout, Action markDirty, TimeProvider? time = null)
    {
        _session = session;
        _playback = playback;
        _blackout = blackout;
        _markDirty = markDirty;
        _time = time ?? TimeProvider.System;
        Reload();
        // Button edits made here, undone, or made on another device: the panel follows.
        session.Document.Changed += change =>
        {
            if (change.Ops.Any(o => o.Path == "buttons" || o.Path.StartsWith("buttons/", StringComparison.Ordinal))) Sync();
            if (change.Ops.Any(o => o.Path.StartsWith("timelines", StringComparison.Ordinal))) RefreshTimelines();
        };
    }

    /// <summary>A button edit through the show's document: one undo step (on the show's history, with the tree's).</summary>
    internal void Edit(string description, Action change, object? mergeKey, params object[] roots) =>
        _session.Document.Record(description, change, roots, mergeKey);

    internal static object Only(object o) => ShowDocument.Shallow(o);

    /// <summary>Matches the panel to the show's buttons: rows for added ones, none for removed ones, the order, and every field.</summary>
    void Sync()
    {
        var model = _session.Project.Buttons;
        if (!Buttons.Select(b => b.Button).SequenceEqual(model))
        {
            var rows = model.Select(b => Buttons.FirstOrDefault(r => r.Button == b) ?? new ShowButtonViewModel(b, this)).ToList();
            Buttons.Clear();
            foreach (var row in rows) Buttons.Add(row);
            if (Selected is not null && !Buttons.Contains(Selected)) Selected = null;
        }
        foreach (var row in Buttons) row.Sync();
        Changed();
        Tick();
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
        LearningKeyFor = null;   // waiting for a key belongs to the button being edited
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (!value) Selected = null;
        else Selected ??= Buttons.FirstOrDefault();
        OnPropertyChanged(nameof(EditLabel));
    }

    /// <summary>The Edit button's text: "Done" while editing, so it's clear how to finish.</summary>
    public string EditLabel => IsEditing ? "Done" : "Edit";

    /// <summary>What the last press did (or why it couldn't), for the panel's footer.</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    /// <summary>
    /// The show's timelines (for the actions' pickers), by their place in the tree. The same list each time until a
    /// timeline is added, removed, renamed or moved: a dropdown handed a new list while a choice is being made in it
    /// loses the choice, and puts back the old one.
    /// </summary>
    public IReadOnlyList<TimelineChoice> Timelines
    {
        get
        {
            var now = _session.Project.Timelines.OrderBy(t => t.TreePath, StringComparer.OrdinalIgnoreCase).Select(t => new TimelineChoice(t.Id, t.TreePath)).ToList();
            if (!now.SequenceEqual(_timelines)) _timelines = now;
            return _timelines;
        }
    }

    IReadOnlyList<TimelineChoice> _timelines = [];

    /// <summary>A timeline changed: the actions' pickers look again (getting a new list only if the timelines differ).</summary>
    void RefreshTimelines()
    {
        OnPropertyChanged(nameof(Timelines));
        foreach (var button in Buttons)
            foreach (var action in button.Actions) action.Refresh();
    }

    /// <summary>A new or opened show: its buttons.</summary>
    public void Reload()
    {
        LearningKeyFor = null;
        TriggersArmed = true;   // a show opens armed, so its Start Show key simply works
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
                    if (Find(run.TimelineId) is { } timeline) _playback.Launch(timeline);
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

    // ---- inputs: keys now, sensors and Stream Deck keys later, all pressing buttons the same way ----

    /// <summary>Every input event, with what came of it in a few words, for the input monitor.</summary>
    public event Action<InputEvent, string>? InputReceived;

    /// <summary>
    /// An input event from any source. A press presses the button with a live trigger (armed and on) for that input, if
    /// the trigger is ready (not still re-arming); a release does nothing yet. Returns the button it pressed, or null. A
    /// click on the button always works, whatever its triggers.
    /// </summary>
    public ShowButtonViewModel? Input(string source, string address, bool isPressed)
    {
        var input = new InputEvent(source, address, isPressed, _time.GetLocalNow());
        var (pressed, outcome) = isPressed ? PressFor(input) : (null, "");
        InputReceived?.Invoke(input, outcome);
        return pressed;
    }

    /// <summary>A key pressed anywhere (see <see cref="Input"/>). Returns whether it pressed a button.</summary>
    public bool PressKey(string key) => Input(ButtonTrigger.Keyboard, key, isPressed: true) is not null;

    (ShowButtonViewModel? Pressed, string Outcome) PressFor(InputEvent input)
    {
        var matches = Buttons.SelectMany(b => b.Triggers.Where(t => t.Matches(input.Source, input.Address)).Select(t => (Button: b, Trigger: t))).ToList();
        if (matches.Count == 0) return (null, "No button uses it");
        if (!TriggersArmed) return (null, "Triggers are off");
        var live = matches.Where(m => m.Trigger.IsOn).ToList();
        if (live.Count == 0) return (null, $"{matches[0].Button.Name}: this trigger is turned off");
        var (button, trigger) = live[0];
        if (!trigger.IsReady(input.Time)) return (null, $"{button.Name}: {trigger.WhyNotReady(input.Time)}");
        trigger.Fired(input.Time);
        Press(button);
        return (button, $"Pressed {button.Name}");
    }

    /// <summary>Whether a live trigger uses this key: then the key is the button's, even while it's re-arming.</summary>
    public bool HasKey(string key) => LiveKeyTrigger(key) is not null;

    (ShowButtonViewModel Button, ButtonTriggerViewModel Trigger)? LiveKeyTrigger(string key)
    {
        if (!TriggersArmed) return null;
        foreach (var button in Buttons)
            foreach (var trigger in button.Triggers)
                if (trigger.IsOn && trigger.IsKey(key)) return (button, trigger);
        return null;
    }

    /// <summary>Whether a button has this key at all (for learning keys: one key, one button).</summary>

    ShowButtonViewModel? ButtonWithKey(string key) => Buttons.FirstOrDefault(b => b.HasKey(key));

    // ---- learning a key: the next key pressed becomes a trigger of the button ----

    /// <summary>The button waiting for a key to be pressed; null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLearningKey))]
    public partial ShowButtonViewModel? LearningKeyFor { get; private set; }

    partial void OnLearningKeyForChanged(ShowButtonViewModel? oldValue, ShowButtonViewModel? newValue)
    {
        oldValue?.IsLearningKey = false;
        newValue?.IsLearningKey = true;
        newValue?.KeyProblem = "";
    }

    public bool IsLearningKey => LearningKeyFor is not null;

    /// <summary>Starts waiting for a key for this button, or stops if it already was.</summary>
    internal void ToggleLearningKey(ShowButtonViewModel button) => LearningKeyFor = LearningKeyFor == button ? null : button;

    public void CancelLearningKey() => LearningKeyFor = null;

    /// <summary>
    /// The key pressed while waiting, whatever has focus. A key that can't be used says why and keeps waiting, so the next
    /// try needs no extra click. Keys are used on their own, so that Ctrl and the like stay with GhostShow's own shortcuts.
    /// A click elsewhere stops the waiting (MainWindow), so the keyboard is never held by a wait that's been forgotten.
    /// </summary>
    public void LearnKey(string key, bool withModifier)
    {
        if (LearningKeyFor is not { } button || TriggerKeys.IsModifier(key)) return;
        string label = TriggerKeys.Label(key);
        if (key == "Escape") LearningKeyFor = null;
        else if (withModifier) button.KeyProblem = "Press the key on its own, without Ctrl, Shift or Alt.";
        else if (!TriggerKeys.CanUse(key)) button.KeyProblem = $"GhostShow uses {label} itself: try a letter, a number or an F key.";
        else if (ButtonWithKey(key) is { } other) button.KeyProblem = $"{label} is already on {other.Name}.";
        else
        {
            LearningKeyFor = null;
            button.AddKey(key);
        }
    }

    /// <summary>From the UI's refresh timer: which buttons are lit (a timeline they run is playing), and how their triggers stand.</summary>
    public void Tick()
    {
        var now = _time.GetLocalNow();
        foreach (var button in Buttons)
        {
            button.IsLit = button.Button.Actions.OfType<RunTimelineAction>().Any(r => _playback.IsRunning(r.TimelineId));
            foreach (var trigger in button.Triggers) trigger.Tick(now, timelinesPlaying: button.IsLit);
        }
    }

    // ---- arming: triggers can be turned off while working on the show ----

    /// <summary>
    /// Whether triggers press buttons. On when a show opens; off while working on the show, so a key or sensor doesn't
    /// start a scare. Not saved with the show. Clicking a button always works.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArmedLabel), nameof(ShowFocusWarning))]
    public partial bool TriggersArmed { get; set; } = true;

    public string ArmedLabel => TriggersArmed ? "Triggers armed" : "Triggers off: click to arm";

    [RelayCommand]
    void ToggleArmed() => TriggersArmed = !TriggersArmed;

    /// <summary>Whether GhostShow is the active window (the window says): its keys only reach it then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFocusWarning))]
    public partial bool IsAppFocused { get; set; } = true;

    /// <summary>Armed key triggers that can't work because another window has the keyboard: worth a loud warning.</summary>
    public bool ShowFocusWarning => TriggersArmed && !IsAppFocused && Buttons.Any(b => b.Triggers.Count > 0);

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
        Edit("Add button", () => _session.Project.Buttons.Add(button), null, Only(_session.Project));
        // Straight on to its trigger: press a key now, or click anywhere else to skip that (see LearnKey).
        StartLearningKey(Buttons.First(r => r.Button == button));
    }

    /// <summary>Right-clicking a button, Edit: the editor opens on it. Clicks select until Edit mode is turned off.</summary>
    internal void OpenEditor(ShowButtonViewModel button)
    {
        IsOpen = true;
        IsEditing = true;
        Selected = button;
    }

    /// <summary>The editor opens on it, waiting for a key.</summary>
    internal void StartLearningKey(ShowButtonViewModel button)
    {
        OpenEditor(button);
        LearningKeyFor = button;   // after selecting, which stops any waiting
    }

    internal void Remove(ShowButtonViewModel button)
    {
        int index = Buttons.IndexOf(button);
        bool wasSelected = Selected == button;
        Edit($"Delete button '{button.Name}'", () => _session.Project.Buttons.Remove(button.Button), null, Only(_session.Project));
        if (wasSelected) Selected = Buttons.Count == 0 ? null : Buttons[Math.Min(index, Buttons.Count - 1)];
    }

    internal void Duplicate(ShowButtonViewModel button)
    {
        var copy = button.Button.Clone();
        copy.Name = $"{button.Name} copy";
        int index = Buttons.IndexOf(button) + 1;
        Edit("Duplicate button", () => _session.Project.Buttons.Insert(index, copy), null, Only(_session.Project));
        Selected = Buttons.First(r => r.Button == copy);
    }

    internal void Move(ShowButtonViewModel button, int by)
    {
        int from = Buttons.IndexOf(button), to = from + by;
        if (from < 0 || to < 0 || to >= Buttons.Count) return;
        Edit("Move button", () =>
        {
            _session.Project.Buttons.RemoveAt(from);
            _session.Project.Buttons.Insert(to, button.Button);
        }, null, Only(_session.Project));
    }

    internal void Changed()
    {
        OnPropertyChanged(nameof(HasButtons));
        OnPropertyChanged(nameof(ShowFocusWarning));
        _markDirty();
    }
}

public sealed record TimelineChoice(Guid Id, string Path)
{
    public override string ToString() => Path;
}

/// <summary>One button: what it shows, and (in edit mode) its name, colour, triggers and actions.</summary>
public sealed partial class ShowButtonViewModel : ObservableObject
{
    readonly ButtonsViewModel _owner;

    public ShowButtonViewModel(ShowButton button, ButtonsViewModel owner)
    {
        Button = button;
        _owner = owner;
        foreach (var action in button.Actions) Actions.Add(new ButtonActionViewModel(action, this, owner));
        foreach (var trigger in button.Triggers) Triggers.Add(new ButtonTriggerViewModel(trigger, this));
    }

    /// <summary>After a change to the show: its actions' rows match its actions, and every field shows the model.</summary>
    internal void Sync()
    {
        if (!Actions.Select(a => a.Action).SequenceEqual(Button.Actions))
        {
            var rows = Button.Actions.Select(a => Actions.FirstOrDefault(r => r.Action == a) ?? new ButtonActionViewModel(a, this, _owner)).ToList();
            Actions.Clear();
            foreach (var row in rows) Actions.Add(row);
        }
        if (!Triggers.Select(t => t.Trigger).SequenceEqual(Button.Triggers))
        {
            var rows = Button.Triggers.Select(t => Triggers.FirstOrDefault(r => r.Trigger == t) ?? new ButtonTriggerViewModel(t, this)).ToList();
            Triggers.Clear();
            foreach (var row in rows) Triggers.Add(row);
        }
        foreach (var trigger in Triggers) trigger.Refresh();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(KeyLabel));
        foreach (var action in Actions) action.Refresh();
    }

    void Edit(string description, Action change, string? field = null) =>
        _owner.Edit(description, change, field is null ? null : (Button.Id, field), ButtonsViewModel.Only(Button));

    public ShowButton Button { get; }

    public string Name
    {
        get => Button.Name;
        set
        {
            if (value == Button.Name || string.IsNullOrWhiteSpace(value)) return;
            Edit("Rename button", () => Button.Name = value, "name");
        }
    }

    public string Color
    {
        get => Button.Color ?? ButtonsViewModel.Palette[0];
        set
        {
            if (value == Button.Color) return;
            Edit("Change button colour", () => Button.Color = value);
        }
    }

    // ---- triggers ----

    public ObservableCollection<ButtonTriggerViewModel> Triggers { get; } = new();

    IEnumerable<string> Keys => Button.Triggers.Where(t => t.Source == ButtonTrigger.Keyboard).Select(t => t.Address);

    internal bool HasKey(string key) => Keys.Contains(key);

    /// <summary>Its keys, shown in the button's corner ("F5", "1 · Q"); empty for none.</summary>
    public string KeyLabel => string.Join(" · ", Keys.Select(TriggerKeys.Label));

    /// <summary>Waiting for a key to be pressed, to add it as a trigger.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LearnKeyLabel))]
    public partial bool IsLearningKey { get; set; }

    public string LearnKeyLabel => IsLearningKey ? "Press a key… (Esc cancels)" : "Add key";

    /// <summary>Why the key just pressed couldn't be added; empty when there's nothing to say.</summary>
    [ObservableProperty]
    public partial string KeyProblem { get; set; } = "";

    [RelayCommand]
    void LearnKey() => _owner.ToggleLearningKey(this);

    [RelayCommand]
    void OpenEditor() => _owner.OpenEditor(this);

    /// <summary>The editor's Done: leaves edit mode, so clicks press buttons again.</summary>
    [RelayCommand]
    void Done() => _owner.IsEditing = false;

    [RelayCommand]
    void AddKeyFromMenu() => _owner.StartLearningKey(this);

    internal void AddKey(string key)
    {
        var trigger = new ButtonTrigger { Address = key };
        Edit("Add key", () => Button.Triggers.Add(trigger));
    }

    internal void RemoveTrigger(ButtonTriggerViewModel trigger) =>
        Edit("Remove key", () => Button.Triggers.Remove(trigger.Trigger));

    /// <summary>A change to one of its triggers' settings (only that trigger is compared).</summary>
    internal void EditTrigger(ButtonTrigger trigger, string description, Action change) =>
        _owner.Edit(description, change, null, ButtonsViewModel.Only(trigger));

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
        Edit("Add action", () => Button.Actions.Add(action));
    }

    internal void RemoveAction(ButtonActionViewModel action)
    {
        Edit("Remove action", () => Button.Actions.Remove(action.Action));
    }

    internal void MoveAction(ButtonActionViewModel action, int by)
    {
        int from = Actions.IndexOf(action), to = from + by;
        if (from < 0 || to < 0 || to >= Actions.Count) return;
        Edit("Move action", () =>
        {
            Button.Actions.RemoveAt(from);
            Button.Actions.Insert(to, action.Action);
        });
    }

    /// <summary>A change to one of its actions (only that action is compared).</summary>
    internal void EditAction(ButtonAction action, string description, Action change) =>
        _owner.Edit(description, change, null, ButtonsViewModel.Only(action));
}

/// <summary>
/// One trigger of a button: its settings (saved with the show), and while the show runs, whether it's on, re-arming, and
/// when it last fired (not saved).
/// </summary>
public sealed partial class ButtonTriggerViewModel(ButtonTrigger trigger, ShowButtonViewModel button) : ObservableObject
{
    public ButtonTrigger Trigger { get; } = trigger;

    public string Label => $"Key {TriggerKeys.Label(Trigger.Address)}";

    internal bool Matches(string source, string address) => Trigger.Source == source && Trigger.Address == address;

    internal bool IsKey(string key) => Matches(ButtonTrigger.Keyboard, key);

    [RelayCommand]
    void Remove() => button.RemoveTrigger(this);

    // ---- settings ----

    public decimal RearmSeconds
    {
        get => (decimal)Trigger.RearmSeconds;
        set
        {
            double seconds = Math.Max(0, (double)value);
            if (seconds == Trigger.RearmSeconds) return;
            button.EditTrigger(Trigger, "Change re-arm time", () => Trigger.RearmSeconds = seconds);
        }
    }

    public bool RearmAfterTimelines
    {
        get => Trigger.RearmAfterTimelines;
        set
        {
            if (value == Trigger.RearmAfterTimelines) return;
            button.EditTrigger(Trigger, value ? "Re-arm after its timelines" : "Re-arm from firing", () => Trigger.RearmAfterTimelines = value);
        }
    }

    /// <summary>The re-arm settings in a few words, for the folded settings: "Re-arms at once", "Re-arms 3 s after its timelines".</summary>
    public string RearmSummary => (Trigger.RearmSeconds, Trigger.RearmAfterTimelines) switch
    {
        (0, false) => "Re-arms at once",
        (0, true) => "Re-arms when its timelines finish",
        (var s, false) => $"Re-arms {s:0.##} s after firing",
        (var s, true) => $"Re-arms {s:0.##} s after its timelines finish",
    };

    /// <summary>After a change to the show: every setting shows the model.</summary>
    internal void Refresh()
    {
        OnPropertyChanged(nameof(RearmSeconds));
        OnPropertyChanged(nameof(RearmAfterTimelines));
        OnPropertyChanged(nameof(RearmSummary));
    }

    // ---- while the show runs (none of this is saved) ----

    /// <summary>Off: does nothing until turned back on or the show is reopened. For a sensor that misbehaves mid-show.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; } = true;

    DateTimeOffset? _lastFired;
    DateTimeOffset? _readyAt;     // re-arming until then
    bool _waitingForTimelines;    // re-arming until the button's timelines have finished (then the re-arm time)

    internal bool IsReady(DateTimeOffset now) => !_waitingForTimelines && (_readyAt is null || now >= _readyAt);

    /// <summary>While it isn't ready, why: "re-arms in 2.5 s", "waiting for its timelines to finish".</summary>
    internal string WhyNotReady(DateTimeOffset now) =>
        _waitingForTimelines ? "waiting for its timelines to finish" : $"re-arms in {((_readyAt ?? now) - now).TotalSeconds:0.0} s";

    internal void Fired(DateTimeOffset now)
    {
        _lastFired = now;
        _waitingForTimelines = Trigger.RearmAfterTimelines;
        _readyAt = _waitingForTimelines ? null : now.AddSeconds(Trigger.RearmSeconds);
        Tick(now, timelinesPlaying: true);
    }

    /// <summary>From the panel's refresh: the re-arm time starts once its timelines have finished, if it waits for them.</summary>
    internal void Tick(DateTimeOffset now, bool timelinesPlaying)
    {
        if (_waitingForTimelines && !timelinesPlaying)
        {
            _waitingForTimelines = false;
            _readyAt = now.AddSeconds(Trigger.RearmSeconds);
        }
        string state = !IsOn ? "Off"
            : _waitingForTimelines ? "Waiting for its timelines to finish"
            : _readyAt is { } ready && now < ready ? $"Re-arms in {(ready - now).TotalSeconds:0.0} s"
            : "Ready";
        State = _lastFired is { } fired ? $"{state} · last fired {fired:HH:mm:ss}" : state;
    }

    /// <summary>"Ready", "Re-arms in 2.5 s", "Off", with when it last fired.</summary>
    [ObservableProperty]
    public partial string State { get; private set; } = "Ready";
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
            button.EditAction(Action, "Change the action's timeline", () => TimelineId = value.Id);
        }
    }

    public bool IsMissing => HasTimeline && owner.Find(TimelineId) is null;

    /// <summary>After a change to the show: every field shows the model.</summary>
    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    void Remove() => button.RemoveAction(this);

    [RelayCommand]
    void MoveEarlier() => button.MoveAction(this, -1);

    [RelayCommand]
    void MoveLater() => button.MoveAction(this, +1);
}
