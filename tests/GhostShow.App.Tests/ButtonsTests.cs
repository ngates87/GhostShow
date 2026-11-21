using GhostShow.App.ViewModels;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;
using GhostShow.Server;

namespace GhostShow.App.Tests;

/// <summary>Show buttons: pressing runs their actions in order; keys; lit while what they run plays; editing.</summary>
public class ButtonsTests : IDisposable
{
    readonly TimelineRig _rig = new();
    readonly ButtonsViewModel _buttons;
    readonly ShowTriggers _triggers;
    readonly ShowTimeline _scare;
    readonly FakeTime _time = new();
    int _blackouts, _edits;

    /// <summary>A clock the test moves by hand, for re-arm times and "last fired".</summary>
    sealed class FakeTime : TimeProvider
    {
        DateTimeOffset _now = new(2026, 11, 5, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(double seconds) => _now = _now.AddSeconds(seconds);
    }

    public ButtonsTests()
    {
        _scare = new ShowTimeline { Name = "Scare", Folder = "Rooms", Length = TimeSpan.FromSeconds(10), Tracks = [] };
        _rig.Session.Project.Timelines.Add(_scare);
        _triggers = new ShowTriggers(_rig.Session, _rig.Playback.Show, () => _blackouts++, _time);
        _buttons = new ButtonsViewModel(_rig.Session, _rig.Playback, () => _edits++, new LocalTriggers(_triggers));
    }

    public void Dispose() => _rig.Dispose();

    /// <summary>The refresh: triggers re-arm and repeat (on the show computer), and buttons light.</summary>
    void Tick()
    {
        _triggers.Tick();
        _buttons.Tick();
    }

    /// <summary>A button with these actions, as if the show was opened with it.</summary>
    ShowButtonViewModel Button(string name, params ButtonAction[] actions)
    {
        _rig.Session.Project.Buttons.Add(new ShowButton { Name = name, Actions = [.. actions] });
        _triggers.Reload();
        _buttons.Reload();
        return _buttons.Buttons[^1];
    }

    ShowButtonViewModel AddButton()
    {
        _buttons.AddCommand.Execute(null);
        return _buttons.Buttons[^1];
    }

    int Copies => _rig.Manager.Running.Count(r => r.TimelineId == _scare.Id);

    [Fact]
    public void Run_timeline_plays_it_without_opening_it_and_again_as_a_copy_unless_one_at_a_time()
    {
        var scare = Button("Scare 1", new RunTimelineAction { TimelineId = _scare.Id });

        _buttons.Press(scare);
        _buttons.Press(scare);

        Assert.Equal(2, Copies);   // VenueMagic: each press runs it again
        Tick();
        Assert.True(scare.IsLit);

        _rig.Manager.StopTimeline(_scare.Id);
        _rig.Manager.Tick();
        scare.Actions[0].OneOnly = true;   // the button's "Play one only"
        _buttons.Press(scare);
        _buttons.Press(scare);
        Assert.Equal(1, Copies);   // it doesn't stack
        _rig.Playback.Tick();
        Assert.True(Assert.Single(_rig.Playback.Running).OneOnly);   // and the Playing list shows it

        _rig.Session.Document.UndoLatest(null);
        Assert.False(((RunTimelineAction)scare.Button.Actions[0]).OneOnly);
    }

    [Fact]
    public void A_button_says_whether_its_run_loops_and_End_loop_lets_it_finish_its_pass()
    {
        var run = Button("Ambient", new RunTimelineAction { TimelineId = _scare.Id });
        var end = Button("Calm down", new EndLoopAction { TimelineId = _scare.Id });
        run.Actions[0].Loop = true;
        Assert.True(((RunTimelineAction)run.Button.Actions[0]).Loop);

        _buttons.Press(run);
        _rig.Playback.Tick();
        var row = Assert.Single(_rig.Playback.Running);
        Assert.True((row.Loops, row.OneOnly) == (true, false));

        _buttons.Press(end);
        Assert.False(Assert.Single(_rig.Manager.Running).Status.Loop);   // it plays to its end, then stops
        Assert.Equal("Calm down", _buttons.Status);
    }

