using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShow.Core.Inputs;

namespace GhostShow.App.ViewModels;

/// <summary>
/// A Buttons panel's way to the show's triggers, which run only on the show computer, in its server (docs/plan-shared-view.md,
/// decision 1): over the connection from a device, in-process on the show computer itself. They send how they stand
/// (<see cref="ButtonsViewModel.ShowStates"/>) and the inputs that arrive (<see cref="ButtonsViewModel.ShowInput"/>).
/// </summary>
public interface IRemoteTriggers
{
    void Press(Guid button);
    void SetArmed(bool armed);
    void SetOn(Guid owner, int index, bool on);
    void Reset(Guid owner, int index);

    /// <summary>This panel is waiting for a trigger (or stopped): the show computer sends it the commands that arrive.</summary>
    void SetLearning(bool learning);

    /// <summary>
    /// An input on the show computer (its keys): its triggers hear it. Returns the button it pressed, or null. Only the
    /// show computer's keyboard fires triggers, so from a device this does nothing.
    /// </summary>
    Guid? Input(InputEvent input) => null;

    /// <summary>The panel that shows them, once it's made: the show computer's own is sent how they stand as it happens.</summary>
    void Attach(ButtonsViewModel panel) { }
}

/// <summary>What has a list of triggers: a show button (they press it), or a timeline (they run it).</summary>
public interface ITriggerOwner
{
    /// <summary>"Scare", "Hallway".</summary>
    string Name { get; }

    /// <summary>The button's or timeline's id.</summary>
    Guid Id { get; }

    /// <summary>The show's list of its triggers.</summary>
    List<InputTrigger> TriggerModels { get; }

    bool IsTimeline { get; }

    /// <summary>An edit to its triggers, as one undo step: the change is made directly, and the show's document records it.</summary>
    /// <param name="only">The one trigger whose settings it changes; null: the list (adding, removing).</param>
    void EditTriggers(string description, Action change, InputTrigger? only = null);
}

/// <summary>
/// A button's or a timeline's triggers, in its editor: one row each, and Add key (or command), which waits for the
/// next key pressed or command received and adds it. The same for both, so they look and work alike.
/// </summary>
public sealed class TriggerListViewModel : ObservableCollection<TriggerViewModel>
{
    readonly ButtonsViewModel _hub;

    internal TriggerListViewModel(ITriggerOwner owner, ButtonsViewModel hub)
    {
        Owner = owner;
        _hub = hub;
        LearnCommand = new RelayCommand(() => hub.ToggleLearning(this));
        Sync();
    }

    internal ITriggerOwner Owner { get; }

    /// <summary>A timeline's: its triggers can say whether it loops.</summary>
    public bool IsTimeline => Owner.IsTimeline;

    public string EmptyLabel => Owner.IsTimeline ? "None yet: buttons and events run it, and its own transport." : "None yet: only a click presses it.";

    /// <summary>After a change to the show: one row per trigger, in order (each keeping how it stands), showing the model.</summary>
    internal void Sync()
    {
        var model = Owner.TriggerModels;
        if (!this.Select(t => t.Trigger).SequenceEqual(model))
        {
            var rows = model.Select(t => this.FirstOrDefault(r => r.Trigger == t) ?? new TriggerViewModel(t, this, _hub)).ToList();
            Clear();
            foreach (var row in rows) Add(row);
        }
        foreach (var row in this) row.Refresh();
    }

    // ---- learning: the next key pressed or command received becomes a trigger ----

    public IRelayCommand LearnCommand { get; }

    /// <summary>Waiting for a key to be pressed (or a command sent), to add it as a trigger.</summary>
    public bool IsLearning
    {
        get => _isLearning;
        internal set
        {
            if (_isLearning == value) return;
            _isLearning = value;
            if (value) Problem = "";
            Notify(nameof(IsLearning));
            Notify(nameof(LearnLabel));
        }
    }

    bool _isLearning;

    public string LearnLabel => !IsLearning ? (_hub.IsSerialListening ? "Add key or command" : "Add key")
                              : _hub.IsSerialListening ? "Press a key or send a command… (Esc cancels)"
                              : "Press a key… (Esc cancels)";

    internal void RefreshLearnLabel() => Notify(nameof(LearnLabel));

