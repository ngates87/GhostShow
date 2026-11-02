using GhostShow.App.ViewModels;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;

namespace GhostShow.App.Tests;

/// <summary>Making a fixture type the libraries don't have, from the Add fixtures wizard, and using it straight away.</summary>
public class CustomFixtureTypeTests : IDisposable
{
    readonly TestRig _rig = new();
    FixtureSetupViewModel Setup => _rig.Setup;

    public void Dispose() => _rig.Dispose();

    static FunctionOption Function(ChannelFunction function) => CustomProfileEditorViewModel.Functions.Single(f => f.Value == function);

    [Fact]
    public void A_new_type_is_built_channel_by_channel_saved_and_ready_to_place()
    {
        Setup.StartAddCommand.Execute(null);
        Setup.NewProfileCommand.Execute(null);
        var editor = Setup.ProfileEditor!;
        Assert.Equal(SetupStep.EditProfile, Setup.Step);
        Assert.False(editor.SaveCommand.CanExecute(null));   // no model name, no channels yet
        Assert.Contains("model name", editor.Problems);

        editor.Manufacturer = "Chauvet";
        editor.Model = "Hurricane 1000";
        editor.Category = CustomProfileEditorViewModel.Categories.Single(c => c.Value == FixtureCategory.Fog);
        var mode = editor.SelectedMode!;
        mode.Name = "2 channel";
        mode.AddChannelCommand.Execute(Function(ChannelFunction.Generic));
        mode.AddChannelCommand.Execute(Function(ChannelFunction.Intensity));
        mode.Channels[0].Name = "Fog";
        Assert.Equal(["Fog", "Dimmer"], mode.Channels.Select(c => c.Name));
        Assert.Equal([1, 2], mode.Channels.Select(c => c.Number));
        Assert.False(editor.HasProblems);

        editor.SaveCommand.Execute(null);

        // Back in the picker with it picked: Next places it.
        Assert.Equal(SetupStep.ChooseType, Setup.Step);
        var saved = Setup.Browser.Pending!.Profile;
        Assert.Equal(("custom/chauvet/hurricane-1000", "Chauvet", "Hurricane 1000", FixtureCategory.Fog), (saved.Id, saved.Manufacturer, saved.Model, saved.Category!.Value));
        Assert.Same(saved, _rig.Session.Profiles.Find(saved.Id));
        Assert.True(File.Exists(CustomProfiles.PathOf(_rig.Paths, saved.Id)));
        Setup.Browser.ApplyCommand.Execute(null);
        Setup.Map.Place(1);
        Assert.Equal([("Hurricane 1000 1", 1, 2)], _rig.Patched());
    }

    [Fact]
    public void Copy_and_edit_starts_from_a_library_type_without_changing_it()
    {
        Setup.StartAddCommand.Execute(null);
        Setup.Browser.Search = "PAR";
        Setup.Browser.OpenCommand.Execute(Setup.Browser.Tiles.Single(t => t.Profile == TestRig.Par));

        Setup.CopyProfileCommand.Execute(null);
        var editor = Setup.ProfileEditor!;
        Assert.Equal(("Acme", "PAR (custom)"), (editor.Manufacturer, editor.Model));
        Assert.Equal(["5ch", "3ch"], editor.Modes.Select(m => m.Name));
        editor.SelectedMode!.Channels[4].Function = Function(ChannelFunction.Strobe);   // the 5th channel: a strobe
        editor.SaveCommand.Execute(null);

        var saved = Setup.Browser.Pending!.Profile;
        Assert.NotEqual(TestRig.Par.Id, saved.Id);
        Assert.Equal(("Ch 5", ChannelFunction.Strobe), (saved.Modes[0].Channels[4].Name, saved.Modes[0].Channels[4].Function));   // a name from the manual stays
        Assert.Equal(ChannelFunction.Generic, TestRig.Par.Modes[0].Channels[4].Function);
        Assert.Contains("Custom", Setup.Browser.Tiles.Single(t => t.Profile == saved).Detail);
    }