    [Fact]
    public void Stop_timeline_stops_every_copy_and_the_other_actions_stop_all_and_black_out()
    {
        var run = Button("Run", new RunTimelineAction { TimelineId = _scare.Id });
        var stop = Button("Stop", new StopTimelineAction { TimelineId = _scare.Id });
        var panic = Button("Panic", new StopAllAction(), new BlackoutAction());
        _buttons.Press(run);
        _buttons.Press(run);

        _buttons.Press(stop);
        _rig.Manager.Tick();

        Assert.False(_rig.Manager.IsRunning(_scare.Id));
        Tick();
        Assert.False(run.IsLit);

        _buttons.Press(run);
        _buttons.Press(panic);
        _rig.Manager.Tick();
        Assert.Empty(_rig.Manager.Running);
        Assert.Equal(1, _blackouts);
    }

    [Fact]
    public void A_key_presses_its_button_and_each_key_belongs_to_one_button()
    {
        var a = Button("A", new RunTimelineAction { TimelineId = _scare.Id });
        var b = Button("B");

        Learn(a, "F5");

        Assert.Equal("F5", a.KeyLabel);
        Assert.True(_buttons.PressKey("F5"));
        Assert.False(_buttons.PressKey("F6"));
        Assert.Equal(1, Copies);
        Learn(b, "D7");
        Assert.Equal("7", b.KeyLabel);
    }

    void Learn(ShowButtonViewModel button, string key, bool withModifier = false)
    {
        if (!button.Triggers.IsLearning) button.Triggers.LearnCommand.Execute(null);
        _buttons.LearnKey(key, withModifier);
    }

    /// <summary>A button that runs the scare, with this key.</summary>
    ShowButtonViewModel ScareOnKey(string key)
    {
        var button = Button("Scare", new RunTimelineAction { TimelineId = _scare.Id });
        Learn(button, key);
        return button;
    }

    void StopScare()
    {
        _rig.Manager.StopTimeline(_scare.Id);
        _rig.Manager.Tick();
        Tick();
    }

    [Fact]
    public void Turned_off_triggers_do_nothing_but_a_click_still_presses_and_reopening_arms_them()
    {
        var button = ScareOnKey("F5");

        _buttons.ToggleArmedCommand.Execute(null);
        Assert.Equal("Triggers off: click to arm", _buttons.ArmedLabel);
        Assert.False(_buttons.HasKey("F5"));   // the key isn't taken, so it does what it would otherwise
        Assert.False(_buttons.PressKey("F5"));
        Assert.Equal(0, Copies);
        _buttons.Press(button);   // a click
        Assert.Equal(1, Copies);

        _triggers.Reload();
        _buttons.Reload();   // the show opened again: armed, not saved as off
        Assert.True(_buttons.TriggersArmed);

        var trigger = _buttons.Buttons.Single().Triggers.Single();
        trigger.IsOn = false;   // just this one
        Assert.False(_buttons.PressKey("F5"));
        Tick();
        Assert.Equal("Off", trigger.State);
        trigger.IsOn = true;
        Assert.True(_buttons.PressKey("F5"));
    }

    [Fact]
    public void A_re_arm_time_stops_a_trigger_firing_again_until_it_has_passed()
    {
        var trigger = ScareOnKey("F5").Triggers.Single();
        trigger.RearmSeconds = 5;
        Assert.Equal("Re-arms 5 s after firing", trigger.RearmSummary);

        Assert.True(_buttons.PressKey("F5"));
        _time.Advance(2);
        Assert.False(_buttons.PressKey("F5"));
        Assert.True(_buttons.HasKey("F5"));   // still the button's key: it's taken, and does nothing yet
        Tick();
        Assert.Equal("Re-arms in 3.0 s · last fired 20:00:00", trigger.State);

        _time.Advance(3);
        Assert.True(_buttons.PressKey("F5"));
        Assert.Equal(2, Copies);
    }

    [Fact]
    public void A_trigger_can_wait_for_its_timelines_to_finish_before_re_arming()
    {
        var trigger = ScareOnKey("F5").Triggers.Single();
        trigger.RearmAfterTimelines = true;
        trigger.RearmSeconds = 1;
        Assert.Equal("Re-arms 1 s after its timelines finish", trigger.RearmSummary);

        Assert.True(_buttons.PressKey("F5"));
        _time.Advance(30);
        Tick();
        Assert.False(_buttons.PressKey("F5"));   // the scare is still playing
        Assert.StartsWith("Waiting for its timelines to finish", trigger.State);

        StopScare();   // finished: now the re-arm time
        Assert.False(_buttons.PressKey("F5"));
        _time.Advance(1);
        Assert.True(_buttons.PressKey("F5"));
        Assert.Equal(1, Copies);   // the first one was stopped
    }

