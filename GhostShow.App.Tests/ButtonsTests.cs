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

    int Copies => _rig.Manager.Running.Count(r => r.TimelineId == _scare.Id);

    [Fact]
    public void Run_timeline_plays_it_without_opening_it_and_again_as_a_copy_unless_play_once_only()
    {
        var scare = Button("Scare 1", new RunTimelineAction { TimelineId = _scare.Id });

        _buttons.Press(scare);
        _buttons.Press(scare);

        Assert.Equal(2, Copies);   // VenueMagic: each press runs it again
        _buttons.Tick();
        Assert.True(scare.IsLit);

        ((RunTimelineAction)scare.Button.Actions[0]).PlayOnceOnly = true;
        _buttons.Press(scare);
        Assert.Equal(2, Copies);
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

        a.SelectedKey = ButtonsViewModel.AllKeys.Single(k => k.Key == "F5");

        Assert.DoesNotContain(b.KeyChoices, k => k.Key == "F5");
        Assert.Contains(a.KeyChoices, k => k.Key == "F5");
        Assert.DoesNotContain(ButtonsViewModel.AllKeys, k => k.Key == "F2");   // F2 renames
        Assert.Equal("F5", a.KeyLabel);
        Assert.True(_buttons.PressKey("F5"));
        Assert.False(_buttons.PressKey("F6"));
        Assert.Equal(1, Copies);
        b.SelectedKey = ButtonsViewModel.AllKeys.Single(k => k.Key == "D7");
        Assert.Equal("7", b.KeyLabel);
    }

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
        second.Actions[0].PlayOnceOnly = true;
        second.Actions[1].MoveEarlierCommand.Execute(null);
        second.MoveEarlierCommand.Execute(null);
        second.DuplicateCommand.Execute(null);
        first.RemoveCommand.Execute(null);

        Assert.Equal(["Second", "Second copy"], _rig.Session.Project.Buttons.Select(b => b.Name));
        Assert.Equal([typeof(BlackoutAction), typeof(RunTimelineAction)], _rig.Session.Project.Buttons[0].Actions.Select(a => a.GetType()));
        var run = (RunTimelineAction)_rig.Session.Project.Buttons[1].Actions[1];
        Assert.Equal((_scare.Id, true), (run.TimelineId, run.PlayOnceOnly));
        Assert.True(_edits >= 7);
        Assert.Equal(_buttons.Buttons.Select(b => b.Button), _rig.Session.Project.Buttons);
    }
}
