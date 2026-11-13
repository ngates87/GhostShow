using GhostShow.Core.Fixtures;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Storage;

public class ProjectStoreTests : IDisposable
{
    readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

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

        Assert.Equal("Lightning Room", Assert.Single(result.Project!.Timelines).Name);
        Assert.Equal(Project.CurrentSchemaVersion, result.Project.Version);
        var binding = Assert.Single(result.MigratedOutputs);
        Assert.Equal(new OutputBinding(1, new OpenDmxDeviceConfig("/dev/ttyUSB0", "AB0PFDWZ")), binding);   // universes now count from 1
    }

    [Fact]
    public void Version_2_show_numbered_from_0_is_renumbered_from_1_everywhere()
    {
        const string version2 = """
            {
              "version": 2,
              "universes": [ { "number": 0, "name": "Universe 0" }, { "number": 1, "name": "Back rooms" } ],
              "fixtures": [ { "name": "Par", "profileId": "generic/dimmer", "modeName": "1 channel", "universe": 1, "startAddress": 5 } ],
              "scenes": [ { "name": "S", "tracks": [ { "type": "channel", "universe": 0, "channel": 3,
                                                         "keyframes": [ { "timeMs": 0, "value": 0 }, { "timeMs": 1000, "value": 200 } ] } ] } ]
            }
            """;

        var project = ProjectStore.FromJson(version2).Project!;

        Assert.Equal([(1, "Universe 1"), (2, "Back rooms")], project.Universes.Select(u => (u.Number, u.Name)));
        Assert.Equal(2, project.Fixtures[0].Universe);
        var clip = Assert.IsType<LightingClip>(Assert.Single(project.Timelines[0].Tracks[0].Events));   // keyframes → points in a block
        Assert.Equal(LaneTarget.Raw(1, 3), Assert.Single(clip.Lanes).Target);
        Assert.Equal(Project.CurrentSchemaVersion, project.Version);
    }

    [Fact]
    public void Old_show_without_a_universe_list_gets_universe_1()
    {
        var project = ProjectStore.FromJson("""{ "version": 1, "scenes": [] }""").Project!;

        Assert.Equal([1], project.Universes.Select(u => u.Number));
    }

    [Fact]
    public void Current_show_is_not_renumbered()
    {
        var project = ProjectStore.FromJson("""{ "version": 3, "universes": [ { "number": 1, "name": "Main" } ] }""").Project!;

        Assert.Equal(1, Assert.Single(project.Universes).Number);
    }

    [Fact]
    public void New_shows_start_with_universe_1()
    {
        Assert.Equal([1], new Project().Universes.Select(u => u.Number));
        Assert.Equal(1, new FixtureInstance().Universe);
    }

    [Fact]
    public void Version_4_single_file_show_keeps_its_timelines()
    {
        var result = ProjectStore.FromJson("""{ "version": 4, "timelines": [ { "name": "A", "loop": true, "tracks": [ { "name": "L", "kind": "Lighting" } ] } ] }""");

        Assert.True(result.IsSingleFileShow);
        Assert.Equal(4, result.FileVersion);
        var timeline = Assert.Single(result.Project!.Timelines);
        Assert.Equal(("A", true, "L"), (timeline.Name, timeline.Loop, timeline.Tracks[0].Name));
    }

    [Fact]
    public void Version_7_show_s_serial_triggers_listen_to_one_serial_device()
    {
        var project = ProjectStore.FromJson("""
            { "version": 7, "buttons": [
              { "name": "Scare", "triggers": [ { "source": "serial", "address": "BTN24" }, { "source": "keyboard", "address": "F5" } ] },
              { "name": "Other", "triggers": [ { "source": "serial", "address": "BTN25" } ] } ] }
            """).Project!;

        var device = Assert.IsType<SerialInputDevice>(Assert.Single(project.Inputs));
        Assert.Equal((LegacySerialTriggers.DeviceName, 9600), (device.Name, device.BaudRate));
        Assert.Equal([device.Id, null, device.Id], project.Buttons.SelectMany(b => b.Triggers).Select(t => t.Input));
    }

    [Fact]
    public void Input_devices_and_the_device_a_trigger_listens_to_are_saved()
    {
        var device = new SerialInputDevice { Name = "Ethereal FX", BaudRate = 57600 };
        var project = new Project
        {
            Inputs = [device],
            Buttons = [new ShowButton { Name = "Scare", Triggers = [new ButtonTrigger { Source = ButtonTrigger.Serial, Input = device.Id, Address = "BTN24" }] }],
        };

        var loaded = ProjectStore.FromJson(ProjectStore.ToJson(project)).Project!;

        var back = Assert.IsType<SerialInputDevice>(Assert.Single(loaded.Inputs));
        Assert.Equal((device.Id, "Ethereal FX", 57600), (back.Id, back.Name, back.BaudRate));
        Assert.Equal(device.Id, loaded.Buttons.Single().Triggers.Single().Input);
    }

    [Fact]
    public void Show_json_does_not_contain_the_timelines()
    {
        Assert.DoesNotContain("timelines", ProjectStore.ToJson(new Project()), StringComparison.OrdinalIgnoreCase);
    }
}