    [Fact]
    public void The_input_monitor_lists_every_input_newest_first_with_what_it_did()
    {
        var monitor = new InputMonitorViewModel(_buttons);
        var trigger = ScareOnKey("F5").Triggers.Single();
        trigger.RearmSeconds = 5;
        void Key(string key, bool pressed = true) => _buttons.Input(InputTrigger.Keyboard, key, pressed);

        Key("F5");
        Key("F5", pressed: false);
        _time.Advance(1);
        Key("F5");
        Key("Q");
        trigger.IsOn = false;
        Key("F5");
        trigger.IsOn = true;
        _buttons.ToggleArmedCommand.Execute(null);
        Key("NumPad1");   // no button has it: that comes first
        Key("F5");

        Assert.Equal(
        [
            ("F5", "pressed", "Pressed Scare"),
            ("F5", "released", ""),
            ("F5", "pressed", "Scare: re-arms in 4.0 s"),
            ("Q", "pressed", "Nothing uses it"),
            ("F5", "pressed", "Scare: this trigger is turned off"),
            ("Num 1", "pressed", "Nothing uses it"),
            ("F5", "pressed", "Triggers are off"),
        ], monitor.Rows.Reverse().Select(r => (r.Address, r.Change, r.Outcome)));
        var first = monitor.Rows[^1];
        Assert.Equal(("20:00:00.00", "Keyboard", true), (first.Time, first.Source, first.PressedAButton));
        Assert.False(monitor.Rows[0].PressedAButton);
        Assert.Equal(1, Copies);
    }

    [Fact]
    public void The_input_monitor_waits_for_timelines_says_so_keeps_the_latest_and_clears()
    {
        var monitor = new InputMonitorViewModel(_buttons);
        ScareOnKey("F5").Triggers.Single().RearmAfterTimelines = true;
        _buttons.PressKey("F5");
        _buttons.PressKey("F5");
        Assert.Equal("Scare: waiting for its timelines to finish", monitor.Rows[0].Outcome);

        for (int i = 0; i < InputMonitorViewModel.Capacity + 5; i++) _buttons.Input(InputTrigger.Keyboard, $"F{i % 12 + 1}", isPressed: false);
        Assert.Equal(InputMonitorViewModel.Capacity, monitor.Rows.Count);
        Assert.Equal("F1", monitor.Rows[0].Address);   // the newest: i = 204

        monitor.ClearCommand.Execute(null);
        Assert.True(monitor.IsEmpty);
    }

    [Fact]
    public void Re_arm_settings_are_undoable_edits_of_the_show()
    {
        var trigger = ScareOnKey("F5").Triggers.Single();

        trigger.RearmSeconds = 2.5m;
        trigger.RearmAfterTimelines = true;

        Assert.Equal((2.5, true), (trigger.Trigger.RearmSeconds, trigger.Trigger.RearmAfterTimelines));
        _rig.Session.Document.UndoLatest(null);
        Assert.False(trigger.RearmAfterTimelines);
        Assert.Equal(2.5m, trigger.RearmSeconds);
    }

    [Fact]
    public void The_focus_warning_shows_only_for_armed_key_triggers_when_another_window_has_the_keyboard()
    {
        _buttons.IsAppFocused = false;
        Assert.False(_buttons.ShowFocusWarning);   // no triggers: nothing to miss

        ScareOnKey("F5");
        Assert.True(_buttons.ShowFocusWarning);

        _buttons.TriggersArmed = false;
        Assert.False(_buttons.ShowFocusWarning);
        _buttons.TriggersArmed = true;
        _buttons.IsAppFocused = true;
        Assert.False(_buttons.ShowFocusWarning);
    }

    [Fact]
    public void Done_finishes_editing_and_the_edit_button_says_so_while_editing()
    {
        var button = AddButton();
        Assert.Equal("Done", _buttons.EditLabel);

        button.DoneCommand.Execute(null);

        Assert.False(_buttons.IsEditing);
        Assert.Null(_buttons.Selected);
        Assert.Equal("Edit", _buttons.EditLabel);
    }