    /// <summary>Why the key just pressed couldn't be added; empty when there's nothing to say.</summary>
    public string Problem
    {
        get => _problem;
        set
        {
            if (_problem == value) return;
            _problem = value;
            Notify(nameof(Problem));
        }
    }

    string _problem = "";

    void Notify(string property) => OnPropertyChanged(new PropertyChangedEventArgs(property));

    // ---- editing ----

    /// <summary>Adds a trigger for this input. A serial command with a value (BTN24=On) is active at that value.</summary>
    internal void AddTrigger(string source, string address, Guid? device = null, string? value = null)
    {
        var trigger = new InputTrigger { Source = source, Input = device, Address = address, Value = value };
        Owner.EditTriggers(source == InputTrigger.Keyboard ? "Add key" : "Add trigger", () => Owner.TriggerModels.Add(trigger));
    }

    internal void RemoveTrigger(TriggerViewModel trigger) =>
        Owner.EditTriggers(trigger.Trigger.Source == InputTrigger.Keyboard ? "Remove key" : "Remove trigger",
                           () => Owner.TriggerModels.Remove(trigger.Trigger));

    /// <summary>A change to one trigger's settings (only that trigger is compared).</summary>
    internal void EditTrigger(InputTrigger trigger, string description, Action change) => Owner.EditTriggers(description, change, trigger);
}

/// <summary>
/// One trigger, in its button's or timeline's editor: its settings (saved with the show), and while the show runs, how
/// it stands on the show computer (not saved), where it runs.
/// </summary>
public sealed partial class TriggerViewModel : ObservableObject
{
    readonly TriggerListViewModel _list;
    readonly ButtonsViewModel _hub;

    internal TriggerViewModel(InputTrigger trigger, TriggerListViewModel list, ButtonsViewModel hub)
    {
        Trigger = trigger;
        _list = list;
        _hub = hub;
    }

    public InputTrigger Trigger { get; }

    internal ITriggerOwner Owner => _list.Owner;

    /// <summary>"Key F5", "Ethereal FX “BTN24=On”".</summary>
    public string Label => _hub.Describe(Trigger);

    internal bool IsKey(string key) => Trigger.Matches(InputTrigger.Keyboard, null, key);

    [RelayCommand]
    void Remove() => _list.RemoveTrigger(this);

    void Edit(string description, Action change) => _list.EditTrigger(Trigger, description, change);

    // ---- how it behaves: presets named after what prop controllers call them, or each setting ----

    /// <summary>Its input has states (a key, BTN24=On/Off), so what it does while held and at rest apply; a bare command is a pulse.</summary>
    public bool HasStates => Trigger.HasStates;

    /// <summary>A command with a value: its active value can be changed, and inverted.</summary>
    public bool HasValue => Trigger.Value is not null;

    public bool IsTimeline => _list.IsTimeline;

    public const string LikeAClick = "Like a click", SingleShot = "Single shot", LoopWhileHeld = "Loop while held", Momentary = "Momentary",
                        Retrigger = "Retrigger", Toggle = "Toggle", Custom = "Custom";

    static readonly (string Name, WhileActive WhileActive, OnRest OnRest, IfPlaying IfPlaying, bool NeedsStates, string Says)[] PresetTable =
    [
        (LikeAClick, WhileActive.PlayOnce, OnRest.KeepPlaying, IfPlaying.RunAgain, false, "Each time it's set off, it does what a click does."),
        (SingleShot, WhileActive.PlayOnce, OnRest.KeepPlaying, IfPlaying.Ignore, false, "Plays once; set off again while that's playing, it's ignored."),
        (LoopWhileHeld, WhileActive.Repeat, OnRest.KeepPlaying, IfPlaying.Ignore, true, "Plays, and again each time it finishes, while the input stays active; the last pass finishes."),
        (Momentary, WhileActive.Repeat, OnRest.Stop, IfPlaying.Ignore, true, "Plays only while the input is active: it stops as soon as it rests."),
        (Retrigger, WhileActive.PlayOnce, OnRest.KeepPlaying, IfPlaying.Restart, false, "Set off again while playing, it starts again from the beginning."),
        (Toggle, WhileActive.PlayOnce, OnRest.KeepPlaying, IfPlaying.Stop, false, "Set off once it starts; set off again while it plays, it stops."),
    ];

