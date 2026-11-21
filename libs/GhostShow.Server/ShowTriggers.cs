using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;

namespace GhostShow.Server;

/// <summary>
/// The show's triggers, running: every button's and timeline's, hearing the show computer's inputs (its keys, its serial
/// devices) and pressing buttons and running timelines as each trigger's settings say (see <see cref="TriggerRunner"/>).
/// They run here only (docs/plan-shared-view.md, decision 1); every Buttons panel, the show computer's own included,
/// shows how they stand (<see cref="States"/>, <see cref="Changed"/>) and asks this to press, arm, turn off or reset.
/// Called on the show's thread.
/// </summary>
public sealed class ShowTriggers : ITriggerControl
{
    readonly ShowSession _session;
    readonly IPlayback _playback;
    readonly Action _blackout;
    readonly TimeProvider _time;
    readonly Dictionary<InputTrigger, TriggerRunner> _runners = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<object, ITriggerTarget> _targets = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<object> _learners = [];

    /// <param name="blackout">The Blackout a button's Blackout action does.</param>
    /// <param name="time">The clock for re-arm times and "last fired"; tests pass their own.</param>
    public ShowTriggers(ShowSession session, IPlayback playback, Action blackout, TimeProvider? time = null)
    {
        _session = session;
        _playback = playback;
        _blackout = blackout;
        _time = time ?? TimeProvider.System;
        // A trigger added, removed or edited (here, undone, or on a device): its runner follows, keeping how it stands.
        session.Document.Changed += change =>
        {
            if (change.Ops.Any(o => o.Path.StartsWith("buttons", StringComparison.Ordinal) || o.Path.StartsWith("timelines", StringComparison.Ordinal)))
                Sync();
        };
    }

    DateTimeOffset Now => _time.GetLocalNow();

    /// <summary>Something about the triggers may have changed: every panel shows <see cref="States"/> again.</summary>
    public event Action? Changed;

    /// <inheritdoc/>
    public event Action<InputEvent, string>? InputReceived;

    /// <summary>Whether triggers set things off. On when a show opens; off while working on the show. Not saved.</summary>
    public bool Armed { get; private set; } = true;

    /// <summary>What the last press or trigger did, or why it couldn't.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Whether a serial device is listened to, so a panel waiting for a trigger says a command will do too.</summary>
    public bool SerialListening
    {
        get => _serialListening;
        set
        {
            if (_serialListening == value) return;
            _serialListening = value;
            Changed?.Invoke();
        }
    }

    bool _serialListening;

    /// <summary>A new or opened show: every trigger ready, and armed, so its Start Show key simply works.</summary>
    public void Reload()
    {
        Armed = true;
        _runners.Clear();
        _targets.Clear();
        Sync();
    }

    // ---- the show's triggers, in order: each button's, then each timeline's ----

    IEnumerable<(Guid Owner, List<InputTrigger> Triggers, ITriggerTarget Target)> Lists =>
        _session.Project.Buttons.Select(b => (b.Id, b.Triggers, Target(b)))
            .Concat(_session.Project.Timelines.Select(t => (t.Id, t.Triggers, Target(t))));

    ITriggerTarget Target(object owner)
    {
        if (!_targets.TryGetValue(owner, out var target))
            _targets[owner] = target = owner is ShowButton button ? new ButtonTarget(button, this) : new TimelineTarget((ShowTimeline)owner, this);
        return target;
    }

    TriggerRunner Runner(InputTrigger trigger, ITriggerTarget target)
    {
        if (!_runners.TryGetValue(trigger, out var runner) || runner.Target != target)
            _runners[trigger] = runner = new TriggerRunner(trigger, target);
        return runner;
    }

    IEnumerable<TriggerRunner> All => Lists.SelectMany(l => l.Triggers.Select(t => Runner(t, l.Target))).ToList();

