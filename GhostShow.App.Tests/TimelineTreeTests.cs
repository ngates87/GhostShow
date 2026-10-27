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
        var marker = _rig.AddMarker(1);
        _rig.Editor.AddPoint(_rig.RedLane(), TimeSpan.FromSeconds(2), 9);
        Tree.SelectedNode = Node("Main");

        Tree.DuplicateCommand.Execute(null);

        var copy = Project.FindTimeline("Main copy")!;
        Assert.NotEqual(main.Id, copy.Id);
        var copiedMarker = copy.Tracks.Single(t => t.Kind == TrackKind.Markers).Events.Single();
        Assert.NotEqual(marker.Id, copiedMarker.Id);
        Assert.Equal(marker.Start, copiedMarker.Start);
        var copiedPoint = copy.Tracks[0].Events.OfType<LightingClip>().Single().Lanes.Single().Points.Single();
        Assert.NotSame(main.Tracks[0].Events.OfType<LightingClip>().Single().Lanes.Single().Points.Single(), copiedPoint);   // editing the copy leaves the original alone
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
