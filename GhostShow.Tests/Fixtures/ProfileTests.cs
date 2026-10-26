using System.Xml.Linq;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;

namespace GhostShow.Tests.Fixtures;

public class BuiltInProfileTests
{
    [Fact]
    public void Every_built_in_profile_loads_and_validates()
    {
        var profiles = FixtureProfileLoader.LoadBuiltIn();

        Assert.Contains(profiles, p => p.Id == "generic/rgbw");
        Assert.All(profiles, p => Assert.Empty(FixtureProfileValidator.Validate(p)));
    }

    [Fact]
    public void Profile_json_round_trips()
    {
        var original = TestProfiles.Spot;

        var copy = FixtureProfileLoader.ParseJson(FixtureProfileLoader.ToJson(original));

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(ChannelFunction.Shutter, copy.Modes[0].Channels[1].Function);
        Assert.Equal(32, copy.Modes[0].Channels[1].DefaultValue);
    }

    [Theory]
    [InlineData("""{ "model": "No id", "modes": [] }""")]
    [InlineData("""{ "id": "x", "model": "Bad function", "modes": [ { "name": "1", "channels": [ { "name": "A", "function": "Smoke" } ] } ] }""")]
    [InlineData("not json")]
    public void Malformed_profile_json_is_rejected_with_a_message(string json)
    {
        var ex = Assert.Throws<InvalidDataException>(() => FixtureProfileLoader.ParseJson(json));
        Assert.False(string.IsNullOrEmpty(ex.Message));
    }

    [Fact]
    public void Profile_without_modes_or_channels_fails_validation()
    {
        var profile = new FixtureProfile
        {
            Id = "x/y", Model = "Y",
            Modes = [new FixtureMode { Name = "Empty", Channels = [] }, new FixtureMode { Name = "empty", Channels = [] }],
        };

        var problems = FixtureProfileValidator.Validate(profile);

        Assert.Contains(problems, p => p.Contains("no channels"));
        Assert.Contains(problems, p => p.Contains("more than one mode"));
    }

    [Fact]
    public void Broken_profile_files_are_listed_and_do_not_stop_the_others_loading()
    {
        using var dir = new TempDirectory();
        File.WriteAllText(dir.File("good.json"), FixtureProfileLoader.ToJson(TestProfiles.Rgbw));
        File.WriteAllText(dir.File("bad.json"), "{ nope");
        File.WriteAllText(dir.File("notes.txt"), "ignored");
        var profiles = new List<FixtureProfile>();
        var errors = new List<ProfileLoadError>();

        FixtureProfileLoader.LoadDirectory(dir.Path, profiles, errors);

        Assert.Single(profiles);
        Assert.EndsWith("bad.json", Assert.Single(errors).Source);
    }

    [Fact]
    public void Later_sources_replace_profiles_with_the_same_id()
    {
        var replacement = new FixtureProfile { Id = "TEST/RGBW", Model = "Mine", Modes = TestProfiles.Rgbw.Modes };

        var library = new FixtureProfileLibrary([TestProfiles.Rgbw, replacement]);

        Assert.Equal("Mine", library.Find("test/rgbw")!.Model);
    }
}

public class QlcFixtureImporterTests
{
    const string Eurolite = """
        <?xml version='1.0' encoding='UTF-8'?>
        <!DOCTYPE FixtureDefinition>
        <FixtureDefinition xmlns="http://www.qlcplus.org/FixtureDefinition">
         <Manufacturer>Eurolite</Manufacturer>
         <Model>LED PARty RGBW</Model>
         <Type>Color Changer</Type>
         <Channel Name="Red" Preset="IntensityRed"/>
         <Channel Name="Green" Preset="IntensityGreen"/>
         <Channel Name="Blue" Preset="IntensityBlue"/>
         <Channel Name="White" Preset="IntensityWhite"/>
         <Channel Name="Dimmer" Preset="IntensityDimmer"/>
         <Channel Name="Shutter">
          <Group Byte="0">Shutter</Group>
          <Capability Min="0" Max="7">Closed</Capability>
          <Capability Min="8" Max="15" Preset="ShutterOpen">Open</Capability>
          <Capability Min="16" Max="255">Strobe</Capability>
         </Channel>
         <Channel Name="Pan" Default="127"><Group Byte="0">Pan</Group></Channel>
         <Channel Name="Pan fine"><Group Byte="1">Pan</Group></Channel>
         <Mode Name="6 Channel">
          <Channel Number="1">Green</Channel>
          <Channel Number="0">Red</Channel>
          <Channel Number="2">Blue</Channel>
          <Channel Number="3">White</Channel>
          <Channel Number="4">Dimmer</Channel>
          <Channel Number="5">Shutter</Channel>
         </Mode>
         <Mode Name="Pan only">
          <Channel Number="0">Pan</Channel>
          <Channel Number="1">Pan fine</Channel>
         </Mode>
        </FixtureDefinition>
        """;

    static FixtureProfile Import(string xml) => QlcFixtureImporter.Import(XDocument.Parse(xml));

    [Fact]
    public void Identity_comes_from_manufacturer_and_model()
    {
        var profile = Import(Eurolite);

        Assert.Equal("qlcplus/Eurolite/LED PARty RGBW", profile.Id);
        Assert.Equal("Color Changer", profile.Type);
    }

    [Fact]
    public void Mode_channels_are_in_dmx_order_with_functions_from_presets_and_groups()
    {
        var mode = Import(Eurolite).FindMode("6 Channel")!;

        Assert.Equal(["Red", "Green", "Blue", "White", "Dimmer", "Shutter"], mode.Channels.Select(c => c.Name));
        Assert.Equal([ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue, ChannelFunction.White,
                      ChannelFunction.Intensity, ChannelFunction.Shutter], mode.Channels.Select(c => c.Function));
    }

    [Fact]
    public void Shutter_defaults_to_open_and_explicit_defaults_are_kept()
    {
        var profile = Import(Eurolite);

        Assert.Equal(8, profile.FindMode("6 Channel")!.Channels[5].DefaultValue);
        Assert.Equal(127, profile.FindMode("Pan only")!.Channels[0].DefaultValue);
    }

    [Fact]
    public void Fine_channels_are_imported_as_generic()
    {
        var mode = Import(Eurolite).FindMode("Pan only")!;

        Assert.Equal(ChannelFunction.Pan, mode.Channels[0].Function);
        Assert.Equal(ChannelFunction.Generic, mode.Channels[1].Function);
    }

    [Fact]
    public void Mode_using_an_undefined_channel_is_rejected()
    {
        string broken = Eurolite.Replace("<Channel Number=\"5\">Shutter</Channel>", "<Channel Number=\"5\">Fog</Channel>");

        var ex = Assert.Throws<InvalidDataException>(() => Import(broken));
        Assert.Contains("Fog", ex.Message);
    }

    [Fact]
    public void Non_fixture_xml_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => Import("<Workspace/>"));
    }

    /// <summary>Runs only where QLC+ is installed: every shipped definition should import and validate.</summary>
    [Fact]
    public void Every_installed_QLC_plus_fixture_imports()
    {
        string? directory = AppPaths.QlcPlusFixtureDirectories.FirstOrDefault(Directory.Exists);
        Assert.SkipWhen(directory is null, "QLC+ isn't installed on this machine.");
        var profiles = new List<FixtureProfile>();
        var errors = new List<ProfileLoadError>();

        FixtureProfileLoader.LoadDirectory(directory!, profiles, errors);

        Assert.True(profiles.Count > 1000, $"only {profiles.Count} imported");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Take(10)));
    }
}