    [Fact]
    public void Keys_that_type_go_to_a_field_and_the_rest_still_press_buttons()
    {
        Assert.All(["Q", "D5", "NumPad5", "Add"], key => Assert.True(TriggerKeys.TypesText(key)));
        Assert.All(["F5", "F13", "PageUp", "Insert", "Pause"], key => Assert.False(TriggerKeys.TypesText(key)));
    }

    [Fact]
    public void A_new_button_opens_its_editor_waiting_for_a_key()
    {
        var button = AddButton();

        Assert.True(_buttons.IsEditing);
        Assert.Same(button, _buttons.Selected);
        Assert.True(button.Triggers.IsLearning);

        _buttons.LearnKey("F5", withModifier: false);
        Assert.Equal("F5", button.KeyLabel);
    }

    [Fact]
    public void A_button_can_have_several_keys_and_each_presses_it()
    {
        var button = AddButton();
        button.AddActionCommand.Execute("run");

        Learn(button, "F5");
        Learn(button, "NumPad1");

        Assert.Equal("F5 · Num 1", button.KeyLabel);
        Assert.Equal(["Key F5", "Key Num 1"], button.Triggers.Select(t => t.Label));
        Assert.True(_buttons.PressKey("NumPad1"));

        button.Triggers[0].RemoveCommand.Execute(null);

        Assert.Equal("Num 1", button.KeyLabel);
        Assert.False(_buttons.PressKey("F5"));
    }

    [Fact]
    public void Learning_a_key_stops_after_a_usable_key_and_waits_on_through_one_it_cannot_use()
    {
        var first = AddButton();
        Learn(first, "F5");
        Assert.False(_buttons.IsLearningKey);

        var second = AddButton();
        Learn(second, "LeftCtrl");   // on the way to a combination: nothing to say yet
        Assert.Equal("", second.Triggers.Problem);
        Assert.True(second.Triggers.IsLearning);

        foreach (var (key, withModifier, problem) in new[]
        {
            ("Q", true, "without Ctrl"),
            ("F2", false, "GhostShow uses F2"),
            ("Space", false, "GhostShow uses Space"),
            ("Delete", false, "GhostShow uses Delete"),
            ("F5", false, "already on " + first.Name),
        })
        {
            Learn(second, key, withModifier);
            Assert.Contains(problem, second.Triggers.Problem);
            Assert.True(second.Triggers.IsLearning);
            Assert.Equal("", second.KeyLabel);
        }

        Learn(second, "Q");
        Assert.Equal("Q", second.KeyLabel);
        Assert.False(second.Triggers.IsLearning);
    }

    [Fact]
    public void Escape_or_choosing_another_button_stops_waiting_for_a_key()
    {
        var first = AddButton();
        var second = AddButton();

        Learn(first, "Escape");
        Assert.False(first.Triggers.IsLearning);
        Assert.Equal("", first.KeyLabel);

        _buttons.Selected = first;
        first.Triggers.LearnCommand.Execute(null);
        Assert.True(_buttons.IsLearningKey);
        _buttons.Selected = second;
        Assert.False(_buttons.IsLearningKey);
    }

    [Fact]
    public void A_new_key_is_an_undoable_edit_and_a_duplicate_does_not_take_it()
    {
        var button = AddButton();
        Learn(button, "F5");

        button.DuplicateCommand.Execute(null);
        Assert.Equal("", _buttons.Buttons[1].KeyLabel);
        _rig.Session.Document.UndoLatest(null);

        _rig.Session.Document.UndoLatest(null);
        Assert.Equal("", button.KeyLabel);
        Assert.Empty(button.Triggers);
        Assert.False(_buttons.PressKey("F5"));

        _rig.Session.Document.RedoLatest(null);
        Assert.Equal("F5", button.KeyLabel);
        Assert.Single(button.Triggers);
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.F1, "F1")]
    [InlineData(Avalonia.Input.Key.F24, "F24")]
    [InlineData(Avalonia.Input.Key.D0, "0")]
    [InlineData(Avalonia.Input.Key.Z, "Z")]
    [InlineData(Avalonia.Input.Key.NumPad9, "Num 9")]
    [InlineData(Avalonia.Input.Key.Add, "Num +")]
    [InlineData(Avalonia.Input.Key.PageUp, "Page Up")]
    [InlineData(Avalonia.Input.Key.PageDown, "Page Down")]
    public void Keys_a_button_can_use_with_their_labels(Avalonia.Input.Key key, string label)
    {
        Assert.True(TriggerKeys.CanUse(key.ToString()));
        Assert.Equal(label, TriggerKeys.Label(key.ToString()));
    }

