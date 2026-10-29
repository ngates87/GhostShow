using GhostShow.App.ViewModels;
using GhostShow.Core.Timeline;

namespace GhostShow.App.Tests;

public class TimelineTreeTests : IDisposable
{
    readonly TimelineRig _rig = new();
    TimelineTreeViewModel Tree => _rig.Editor.Tree;
    Core.Model.Project Project => _rig.Session.Project;

    public void Dispose() => _rig.Dispose();

    TimelineFolderNode Folder(string path) => Tree.AllNodes().OfType<TimelineFolderNode>().Single(f => f.Path == path);
    TimelineNode Node(string treePath) => Tree.AllNodes().OfType<TimelineNode>().Single(n => n.Timeline.TreePath == treePath);

    /// <summary>The tree as text: folders end with '/', children indented.</summary>
    string Shape()
    {
        var lines = new List<string>();
        void Walk(IEnumerable<TimelineTreeNode> nodes, int depth)
        {
            foreach (var n in nodes)
            {
                lines.Add(new string(' ', depth * 2) + n.Name + (n.IsFolder ? "/" : ""));
                Walk(n.Children, depth + 1);
            }
        }
        Walk(Tree.Roots, 0);
        return string.Join("\n", lines);
    }

    void NewFolder(string name, string? inside = null)
    {
        Tree.SelectedNode = inside is null ? null : Folder(inside);
        Tree.NewFolderCommand.Execute(null);
        Assert.True(Tree.Rename(Tree.SelectedNode!, name));
    }

    ShowTimeline NewTimeline(string name, string? inside = null)
    {
        Tree.SelectedNode = inside is null ? null : Folder(inside);
        Tree.NewTimelineCommand.Execute(null);
        Assert.True(Tree.Rename(Tree.SelectedNode!, name));
        return ((TimelineNode)Tree.SelectedNode!).Timeline;
    }

    [Fact]
    public void Folders_come_first_then_timelines_by_name_at_every_level()
    {
        NewFolder("Rooms");
        NewTimeline("Troll room", inside: "Rooms");
        NewTimeline("Attic", inside: "Rooms");
        NewFolder("Effects", inside: "Rooms");
        NewTimeline("Ambient");

        Assert.Equal("""
            Rooms/
              Effects/
              Attic
              Troll room
            Ambient
            Main
            """.ReplaceLineEndings("\n"), Shape());
    }

    [Fact]
    public void A_new_timeline_opens_in_a_tab_and_starts_renaming_with_a_unique_name()
    {
        Tree.NewTimelineCommand.Execute(null);
        Tree.NewTimelineCommand.Execute(null);

        var node = (TimelineNode)Tree.SelectedNode!;
        Assert.Equal("New timeline 2", node.Name);
        Assert.True(node.IsEditing);
        Assert.Same(node.Timeline, _rig.Editor.SelectedTimeline);
        Assert.True(_rig.Dirty);
    }

    [Fact]
    public void Renaming_refuses_invalid_and_taken_names_and_says_why()
    {
        NewTimeline("Scare");
        var main = Node("Main");

        Assert.False(Tree.Rename(main, "scare"));
        Assert.Contains("already", Tree.Message);
        Assert.False(Tree.Rename(main, "a/b"));
        Assert.Contains("contain", Tree.Message);
        Assert.Equal("Main", main.Timeline.Name);

        Assert.True(Tree.Rename(Node("Main"), "MAIN"));   // changing capitals of its own name is fine
        Assert.Null(Tree.Message);
    }

    [Fact]
    public void Rename_in_place_commits_on_enter_and_cancel_keeps_the_name()
    {
        var main = Node("Main");
        Tree.SelectedNode = main;
        Tree.StartRenameCommand.Execute(null);
        main.EditName = "Lobby";
        Tree.CancelRename(main);
        Assert.Equal("Main", main.Timeline.Name);

        Tree.StartRenameCommand.Execute(null);
        Assert.Equal("Main", main.EditName);
        main.EditName = "  Lobby ";
        Assert.True(Tree.CommitRename(main));
        Assert.Equal("Lobby", Node("Lobby").Timeline.Name);
    }

    [Fact]
    public void Renaming_updates_the_open_tab_and_the_transport_name()
    {
        Project.Timelines[0].HasUnsavedChanges = false;   // as if just saved

        Tree.Rename(Node("Main"), "Lobby");

        Assert.Equal("Lobby *", _rig.Editor.Tabs.Single().Title);
        Assert.Equal("Lobby", _rig.Editor.TimelineName);
    }