    /// <summary>The presets that make sense for its input: holding and resting need an input with states.</summary>
    public IReadOnlyList<string> Presets => [.. PresetTable.Where(p => HasStates || !p.NeedsStates).Select(p => p.Name), Custom];

    public string Preset
    {
        get => PresetTable.FirstOrDefault(p => (p.WhileActive, p.OnRest, p.IfPlaying) == (Trigger.WhileActive, Trigger.OnRest, Trigger.IfPlaying)).Name ?? Custom;
        set
        {
            if (value == Preset || PresetTable.FirstOrDefault(p => p.Name == value) is not { Name: not null } preset) return;
            Edit($"Trigger: {value}", () => (Trigger.WhileActive, Trigger.OnRest, Trigger.IfPlaying) = (preset.WhileActive, preset.OnRest, preset.IfPlaying));
        }
    }

    /// <summary>What the preset does, in a sentence.</summary>
    public string PresetDescription => PresetTable.FirstOrDefault(p => p.Name == Preset).Says ?? "Set below.";

    public static IReadOnlyList<string> WhileActiveChoices { get; } = ["Nothing more", "Play it again"];

    /// <summary>If the input is still active when what it started finishes.</summary>
    public string WhileActiveChoice
    {
        get => WhileActiveChoices[(int)Trigger.WhileActive];
        set => SetChoice(WhileActiveChoices, value, (WhileActive)Math.Max(0, WhileActiveChoices.IndexOf(value)), Trigger.WhileActive,
                         v => Trigger.WhileActive = v, "Trigger: while active");
    }

    public static IReadOnlyList<string> OnRestChoices { get; } = ["Keep playing", "Finish the pass, then stop", "Stop"];

    /// <summary>When the input goes back to rest.</summary>
    public string OnRestChoice
    {
        get => OnRestChoices[(int)Trigger.OnRest];
        set => SetChoice(OnRestChoices, value, (OnRest)Math.Max(0, OnRestChoices.IndexOf(value)), Trigger.OnRest, v => Trigger.OnRest = v, "Trigger: at rest");
    }

    public static IReadOnlyList<string> IfPlayingChoices { get; } = ["Run it again", "Ignore it", "Restart it", "Stop it"];

    /// <summary>If it's set off again while what it started is still playing.</summary>
    public string IfPlayingChoice
    {
        get => IfPlayingChoices[(int)Trigger.IfPlaying];
        set => SetChoice(IfPlayingChoices, value, (IfPlaying)Math.Max(0, IfPlayingChoices.IndexOf(value)), Trigger.IfPlaying,
                         v => Trigger.IfPlaying = v, "Trigger: if playing");
    }

    void SetChoice<T>(IReadOnlyList<string> choices, string? picked, T value, T current, Action<T> set, string description) where T : struct, Enum
    {
        // A dropdown being rebuilt hands back null: that's no choice.
        if (picked is null || !choices.Contains(picked) || EqualityComparer<T>.Default.Equals(value, current)) return;
        Edit(description, () => set(value));
    }