    [Theory]
    [InlineData(Avalonia.Input.Key.F2)]
    [InlineData(Avalonia.Input.Key.Space)]
    [InlineData(Avalonia.Input.Key.Enter)]
    [InlineData(Avalonia.Input.Key.Escape)]
    [InlineData(Avalonia.Input.Key.Tab)]
    [InlineData(Avalonia.Input.Key.Delete)]
    [InlineData(Avalonia.Input.Key.Back)]
    [InlineData(Avalonia.Input.Key.Home)]
    [InlineData(Avalonia.Input.Key.End)]
    [InlineData(Avalonia.Input.Key.Left)]
    [InlineData(Avalonia.Input.Key.Up)]
    public void Keys_GhostShow_needs_itself_cannot_be_used(Avalonia.Input.Key key) =>
        Assert.False(TriggerKeys.CanUse(key.ToString()));

    [Fact]
    public void A_deleted_timeline_is_reported_not_run()
    {
        var gone = Button("Gone", new RunTimelineAction { TimelineId = Guid.NewGuid() }, new StopAllAction());

        _buttons.Press(gone);

        Assert.Equal("Gone: a timeline it runs is gone.", _buttons.Status);
        Assert.True(gone.Actions[0].IsMissing);
        Assert.Null(gone.Actions[0].Timeline);
    }

    [Fact]
    public void Editing_keeps_the_show_s_buttons_in_step_and_marks_it_unsaved()
    {
        Button("First");
        Button("Second");
        var (first, second) = (_buttons.Buttons[0], _buttons.Buttons[1]);
        _edits = 0;

        second.AddActionCommand.Execute("run");
        second.AddActionCommand.Execute("blackout");
        second.Actions[0].Timeline = _buttons.Timelines.Single(t => t.Path == "Rooms/Scare");
        second.Actions[1].MoveEarlierCommand.Execute(null);
        second.MoveEarlierCommand.Execute(null);
        second.DuplicateCommand.Execute(null);
        first.RemoveCommand.Execute(null);

        Assert.Equal(["Second", "Second copy"], _rig.Session.Project.Buttons.Select(b => b.Name));
        Assert.Equal([typeof(BlackoutAction), typeof(RunTimelineAction)], _rig.Session.Project.Buttons[0].Actions.Select(a => a.GetType()));
        var run = (RunTimelineAction)_rig.Session.Project.Buttons[1].Actions[1];
        Assert.Equal(_scare.Id, run.TimelineId);
        Assert.True(_edits >= 7);
        Assert.Equal(_buttons.Buttons.Select(b => b.Button), _rig.Session.Project.Buttons);
    }

    [Fact]
    public void Button_edits_undo_and_the_panel_follows_and_edits_from_elsewhere_show()
    {
        Button("First");
        var first = _buttons.Buttons[0];
        first.Name = "Scare";
        first.AddActionCommand.Execute("run");
        first.RemoveCommand.Execute(null);
        Assert.Empty(_buttons.Buttons);

        _rig.Editor.UndoCommand.Execute(null);   // the delete: the same button comes back, with its action
        var back = Assert.Single(_buttons.Buttons);
        Assert.Same(first.Button, back.Button);
        Assert.Equal(("Scare", 1), (back.Name, back.Actions.Count));

        _rig.Editor.UndoCommand.Execute(null);   // the action
        Assert.Empty(back.Actions);
        _rig.Editor.UndoCommand.Execute(null);   // the rename
        Assert.Equal("First", back.Name);

        var document = _rig.Session.Document;
        document.Apply(new Core.Editing.Transaction("Rename button",
            new Core.Editing.SetOp(Core.Editing.ShowPath.Button(back.Button.Id).Then("name"), "From the iPad")), origin: "iPad");
        Assert.Equal("From the iPad", back.Name);
    }

    /// <summary>One of the show's serial input devices (the Inputs screen adds them).</summary>
    Guid Device(string name)
    {
        var device = new SerialInputDevice { Name = name };
        _rig.Session.Project.Inputs.Add(device);
        return device.Id;
    }

