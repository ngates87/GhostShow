using GhostShow.Core.Inputs;

namespace GhostShow.Tests.Inputs;

/// <summary>
/// How a trigger behaves, as a prop controller's trigger modes do, with a sensor box that sends <c>BTN24=On</c> when
/// tripped and <c>BTN24=Off</c> when it rests, a key, and a box that sends a bare <c>GO</c>.
/// </summary>
public class TriggerRunnerTests
{
    static readonly Guid Box = Guid.NewGuid();
    static readonly DateTimeOffset Start = new(2026, 10, 31, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A scare that plays until <see cref="Finish"/> (or a stop), noting what it was told.</summary>
    sealed class Scare(bool canPlay = true) : ITriggerTarget
    {
        public List<string> Log { get; } = [];
        public string Name => "Scare";
        public bool CanPlay => canPlay;
        public bool IsPlaying { get; private set; }

        public string Fire(InputTrigger trigger)
        {
            Log.Add(trigger.Loop ? "fire looped" : "fire");
            IsPlaying = canPlay;
            return "Pressed Scare";
        }

        public void Stop()
        {
            Log.Add("stop");
            IsPlaying = false;
        }

        public void FinishPass() => Log.Add("finish pass");

        public void Finish() => IsPlaying = false;
    }

    readonly Scare _scare = new();
    TimeSpan _clock;

    static InputTrigger Sensor(Action<InputTrigger>? settings = null)
    {
        var trigger = new InputTrigger { Source = InputTrigger.Serial, Input = Box, Address = "BTN24", Value = "On" };
        settings?.Invoke(trigger);
        return trigger;
    }

    TriggerRunner Runner(InputTrigger trigger) => new(trigger, _scare);

    InputEvent Command(string command, double afterSeconds = 0.1)
    {
        _clock += TimeSpan.FromSeconds(afterSeconds);
        var (address, value) = InputEvent.SplitCommand(command);
        return new InputEvent(InputTrigger.Serial, address, true, Start + _clock, Box, value);
    }

    InputEvent Key(bool down, double afterSeconds = 0.1)
    {
        _clock += TimeSpan.FromSeconds(afterSeconds);
        return new InputEvent(InputTrigger.Keyboard, "F5", down, Start + _clock);
    }

    static string Send(TriggerRunner runner, InputEvent e, bool armed = true) => runner.Handle(e, armed).Outcome;

    [Fact]
    public void A_command_with_a_value_is_an_input_and_its_state()
    {
        Assert.Equal(("BTN24", "On"), InputEvent.SplitCommand("BTN24=On"));
        Assert.Equal(("BTN24", "Off"), InputEvent.SplitCommand("BTN24 = Off"));
        Assert.Equal(("BTN24", (string?)null), InputEvent.SplitCommand("BTN24"));
        Assert.Equal(("=On", (string?)null), InputEvent.SplitCommand("=On"));
    }

    [Fact]
    public void A_sensor_fires_once_when_tripped_and_again_only_after_it_rests()
    {
        var runner = Runner(Sensor());

        Assert.Equal("Pressed Scare", Send(runner, Command("BTN24=On")));
        Assert.Equal("", Send(runner, Command("BTN24=On")));     // still tripped: not a new trigger
        _scare.Finish();
        Assert.Equal("", Send(runner, Command("BTN24=Off")));
        Assert.Equal("Pressed Scare", Send(runner, Command("BTN24=On")));

        Assert.Equal(["fire", "fire"], _scare.Log);
    }

    [Fact]
    public void Its_active_value_is_matched_whatever_its_case_and_inverted_for_a_normally_closed_sensor()
    {
        var normal = Runner(Sensor());
        Assert.True(normal.Handle(Command("BTN24=ON"), true).Fired);

        var closed = new TriggerRunner(Sensor(t => t.Invert = true), new Scare());
        Assert.False(closed.Handle(Command("BTN24=On"), true).Fired);
        Assert.True(closed.Handle(Command("BTN24=Off"), true).Fired);
    }

    [Fact]
    public void A_bare_command_is_a_pulse_that_fires_every_time()
    {
        var runner = Runner(new InputTrigger { Source = InputTrigger.Serial, Input = Box, Address = "GO" });

        Assert.True(runner.Handle(Command("GO"), true).Fired);
        Assert.True(runner.Handle(Command("GO"), true).Fired);
        Assert.False(runner.IsActive);
    }

    [Fact]
    public void Repeat_while_active_plays_again_each_time_it_finishes_until_the_input_rests()
    {
        var runner = Runner(Sensor(t => t.WhileActive = WhileActive.Repeat));

        Send(runner, Command("BTN24=On"));
        Assert.Null(runner.Tick(Start + _clock, armed: true));   // still playing
        _scare.Finish();
        Assert.Equal("Pressed Scare", runner.Tick(Start + _clock, armed: true));
        _scare.Finish();
        Send(runner, Command("BTN24=Off"));
        Assert.Null(runner.Tick(Start + _clock, armed: true));

        Assert.Equal(["fire", "fire"], _scare.Log);
    }

    [Fact]
    public void Play_once_does_not_repeat_however_long_the_input_is_held()
    {
        var runner = Runner(Sensor());

        Send(runner, Command("BTN24=On"));
        _scare.Finish();

        Assert.Null(runner.Tick(Start + _clock, armed: true));
        Assert.Equal(["fire"], _scare.Log);
    }

    [Fact]
    public void A_momentary_trigger_stops_what_it_started_when_the_input_rests()
    {
        var runner = Runner(Sensor(t => t.OnRest = OnRest.Stop));

        Send(runner, Command("BTN24=On"));
        Assert.Equal("Stopped Scare", Send(runner, Command("BTN24=Off")));

        Assert.Equal(["fire", "stop"], _scare.Log);
    }

    [Fact]
    public void Finish_the_pass_ends_the_loop_when_the_input_rests()
    {
        var runner = Runner(Sensor(t => t.OnRest = OnRest.FinishPass));

        Send(runner, Command("BTN24=On"));
        Assert.Equal("Scare finishes its pass", Send(runner, Command("BTN24=Off")));

        Assert.Equal(["fire", "finish pass"], _scare.Log);
    }

    [Fact]
    public void Resting_stops_nothing_it_did_not_start()
    {
        var runner = Runner(Sensor(t => t.OnRest = OnRest.Stop));
        Send(runner, Command("BTN24=On"), armed: false);   // tripped while triggers were off: it didn't fire

        Assert.Equal("", Send(runner, Command("BTN24=Off")));

        Assert.Empty(_scare.Log);
    }

    [Fact]
    public void A_key_is_down_then_up_like_a_sensor()
    {
        var runner = Runner(new InputTrigger { Address = "F5", OnRest = OnRest.Stop });

        Assert.Equal("Pressed Scare", Send(runner, Key(down: true)));
        Assert.Equal("Active", runner.Describe(Start + _clock).Split(" · ")[0]);
        Assert.Equal("Stopped Scare", Send(runner, Key(down: false)));
    }

    [Fact]
    public void A_key_pressed_again_without_being_let_go_is_a_new_press_as_its_release_was_missed()
    {
        var runner = Runner(new InputTrigger { Address = "F5" });

        Assert.True(runner.Handle(Key(down: true), true).Fired);
        Assert.True(runner.Handle(Key(down: true), true).Fired);
    }

    [Theory]
    [InlineData(IfPlaying.RunAgain, "Pressed Scare", new[] { "fire", "fire" })]
    [InlineData(IfPlaying.Ignore, "Scare: still playing", new[] { "fire" })]
    [InlineData(IfPlaying.Restart, "Pressed Scare", new[] { "fire", "stop", "fire" })]
    [InlineData(IfPlaying.Stop, "Stopped Scare", new[] { "fire", "stop" })]
    public void Set_off_again_while_playing_it_does_as_set(IfPlaying ifPlaying, string outcome, string[] log)
    {
        var runner = Runner(Sensor(t => t.IfPlaying = ifPlaying));
        Send(runner, Command("BTN24=On"));
        Send(runner, Command("BTN24=Off"));

        Assert.Equal(outcome, Send(runner, Command("BTN24=On")));

        Assert.Equal(log, _scare.Log);
    }

    [Fact]
    public void A_toggle_starts_it_then_stops_it_then_starts_it()
    {
        var runner = Runner(new InputTrigger { Source = InputTrigger.Serial, Input = Box, Address = "GO", IfPlaying = IfPlaying.Stop });

        Assert.Equal(["Pressed Scare", "Stopped Scare", "Pressed Scare"], new[] { Command("GO"), Command("GO"), Command("GO") }.Select(e => Send(runner, e)));
    }

    [Fact]
    public void Once_it_has_finished_what_it_started_it_fires_again_whatever_the_if_playing_setting()
    {
        var runner = Runner(Sensor(t => t.IfPlaying = IfPlaying.Ignore));
        Send(runner, Command("BTN24=On"));
        Send(runner, Command("BTN24=Off"));
        _scare.Finish();

        Assert.Equal("Pressed Scare", Send(runner, Command("BTN24=On")));
    }

    [Fact]
    public void Run_once_fires_once_until_it_is_reset()
    {
        var runner = Runner(Sensor(t => t.RunOnce = true));
        Send(runner, Command("BTN24=On"));
        Send(runner, Command("BTN24=Off"));
        _scare.Finish();

        Assert.Equal("Scare: ran once, waiting for its reset", Send(runner, Command("BTN24=On")));
        Assert.StartsWith("Ran once: waiting for its reset", runner.Describe(Start + _clock));

        runner.Reset();
        Send(runner, Command("BTN24=Off"));
        Assert.Equal("Pressed Scare", Send(runner, Command("BTN24=On")));
    }

    [Fact]
    public void A_reset_input_is_set_off_by_its_command_and_value()
    {
        var reset = new InputAddress { Source = InputTrigger.Serial, Input = Box, Address = "BTN25", Value = "On" };

        Assert.True(reset.IsSetOffBy(Command("BTN25=On")));
        Assert.False(reset.IsSetOffBy(Command("BTN25=Off")));
        Assert.False(reset.IsSetOffBy(Command("BTN24=On")));
        Assert.True(new InputAddress { Address = "F6" }.IsSetOffBy(new InputEvent(InputTrigger.Keyboard, "F6", true, Start)));
        Assert.False(new InputAddress { Address = "F6" }.IsSetOffBy(new InputEvent(InputTrigger.Keyboard, "F6", false, Start)));
    }

    [Fact]
    public void A_re_arm_time_keeps_it_from_firing_again_too_soon()
    {
        var runner = Runner(Sensor(t => t.RearmSeconds = 3));
        Send(runner, Command("BTN24=On"));
        Send(runner, Command("BTN24=Off"));

        Assert.Equal("Scare: re-arms in 2.8 s", Send(runner, Command("BTN24=On")));
        Send(runner, Command("BTN24=Off"));
        Assert.Equal("Pressed Scare", Send(runner, Command("BTN24=On", afterSeconds: 3)));
    }

    [Fact]
    public void Re_arming_after_its_timelines_waits_for_them_to_finish()
    {
        var runner = Runner(Sensor(t => (t.RearmAfterTimelines, t.RearmSeconds) = (true, 1)));
        Send(runner, Command("BTN24=On"));
        Send(runner, Command("BTN24=Off"));
        Assert.Equal("Scare: waiting for its timelines to finish", Send(runner, Command("BTN24=On")));

        _scare.Finish();
        runner.Tick(Start + _clock, armed: true);
        Send(runner, Command("BTN24=Off"));
        Assert.Equal("Scare: re-arms in 0.8 s", Send(runner, Command("BTN24=On")));
    }

    [Fact]
    public void Disarmed_or_off_it_still_follows_its_input_so_it_fires_at_the_next_trip_not_the_next_rest()
    {
        var runner = Runner(Sensor());
        Assert.Equal("Triggers are off", Send(runner, Command("BTN24=On"), armed: false));

        Assert.Equal("", Send(runner, Command("BTN24=Off")));   // armed again here: a rest is no trip
        Assert.Equal("Pressed Scare", Send(runner, Command("BTN24=On")));

        runner.IsOn = false;
        Send(runner, Command("BTN24=Off"));
        Assert.Equal("Scare: this trigger is turned off", Send(runner, Command("BTN24=On")));
        Assert.Equal("Off", runner.Describe(Start + _clock).Split(" · ")[0]);
    }

    [Fact]
    public void A_button_that_plays_nothing_fires_every_time_and_has_nothing_to_repeat()
    {
        var blackout = new Scare(canPlay: false);
        var runner = new TriggerRunner(Sensor(t => (t.WhileActive, t.IfPlaying) = (WhileActive.Repeat, IfPlaying.Ignore)), blackout);

        runner.Handle(Command("BTN24=On"), true);
        Assert.Null(runner.Tick(Start + _clock, armed: true));
        runner.Handle(Command("BTN24=Off"), true);

        Assert.True(runner.Handle(Command("BTN24=On"), true).Fired);
        Assert.Equal(["fire", "fire"], blackout.Log);
    }
}
