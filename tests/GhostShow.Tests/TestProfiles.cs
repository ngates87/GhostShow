using GhostShow.Core.Fixtures;

namespace GhostShow.Tests;

/// <summary>Small profiles and patches for tests, built in code so each test shows exactly what it depends on.</summary>
static class TestProfiles
{
    public static readonly FixtureProfile Rgbw = new()
    {
        Id = "test/rgbw",
        Model = "RGBW",
        Modes =
        [
            new FixtureMode
            {
                Name = "5ch",
                Channels =
                [
                    new FixtureChannel { Name = "Dimmer", Function = ChannelFunction.Intensity },
                    new FixtureChannel { Name = "Red", Function = ChannelFunction.Red },
                    new FixtureChannel { Name = "Green", Function = ChannelFunction.Green },
                    new FixtureChannel { Name = "Blue", Function = ChannelFunction.Blue },
                    new FixtureChannel { Name = "White", Function = ChannelFunction.White },
                ],
            },
            new FixtureMode
            {
                Name = "4ch",
                Channels =
                [
                    new FixtureChannel { Name = "Red", Function = ChannelFunction.Red },
                    new FixtureChannel { Name = "Green", Function = ChannelFunction.Green },
                    new FixtureChannel { Name = "Blue", Function = ChannelFunction.Blue },
                    new FixtureChannel { Name = "White", Function = ChannelFunction.White },
                ],
            },
        ],
    };

    public static readonly FixtureProfile Spot = new()
    {
        Id = "test/spot",
        Model = "Spot",
        Modes =
        [
            new FixtureMode
            {
                Name = "3ch",
                Channels =
                [
                    new FixtureChannel { Name = "Dimmer", Function = ChannelFunction.Intensity },
                    new FixtureChannel { Name = "Shutter", Function = ChannelFunction.Shutter, DefaultValue = 32 },
                    new FixtureChannel { Name = "Gobo", Function = ChannelFunction.Gobo },
                ],
            },
        ],
    };

    public static readonly FixtureProfileLibrary Library = new([Rgbw, Spot]);

    public static FixtureInstance Fixture(string name, int startAddress, string profileId = "test/rgbw",
                                          string mode = "5ch", int universe = 1) =>
        new() { Name = name, ProfileId = profileId, ModeName = mode, StartAddress = startAddress, Universe = universe };

    public static PatchResult Patch(params FixtureInstance[] fixtures) => FixturePatcher.Patch(fixtures, [1], Library);
}