    /// <summary>Gives a button a trigger for a device's command, as an edit (another device could have made it).</summary>
    void Listen(ShowButtonViewModel button, Guid device, string command) =>
        _rig.Session.Document.Record("Add trigger", () => button.Button.Triggers.Add(new InputTrigger { Source = InputTrigger.Serial, Input = device, Address = command }), [button.Button]);

    [Fact]
    public void A_serial_command_is_learned_while_a_button_waits_then_presses_it()
    {
        var monitor = new InputMonitorViewModel(_buttons);
        var ethereal = Device("Ethereal FX");
        var scare = Button("Scare", new RunTimelineAction { TimelineId = _scare.Id });
        var other = Button("Other");
        scare = _buttons.Buttons[0];
        void Serial(string command) => _buttons.Input(InputTrigger.Serial, command, isPressed: true, ethereal);

        Assert.Equal("Add key", scare.Triggers.LearnLabel);
        _buttons.IsSerialListening = true;   // the Inputs screen says a serial device is being listened to
        Assert.Equal("Add key or command", scare.Triggers.LearnLabel);
        scare.Triggers.LearnCommand.Execute(null);
        Assert.Equal("Press a key or send a command… (Esc cancels)", scare.Triggers.LearnLabel);

        Serial("BTN24");
        Assert.False(_buttons.IsLearningKey);
        var trigger = Assert.Single(scare.Triggers);
        Assert.Equal("Ethereal FX “BTN24”", trigger.Label);
        Assert.Equal(ethereal, trigger.Trigger.Input);   // it listens to the device the command came from
        Assert.Equal(0, Copies);   // learning it doesn't press it

        Serial("BTN24");
        Serial("BTN25");
        Assert.Equal(1, Copies);

        // One command, one button.
        other.Triggers.LearnCommand.Execute(null);
        Serial("BTN24");
        Assert.True(other.Triggers.IsLearning);
        Assert.Equal("Ethereal FX “BTN24” is already on Scare.", other.Triggers.Problem);
        Assert.Empty(other.Triggers);

        Assert.Equal(
        [
            ("Ethereal FX", "BTN24", "received", "Learned by Scare"),
            ("Ethereal FX", "BTN24", "received", "Pressed Scare"),
            ("Ethereal FX", "BTN25", "received", "Nothing uses it"),
            ("Ethereal FX", "BTN24", "received", "Not learned: it's already on Scare"),
        ], monitor.Rows.Reverse().Select(r => (r.Source, r.Address, r.Change, r.Outcome)));
    }

    [Fact]
    public void The_same_command_from_two_devices_presses_different_buttons()
    {
        var hall = Device("Hall box");
        var ward = Device("Ward box");
        var hallScare = Button("Hall scare", new RunTimelineAction { TimelineId = _scare.Id });
        var wardScare = Button("Ward scare");
        Listen(hallScare, hall, "GO");
        Listen(wardScare, ward, "GO");

        Assert.Equal("Ward scare", _buttons.Input(InputTrigger.Serial, "GO", isPressed: true, ward)?.Name);
        Assert.Equal("Hall scare", _buttons.Input(InputTrigger.Serial, "GO", isPressed: true, hall)?.Name);
        Assert.Equal("Ward box “GO”", _buttons.Buttons.Single(b => b.Name == "Ward scare").Triggers.Single().Label);
    }

    [Fact]
    public void A_trigger_follows_its_device_s_name_and_says_when_it_s_gone()
    {
        var box = Device("COM5");
        var scare = Button("Scare");
        Listen(scare, box, "BTN24");
        var device = _rig.Session.Project.Inputs.Single();
        var document = _rig.Session.Document;

        document.Record("Rename input", () => device.Name = "Ethereal FX", [Core.Editing.ShowDocument.Shallow(device)]);
        Assert.Equal("Ethereal FX “BTN24”", scare.Triggers.Single().Label);

        document.Record("Remove input", () => _rig.Session.Project.Inputs.Remove(device), [Core.Editing.ShowDocument.Shallow(_rig.Session.Project)]);
        Assert.Equal("A removed input “BTN24”", scare.Triggers.Single().Label);
    }