    /// <summary>After a change to the show: runners for the triggers there are, none for those that went.</summary>
    void Sync()
    {
        var owners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var button in _session.Project.Buttons) owners.Add(button);
        foreach (var timeline in _session.Project.Timelines) owners.Add(timeline);
        foreach (var gone in _targets.Keys.Where(o => !owners.Contains(o)).ToList()) _targets.Remove(gone);
        var live = new HashSet<InputTrigger>(All.Select(r => r.Trigger), ReferenceEqualityComparer.Instance);
        foreach (var gone in _runners.Keys.Where(t => !live.Contains(t)).ToList()) _runners.Remove(gone);
        Changed?.Invoke();
    }

    /// <inheritdoc/>
    public TriggerStates States
    {
        get
        {
            var now = Now;
            return new(Armed, Status, SerialListening,
                [.. Lists.SelectMany(l => l.Triggers.Select((t, i) => (Runner: Runner(t, l.Target), l.Owner, Index: i)))
                         .Select(r => new TriggerState(r.Owner, r.Index, r.Runner.IsOn, r.Runner.IsSpent, r.Runner.Describe(now)))]);
        }
    }

    TriggerRunner? Find(Guid owner, int index) =>
        Lists.FirstOrDefault(l => l.Owner == owner) is { Triggers: { } triggers, Target: { } target } && index >= 0 && index < triggers.Count
            ? Runner(triggers[index], target) : null;

    // ---- pressing ----

    /// <summary>Runs a button's actions, in order, as a click on it does. One that can't run (its timeline was deleted) is skipped and reported.</summary>
    public string? Press(Guid buttonId)
    {
        if (_session.Project.Buttons.FirstOrDefault(b => b.Id == buttonId) is not { } button) return "That button isn't in the show any more.";
        Press(button);
        Changed?.Invoke();
        return null;
    }

    void Press(ShowButton button)
    {
        var problems = new List<string>();
        foreach (var action in button.Actions)
        {
            switch (action)
            {
                case RunTimelineAction run:
                    if (Has(run.TimelineId)) _playback.Launch(run.TimelineId, run.Options);
                    else problems.Add("a timeline it runs is gone");
                    break;
                case EndLoopAction endLoop:
                    if (Has(endLoop.TimelineId)) _playback.EndLoop(endLoop.TimelineId);
                    else problems.Add("a timeline it ends the loop of is gone");
                    break;
                case StopTimelineAction stop:
                    if (Has(stop.TimelineId)) _playback.StopTimeline(stop.TimelineId);
                    else problems.Add("a timeline it stops is gone");
                    break;
                case StopAllAction:
                    _playback.StopAll();
                    break;
                case BlackoutAction:
                    _blackout();
                    break;
            }
        }
        Status = problems.Count > 0 ? $"{button.Name}: {string.Join("; ", problems)}." : button.Actions.Count == 0 ? $"{button.Name} has no actions yet." : button.Name;
    }

    bool Has(Guid timelineId) => _session.Project.Timelines.Any(t => t.Id == timelineId);

    // ---- inputs: keys and serial commands, setting off buttons' and timelines' triggers ----

    /// <summary>A serial command, as it arrived: BTN24=On is input BTN24 at On (see <see cref="InputEvent.SplitCommand"/>).</summary>
    public Guid? Command(Guid device, string command)
    {
        var (address, value) = InputEvent.SplitCommand(command);
        return Input(new InputEvent(InputTrigger.Serial, address, IsPressed: true, Now, device, value));
    }

    /// <summary>
    /// An input event from any source (stamped now), handed to every trigger listening to that input, on buttons and
    /// timelines alike: each decides what to do as its settings say. Returns the button it pressed, or null. While a
    /// panel waits for a trigger to learn, a command goes to it instead (see <see cref="InputReceived"/>); keys still work.
    /// </summary>
    public Guid? Input(InputEvent input)
    {
        input = input with { Time = Now };
        var (pressed, outcome) = input.Source != InputTrigger.Keyboard && _learners.Count > 0
            ? (null, "Sent to the trigger being learned")
            : Dispatch(input);
        InputReceived?.Invoke(input, outcome);
        Changed?.Invoke();
        return pressed;
    }

    (Guid? Pressed, string Outcome) Dispatch(InputEvent input)
    {
        var all = All.ToList();
        // A run-once trigger's reset input resets it, whatever else the input does.
        var resets = all.Where(r => r.Trigger.RunOnce && r.Trigger.ResetBy?.IsSetOffBy(input) == true).ToList();
        foreach (var runner in resets) runner.Reset();

        var matches = all.Where(r => r.Trigger.Matches(input)).ToList();
        if (matches.Count == 0)
            return (null, resets.Count > 0 ? $"Reset {string.Join(", ", resets.Select(r => r.Target.Name).Distinct())}"
                        : input.Source == InputTrigger.Keyboard && !input.IsPressed ? "" : "Nothing uses it");

        Guid? pressed = null;
        var outcomes = new List<string>();
        foreach (var runner in matches)
        {
            var (fired, outcome) = runner.Handle(input, Armed);
            if (fired && runner.Target is ButtonTarget button) pressed ??= button.Button.Id;
            if (outcome.Length > 0 && !outcomes.Contains(outcome)) outcomes.Add(outcome);
            if (outcome.StartsWith("Ran ", StringComparison.Ordinal) || outcome.StartsWith("Stopped ", StringComparison.Ordinal)) Status = outcome;
        }
        if (outcomes.Count > 0 || resets.Count > 0) Tick(raise: false);
        return (pressed, string.Join("; ", outcomes));
    }

    /// <summary>A few times a second: triggers re-arm, and fire again while their input is held.</summary>
    public void Tick() => Tick(raise: true);

    void Tick(bool raise)
    {
        var now = Now;
        foreach (var runner in All)
            if (runner.Tick(now, Armed) is { } did) Status = did;
        if (raise) Changed?.Invoke();
    }

    // ---- what panels and devices ask ----

    /// <inheritdoc/>
    public string? SetArmed(bool armed)
    {
        if (armed == Armed) return null;
        Armed = armed;
        if (armed)
            foreach (var runner in All.Where(r => r.IsSpent)) runner.Reset();   // ready for the next group
        Changed?.Invoke();
        return null;
    }

    const string Gone = "That trigger isn't in the show any more.";

    /// <inheritdoc/>
    public string? SetOn(Guid owner, int index, bool on)
    {
        if (Find(owner, index) is not { } runner) return Gone;
        runner.IsOn = on;
        Tick();
        return null;
    }

    /// <inheritdoc/>
    public string? Reset(Guid owner, int index)
    {
        if (Find(owner, index) is not { } runner) return Gone;
        runner.Reset();
        Changed?.Invoke();
        return null;
    }

    static readonly object Devices = new();

    /// <inheritdoc/>
    public void SetDeviceLearning(bool learning) => SetLearning(Devices, learning);

    /// <summary>A panel waits for a trigger to learn (or stopped): while any does, commands go to it instead of setting off triggers.</summary>
    public void SetLearning(object panel, bool learning)
    {
        if (learning) _learners.Add(panel);
        else _learners.Remove(panel);
    }

    // ---- what triggers set off ----

    /// <summary>A button: its triggers press it.</summary>
    sealed class ButtonTarget(ShowButton button, ShowTriggers triggers) : ITriggerTarget
    {
        public ShowButton Button => button;
        public string Name => button.Name;
        IEnumerable<Guid> Runs => button.Actions.OfType<RunTimelineAction>().Select(r => r.TimelineId).Where(triggers.Has);
        public bool CanPlay => button.Actions.OfType<RunTimelineAction>().Any();
        public bool IsPlaying => button.Actions.OfType<RunTimelineAction>().Any(r => triggers._playback.IsRunning(r.TimelineId));

        public string Fire(InputTrigger trigger)
        {
            triggers.Press(button);
            return $"Pressed {Name}";
        }

        public void Stop()
        {
            foreach (var timeline in Runs) triggers._playback.StopTimeline(timeline);
        }

        public void FinishPass()
        {
            foreach (var timeline in Runs) triggers._playback.EndLoop(timeline);
        }
    }

    /// <summary>A timeline: its triggers run it.</summary>
    sealed class TimelineTarget(ShowTimeline timeline, ShowTriggers triggers) : ITriggerTarget
    {
        public string Name => timeline.Name;
        public bool CanPlay => true;
        public bool IsPlaying => triggers._playback.IsRunning(timeline.Id);

        public string Fire(InputTrigger trigger)
        {
            triggers._playback.Launch(timeline.Id, new RunOptions(trigger.Loop));
            return $"Ran {timeline.Name}";
        }

        public void Stop() => triggers._playback.StopTimeline(timeline.Id);
        public void FinishPass() => triggers._playback.EndLoop(timeline.Id);
    }
}