    [Fact]
    public void Renaming_a_folder_moves_everything_inside_it()
    {
        NewFolder("Rooms");
        NewFolder("Effects", inside: "Rooms");
        var candle = NewTimeline("Candle", inside: "Rooms/Effects");

        Assert.True(Tree.Rename(Folder("Rooms"), "Areas"));

        Assert.Equal("Areas/Effects", candle.Folder);
        Assert.Equal(["Areas", "Areas/Effects"], Project.TimelineFolders);
    }

    [Fact]
    public void A_renamed_or_moved_folder_stays_selected_so_new_items_go_into_it()
    {
        Tree.SelectedNode = null;
        Tree.NewFolderCommand.Execute(null);
        Tree.Rename(Tree.SelectedNode!, "Ambient");
        Tree.NewTimelineCommand.Execute(null);

        Assert.Equal("Ambient", ((TimelineNode)Tree.SelectedNode!).Timeline.Folder);

        NewFolder("Rooms");
        Tree.SelectedNode = Folder("Ambient");   // pressing on it to drag selects it
        Assert.True(Tree.Move(Folder("Ambient"), "Rooms"));
        Assert.Equal("Rooms/Ambient", Assert.IsType<TimelineFolderNode>(Tree.SelectedNode).Path);
    }

    [Fact]
    public void Moving_puts_a_timeline_or_folder_into_another_folder_but_never_a_folder_into_itself()
    {
        NewFolder("Rooms");
        NewFolder("Effects");
        var candle = NewTimeline("Candle", inside: "Effects");

        Assert.True(Tree.Move(Node("Main"), "Rooms"));
        Assert.Equal("Rooms", Project.FindTimeline("Main")!.Folder);

        Assert.True(Tree.Move(Folder("Effects"), "Rooms"));
        Assert.Equal("Rooms/Effects", candle.Folder);

        Assert.False(Tree.Move(Folder("Rooms"), "Rooms/Effects"));
        Assert.Contains("itself", Tree.Message);

        Assert.True(Tree.Move(Node("Rooms/Main"), ""));   // back to the top level
        Assert.Equal("", Project.FindTimeline("Main")!.Folder);
    }

    [Fact]
    public void Move_to_lists_every_other_folder_but_never_a_folder_s_own_inside()
    {
        NewFolder("Rooms");
        NewFolder("Effects", inside: "Rooms");
        NewFolder("Ambient");

        Tree.SelectedNode = Folder("Rooms");
        Assert.Equal(["Ambient"], Tree.MoveTargets.Select(t => t.Label));   // already at the top level; not into itself

        Tree.SelectedNode = Node("Main");
        Assert.Equal(["Ambient", "Rooms", "Rooms/Effects"], Tree.MoveTargets.Select(t => t.Label));
        var target = Tree.MoveTargets.Single(t => t.Folder == "Rooms/Effects");
        target.Move.Execute(target);
        Assert.Equal("Rooms/Effects", Project.FindTimeline("Main")!.Folder);
        Assert.Equal(["(top level)", "Ambient", "Rooms"], Tree.MoveTargets.Select(t => t.Label));
    }

    [Fact]
    public void Moving_refuses_a_name_already_used_in_the_target_folder()
    {
        NewFolder("Rooms");
        NewTimeline("Main", inside: "Rooms");

        Assert.False(Tree.Move(Node("Main"), "Rooms"));
        Assert.Contains("already", Tree.Message);
    }

    [Fact]
    public void Duplicate_copies_with_new_ids_next_to_the_original_and_opens_it()
    {
        var main = Project.FindTimeline("Main")!;
        var sound = _rig.AddSound(1);
        var red = _rig.RedLane();
        _rig.Editor.SelectedTrack = _rig.Track(TrackKind.Lighting);
        _rig.Editor.AddEventCommand.Execute(null);
        _rig.Editor.AddHandle(main.Tracks[0].Events.OfType<LightingClip>().Single(), red.Target, TimeSpan.FromSeconds(2));
        _rig.Editor.PointProperties!.Dmx = 9;
        Tree.SelectedNode = Node("Main");

        Tree.DuplicateCommand.Execute(null);

        var copy = Project.FindTimeline("Main copy")!;
        Assert.NotEqual(main.Id, copy.Id);
        var copiedSound = copy.Tracks.Single(t => t.Kind == TrackKind.Audio).Events.Single();
        Assert.NotEqual(sound.Id, copiedSound.Id);
        Assert.Equal(sound.Start, copiedSound.Start);
        var copiedPoint = copy.Tracks[0].Events.OfType<LightingClip>().Single().Lanes.Single().Points[0];
        Assert.NotSame(main.Tracks[0].Events.OfType<LightingClip>().Single().Lanes.Single().Points[0], copiedPoint);   // editing the copy leaves the original alone
        Assert.Equal(9, copiedPoint.Value);
        Assert.Same(copy, _rig.Editor.SelectedTimeline);
    }

