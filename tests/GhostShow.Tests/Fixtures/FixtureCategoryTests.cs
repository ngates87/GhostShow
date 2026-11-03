using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;

namespace GhostShow.Tests.Fixtures;

public class FixtureCategoryTests
{
    static readonly IReadOnlyList<FixtureMode> RgbModes = TestProfiles.Rgbw.Modes;
    static readonly IReadOnlyList<FixtureMode> UvOnly =
        [new FixtureMode { Name = "1", Channels = [new FixtureChannel { Name = "UV", Function = ChannelFunction.UV }] }];

    [Theory]
    [InlineData("Smoke", "Z-1200", FixtureCategory.Fog)]
    [InlineData("Other", "Fog Fury 3000", FixtureCategory.Fog)]
    [InlineData("Hazer", "Hazer Pro", FixtureCategory.Haze)]
    [InlineData("Effect", "Snow Flurry", FixtureCategory.Snow)]
    [InlineData("Effect", "Snowball", FixtureCategory.EffectLight)]
    [InlineData("Other", "Bubble Blitz", FixtureCategory.Bubble)]
    [InlineData("Other", "DMX Relay 8", FixtureCategory.Relay)]
    [InlineData("Moving Head", "Intimidator Spot", FixtureCategory.MovingHead)]
    [InlineData("Scanner", "Trackspot", FixtureCategory.Scanner)]
    [InlineData("Strobe", "Atomic 3000", FixtureCategory.Strobe)]
    [InlineData("Dimmer", "DP-415", FixtureCategory.Dimmer)]
    [InlineData("Flower", "Moonflower", FixtureCategory.EffectLight)]
    [InlineData("Laser", "Galaxian", FixtureCategory.Laser)]
    public void Type_and_model_name_pick_the_category(string type, string model, FixtureCategory expected)
    {
        Assert.Equal(expected, FixtureCategories.Infer(type, model, null, RgbModes));
    }

    [Fact]
    public void Effects_named_in_the_model_win_over_a_vague_type()
    {
        Assert.Equal(FixtureCategory.Fog, FixtureCategories.Infer("Color Changer", "LED Fog Machine", "LED", RgbModes));
    }

    [Fact]
    public void Colour_fixture_with_a_discharge_lamp_is_conventional()
    {
        Assert.Equal(FixtureCategory.Conventional, FixtureCategories.Infer("Color Changer", "Colorspot 250", "MSD 250W", RgbModes));
    }

    [Fact]
    public void Placeholder_lamp_type_does_not_make_a_fixture_conventional()
    {
        Assert.Equal(FixtureCategory.Color, FixtureCategories.Infer("Color Changer", "GigBAR 2", "Other", RgbModes));
    }

    [Fact]
    public void Led_colour_fixture_is_color()
    {
        Assert.Equal(FixtureCategory.Color, FixtureCategories.Infer("Color Changer", "PAR 64", "LED", RgbModes));
    }

    [Fact]
    public void Fixture_with_uv_but_no_rgb_is_a_blacklight()
    {
        Assert.Equal(FixtureCategory.Blacklight, FixtureCategories.Infer("Color Changer", "UV Bar", "LED", UvOnly));
    }

    [Fact]
    public void Stated_category_overrides_inference()
    {
        var profile = new FixtureProfile { Id = "x", Model = "Fog Fury", Category = FixtureCategory.Other, Modes = RgbModes };

        Assert.Equal(FixtureCategory.Other, profile.EffectiveCategory);
    }

    [Fact]
    public void Every_built_in_profile_states_its_category()
    {
        Assert.All(FixtureProfileLoader.LoadBuiltIn(), p => Assert.NotNull(p.Category));
    }

    [Fact]
    public void Built_ins_cover_the_haunt_effects()
    {
        var categories = FixtureProfileLoader.LoadBuiltIn().Select(p => p.EffectiveCategory).ToHashSet();

        var hauntEffects = new HashSet<FixtureCategory>
        {
            FixtureCategory.Dimmer, FixtureCategory.Conventional, FixtureCategory.Color, FixtureCategory.Blacklight,
            FixtureCategory.Fog, FixtureCategory.Haze, FixtureCategory.Snow, FixtureCategory.Bubble, FixtureCategory.Relay,
        };

        Assert.Superset(hauntEffects, categories);
    }

    [Fact]
    public void Qlc_import_reads_the_lamp_type()
    {
        var profile = QlcFixtureImporter.Import(System.Xml.Linq.XDocument.Parse("""
            <FixtureDefinition xmlns="http://www.qlcplus.org/FixtureDefinition">
             <Manufacturer>Old</Manufacturer><Model>Colorspot 250</Model><Type>Color Changer</Type>
             <Channel Name="Red" Preset="IntensityRed"/>
             <Mode Name="1"><Channel Number="0">Red</Channel></Mode>
             <Physical><Bulb Type="MSD 250W" Lumens="0" ColourTemperature="0"/></Physical>
            </FixtureDefinition>
            """));

        Assert.Equal(FixtureCategory.Conventional, profile.EffectiveCategory);
    }
}
