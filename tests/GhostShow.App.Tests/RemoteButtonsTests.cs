using GhostShow.App.ViewModels;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Timeline;

namespace GhostShow.App.Tests;

/// <summary>
/// The Buttons panel on a device: the triggers run on the show computer only (docs/plan-shared-view.md, decision 1),
/// so a device's panel asks it to press, arm and reset, and shows how its triggers stand. And the show computer's side:
/// while a device learns a trigger, its commands go there.
/// </summary>
public class RemoteButtonsTests : IDisposable
{
    readonly TimelineRig _rig = new();
    readonly ShowTimeline _scare;
    readonly ShowButton _button;
    readonly Guid _box;

    /// <summary>The show computer, as a device's panel reaches it: records what it was asked.</summary>
    sealed class ShowComputer : IRemoteTriggers
    {
        public readonly List<string> Calls = [];
        public void Press(Guid button) => Calls.Add($"press {button}");
        public void SetArmed(bool armed) => Calls.Add($"armed {armed}");
        public void SetOn(Guid owner, int index, bool on) => Calls.Add($"on {owner} {index} {on}");
        public void Reset(Guid owner, int index) => Calls.Add($"reset {owner} {index}");
        public void SetLearning(bool learning) => Calls.Add($"learning {learning}");
    }

    public RemoteButtonsTests()
    {
        _scare = new ShowTimeline { Name = "Scare", Length = TimeSpan.FromSeconds(10), Tracks = [] };
        _rig.Session.Project.Timelines.Add(_scare);
        var box = new SerialInputDevice { Name = "Ethereal FX" };
        _rig.Session.Project.Inputs.Add(box);
        _box = box.Id;
        _button = new ShowButton
        {
            Name = "Scare",
            Actions = [new RunTimelineAction { TimelineId = _scare.Id }],
            Triggers = [new InputTrigger { Address = "F5" }, new InputTrigger { Source = InputTrigger.Serial, Input = _box, Address = "BTN24", RunOnce = true }],
        };
        _rig.Session.Project.Buttons.Add(_button);
    }

    public void Dispose() => _rig.Dispose();

    ButtonsViewModel Device(ShowComputer showComputer) => new(_rig.Session, _rig.Playback, () => { }, () => { }, remote: showComputer);

    ButtonsViewModel ShowComputerPanel() => new(_rig.Session, _rig.Playback, () => { }, () => { });

    int Copies => _rig.Manager.Running.Count(r => r.TimelineId == _scare.Id);

    [Fact]
    public void A_device_presses_arms_and_resets_on_the_show_computer_and_never_fires_a_trigger_itself()
    {
        var showComputer = new ShowComputer();
        var buttons = Device(showComputer);
        Assert.Empty(showComputer.Calls);   // opening the show doesn't arm anything there

        buttons.Press(buttons.Buttons[0]);
        buttons.TriggersArmed = false;
        var serial = buttons.Buttons[0].Triggers[1];
        serial.IsOn = false;
        serial.ResetCommand.Execute(null);
        Assert.Equal([$"press {_button.Id}", "armed False", $"on {_button.Id} 1 False", $"reset {_button.Id} 1"], showComputer.Calls);
        Assert.Equal(0, Copies);   // the show computer runs it, not this copy of the show

        buttons.TriggersArmed = true;
        showComputer.Calls.Clear();
        buttons.Input(InputTrigger.Keyboard, "F5", isPressed: true);
        buttons.Input(InputTrigger.Serial, "BTN24", isPressed: true, _box);
        buttons.Tick();
        Assert.Equal(0, Copies);
        Assert.Empty(showComputer.Calls);   // the show computer hears its own inputs
    }

    [Fact]
    public void A_device_shows_how_the_show_computer_s_triggers_stand()
    {
        var showComputer = new ShowComputer();
        var buttons = Device(showComputer);

        buttons.ShowStates(new TriggerStates(false, "Ran Scare", true,
            [new TriggerState(_button.Id, 1, false, true, "Off · fired 20:01")]));

        Assert.False(buttons.TriggersArmed);
        Assert.Equal("Ran Scare", buttons.Status);
        Assert.Equal("Add key or command", buttons.Buttons[0].Triggers.LearnLabel);   // a serial device is listened to there
        var serial = buttons.Buttons[0].Triggers[1];
        Assert.Equal((false, true, "Off · fired 20:01"), (serial.IsOn, serial.IsSpent, serial.State));
        Assert.Empty(showComputer.Calls);   // shown, not sent back

        buttons.Reload();   // another show arrived: still as the show computer says, until it says otherwise
        Assert.False(buttons.TriggersArmed);
        Assert.Empty(showComputer.Calls);
    }

    [Fact]
    public void A_device_learns_a_command_that_arrived_on_the_show_computer()
    {
        var showComputer = new ShowComputer();
        var buttons = Device(showComputer);
        var row = buttons.Buttons[0];
        _rig.Session.Document.Record("Remove trigger", () => _button.Triggers.RemoveAt(1), [_button]);

        row.Triggers.LearnCommand.Execute(null);
        Assert.Equal(["learning True"], showComputer.Calls);
        buttons.Command(_box, "BTN30=On");   // sent on by the show computer

        Assert.Equal("Ethereal FX “BTN30=On”", row.Triggers[1].Label);
        Assert.Equal(["learning True", "learning False"], showComputer.Calls);
    }

    [Fact]
    public void While_a_device_learns_the_show_computer_holds_commands_for_it_but_its_keys_still_work()
    {
        var buttons = ShowComputerPanel();
        string? outcome = null;
        buttons.InputReceived += (_, said) => outcome = said;

        buttons.IsDeviceLearning = true;
        buttons.Command(_box, "BTN24");
        Assert.Equal(0, Copies);
        Assert.Equal("Sent to the device waiting for a trigger", outcome);
        buttons.PressKey("F5");
        Assert.Equal(1, Copies);

        buttons.IsDeviceLearning = false;
        buttons.Command(_box, "BTN24");
        Assert.Equal(2, Copies);
    }

    [Fact]
    public void The_show_computer_says_how_each_trigger_stands_by_its_button_and_place()
    {
        var buttons = ShowComputerPanel();
        buttons.Command(_box, "BTN24");   // the run-once trigger fires, and is spent
        buttons.FindTrigger(_button.Id, 0)!.IsOn = false;

        var states = buttons.States;
        Assert.True(states.Armed);
        Assert.Equal([(0, false, false), (1, true, true)], states.Triggers.Where(t => t.Owner == _button.Id).Select(t => (t.Index, t.IsOn, t.IsSpent)));
        Assert.Null(buttons.FindTrigger(_button.Id, 2));
        Assert.Null(buttons.FindTrigger(Guid.NewGuid(), 0));
    }

    [Fact]
    public void An_operator_s_panel_can_t_be_edited()
    {
        var buttons = Device(new ShowComputer());
        buttons.IsEditing = true;

        buttons.CanEdit = false;

        Assert.False(buttons.IsEditing);
    }
}
