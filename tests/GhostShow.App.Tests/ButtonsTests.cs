using GhostShow.App.ViewModels;
using GhostShow.Core.Model;
using GhostShow.Core.Playback;
using GhostShow.Core.Timeline;

namespace GhostShow.App.Tests;

/// <summary>Show buttons: pressing runs their actions in order; keys; lit while what they run plays; editing.</summary>
public class ButtonsTests : IDisposable
{
    readonly TimelineRig _rig = new();
    readonly ButtonsViewModel _buttons;
    readonly ShowTimeline _scare;
    int _blackouts, _edits;

    public ButtonsTests()
    {
        _scare = new ShowTimeline { Name = "Scare", Folder = "Rooms", Length = TimeSpan.FromSeconds(10), Tracks = [] };
        _rig.Session.Project.Timelines.Add(_scare);
        _buttons = new ButtonsViewModel(_rig.Session, _rig.Playback, () => _blackouts++, () => _edits++);
    }

    public void Dispose() => _rig.Dispose();

    /// <summary>A button with these actions, as if the show was opened with it.</summary>
    ShowButtonViewModel Button(string name, params ButtonAction[] actions)
    {
        _rig.Session.Project.Buttons.Add(new ShowButton { Name = name, Actions = [.. actions] });
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
        _buttons.Tick();
        Assert.True(scare.IsLit);

        _rig.Manager.StopTimeline(_scare.Id);
        _rig.Manager.Tick();
        // An edit, as Timeline properties makes it: playback picks it up.
        _rig.Session.Document.Record("Play one at a time", () => _scare.Playback = _scare.Playback with { OneAtATime = true }, Core.Editing.ShowDocument.Shallow(_scare));
        _buttons.Press(scare);
        _buttons.Press(scare);
        Assert.Equal(1, Copies);   // a timeline that plays one at a time doesn't stack
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
        _buttons.Tick();
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
        if (!button.IsLearningKey) button.LearnKeyCommand.Execute(null);
        _buttons.LearnKey(key, withModifier);
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
        Assert.True(button.IsLearningKey);

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
        Assert.Equal("", second.KeyProblem);
        Assert.True(second.IsLearningKey);

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
            Assert.Contains(problem, second.KeyProblem);
            Assert.True(second.IsLearningKey);
            Assert.Equal("", second.KeyLabel);
        }

        Learn(second, "Q");
        Assert.Equal("Q", second.KeyLabel);
        Assert.False(second.IsLearningKey);
    }

    [Fact]
    public void Escape_or_choosing_another_button_stops_waiting_for_a_key()
    {
        var first = AddButton();
        var second = AddButton();

        Learn(first, "Escape");
        Assert.False(first.IsLearningKey);
        Assert.Equal("", first.KeyLabel);

        _buttons.Selected = first;
        first.LearnKeyCommand.Execute(null);
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
}
