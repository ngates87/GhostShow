using GhostShow.App.ViewModels;

namespace GhostShow.App.Tests;

public class AddFixturesWizardTests : IDisposable
{
    readonly TestRig _rig = new();
    FixtureSetupViewModel Setup => _rig.Setup;

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void Starts_in_the_overview()
    {
        Assert.Equal(SetupStep.Overview, Setup.Step);
    }

    [Fact]
    public void Choosing_a_type_moves_to_placing_with_openings_for_its_footprint()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");

        Assert.Equal(SetupStep.Place, Setup.Step);
        Assert.Equal(5, Setup.Map.Footprint);
        Assert.Contains("1-512", Setup.Map.Openings.Select(o => o.ToString()));
    }

    [Fact]
    public void Picking_a_type_without_confirming_changes_nothing()
    {
        Setup.StartAddCommand.Execute(null);
        Setup.Browser.Search = "PAR";
        Setup.Browser.OpenCommand.Execute(Setup.Browser.Tiles.Single(t => t.Profile == TestRig.Par));

        Assert.Equal(SetupStep.ChooseType, Setup.Step);
        Assert.Empty(Setup.Fixtures);
    }

    [Fact]
    public void Each_placement_adds_a_numbered_fixture_at_the_clicked_address()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");

        Setup.Map.Place(1);
        Setup.Map.Place(20);

        Assert.Equal([("PAR 1", 1, 5), ("PAR 2", 20, 24)], _rig.Patched());
        Assert.True(_rig.Dirty);
    }

    [Fact]
    public void Openings_shrink_as_fixtures_are_placed()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");

        Setup.Map.Place(4);

        Assert.Equal(["9-512"], Setup.Map.Openings.Select(o => o.ToString()));   // 1-3 is too small for 5 channels
    }

    [Fact]
    public void Quick_add_places_a_row_back_to_back_in_the_first_gap_that_fits_them_all()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(12);                          // leaves 1-11 free: room for 2, not 3
        Setup.Placement.Quantity = 3;

        Setup.AddInARowCommand.Execute(null);

        Assert.Equal([17, 22, 27], _rig.Patched().Skip(1).Select(f => f.Start));
    }

    [Fact]
    public void Quick_add_that_cannot_fit_adds_nothing_and_says_why()
    {
        _rig.ChooseTypeToAdd(TestRig.Wash, "10ch");
        Setup.Placement.Quantity = 52;   // 520 channels

        Setup.AddInARowCommand.Execute(null);

        Assert.Empty(Setup.Fixtures);
        Assert.Contains("520", Setup.Placement.Status);
    }

    [Fact]
    public void Undo_removes_the_most_recent_fixture()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.Map.Place(10);

        Setup.Placement.UndoLastCommand.Execute(null);

        Assert.Equal([("PAR 1", 1, 5)], _rig.Patched());
    }

    [Fact]
    public void Done_returns_to_the_overview_with_the_last_fixture_selected()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.Map.Place(10);

        Setup.DoneCommand.Execute(null);

        Assert.Equal(SetupStep.Overview, Setup.Step);
        Assert.Equal("PAR 2", Setup.Selected!.Name);
        Assert.Equal(0, Setup.Map.Footprint);
    }

    [Fact]
    public void Cancel_from_choosing_returns_to_the_overview_and_adds_nothing()
    {
        Setup.StartAddCommand.Execute(null);

        Setup.CancelWizardCommand.Execute(null);

        Assert.Equal(SetupStep.Overview, Setup.Step);
        Assert.Empty(Setup.Fixtures);
    }

    [Fact]
    public void Names_continue_after_existing_ones_and_follow_the_prefix()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.Placement.NamePrefix = "Graveyard";
        Setup.Map.Place(10);
        Setup.Map.Place(20);

        Assert.Equal(["PAR 1", "Graveyard 1", "Graveyard 2"], Setup.Fixtures.Select(f => f.Name));
    }

    [Fact]
    public void Change_type_applies_to_the_selected_fixture_and_returns_to_the_overview()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.DoneCommand.Execute(null);

        Setup.ChangeTypeCommand.Execute(null);
        Setup.Browser.Search = "Wash";
        Setup.Browser.OpenCommand.Execute(Setup.Browser.Tiles.Single(t => t.Profile == TestRig.Wash));
        Setup.Browser.ApplyCommand.Execute(null);

        Assert.Equal(SetupStep.Overview, Setup.Step);
        Assert.Equal([("PAR 1", 1, 10)], _rig.Patched());
        Assert.Equal(TestRig.Wash, Setup.Selected!.Profile);
    }

    [Fact]
    public void Duplicate_puts_the_copy_in_the_first_gap_it_fits()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "3ch");
        Setup.Map.Place(1);
        Setup.Map.Place(7);                           // free gap 4-6 fits a 3-channel copy
        Setup.DoneCommand.Execute(null);

        Setup.DuplicateCommand.Execute(null);

        Assert.Equal(("PAR 3", 4, 6), _rig.Patched()[1]);
    }

    [Fact]
    public void Clicking_a_fixture_on_the_map_selects_it()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.Map.Place(10);
        Setup.DoneCommand.Execute(null);

        Setup.Map.Select(Setup.Fixtures[0].Model.Id);

        Assert.Equal("PAR 1", Setup.Selected!.Name);
        Assert.Equal(Setup.Fixtures[0].Model.Id, Setup.Map.SelectedId);
    }

    [Fact]
    public void Dragging_on_the_map_re_addresses_the_fixture()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.DoneCommand.Execute(null);

        Setup.Map.Move(Setup.Fixtures[0].Model.Id, 100);

        Assert.Equal([("PAR 1", 100, 104)], _rig.Patched());
    }

    [Fact]
    public void Overview_map_reports_usage_and_collisions()
    {
        _rig.ChooseTypeToAdd(TestRig.Par, "5ch");
        Setup.Map.Place(1);
        Setup.DoneCommand.Execute(null);
        Setup.DuplicateCommand.Execute(null);
        Setup.Selected!.StartAddress = 3;             // overlaps PAR 1 at 3-5

        Assert.Contains("overlapping: 3-5", Setup.Map.Usage);
        Assert.Contains("3 channels collide", Setup.Map.UsageShort);
    }
}
