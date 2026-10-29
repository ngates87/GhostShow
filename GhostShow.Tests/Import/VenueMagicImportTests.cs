using System.Text;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Import;
using GhostShow.Core.Timeline;

namespace GhostShow.Tests.Import;

/// <summary>
/// The VenueMagic importer, on a small project built here with the record shapes of a real VenueMagic 4 file (its
/// layout was worked out from one; see docs/import-venuemagic.md).
/// </summary>
public class VenueMagicImportTests : IDisposable
{
    readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    const string DimmerType = "df81813c-2521-4c65-8524-e169d20e56c4", RgbType = "b8501e81-7e06-4878-84ae-2cb7eb318a33";

    static readonly Encoding Latin1 = Encoding.Latin1;

    sealed class Writer
    {
        readonly MemoryStream _out = new();
        public Writer Bytes(params byte[] bytes) { _out.Write(bytes); return this; }
        public Writer Int(int value) => Bytes(BitConverter.GetBytes(value));
        public Writer Text(string text) { var b = Latin1.GetBytes(text); return Bytes((byte)b.Length).Bytes(b); }
        public Writer Raw(string text) => Bytes(Latin1.GetBytes(text));
        public Writer Double(double value) => Bytes(BitConverter.GetBytes(value));
        public Writer Zeros(int count) => Bytes(new byte[count]);

        /// <summary>The header every track and timeline item starts with: FF×8, 1, ten bytes, 1, start, a double, length.</summary>
        public Writer Header(double start, double length) => Bytes(0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff).Int(1).Zeros(10).Int(1).Double(start).Double(0).Double(length).Zeros(6);

        /// <summary>An envelope handle: its level, then (36 bytes on) its index, flag, position (percent) and the rest of the record.</summary>
        public Writer Handle(int index, double position, double level) =>
            Double(level).Zeros(28).Bytes((byte)index, 1).Double(position).Zeros(12).Bytes(0xdb, 1, 0, 0).Zeros(4).Int(10).Int(20).Int(19).Int(26).Bytes(0, 7, 7);
        public byte[] ToArray() => _out.ToArray();
    }

    static string Id() => Guid.NewGuid().ToString();

    /// <summary>A fixture type: [len]name [int] [36]guid (the length byte, 36, is the "$" before every ID).</summary>
    static Writer Type(Writer w, string name, string id) => w.Text(name).Int(1).Text(id).Bytes(0x3c, 0x60);

    /// <summary>A patched fixture: its header (the address just before its name), then its channels.</summary>
    static Writer Fixture(Writer w, string name, string type, int address, params (string Name, int Number)[] channels)
    {
        w.Int(211).Int(address).Text(name).Text(type).Bytes(0).Text(Id()).Int(0).Int(-1).Int(0).Bytes(0, (byte)channels.Length, 0, 0x83, 0x86);
        foreach (var (channel, number) in channels)
            w.Bytes(0xdb, 1, 0, 0, 0xdb, 1, 0, 0).Int(number).Int(0).Bytes(0xdb, 1, 0, 0, 0, 0, 0).Text("Channel Group Notes").Text(Id()).Text(channel).Int(0);
        return w;
    }