    [Fact]
    public void Deleting_asks_first_then_removes_the_folder_its_timelines_and_their_tabs()
    {
        NewFolder("Rooms");
        var troll = NewTimeline("Troll", inside: "Rooms");
        Assert.Contains(_rig.Editor.Tabs, t => t.Timeline == troll);

        Tree.SelectedNode = Folder("Rooms");
        Tree.AskDeleteCommand.Execute(null);
        Assert.True(Tree.IsConfirmingDelete);
        Assert.Contains("1 timelines", Tree.DeleteQuestion);
        Tree.DeleteCommand.Execute(null);

        Assert.DoesNotContain(troll, Project.Timelines);
        Assert.Empty(Project.TimelineFolders);
        Assert.DoesNotContain(_rig.Editor.Tabs, t => t.Timeline == troll);
        Assert.False(Tree.IsConfirmingDelete);
    }

    [Fact]
    public void Tree_changes_undo_and_redo_one_at_a_time_and_the_tree_follows()
    {
        NewFolder("Rooms");
        var troll = NewTimeline("Troll", inside: "Rooms");
        Assert.True(Tree.Rename(Folder("Rooms"), "Areas"));
        Assert.Equal("Areas/Troll", troll.TreePath);

        _rig.Editor.UndoCommand.Execute(null);   // the folder's rename, with what's inside it
        Assert.Equal("Rooms/Troll", troll.TreePath);
        Assert.Equal(["Rooms"], Project.TimelineFolders);
        Assert.Equal("Rooms/\n  Troll\nMain", Shape());

        Tree.SelectedNode = Folder("Rooms");
        Tree.DeleteCommand.Execute(null);
        Assert.DoesNotContain(troll, Project.Timelines);
        Assert.Equal("Undo Delete folder 'Rooms'", _rig.Editor.UndoLabel);

        _rig.Editor.UndoCommand.Execute(null);   // the delete: the folder and the timeline come back
        Assert.Contains(troll, Project.Timelines);
        Assert.Equal(["Rooms"], Project.TimelineFolders);
        Assert.Contains("ROOMS", Project.TimelineFolders);   // still case-insensitive after the undo replaced the set
        Assert.Equal("Rooms/\n  Troll\nMain", Shape());

        _rig.Editor.RedoCommand.Execute(null);
        Assert.DoesNotContain(troll, Project.Timelines);
        Assert.Empty(Project.TimelineFolders);
    }

    [Fact]
    public void A_deleted_timeline_that_comes_back_can_be_opened()
    {
        NewFolder("Rooms");
        var troll = NewTimeline("Troll", inside: "Rooms");
        Tree.SelectedNode = Node("Rooms/Troll");
        Tree.DeleteCommand.Execute(null);
        _rig.Editor.UndoCommand.Execute(null);

        _rig.Editor.OpenTimeline(troll);
        Assert.Same(troll, _rig.Editor.SelectedTimeline);
    }

    [Fact]
    public void Rebuilding_keeps_open_folders_open_and_the_selection()
    {
        NewFolder("Rooms");
        var troll = NewTimeline("Troll", inside: "Rooms");
        Folder("Rooms").IsExpanded = true;
        Tree.Reveal(troll);

        Tree.Rebuild();

        Assert.True(Folder("Rooms").IsExpanded);
        Assert.Same(troll, ((TimelineNode)Tree.SelectedNode!).Timeline);
    }
}

public class SetupGuideTests : IDisposable
{
    readonly TimelineRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void The_guide_shows_on_the_timeline_until_fixtures_are_patched_and_a_lighting_track_has_channels()
    {
        var settings = new Core.Storage.MachineSettings();
        Core.Engine.ShowSession session = _rig.Session;
        Page? went = null;
        var guide = new SetupGuideViewModel(session, settings, page => went = page, () => { }) { OnTimeline = true };

        guide.Refresh();
        Assert.True(guide.IsVisible);
        Assert.Equal([false, true, false], guide.Steps.Select(s => s.IsDone));   // fixtures are patched in the rig; no adapter, no channels

        guide.Adapter.GoCommand.Execute(null);
        Assert.Equal(Page.DmxSetup, went);

        _rig.RedLane();   // the lighting track gets a channel
        guide.Refresh();
        Assert.False(guide.IsVisible);   // the adapter is optional

        guide.OnTimeline = false;
        guide.Refresh();
        Assert.False(guide.IsVisible);
    }

    [Fact]
    public void Dismissing_hides_the_guide_until_another_show_is_opened()
    {
        var guide = new SetupGuideViewModel(_rig.Session, new Core.Storage.MachineSettings(), _ => { }, () => { }) { OnTimeline = true };
        guide.Refresh();

        guide.DismissCommand.Execute(null);
        Assert.False(guide.IsVisible);

        guide.Reset();
        Assert.True(guide.IsVisible);
    }
}