    [Fact]
    public void Editing_a_custom_type_keeps_its_id_and_moves_fixtures_off_a_renamed_mode()
    {
        Setup.StartAddCommand.Execute(null);
        Setup.NewProfileCommand.Execute(null);
        var editor = Setup.ProfileEditor!;
        editor.Model = "Prop";
        editor.SelectedMode!.Name = "Relay";
        editor.SelectedMode.AddChannelCommand.Execute(Function(ChannelFunction.Generic));
        editor.SaveCommand.Execute(null);
        var original = Setup.Browser.Pending!.Profile;
        Setup.Browser.ApplyCommand.Execute(null);
        Setup.Map.Place(10);
        Setup.DoneCommand.Execute(null);

        Setup.StartAddCommand.Execute(null);
        Setup.Browser.Pick(original);
        Assert.True(Setup.Browser.PendingIsCustom);
        Setup.EditCustomProfileCommand.Execute(null);
        editor = Setup.ProfileEditor!;
        Assert.True(editor.IsEditing);
        Assert.False(editor.CanDelete);   // a fixture uses it
        editor.Model = "Coffin prop";
        editor.SelectedMode!.Name = "1 relay";
        editor.SaveCommand.Execute(null);

        var edited = _rig.Session.Profiles.Find(original.Id)!;
        Assert.Equal("Coffin prop", edited.Model);
        var fixture = Assert.Single(_rig.Session.Project.Fixtures);
        Assert.Equal("1 relay", fixture.ModeName);
        Assert.Empty(_rig.Session.Patch.Issues);
    }

    [Fact]
    public void An_unused_custom_type_can_be_deleted_after_confirming()
    {
        Setup.StartAddCommand.Execute(null);
        Setup.NewProfileCommand.Execute(null);
        Setup.ProfileEditor!.Model = "Spare";
        Setup.ProfileEditor.SelectedMode!.AddChannelCommand.Execute(Function(ChannelFunction.Intensity));
        Setup.ProfileEditor.SaveCommand.Execute(null);
        var profile = Setup.Browser.Pending!.Profile;
        Setup.EditCustomProfileCommand.Execute(null);
        var editor = Setup.ProfileEditor!;

        editor.DeleteCommand.Execute(null);
        Assert.True(editor.IsConfirmingDelete);
        Assert.NotNull(_rig.Session.Profiles.Find(profile.Id));
        editor.DeleteCommand.Execute(null);

        Assert.Null(_rig.Session.Profiles.Find(profile.Id));
        Assert.False(File.Exists(CustomProfiles.PathOf(_rig.Paths, profile.Id)));
        Assert.Equal(SetupStep.ChooseType, Setup.Step);
    }

    [Fact]
    public void Problems_are_listed_until_fixed_and_cancel_saves_nothing()
    {
        Setup.StartAddCommand.Execute(null);
        Setup.NewProfileCommand.Execute(null);
        var editor = Setup.ProfileEditor!;
        editor.Model = "X";
        editor.SelectedMode!.AddChannelCommand.Execute(Function(ChannelFunction.Red));
        editor.AddModeCommand.Execute(null);
        Assert.Contains("has no channels", editor.Problems);
        editor.SelectedMode!.Name = editor.Modes[0].Name;
        Assert.Contains("Two modes are called", editor.Problems);
        editor.SelectedMode.Channels.Clear();
        editor.RemoveModeCommand.Execute(null);
        editor.Modes[0].Channels[0].Name = " ";
        Assert.Contains("needs a name", editor.Problems);

        editor.CancelCommand.Execute(null);

        Assert.Equal(SetupStep.ChooseType, Setup.Step);
        Assert.Equal(4, _rig.Session.Profiles.Profiles.Count);
        Assert.False(Directory.Exists(_rig.Paths.UserProfilesDirectory));
    }
}