    string Write(byte[] bytes)
    {
        var folder = Path.Combine(_dir.Path, "Haunt 2024");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Haunt 2024.vmp.bak");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    byte[] Sample()
    {
        var w = new Writer().Bytes(0xff, 0xff, 1, 0, 10, 0).Raw("CVMProject").Bytes(0xdb, 1, 0, 0).Text(Id());
        Type(w, "MF2:DMX-4", DimmerType);
        Type(w, "MF5:Dimmer+RGB+Strobe", RgbType);
        Fixture(w, "Zone_1_Area_1_Dim_1", DimmerType, 1, ("Channel_1", 4), ("Channel_2", 5), ("Channel_3", 6), ("Channel_4", 7));
        Fixture(w, "FanLights", RgbType, 175, ("Level", 87), ("RGB", 5832707), ("Red", 89), ("Green", 90), ("Blue", 91), ("Strobe", 92));
        Fixture(w, "254_MED_AREA_DECODER", DimmerType, 254, ("Channel_1", 151), ("Channel_2", 152), ("Channel_3", 153), ("Channel_4", 154));
        w.Text(@"C:\Users\station5\Documents\VenueMagic\Projects\Haunt 2024\Zone 2\cages\cage-lights.vmt")
         .Text(@"C:\Users\station5\Documents\VenueMagic\Projects\Haunt 2024\SartShow.vmt")
         .Text(@"C:\Users\station5\Documents\VenueMagic\Projects\Timeline.vmt");   // outside the project: left out
        return w.ToArray();
    }

    [Fact]
    public void The_patch_and_the_timeline_files_are_read()
    {
        var project = VenueMagicProject.Read(Write(Sample()));

        Assert.Equal([("Zone_1_Area_1_Dim_1", 1, 4), ("FanLights", 175, 5), ("254_MED_AREA_DECODER", 254, 4)],
                     project.Fixtures.Select(f => (f.Name, f.Address, f.Channels.Count)));
        Assert.Equal(["Level", "Red", "Green", "Blue", "Strobe"], project.Fixtures[1].Channels);   // "RGB" groups them: no DMX channel
        Assert.Equal(["MF2:DMX-4", "MF5:Dimmer+RGB+Strobe"], project.FixtureTypes.Select(t => t.Name));
        Assert.Equal(["SartShow.vmt", "Zone 2/cages/cage-lights.vmt"], project.Timelines);
    }

    [Fact]
    public void The_import_makes_custom_fixture_types_fixtures_at_their_addresses_and_the_timeline_tree()
    {
        var result = VenueMagicImporter.Import(VenueMagicProject.Read(Write(Sample())), "Haunt 2024");

        var types = result.Profiles.ToDictionary(p => p.Model);
        Assert.Equal(["DMX-4", "Dimmer+RGB+Strobe"], types.Keys);
        Assert.Equal(("custom/venuemagic-dmx-4", "VenueMagic"), (types["DMX-4"].Id, types["DMX-4"].Manufacturer));
        Assert.Equal([ChannelFunction.Intensity, ChannelFunction.Red, ChannelFunction.Green, ChannelFunction.Blue, ChannelFunction.Strobe],
                     types["Dimmer+RGB+Strobe"].Modes[0].Channels.Select(c => c.Function));
        Assert.Equal([("Zone_1_Area_1_Dim_1", 1, "custom/venuemagic-dmx-4"), ("FanLights", 175, "custom/venuemagic-dimmer-rgb-strobe"), ("254_MED_AREA_DECODER", 254, "custom/venuemagic-dmx-4")],
                     result.Project.Fixtures.Select(f => (f.Name, f.StartAddress, f.ProfileId)));
        Assert.Equal(["SartShow", "Zone 2/cages/cage-lights"], result.Project.Timelines.Select(t => t.TreePath));
        Assert.Contains("Zone 2/cages", result.Project.TimelineFolders);
        Assert.Empty(FixturePatcher.Patch(result.Project.Fixtures, [1], new FixtureProfileLibrary(result.Profiles)).Issues);
    }

    /// <summary>A timeline: a lighting track with a Level Control clip and a Lightning Storm, an audio track, an event track.</summary>
    static byte[] Timeline(string project)
    {
        var w = new Writer().Int(475).Zeros(8);
        w.Header(0, 35).Text("Control Track Notes").Zeros(4).Text("Room lights").Zeros(8);
        w.Text("GIID_7.Level").Text("Zone_1_Area_1_Dim_1.Channel_2").Zeros(8);                  // which channel GIID_7 is
        w.Raw("\x0dGIID_7.Level").Bytes(0x1b, 0x80, 0xdb, 1, 0, 0, 2, 0, 0x1d, 0x80).Zeros(30);    // an envelope for it
        w.Handle(1, 0, 20).Handle(2, 50, 75).Handle(2, 100, 100);
        w.Int(1).Double(2).Double(0).Double(4).Int(0).Double(2).Int(0).Int(1).Raw("\x0dLevel Control\x00").Zeros(8);
        w.Header(10, 20).Text("Lightning Storm").Zeros(4).Text("Lamp Effect Notes").Zeros(8);
        w.Header(0, 35).Text("Audio Track Notes").Zeros(4).Text("Screams").Zeros(8);
        w.Handle(1, 0, 80).Handle(2, 100, 80).Handle(1, 0, 50).Handle(2, 100, 50);                 // volume, pan
        w.Header(3, 6.5).Text("scream").Zeros(4).Text("Audio Clip Notes").Zeros(4).Text($@"C:\Users\x\VenueMagic\Projects\{project}\audio\scream.wav").Zeros(8);
        w.Header(0, 35).Text("Event Track Notes").Zeros(4).Text("Events").Zeros(8);
        w.Header(1.5, 0).Text("Run Timeline").Zeros(4).Text("Event Notes").Zeros(4).Text("Go").Text("cage-lights").Zeros(8);
        return w.ToArray();
    }

    [Fact]
    public void Timelines_bring_their_tracks_clips_effects_and_sounds_and_list_what_they_leave_out()
    {
        var path = Write(Sample());
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(Path.Combine(folder, "Zone 2", "cages"));
        File.WriteAllBytes(Path.Combine(folder, "Zone 2", "cages", "cage-lights.vmt"), Timeline("Haunt 2024"));
        Directory.CreateDirectory(Path.Combine(folder, "audio"));
        File.WriteAllBytes(Path.Combine(folder, "audio", "scream.wav"), [0]);

        var result = VenueMagicImporter.Import(VenueMagicProject.Read(path), "Haunt 2024");

        var timeline = result.Project.FindTimeline("Zone 2/cages/cage-lights")!;
        Assert.Equal([("Room lights", TrackKind.Lighting), ("Screams", TrackKind.Audio)], timeline.Tracks.Select(t => (t.Name, t.Kind)));
        var lights = timeline.Tracks[0];
        var dimmer = result.Project.Fixtures.Single(f => f.Name == "Zone_1_Area_1_Dim_1");
        Assert.Equal((LaneTarget.FixtureChannel(dimmer.Id, 2), (byte)51), (Assert.Single(lights.Channels).Target, lights.Channels[0].StartLevel));   // 20%: where the first clip starts
        var clip = Assert.IsType<LightingClip>(lights.Events[0]);
        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)), (clip.Start, clip.Length));
        Assert.Equal([(TimeSpan.FromSeconds(2), (byte)191), (TimeSpan.FromSeconds(4), (byte)255)],   // 75% half way, 100% at the end (the start handle carries on)
                     Assert.Single(clip.Lanes).Points.Select(p => (p.Time, p.Value)));
        var storm = Assert.IsType<LightingClip>(lights.Events[1]);
        Assert.Equal((EffectKind.Lightning, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)), (storm.Effect!.Kind, storm.Start, storm.Length));
        var sound = Assert.IsType<AudioEvent>(Assert.Single(timeline.Tracks[1].Events));
        Assert.Equal((TimeSpan.FromSeconds(3), (TimeSpan?)TimeSpan.FromSeconds(6.5), Path.Combine(folder, "audio", "scream.wav"), 0.8),
                     (sound.Start, sound.Length, sound.File, sound.Volume));
        Assert.Contains(result.Notes, n => n.Contains("runs \"cage-lights\" at 1.5 s"));
        Assert.Contains(result.Notes, n => n.Contains("Lightning Storm") && n.Contains("Lightning effect"));
        Assert.Contains(result.Notes, n => n.StartsWith("SartShow") && n.Contains("wasn't found"));
    }

    [Theory]
    [InlineData("Total_Dimming", ChannelFunction.Intensity)]
    [InlineData("Red_dimming", ChannelFunction.Red)]
    [InlineData("Level12", ChannelFunction.Intensity)]
    [InlineData("Channel 3", ChannelFunction.Intensity)]
    [InlineData("Horizontal", ChannelFunction.Pan)]
    [InlineData("Builtin_effect_speed_adj", ChannelFunction.Speed)]
    [InlineData("Builtin_Effects", ChannelFunction.Effect)]
    [InlineData("Fog", ChannelFunction.Generic)]
    public void Channel_functions_are_guessed_from_names(string name, ChannelFunction expected) =>
        Assert.Equal(expected, VenueMagicImporter.FunctionOf(name));

    [Fact]
    public void Something_else_is_refused()
    {
        var path = Write(Latin1.GetBytes("not a venuemagic file at all"));

        Assert.Throws<InvalidDataException>(() => VenueMagicProject.Read(path));
    }
}