    [Fact]
    public void Serial_commands_show_their_control_characters()
    {
        var monitor = new InputMonitorViewModel(_buttons);
        var box = Device("Box");
        _buttons.Input(InputTrigger.Serial, "GO\u001b1", isPressed: true, box);
        Assert.Equal("GO[1B]1", monitor.Rows.Single().Address);
        Assert.Equal("Box “GO[1B]1”", _buttons.Describe(InputTrigger.Serial, box, "GO\u001b1"));
    }

    // ---- triggers with states, how they behave, and timelines' triggers ----

    void Released(string key) => _buttons.Input(InputTrigger.Keyboard, key, isPressed: false);

    [Fact]
    public void A_sensor_s_on_and_off_are_one_input_learned_from_its_on_and_a_momentary_trigger_stops_the_scare_at_rest()
    {
        var monitor = new InputMonitorViewModel(_buttons);
        var ethereal = Device("Ethereal FX");
        var scare = Button("Scare", new RunTimelineAction { TimelineId = _scare.Id });
        scare.Triggers.LearnCommand.Execute(null);

        _buttons.Command(ethereal, "BTN24=Off");   // let go just as Learn was clicked: that's its rest, not what trips it
        Assert.Empty(scare.Triggers);
        _buttons.Command(ethereal, "BTN24=On");
        var trigger = Assert.Single(scare.Triggers);
        Assert.Equal(("BTN24", "On"), (trigger.Trigger.Address, trigger.Trigger.Value));
        Assert.Equal("Ethereal FX “BTN24=On”", trigger.Label);

        trigger.Preset = TriggerViewModel.Momentary;
        Assert.Equal((WhileActive.Repeat, OnRest.Stop, IfPlaying.Ignore), (trigger.Trigger.WhileActive, trigger.Trigger.OnRest, trigger.Trigger.IfPlaying));
        _buttons.Command(ethereal, "BTN24=On");
        _buttons.Command(ethereal, "BTN24=On");   // sent again while still tripped: not another trip
        Assert.Equal(1, Copies);
        _buttons.Command(ethereal, "BTN24=Off");
        _rig.Manager.Tick();
        Assert.False(_rig.Manager.IsRunning(_scare.Id));

        Assert.Equal(
        [
            ("BTN24=Off", "Not learned: Off looks like it's at rest; trip it"),
            ("BTN24=On", "Learned by Scare"),
            ("BTN24=On", "Pressed Scare"),
            ("BTN24=On", ""),
            ("BTN24=Off", "Stopped Scare"),
        ], monitor.Rows.Reverse().Select(r => (r.Address, r.Outcome)));
    }

    [Fact]
    public void Loop_while_held_plays_the_scare_again_each_time_it_finishes_until_the_sensor_rests()
    {
        var box = Device("Box");
        var scare = Button("Scare", new RunTimelineAction { TimelineId = _scare.Id });
        Listen(scare, box, "BTN24");
        var trigger = scare.Triggers.Single();
        trigger.ActiveValue = "On";
        trigger.Preset = TriggerViewModel.LoopWhileHeld;

        _buttons.Command(box, "BTN24=On");
        StopScare();   // it finished, still tripped: again
        Assert.Equal(1, Copies);
        Assert.StartsWith("Active · last fired", trigger.State);

        _buttons.Command(box, "BTN24=Off");
        StopScare();   // finished at rest: that's all
        Assert.Equal(0, Copies);
    }

    [Fact]
    public void A_normally_closed_sensor_is_inverted_and_a_bare_command_offers_only_what_a_pulse_can_do()
    {
        var box = Device("Box");
        var scare = Button("Scare", new RunTimelineAction { TimelineId = _scare.Id });
        Listen(scare, box, "DOOR");
        var trigger = scare.Triggers.Single();
        Assert.False(trigger.HasStates);
        Assert.DoesNotContain(TriggerViewModel.Momentary, trigger.Presets);

        trigger.ActiveValue = "Closed";
        trigger.Invert = true;
        Assert.Equal("Box “DOOR” not Closed", trigger.Label);
        Assert.True(trigger.HasStates);
        Assert.Contains(TriggerViewModel.Momentary, trigger.Presets);

        Assert.Null(_buttons.Command(box, "DOOR=Closed"));
        Assert.Equal("Scare", _buttons.Command(box, "DOOR=Open")?.Name);
    }