    /// <summary>The value that means active ("On"). Emptied, the command is a pulse whatever its value.</summary>
    public string ActiveValue
    {
        get => Trigger.Value ?? "";
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v == Trigger.Value) return;
            Edit("Change active value", () =>
            {
                Trigger.Value = v;
                if (v is null) Trigger.Invert = false;
            });
        }
    }

    /// <summary>Active when the input isn't at its value: a normally closed sensor.</summary>
    public bool Invert
    {
        get => Trigger.Invert;
        set
        {
            if (value == Trigger.Invert) return;
            Edit(value ? "Invert trigger" : "Don't invert trigger", () => Trigger.Invert = value);
        }
    }

    /// <summary>A timeline's: it plays in a loop until stopped. Otherwise once.</summary>
    public bool Loop
    {
        get => Trigger.Loop;
        set
        {
            if (value == Trigger.Loop) return;
            Edit(value ? "Play looped" : "Play once", () => Trigger.Loop = value);
        }
    }

    /// <summary>It fires once, then not until it's reset.</summary>
    public bool RunOnce
    {
        get => Trigger.RunOnce;
        set
        {
            if (value == Trigger.RunOnce) return;
            Edit(value ? "Run once until reset" : "Run every time", () => Trigger.RunOnce = value);
        }
    }

    /// <summary>"Ethereal FX “BTN25=On”", or "None (Reset, or arming the triggers)".</summary>
    public string ResetLabel => Trigger.ResetBy is { } reset ? _hub.Describe(reset) : "No input";

    public bool HasResetInput => Trigger.ResetBy is not null;

    /// <summary>Waiting for the key or command that will reset it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LearnResetLabel))]
    public partial bool IsLearningReset { get; internal set; }

    public string LearnResetLabel => IsLearningReset ? "Press a key or send a command… (Esc cancels)" : "Set reset input";

    [RelayCommand]
    void LearnReset() => _hub.ToggleLearning(this);

    [RelayCommand]
    void ClearReset()
    {
        if (Trigger.ResetBy is not null) Edit("Remove reset input", () => Trigger.ResetBy = null);
    }

    internal void SetReset(InputAddress reset) => Edit("Set reset input", () => Trigger.ResetBy = reset);

    public decimal RearmSeconds
    {
        get => (decimal)Trigger.RearmSeconds;
        set
        {
            double seconds = Math.Max(0, (double)value);
            if (seconds == Trigger.RearmSeconds) return;
            Edit("Change re-arm time", () => Trigger.RearmSeconds = seconds);
        }
    }

    public bool RearmAfterTimelines
    {
        get => Trigger.RearmAfterTimelines;
        set
        {
            if (value == Trigger.RearmAfterTimelines) return;
            Edit(value ? "Re-arm after its timelines" : "Re-arm from firing", () => Trigger.RearmAfterTimelines = value);
        }
    }

    /// <summary>The re-arm settings in a few words: "Re-arms at once", "Re-arms 3 s after its timelines".</summary>
    public string RearmSummary => (Trigger.RearmSeconds, Trigger.RearmAfterTimelines) switch
    {
        (0, false) => "Re-arms at once",
        (0, true) => "Re-arms when its timelines finish",
        (var s, false) => $"Re-arms {s:0.##} s after firing",
        (var s, true) => $"Re-arms {s:0.##} s after its timelines finish",
    };

    /// <summary>Its settings in a few words, for the folded settings: "Single shot · re-arms at once · once until reset".</summary>
    public string Summary => string.Join(" · ", new[]
    {
        Preset, IsTimeline && Trigger.Loop ? "looped" : null, Trigger.Invert ? "inverted" : null, RearmSummary.ToLowerInvariant(),
        Trigger.RunOnce ? "once until reset" : null,
    }.OfType<string>());

    /// <summary>After a change to the show: every setting shows the model.</summary>
    internal void Refresh()
    {
        foreach (var property in (string[])[nameof(Label), nameof(HasStates), nameof(HasValue), nameof(Presets), nameof(Preset), nameof(PresetDescription),
                     nameof(WhileActiveChoice), nameof(OnRestChoice), nameof(IfPlayingChoice), nameof(ActiveValue), nameof(Invert), nameof(Loop), nameof(RunOnce),
                     nameof(ResetLabel), nameof(HasResetInput), nameof(RearmSeconds), nameof(RearmAfterTimelines), nameof(RearmSummary), nameof(Summary)])
            OnPropertyChanged(property);
    }

    // ---- while the show runs (none of this is saved) ----

    /// <summary>Off: does nothing until turned back on or the show is reopened. For a sensor that misbehaves mid-show.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;
            _isOn = value;
            OnPropertyChanged();
            _hub.Remote.SetOn(Owner.Id, _list.IndexOf(this), value);
        }
    }

    bool _isOn = true;

    /// <summary>A run-once trigger that has fired: Reset lets it fire again.</summary>
    [ObservableProperty]
    public partial bool IsSpent { get; private set; }

    [RelayCommand]
    void Reset() => _hub.Remote.Reset(Owner.Id, _list.IndexOf(this));

    /// <summary>"Ready", "Active", "Re-arms in 2.5 s", "Off", with when it last fired.</summary>
    [ObservableProperty]
    public partial string State { get; private set; } = "Ready";

    /// <summary>How it stands on the show computer, where it runs.</summary>
    internal void ShowRemote(TriggerState state)
    {
        if (_isOn != state.IsOn)
        {
            _isOn = state.IsOn;
            OnPropertyChanged(nameof(IsOn));
        }
        IsSpent = state.IsSpent;
        State = state.State;
    }
}

file static class ListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> list, string item)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == item) return i;
        return -1;
    }
}
