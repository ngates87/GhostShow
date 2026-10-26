using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;

namespace GhostShow.Tests.Storage;

public class ProjectStoreTests : IDisposable
{
    readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Saved_show_reloads_with_its_universes_fixtures_and_scenes()
    {
        var fixture = new FixtureInstance { Name = "Graveyard wash", ProfileId = "generic/rgbw", ModeName = "4 channel", StartAddress = 17, Group = "Graveyard", Enabled = false };
        var track = new ChannelTrack { Channel = 3 };
        track.SetKeyframe(0, 50, Interpolation.Step);
        var project = new Project
        {
            Name = "Haunt",
            Universes = [new UniverseConfig { Number = 0, Name = "Front" }, new UniverseConfig { Number = 3, Name = "Back" }],
            Fixtures = [fixture],
            Scenes = [new Scene { Name = "Lightning", Tracks = { track, new AudioTrack { Clips = { new AudioClip(10, "z.wav", 80) } } } }],
        };
        string path = _dir.File("haunt.json");

        await ProjectStore.SaveAsync(project, path, TestContext.Current.CancellationToken);
        var loaded = (await ProjectStore.LoadAsync(path, TestContext.Current.CancellationToken)).Project!;

        Assert.Equal("Haunt", loaded.Name);
        Assert.Equal([0, 3], loaded.Universes.Select(u => u.Number));
        var reloaded = Assert.Single(loaded.Fixtures);
        Assert.Equal((fixture.Id, "generic/rgbw", "4 channel", 17, "Graveyard", false),
                     (reloaded.Id, reloaded.ProfileId, reloaded.ModeName, reloaded.StartAddress, reloaded.Group, reloaded.Enabled));
        var scene = Assert.Single(loaded.Scenes);
        Assert.IsType<ChannelTrack>(scene.Tracks[0]);
        Assert.Equal(80, Assert.IsType<AudioTrack>(scene.Tracks[1]).Clips[0].Volume);
    }

    [Fact]
    public async Task Missing_file_is_reported_not_thrown()
    {
        var result = await ProjectStore.LoadAsync(_dir.File("nope.json"), TestContext.Current.CancellationToken);

        Assert.Null(result.Project);
        Assert.Contains("nope.json", result.Error);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "fixtures": "should be a list" }""")]
    public void Malformed_show_is_reported_not_thrown(string json)
    {
        var result = ProjectStore.FromJson(json);

        Assert.Null(result.Project);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void Show_from_a_newer_version_is_refused_with_an_explanation()
    {
        var result = ProjectStore.FromJson("""{ "version": 99 }""");

        Assert.Null(result.Project);
        Assert.Contains("newer", result.Error);
    }

    [Fact]
    public void Version_1_show_keeps_its_scenes_and_hands_back_its_output_assignments()
    {
        const string version1 = """
            {
              "Version": 1,
              "Name": "Demo Haunt",
              "Scenes": [ { "Name": "Lightning Room", "DurationMs": 8000, "Loop": true, "Tracks": [] } ],
              "Universes": [ { "Number": 0, "Name": "Main", "Output": { "type": "openDmx", "Port": "/dev/ttyUSB0", "SerialNumber": "AB0PFDWZ" } } ]
            }
            """;

        var result = ProjectStore.FromJson(version1);

        Assert.Equal("Lightning Room", Assert.Single(result.Project!.Scenes).Name);
        Assert.Equal(Project.CurrentSchemaVersion, result.Project.Version);
        var binding = Assert.Single(result.MigratedOutputs);
        Assert.Equal(new OutputBinding(0, new OpenDmxDeviceConfig("/dev/ttyUSB0", "AB0PFDWZ")), binding);
    }

    [Fact]
    public async Task Failed_save_leaves_the_previous_file_intact_and_no_temp_files()
    {
        string path = _dir.File("show.json");
        await ProjectStore.SaveAsync(new Project { Name = "Good" }, path, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectStore.SaveAsync(new Project { Name = "Half-written" }, path, cancelled.Token));

        Assert.Equal("Good", (await ProjectStore.LoadAsync(path, TestContext.Current.CancellationToken)).Project!.Name);
        Assert.Single(Directory.GetFiles(_dir.Path));
    }
}