    [Fact]
    public void A_toggle_starts_the_scare_and_stops_it_and_a_retrigger_starts_it_again()
    {
        var trigger = ScareOnKey("F5").Triggers.Single();
        trigger.Preset = TriggerViewModel.Toggle;

        _buttons.PressKey("F5");
        Released("F5");
        Assert.False(_buttons.PressKey("F5"));
        _rig.Manager.Tick();
        Assert.Equal(0, Copies);

        Released("F5");
        trigger.Preset = TriggerViewModel.Retrigger;
        _buttons.PressKey("F5");
        Released("F5");
        Assert.True(_buttons.PressKey("F5"));
        _rig.Manager.Tick();
        Assert.Equal(1, Copies);   // the first stopped, it started again
    }

    [Fact]
    public void Run_once_waits_for_its_reset_input_or_for_the_triggers_to_be_armed_again()
    {
        var monitor = new InputMonitorViewModel(_buttons);
        var trigger = ScareOnKey("F5").Triggers.Single();
        trigger.RunOnce = true;
        trigger.LearnResetCommand.Execute(null);
        Assert.Equal("Press a key or send a command… (Esc cancels)", trigger.LearnResetLabel);
        _buttons.LearnKey("F6", withModifier: false);
        Assert.Equal("Key F6", trigger.ResetLabel);
        Assert.Contains("once until reset", trigger.Summary);

        Assert.True(_buttons.PressKey("F5"));
        Released("F5");
        Assert.False(_buttons.PressKey("F5"));
        Assert.True(trigger.IsSpent);
        Assert.StartsWith("Ran once: waiting for its reset", trigger.State);

        _buttons.PressKey("F6");
        Assert.Equal("Reset Scare", monitor.Rows[0].Outcome);
        Assert.True(_buttons.PressKey("F5"));

        _buttons.TriggersArmed = false;
        _buttons.TriggersArmed = true;
        Assert.False(trigger.IsSpent);
    }

    [Fact]
    public void A_timeline_has_triggers_of_its_own_that_run_it_saved_with_it_and_undone_like_its_other_edits()
    {
        var monitor = new InputMonitorViewModel(_buttons);
        var button = Button("Other");   // (as if the show was opened with it: before the timeline's trigger is made)
        var triggers = _buttons.TriggersFor(_scare)!;
        Assert.True(triggers.IsTimeline);
        triggers.LearnCommand.Execute(null);
        _buttons.LearnKey("F7", withModifier: false);

        var trigger = Assert.Single(triggers);
        Assert.Equal("F7", Assert.Single(_scare.Triggers).Address);
        Assert.Null(_buttons.PressKey("F7") ? "a button" : null);   // no button: the timeline itself
        Assert.Equal(1, Copies);
        Assert.Equal("Ran Scare", monitor.Rows[0].Outcome);
        Assert.True(monitor.Rows[0].PressedAButton);
        Assert.True(_buttons.HasKey("F7"));

        trigger.Loop = true;
        Released("F7");
        _rig.Manager.StopTimeline(_scare.Id);
        _rig.Manager.Tick();
        _buttons.PressKey("F7");
        Assert.True(_rig.Manager.Running.Single(r => r.TimelineId == _scare.Id).Status.Loop);

        // One input, one thing: a button can't have it too.
        button.Triggers.LearnCommand.Execute(null);
        _buttons.LearnKey("F7", withModifier: false);
        Assert.Equal("F7 is already on Scare.", button.Triggers.Problem);
        _buttons.CancelLearningKey();

        var document = _rig.Session.Document;
        document.Undo(_scare.Id);   // Play looped
        document.Undo(_scare.Id);   // the trigger
        Assert.Empty(_scare.Triggers);
        Assert.Empty(triggers);
        Assert.False(_buttons.HasKey("F7"));
    }

    [Fact]
    public void Reloading_the_panel_keeps_a_timeline_s_trigger_list_that_Timeline_properties_may_be_showing()
    {
        _scare.Triggers.Add(new InputTrigger { Address = "F7" });
        var shown = _buttons.TriggersFor(_scare)!;

        _triggers.Reload();
        _buttons.Reload();

        Assert.Same(shown, _buttons.TriggersFor(_scare));
        Assert.Equal(1, _buttons.PressKey("F7") ? 0 : Copies);
    }

    [Fact]
    public void A_duplicated_timeline_takes_none_of_its_triggers()
    {
        _scare.Triggers.Add(new InputTrigger { Address = "F7" });

        Assert.Empty(_scare.Clone("Scare copy").Triggers);
    }
}
