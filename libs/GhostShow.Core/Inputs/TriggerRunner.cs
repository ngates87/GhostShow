namespace GhostShow.Core.Inputs;

/// <summary>What a trigger sets off: a show button, or a timeline.</summary>
public interface ITriggerTarget
{
    /// <summary>"Scare", "Rooms/Hallway".</summary>
    string Name { get; }

    /// <summary>Presses the button or runs the timeline, as the trigger says (a timeline's loop). Returns what it did: "Pressed Scare".</summary>
    string Fire(InputTrigger trigger);

    /// <summary>
    /// Whether it starts anything that keeps playing: a timeline does; a button does if one of its actions runs a
    /// timeline. Without, there's nothing to repeat, stop or wait for.
    /// </summary>
    bool CanPlay { get; }

    /// <summary>Whether what it starts is playing: the timeline (any copy), or a timeline the button runs.</summary>
    bool IsPlaying { get; }

    /// <summary>Stops what it starts, each with its release fade.</summary>
    void Stop();

    /// <summary>What it starts finishes the pass it's on, then stops (End loop).</summary>
    void FinishPass();
}

/// <summary>
/// One trigger while the show runs: it hears its input's events and decides what to do, as a prop controller's trigger
/// input does (see <see cref="InputTrigger"/>'s settings), and keeps how it stands: on or off, its input active or at
/// rest, re-arming, spent (run once), when it last fired. None of this is saved: a show opens with every trigger on,
/// at rest and ready. Call it from one thread (the UI's).
/// </summary>
public sealed class TriggerRunner(InputTrigger trigger, ITriggerTarget target)
{
    DateTimeOffset? _readyAt;       // re-arming until then
    bool _waitingForTimelines;      // re-arming until what it started has finished (then the re-arm time)
    bool _firedThisActivation;      // it fired since its input last became active: rest and repeats act only then

    public InputTrigger Trigger { get; } = trigger;
    public ITriggerTarget Target { get; } = target;

    /// <summary>Off: it does nothing until turned back on or the show is reopened. For a sensor that misbehaves mid-show.</summary>
    public bool IsOn { get; set; } = true;

    /// <summary>Its input is active now (a key held, <c>BTN24=On</c> last). Pulses never are.</summary>
    public bool IsActive { get; private set; }

    /// <summary>A <see cref="InputTrigger.RunOnce"/> trigger that has fired, until it's reset.</summary>
    public bool IsSpent { get; private set; }

    public DateTimeOffset? LastFired { get; private set; }

    /// <summary>
    /// An event from its input (one <see cref="InputTrigger.Matches(InputEvent)"/> says is its). Returns whether it
    /// fired, and what came of it in a few words for the input monitor: "Pressed Scare", "Stopped Hallway",
    /// "Scare: re-arms in 2.0 s"; empty when there's nothing to say (a key let go with nothing to stop).
    /// </summary>
    /// <param name="armed">Whether triggers are armed: if not, it only keeps track of its input.</param>
    public (bool Fired, string Outcome) Handle(InputEvent e, bool armed)
    {
        var now = e.Time;
        switch (Trigger.LevelOf(e))
        {
            case InputLevel.Rest:
                if (!IsActive) return (false, "");
                IsActive = false;
                bool ours = _firedThisActivation;
                _firedThisActivation = false;
                if (!armed || !IsOn || !ours || !Target.IsPlaying) return (false, "");
                return Trigger.OnRest switch
                {
                    OnRest.Stop => Stopped(),
                    OnRest.FinishPass => Finishing(),
                    _ => (false, ""),
                };

            case InputLevel.Active:
                // The same state again (BTN24=On twice: a device may repeat it) isn't a new trip: it hasn't rested. A key
                // is: the window drops auto-repeat, so a second key-down means its key-up was missed (focus moved away
                // while it was held), and the key mustn't stay stuck.
                if (IsActive && e.Source != InputTrigger.Keyboard) return (false, "");
                IsActive = true;
                _firedThisActivation = false;
                break;
        }

        if (!armed) return (false, "Triggers are off");
        if (!IsOn) return (false, $"{Target.Name}: this trigger is turned off");
        if (IsSpent) return (false, $"{Target.Name}: ran once, waiting for its reset");
        if (!IsReady(now)) return (false, $"{Target.Name}: {WhyNotReady(now)}");
        if (Target.CanPlay && Target.IsPlaying)
        {
            switch (Trigger.IfPlaying)
            {
                case IfPlaying.Ignore:
                    return (false, $"{Target.Name}: still playing");
                case IfPlaying.Stop:
                    return Stopped();
                case IfPlaying.Restart:
                    Target.Stop();
                    break;
            }
        }
        return (true, Fire(now));

        (bool, string) Stopped()
        {
            Target.Stop();
            return (false, $"Stopped {Target.Name}");
        }

        (bool, string) Finishing()
        {
            Target.FinishPass();
            return (false, $"{Target.Name} finishes its pass");
        }
    }

    string Fire(DateTimeOffset now)
    {
        LastFired = now;
        _firedThisActivation = true;
        if (Trigger.RunOnce) IsSpent = true;
        _waitingForTimelines = Trigger.RearmAfterTimelines && Target.CanPlay;
        _readyAt = _waitingForTimelines ? null : now.AddSeconds(Trigger.RearmSeconds);
        return Target.Fire(Trigger);
    }

    /// <summary>
    /// From the panel's refresh, a few times a second: the re-arm time starts once what it started has finished (if it
    /// waits for that), and a trigger that repeats while its input is held fires again once what it started has
    /// finished. Returns what it did ("Pressed Scare"), or null.
    /// </summary>
    public string? Tick(DateTimeOffset now, bool armed)
    {
        if (_waitingForTimelines && !Target.IsPlaying)
        {
            _waitingForTimelines = false;
            _readyAt = now.AddSeconds(Trigger.RearmSeconds);
        }
        // A repeat belongs to the activation that started it: it doesn't wait for a re-arm or count as another run.
        if (armed && IsOn && IsActive && _firedThisActivation && Trigger.WhileActive == WhileActive.Repeat && Target.CanPlay && !Target.IsPlaying)
        {
            LastFired = now;
            return Target.Fire(Trigger);
        }
        return null;
    }

    /// <summary>A run-once trigger can fire again (its reset input, by hand, or arming the triggers).</summary>
    public void Reset() => IsSpent = false;

    bool IsReady(DateTimeOffset now) => !_waitingForTimelines && (_readyAt is null || now >= _readyAt);

    string WhyNotReady(DateTimeOffset now) =>
        _waitingForTimelines ? "waiting for its timelines to finish" : $"re-arms in {((_readyAt ?? now) - now).TotalSeconds:0.0} s";

    /// <summary>How it stands, for its row: "Ready", "Active", "Re-arms in 2.5 s", "Off", with when it last fired.</summary>
    public string Describe(DateTimeOffset now)
    {
        string state = !IsOn ? "Off"
            : IsSpent ? "Ran once: waiting for its reset"
            : _waitingForTimelines ? "Waiting for its timelines to finish"
            : _readyAt is { } ready && now < ready ? $"Re-arms in {(ready - now).TotalSeconds:0.0} s"
            : IsActive ? "Active"
            : "Ready";
        return LastFired is { } fired ? $"{state} · last fired {fired:HH:mm:ss}" : state;
    }
}
