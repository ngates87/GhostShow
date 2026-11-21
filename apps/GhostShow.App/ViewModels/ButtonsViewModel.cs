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
    readonly Action _markDirty;
    readonly IRemoteTriggers _remote;

    /// <param name="triggers">The show's triggers, which run on the show computer: in-process there, over the connection from a device.</param>
    public ButtonsViewModel(ShowSession session, PlaybackViewModel playback, Action markDirty, IRemoteTriggers triggers)
    {
        _session = session;
        _playback = playback;
        _markDirty = markDirty;
        _remote = triggers;
        Reload();
        triggers.Attach(this);
        // Button edits made here, undone, or made on another device: the panel follows.
        session.Document.Changed += change =>
        {
            if (change.Ops.Any(o => o.Path == "buttons" || o.Path.StartsWith("buttons/", StringComparison.Ordinal))) Sync();
            if (change.Ops.Any(o => o.Path.StartsWith("timelines", StringComparison.Ordinal)))
            {
                RefreshTimelines();
                SyncTimelineTriggers();   // a timeline's triggers edited, undone, or the timeline added or removed
            }
            if (change.Touches("inputs")) RefreshTriggerLabels();   // an input was renamed, removed or put back
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

    /// <summary>The playback controls shown alongside the show's buttons.</summary>
    public PlaybackViewModel Playback => _playback;

    public bool HasButtons => Buttons.Count > 0;

    /// <summary>Whether the panel is showing (it folds away to a strip at the edge).</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; } = true;

    [RelayCommand]
    void ToggleOpen() => IsOpen = !IsOpen;

    /// <summary>The panel shows what's playing (its Playing tab) rather than the buttons.</summary>
    [ObservableProperty]
    public partial bool IsPlayingTab { get; set; }

    [RelayCommand]
    void ShowButtonsTab() => IsPlayingTab = false;

    [RelayCommand]
    void ShowPlayingTab()
    {
        IsPlayingTab = true;
        IsEditing = false;   // editing is for the buttons
    }

        public const double UsualWidth = 268;

    /// <summary>The panel's width, as dragged by its grip.</summary>
    [ObservableProperty]
    public partial double PanelWidth { get; set; } = UsualWidth;

    /// <summary>
    /// Whether the buttons and triggers can be changed here: not by a device paired as an operator, which presses buttons
    /// and arms triggers but doesn't change the show (docs/plan-shared-view.md, decision 2).
    /// </summary>
    [ObservableProperty]
    public partial bool CanEdit { get; set; } = true;

    partial void OnCanEditChanged(bool value)
    {
        if (!value) IsEditing = false;
    }

    /// <summary>Edit mode: a click selects a button to edit instead of pressing it.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial ShowButtonViewModel? Selected { get; set; }

    partial void OnSelectedChanged(ShowButtonViewModel? oldValue, ShowButtonViewModel? newValue)
    {
        oldValue?.IsSelected = false;
        newValue?.IsSelected = true;
        LearningFor = null;   // waiting for a key belongs to the button being edited
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (value) IsPlayingTab = false;   // editing shows the buttons
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
        LearningFor = null;
        _applyingStates = true;   // on a device, the show computer says how its triggers stand (below)
        TriggersArmed = true;   // a show opens armed, so its Start Show key simply works
        _applyingStates = false;
        Buttons.Clear();
        foreach (var button in _session.Project.Buttons) Buttons.Add(new ShowButtonViewModel(button, this));
        // Another show has other timelines, so they get new lists (every trigger ready); one still in the show keeps its
        // list, which Timeline properties may already be showing.
        SyncTimelineTriggers();
        Selected = null;
        IsOpen = Buttons.Count > 0;   // a show without buttons keeps the room for the timeline (the strip brings them up)
        OnPropertyChanged(nameof(HasButtons));
        OnPropertyChanged(nameof(Timelines));
        if (_states is { } states) ShowStates(states);
    }

    // ---- pressing ----

    /// <summary>Runs a button's actions, in order, on the show computer, as a click there does.</summary>
    public void Press(ShowButtonViewModel button) => _remote.Press(button.Button.Id);

    // ---- inputs: keys and serial commands now, Stream Deck keys later, setting off buttons' and timelines' triggers ----

    /// <summary>Every input event, with what came of it in a few words, for the input monitor.</summary>
    public event Action<InputEvent, string>? InputReceived;

    /// <summary>
    /// An input event, handed to the show computer's triggers (see GhostShow.Server's ShowTriggers), which say what came
    /// of it (<see cref="ShowInput"/>). Returns the button it pressed, or null. A click on the button always works,
    /// whatever its triggers.
    ///
    /// While a button or timeline here waits for a trigger, a serial command is learned instead: it becomes its trigger,
    /// listening to the device it came from. (Keys being learned never get here: the window hands them to <see cref="LearnKey"/>.)
    /// </summary>
    /// <param name="device">The show's input device it came from (<see cref="Project.Inputs"/>); null for the keyboard.</param>
    /// <param name="value">A serial command's value (On, for BTN24=On, whose address is BTN24); null for one without.</param>
    public ShowButtonViewModel? Input(string source, string address, bool isPressed, Guid? device = null, string? value = null)
    {
        var input = new InputEvent(source, address, isPressed, DateTimeOffset.Now, device, value);
        if (source != InputTrigger.Keyboard && LearningFor is not null)
        {
            string learned = Learn(input);   // (not inside ?.Invoke: with no one listening, it wouldn't be learned)
            InputReceived?.Invoke(input, learned);
            return null;
        }
        return _remote.Input(input) is { } pressed ? Buttons.FirstOrDefault(b => b.Button.Id == pressed) : null;
    }

    /// <summary>
    /// An input that arrived on the show computer, and what came of it there, for the input monitor; while a button or
    /// timeline here waits for a trigger, a command is learned instead.
    /// </summary>
    public void ShowInput(InputEvent input, string outcome)
    {
        if (input.Source != InputTrigger.Keyboard && LearningFor is not null) outcome = Learn(input);
        InputReceived?.Invoke(input, outcome);
    }

    /// <summary>A serial command, as it arrived: BTN24=On is input BTN24 at On (see <see cref="InputEvent.SplitCommand"/>).</summary>
    public ShowButtonViewModel? Command(Guid device, string command)
    {
        var (address, value) = InputEvent.SplitCommand(command);
        return Input(InputTrigger.Serial, address, isPressed: true, device, value);
    }

    /// <summary>Every trigger, on every button and timeline.</summary>
    IEnumerable<TriggerViewModel> AllTriggers => Buttons.SelectMany(b => b.Triggers).Concat(_timelineTriggers.Values.SelectMany(l => l));

    /// <summary>An input in words: "Key F5", "Ethereal FX “BTN24”" (the device's name in the show).</summary>
    public string Describe(string source, Guid? device, string address, string? value = null) => source switch
    {
        InputTrigger.Keyboard => $"Key {TriggerKeys.Label(address)}",
        InputTrigger.Serial => $"{InputName(device)} “{InputText.Visible(value is null ? address : $"{address}={value}")}”",
        _ => $"{source} {address}",
    };

    /// <summary>A trigger's input in words: "Key F5", "Ethereal FX “BTN24=On”", "Ethereal FX “BTN24” not On" (inverted).</summary>
    public string Describe(InputTrigger trigger) => trigger is { Invert: true, Value: { } value }
        ? $"{Describe(trigger.Source, trigger.Input, trigger.Address)} not {InputText.Visible(value)}"
        : Describe(trigger.Source, trigger.Input, trigger.Address, trigger.Value);

    public string Describe(InputAddress input) => Describe(input.Source, input.Input, input.Address, input.Value);

    /// <summary>The show's name for an input device: "Ethereal FX", or "Keyboard"; "A removed input" once it's gone.</summary>
    public string InputName(Guid? device) =>
        device is null ? "Keyboard" : _session.Project.Inputs.FirstOrDefault(i => i.Id == device)?.Name ?? "A removed input";

    void RefreshTriggerLabels()
    {
        foreach (var trigger in AllTriggers) trigger.Refresh();
    }

    /// <summary>
    /// Whether a serial device is being listened to, so a button waiting for its trigger says a command will do too.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSerialListening { get; set; }

    partial void OnIsSerialListeningChanged(bool value)
    {
        foreach (var list in Buttons.Select(b => b.Triggers).Concat(_timelineTriggers.Values)) list.RefreshLearnLabel();
    }

    /// <summary>A key pressed anywhere (see <see cref="Input"/>). Returns whether it pressed a button.</summary>
    public bool PressKey(string key) => Input(InputTrigger.Keyboard, key, isPressed: true) is not null;

    /// <summary>Whether a live trigger uses this key: then the key is the trigger's, even while it's re-arming.</summary>
    public bool HasKey(string key) => TriggersArmed && AllTriggers.Any(t => t.IsOn && t.IsKey(key));

    /// <summary>What has this input as a trigger, on or off (for learning: one input, one button or timeline).</summary>
    ITriggerOwner? OwnerWith(string source, Guid? device, string address) =>
        AllTriggers.FirstOrDefault(t => t.Trigger.Matches(source, device, address))?.Owner;

    // ---- timelines' triggers ----

    readonly Dictionary<Guid, TriggerListViewModel> _timelineTriggers = [];

    /// <summary>A timeline's triggers, for its Timeline properties (and to keep them listening when it isn't open).</summary>
    public TriggerListViewModel? TriggersFor(ShowTimeline? timeline)
    {
        if (timeline is null) return null;
        if (!_timelineTriggers.TryGetValue(timeline.Id, out var list))
            _timelineTriggers[timeline.Id] = list = new TriggerListViewModel(new TimelineTriggerOwner(timeline, this), this);
        return list;
    }

    /// <summary>After a change to the timelines: lists for those with triggers, none for removed ones, each matching its timeline.</summary>
    void SyncTimelineTriggers()
    {
        var timelines = _session.Project.Timelines.ToDictionary(t => t.Id);
        foreach (var (id, list) in _timelineTriggers.ToList())
        {
            if (timelines.TryGetValue(id, out var timeline) && ((TimelineTriggerOwner)list.Owner).Timeline == timeline) continue;
            if (LearningFor == list || list.Any(t => t == LearningFor)) LearningFor = null;
            _timelineTriggers.Remove(id);
        }
        foreach (var timeline in timelines.Values.Where(t => t.Triggers.Count > 0)) TriggersFor(timeline);
        foreach (var list in _timelineTriggers.Values) list.Sync();
        OnPropertyChanged(nameof(ShowFocusWarning));
    }

    /// <summary>A timeline, whose triggers run it.</summary>
    sealed class TimelineTriggerOwner(ShowTimeline timeline, ButtonsViewModel hub) : ITriggerOwner
    {
        public ShowTimeline Timeline => timeline;
        public Guid Id => timeline.Id;
        public string Name => timeline.Name;
        public List<InputTrigger> TriggerModels => timeline.Triggers;
        public bool IsTimeline => true;

        public void EditTriggers(string description, Action change, InputTrigger? only = null) =>
            hub._session.Document.Record(description, change, [only ?? ShowDocument.Shallow(timeline)], within: timeline);
    }

    // ---- learning: the next key pressed (or command sent) becomes a trigger, or a run-once trigger's reset ----

    /// <summary>What's waiting for a key: a button's or timeline's triggers (to add one), or a trigger (its reset input); null when nothing is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLearningKey))]
    public partial object? LearningFor { get; private set; }

    partial void OnLearningForChanged(object? oldValue, object? newValue)
    {
        if (oldValue is null != newValue is null) _remote.SetLearning(newValue is not null);   // its commands come here to learn
        if (oldValue is TriggerListViewModel oldList) oldList.IsLearning = false;
        if (oldValue is TriggerViewModel oldTrigger) oldTrigger.IsLearningReset = false;
        if (newValue is TriggerListViewModel newList) newList.IsLearning = true;
        if (newValue is TriggerViewModel newTrigger) newTrigger.IsLearningReset = true;
    }

    public bool IsLearningKey => LearningFor is not null;

    /// <summary>Starts waiting for a key for these triggers or this reset, or stops if it already was.</summary>
    internal void ToggleLearning(object what) => LearningFor = LearningFor == what ? null : what;

    public void CancelLearningKey() => LearningFor = null;

    /// <summary>Says what's wrong with the key or command just given, where the one waiting shows it.</summary>
    void Problem(string problem)
    {
        if (LearningFor is TriggerListViewModel list) list.Problem = problem;
        else Status = problem;
    }

    /// <summary>
    /// The key pressed while waiting, whatever has focus. A key that can't be used says why and keeps waiting, so the next
    /// try needs no extra click. Keys are used on their own, so that Ctrl and the like stay with GhostShow's own shortcuts.
    /// A click elsewhere stops the waiting (MainWindow), so the keyboard is never held by a wait that's been forgotten.
    /// </summary>
    public void LearnKey(string key, bool withModifier)
    {
        if (LearningFor is null || TriggerKeys.IsModifier(key)) return;
        string label = TriggerKeys.Label(key);
        if (key == "Escape") LearningFor = null;
        else if (withModifier) Problem("Press the key on its own, without Ctrl, Shift or Alt.");
        else if (!TriggerKeys.CanUse(key)) Problem($"GhostShow uses {label} itself: try a letter, a number or an F key.");
        else if (LearningFor is TriggerViewModel trigger)
        {
            LearningFor = null;
            trigger.SetReset(new InputAddress { Source = InputTrigger.Keyboard, Address = key });
        }
        else if (OwnerWith(InputTrigger.Keyboard, null, key) is { } other) Problem($"{label} is already on {other.Name}.");
        else if (LearningFor is TriggerListViewModel list)
        {
            LearningFor = null;
            list.AddTrigger(InputTrigger.Keyboard, key);
        }
    }

    /// <summary>Values a sensor box sends when its input rests: one of these arriving first isn't learned as the active value.</summary>
    static readonly HashSet<string> RestValues = new(["off", "0", "low", "false", "released", "up", "open"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A serial command (or later input) arriving while something waits for a trigger. Returns what came of it.</summary>
    string Learn(InputEvent input)
    {
        if (input.Value is { } value && RestValues.Contains(value))
            return $"Not learned: {value} looks like it's at rest; trip it";
        if (LearningFor is TriggerViewModel trigger)
        {
            LearningFor = null;
            trigger.SetReset(new InputAddress { Source = input.Source, Input = input.Input, Address = input.Address, Value = input.Value });
            return $"Learned as {trigger.Owner.Name}'s reset";
        }
        var list = (TriggerListViewModel)LearningFor!;
        if (OwnerWith(input.Source, input.Input, input.Address) is { } other)
        {
            list.Problem = $"{Describe(input.Source, input.Input, input.Address)} is already on {other.Name}.";
            return $"Not learned: it's already on {other.Name}";
        }
        LearningFor = null;
        list.AddTrigger(input.Source, input.Address, input.Input, input.Value);
        return $"Learned by {list.Owner.Name}";
    }

    /// <summary>From the UI's refresh timer: which buttons are lit (a timeline they run is playing). How triggers stand comes from <see cref="ShowStates"/>.</summary>
    public void Tick()
    {
        foreach (var button in Buttons) button.IsLit = button.IsPlaying;
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

    /// <summary>Arming the triggers again resets the run-once ones, ready for the next group.</summary>
    partial void OnTriggersArmedChanged(bool value)
    {
        if (!_applyingStates) _remote.SetArmed(value);
    }

    [RelayCommand]
    void ToggleArmed() => TriggersArmed = !TriggersArmed;

    /// <summary>Whether GhostShow is the active window (the window says): its keys only reach it then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFocusWarning))]
    public partial bool IsAppFocused { get; set; } = true;

    /// <summary>Armed key triggers that can't work because another window has the keyboard: worth a loud warning.</summary>
    public bool ShowFocusWarning => TriggersArmed && !IsAppFocused && AllTriggers.Any();

    // ---- the show computer's triggers, and a device's view of them ----

    /// <summary>The show computer's triggers, where they run.</summary>
    internal IRemoteTriggers Remote => _remote;

    bool _applyingStates;
    TriggerStates? _states;
    string _lastRemoteStatus = "";

    /// <summary>The trigger at this place: on a button or timeline (by its id), at its index in that one's list.</summary>
    public TriggerViewModel? FindTrigger(Guid owner, int index) =>
        TriggerLists.FirstOrDefault(l => l.Owner.Id == owner) is { } list && index >= 0 && index < list.Count ? list[index] : null;

    IEnumerable<TriggerListViewModel> TriggerLists => Buttons.Select(b => b.Triggers).Concat(_timelineTriggers.Values);

    /// <summary>How the show computer's triggers stand, shown here.</summary>
    public void ShowStates(TriggerStates states)
    {
        _states = states;
        _applyingStates = true;
        TriggersArmed = states.Armed;
        _applyingStates = false;
        IsSerialListening = states.SerialListening;
        if (states.Status != _lastRemoteStatus) Status = _lastRemoteStatus = states.Status;
        foreach (var state in states.Triggers) FindTrigger(state.Owner, state.Index)?.ShowRemote(state);
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
        LearningFor = button.Triggers;   // after selecting, which stops any waiting
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
public sealed partial class ShowButtonViewModel : ObservableObject, ITriggerOwner
{
    readonly ButtonsViewModel _owner;

    public ShowButtonViewModel(ShowButton button, ButtonsViewModel owner)
    {
        Button = button;
        _owner = owner;
        foreach (var action in button.Actions) Actions.Add(new ButtonActionViewModel(action, this, owner));
        Triggers = new TriggerListViewModel(this, owner);
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
        Triggers.Sync();
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(KeyLabel));
        foreach (var action in Actions) action.Refresh();
    }

    void Edit(string description, Action change, string? field = null) =>
        _owner.Edit(description, change, field is null ? null : (Button.Id, field), ButtonsViewModel.Only(Button));

    public ShowButton Button { get; }

    internal ButtonsViewModel Owner => _owner;

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

    // ---- triggers: they press it ----

    /// <summary>Its triggers, and Add key (or command).</summary>
    public TriggerListViewModel Triggers { get; }

    IEnumerable<string> Keys => Button.Triggers.Where(t => t.Source == InputTrigger.Keyboard).Select(t => t.Address);

    internal bool HasKey(string key) => Keys.Contains(key);

    /// <summary>Its keys, shown in the button's corner ("F5", "1 · Q"); empty for none.</summary>
    public string KeyLabel => string.Join(" · ", Keys.Select(TriggerKeys.Label));

    [RelayCommand]
    void OpenEditor() => _owner.OpenEditor(this);

    /// <summary>The editor's Done: leaves edit mode, so clicks press buttons again.</summary>
    [RelayCommand]
    void Done() => _owner.IsEditing = false;

    [RelayCommand]
    void AddKeyFromMenu() => _owner.StartLearningKey(this);

    /// <summary>A timeline it runs is playing (any copy).</summary>
    internal bool IsPlaying => Button.Actions.OfType<RunTimelineAction>().Any(r => _owner.Playback.IsRunning(r.TimelineId));

    Guid ITriggerOwner.Id => Button.Id;
    List<InputTrigger> ITriggerOwner.TriggerModels => Button.Triggers;
    bool ITriggerOwner.IsTimeline => false;

    void ITriggerOwner.EditTriggers(string description, Action change, InputTrigger? only) =>
        _owner.Edit(description, change, null, only ?? ButtonsViewModel.Only(Button));

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

    /// <summary>Adds an action: "run", "stop", "endLoop", "stopAll" or "blackout". A timeline action starts on the first timeline.</summary>
    [RelayCommand]
    void AddAction(string kind)
    {
        var first = _owner.Timelines.FirstOrDefault()?.Id ?? Guid.Empty;
        ButtonAction action = kind switch
        {
            "stop" => new StopTimelineAction { TimelineId = first },
            "endLoop" => new EndLoopAction { TimelineId = first },
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

/// <summary>One action of a button, in the editor.</summary>
public sealed partial class ButtonActionViewModel(ButtonAction action, ShowButtonViewModel button, ButtonsViewModel owner) : ObservableObject
{
    public ButtonAction Action { get; } = action;

    public string Kind => Action switch
    {
        RunTimelineAction => "Run timeline",
        StopTimelineAction => "Stop timeline",
        EndLoopAction => "End loop",
        StopAllAction => "Stop all timelines",
        BlackoutAction => "Blackout",
        _ => "?",
    };

    public bool HasTimeline => Action is RunTimelineAction or StopTimelineAction or EndLoopAction;

    /// <summary>A Run timeline: it has "Play looped" and "Play one only".</summary>
    public bool IsRun => Action is RunTimelineAction;

    /// <summary>Run: the timeline plays in a loop until it's stopped (or a button's or event's End loop).</summary>
    public bool Loop
    {
        get => Action is RunTimelineAction { Loop: true };
        set
        {
            if (Action is RunTimelineAction r && r.Loop != value)
                button.EditAction(Action, value ? "Play looped" : "Play once", () => r.Loop = value);
        }
    }

    /// <summary>Run: nothing happens if the timeline is already playing (otherwise another copy plays).</summary>
    public bool OneOnly
    {
        get => Action is RunTimelineAction { OneOnly: true };
        set
        {
            if (Action is RunTimelineAction r && r.OneOnly != value)
                button.EditAction(Action, value ? "Play one only" : "Play a copy each time", () => r.OneOnly = value);
        }
    }

    public IReadOnlyList<TimelineChoice> Timelines => owner.Timelines;

    Guid TimelineId
    {
        get => Action switch { RunTimelineAction r => r.TimelineId, StopTimelineAction s => s.TimelineId, EndLoopAction e => e.TimelineId, _ => Guid.Empty };
        set
        {
            if (Action is RunTimelineAction r) r.TimelineId = value;
            else if (Action is StopTimelineAction s) s.TimelineId = value;
            else if (Action is EndLoopAction e) e.TimelineId = value;
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
