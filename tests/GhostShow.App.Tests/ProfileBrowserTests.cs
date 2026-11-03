using GhostShow.App.ViewModels;
using GhostShow.Core.Fixtures;

namespace GhostShow.App.Tests;

public class ProfileBrowserTests
{
    readonly List<(FixtureProfile Profile, FixtureMode Mode)> _applied = [];
    readonly ProfileBrowserViewModel _browser;

    public ProfileBrowserTests()
    {
        _browser = new ProfileBrowserViewModel((p, m) => _applied.Add((p, m)));
        _browser.SetLibrary(new FixtureProfileLibrary([TestRig.Par, TestRig.Wash, TestRig.Fogger, TestRig.Dimmer]));
        _browser.Start(null, null, "Next");
    }

    [Fact]
    public void Manufacturers_differing_only_in_case_are_one_group()
    {
        var groups = _browser.Tiles.Select(t => (t.Title, t.Detail)).ToList();

        Assert.Equal([("Acme", "3 fixtures"), ("Other", "1 fixture")], groups);
    }

    [Fact]
    public void By_type_groups_by_category_in_category_order()
    {
        _browser.ByType = true;
        _browser.BackCommand.Execute(null);

        Assert.Equal(["Dimmers", "Color / RGB", "Fog & smoke"], _browser.Tiles.Select(t => t.Title));
    }

    [Fact]
    public void Opening_a_group_lists_its_profiles()
    {
        _browser.OpenCommand.Execute(_browser.Tiles.Single(t => t.Title == "Acme"));

        Assert.Equal(["Fogger", "PAR", "Wash"], _browser.Tiles.Select(t => t.Title));
        Assert.True(_browser.IsInsideGroup);
    }

    [Fact]
    public void Search_matches_across_groups()
    {
        _browser.Search = "dimmer";

        Assert.Equal(["Dimmer"], _browser.Tiles.Select(t => t.Title));
    }

    [Fact]
    public void Picking_a_profile_waits_for_confirmation_with_its_first_mode_selected()
    {
        _browser.Search = "PAR";
        _browser.OpenCommand.Execute(_browser.Tiles.Single());

        Assert.True(_browser.HasPending);
        Assert.Equal("5ch", _browser.Pending!.Selected.Name);
        Assert.Empty(_applied);
    }

    [Fact]
    public void Confirming_passes_on_the_chosen_mode()
    {
        _browser.Search = "PAR";
        _browser.OpenCommand.Execute(_browser.Tiles.Single());
        _browser.Pending!.Options[1].IsSelected = true;

        _browser.ApplyCommand.Execute(null);

        Assert.Equal((TestRig.Par, TestRig.Par.Modes[1]), Assert.Single(_applied));
        Assert.False(_browser.HasPending);
    }

    [Fact]
    public void Starting_from_a_current_profile_opens_its_group_and_marks_it_in_use()
    {
        _browser.Start(TestRig.Wash, "10ch", "Use this type");

        Assert.Equal("Acme", _browser.OpenGroup);
        Assert.True(_browser.Tiles.Single(t => t.Profile == TestRig.Wash).IsCurrent);
    }
}
