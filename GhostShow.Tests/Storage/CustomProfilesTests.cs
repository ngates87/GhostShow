using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;

namespace GhostShow.Tests.Storage;

/// <summary>Custom fixture types: saved as profile files in the user's folder, and loaded back like any other profile.</summary>
public class CustomProfilesTests : IDisposable
{
    readonly TempDirectory _dir = new();
    AppPaths Paths => new(_dir.Path);

    public void Dispose() => _dir.Dispose();

    static FixtureProfile Fogger(string id) => new()
    {
        Id = id, Manufacturer = "Chauvet", Model = "Hurricane 1000", Category = FixtureCategory.Fog,
        Modes =
        [
            new FixtureMode
            {
                Name = "2 channel",
                Channels = [new FixtureChannel { Name = "Fog", Function = ChannelFunction.Generic }, new FixtureChannel { Name = "Shutter", Function = ChannelFunction.Shutter, DefaultValue = 32 }],
            },
        ],
    };

    [Fact]
    public void A_new_id_comes_from_the_names_and_is_unique_in_the_library()
    {
        var library = new FixtureProfileLibrary([Fogger("custom/chauvet/hurricane-1000")]);

        Assert.Equal("custom/chauvet/hurricane-1000-2", CustomProfiles.NewId("Chauvet", "Hurricane 1000", library));
        Assert.Equal("custom/pro-lights/led-par-64", CustomProfiles.NewId("Pro-Lights", "LED Par 64!", library));
        Assert.Equal("custom/custom/fixture", CustomProfiles.NewId("", "???", library));
    }

    [Fact]
    public void A_saved_custom_profile_loads_back_with_the_rest_of_the_library()
    {
        var profile = Fogger(CustomProfiles.NewId("Chauvet", "Hurricane 1000", FixtureProfileLibrary.Empty));

        CustomProfiles.Save(Paths, profile);
        var library = FixtureProfileLoader.Load(Paths, new MachineSettings());

        Assert.True(File.Exists(Path.Combine(_dir.Path, "profiles", "custom", "chauvet", "hurricane-1000.json")));
        var loaded = library.Find(profile.Id)!;
        Assert.True(CustomProfiles.IsCustom(loaded));
        Assert.Equal((FixtureCategory.Fog, "Shutter", ChannelFunction.Shutter, (byte)32),
                     (loaded.Category!.Value, loaded.Modes[0].Channels[1].Name, loaded.Modes[0].Channels[1].Function, loaded.Modes[0].Channels[1].DefaultValue));
        Assert.Empty(library.LoadErrors);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp", SearchOption.AllDirectories));

        CustomProfiles.Delete(Paths, loaded);
        Assert.Null(FixtureProfileLoader.Load(Paths, new MachineSettings()).Find(profile.Id));
    }

    [Fact]
    public void An_invalid_or_non_custom_profile_is_refused()
    {
        var noChannels = new FixtureProfile { Id = "custom/x/y", Model = "Y", Modes = [new FixtureMode { Name = "A", Channels = [] }] };

        Assert.Throws<InvalidDataException>(() => CustomProfiles.Save(Paths, noChannels));
        Assert.Throws<InvalidDataException>(() => CustomProfiles.Save(Paths, Fogger("generic/fog")));
    }

    [Fact]
    public void A_library_can_take_a_profile_in_or_out_without_reloading()
    {
        var library = new FixtureProfileLibrary([Fogger("custom/a/b")]);
        var changed = Fogger("custom/a/b");

        Assert.Same(changed, library.With(changed).Find("custom/a/b"));
        Assert.Single(library.With(changed).Profiles);
        Assert.Null(library.Without("custom/a/b").Find("custom/a/b"));
    }
}
