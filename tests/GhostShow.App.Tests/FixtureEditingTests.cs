using System.Text.Json.Nodes;
using GhostShow.App.ViewModels;
using GhostShow.Core.Editing;

namespace GhostShow.App.Tests;

/// <summary>
/// The Fixtures screen edits through the show's document: every edit can be undone, and the screen follows the show
/// however it changed (an undo, another device), keeping what's on screen.
/// </summary>
public sealed class FixtureEditingTests : IDisposable
{
    readonly TestRig _rig = new();

    FixtureSetupViewModel Setup => _rig.Setup;
    ShowDocument Document => _rig.Session.Document;

    public void Dispose() => _rig.Dispose();

    /// <summary>Two PARs placed at 1 and 20, back on the overview with the last one selected.</summary>
    void PlaceTwoPars()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.Map.Place(20);
        Setup.DoneCommand.Execute(null);
    }

    /// <summary>Operations as another device's edit arrives: as JSON, from the show computer's point of view.</summary>
    void FromAnotherDevice(string description, string json) =>
        Document.Apply(new Transaction(description, [ShowDocument.Decode(JsonNode.Parse(json)!)]), origin: "Laptop");

    [Fact]
    public void Undo_takes_back_an_added_fixture_and_redo_puts_it_back()
    {
        PlaceTwoPars();

        Setup.History.UndoCommand.Execute(null);

        Assert.Equal(["PAR 1"], Setup.Fixtures.Select(f => f.Name));
        Assert.Equal([("PAR 1", 1, 5)], _rig.Patched());

        Setup.History.RedoCommand.Execute(null);

        Assert.Equal(["PAR 1", "PAR 2"], Setup.Fixtures.Select(f => f.Name));
        Assert.Equal([("PAR 1", 1, 5), ("PAR 2", 20, 24)], _rig.Patched());
    }

    [Fact]
    public void Undo_brings_back_a_removed_fixture()
    {
        PlaceTwoPars();

        Setup.RemoveCommand.Execute(Setup.Fixtures[0]);
        Assert.Equal("PAR 2", Setup.Selected?.Name);

        Setup.History.UndoCommand.Execute(null);

        Assert.Equal(["PAR 1", "PAR 2"], Setup.Fixtures.Select(f => f.Name));
        Assert.Equal("PAR 2", Setup.Selected?.Name);   // the selection stays where it was
    }

    [Fact]
    public void Typing_a_name_is_one_undo_step_and_the_screen_follows_the_undo()
    {
        PlaceTwoPars();
        var row = Setup.Fixtures[0];

        row.Name = "F";
        row.Name = "Fr";
        row.Name = "Front";
        Assert.Equal("Front", row.Model.Name);

        var heard = new List<string?>();
        row.PropertyChanged += (_, e) => heard.Add(e.PropertyName);
        Setup.History.UndoCommand.Execute(null);

        Assert.Equal("PAR 1", row.Name);
        Assert.NotEmpty(heard);   // so the text box shows it
    }

    [Fact]
    public void Undoing_a_change_of_address_moves_it_back_on_the_lights_and_on_the_map()
    {
        PlaceTwoPars();
        var row = Setup.Fixtures[0];
        Setup.Selected = row;

        row.StartAddress = 100;
        Assert.Equal([("PAR 2", 20, 24), ("PAR 1", 100, 104)], _rig.Patched());

        Setup.History.UndoCommand.Execute(null);

        Assert.Equal(1, row.StartAddress);
        Assert.Equal([("PAR 1", 1, 5), ("PAR 2", 20, 24)], _rig.Patched());
        Assert.Same(row, Setup.Selected);
    }

    [Fact]
    public void Undo_puts_back_a_fixture_s_mode_and_its_type()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.DoneCommand.Execute(null);
        var row = Setup.Fixtures[0];

        row.SetProfile(TestRig.Fogger, TestRig.Fogger.Modes[0]);
        Assert.Equal(TestRig.Fogger, row.ModeChoice?.Profile);

        Setup.History.UndoCommand.Execute(null);

        Assert.Equal(TestRig.Par, row.Profile);
        Assert.Equal(TestRig.Par, row.ModeChoice?.Profile);
        Assert.Equal("5ch", row.ModeChoice?.Selected.Name);
    }

    [Fact]
    public void Add_in_a_row_is_one_undo_step()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Placement.Quantity = 3;
        Setup.AddInARowCommand.Execute(null);
        Assert.Equal(3, Setup.Fixtures.Count);

        Setup.History.UndoCommand.Execute(null);

        Assert.Empty(Setup.Fixtures);
        Assert.Empty(Setup.Placement.Added);
    }

    [Fact]
    public void Duplicate_is_one_undo_step_and_keeps_the_group()
    {
        PlaceTwoPars();
        Setup.Selected!.Group = "Front";

        Setup.DuplicateCommand.Execute(null);
        Assert.Equal("Front", Setup.Selected?.Group);
        Assert.Equal(3, Setup.Fixtures.Count);

        Setup.History.UndoCommand.Execute(null);

        Assert.Equal(2, Setup.Fixtures.Count);
        Assert.Equal("Front", Setup.Fixtures[1].Group);   // only the copy went
    }

    [Fact]
    public void An_edit_from_another_device_shows_up_without_losing_the_selection_or_the_wizard()
    {
        PlaceTwoPars();
        var selected = Setup.Fixtures[1];
        Setup.Selected = selected;
        var other = Setup.Fixtures[0];
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Assert.True(Setup.IsPlacing);

        FromAnotherDevice("Rename fixture", $$"""{"op":"set","path":"fixtures/{{other.Model.Id}}/name","value":"Laptop's name"}""");

        Assert.Equal("Laptop's name", other.Name);
        Assert.Same(other, Setup.Fixtures[0]);   // the row stayed, so a text box in it keeps its place
        Assert.Same(selected, Setup.Selected);
        Assert.True(Setup.IsPlacing);
    }

    [Fact]
    public void A_fixture_removed_on_another_device_leaves_the_list_and_moves_the_selection()
    {
        PlaceTwoPars();
        var gone = Setup.Fixtures[1];
        Setup.Selected = gone;

        FromAnotherDevice("Remove fixture", $$"""{"op":"remove","path":"fixtures/{{gone.Model.Id}}"}""");

        Assert.Equal(["PAR 1"], Setup.Fixtures.Select(f => f.Name));
        Assert.Equal("PAR 1", Setup.Selected?.Name);
        Assert.Equal([("PAR 1", 1, 5)], _rig.Patched());
    }

    [Fact]
    public void Undo_on_this_screen_leaves_the_timelines_alone()
    {
        PlaceTwoPars();
        var timeline = _rig.Session.Project.Timelines[0];
        Document.Record("Change timeline length", () => timeline.Length = TimeSpan.FromSeconds(9), [ShowDocument.Shallow(timeline)]);

        Setup.History.UndoCommand.Execute(null);

        Assert.Equal(TimeSpan.FromSeconds(9), timeline.Length);
        Assert.Equal(["PAR 1"], Setup.Fixtures.Select(f => f.Name));
    }
}
