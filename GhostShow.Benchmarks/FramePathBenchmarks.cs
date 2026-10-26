using BenchmarkDotNet.Attributes;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Dmx.Protocol;

namespace GhostShow.Benchmarks;

/// <summary>
/// The per-frame work the output thread does, plus the commands sliders issue. All of it should be allocation-free:
/// anything allocated here happens 40 times a second per universe, or once per slider movement.
/// </summary>
[MemoryDiagnoser]
public class FramePathBenchmarks
{
    readonly byte[] _frame = new byte[512];
    readonly byte[] _proPacket = new byte[EnttecProPacket.MaxDmxPacketLength];
    readonly byte[] _artNetPacket = new byte[ArtNetPacket.MaxLength];
    ShowEngine _engine = null!;
    Guid _fixtureId;
    int _value;

    [GlobalSetup]
    public void Setup()
    {
        var fixtures = Enumerable.Range(0, 100)
            .Select(i => new FixtureInstance { Name = $"Par {i}", ProfileId = "bench/rgbw", ModeName = "5ch", StartAddress = 1 + i * 5 })
            .ToList();
        var patch = FixturePatcher.Patch(fixtures, [0], BenchmarkProfiles.Library);
        _engine = new ShowEngine([0], patch.Fixtures);
        _fixtureId = fixtures[50].Id;
    }

    [Benchmark(Description = "Engine → frame snapshot (output thread, per frame)")]
    public bool CopyUniverse() => _engine.TryCopyUniverse(0, _frame, out _);

    [Benchmark(Description = "Frame → Enttec Pro packet")]
    public int BuildProPacket() => EnttecProPacket.WriteDmx(_proPacket, _frame);

    [Benchmark(Description = "Frame → Art-Net packet")]
    public int BuildArtNetPacket() => ArtNetPacket.Write(_artNetPacket, 0, 1, _frame);

    [Benchmark(Description = "SetChannel command (raw fader)")]
    public CommandResult SetChannel() => _engine.SetChannel(0, 300, ++_value & 0xFF);

    [Benchmark(Description = "SetFixtureControl command (fixture fader, 100 patched)")]
    public CommandResult SetFixtureControl() => _engine.SetFixtureControl(_fixtureId, ChannelFunction.Red, ++_value & 0xFF);
}

/// <summary>Setup-time work: validating a patch. Runs on every edit in Fixture setup, so it must stay well under a frame.</summary>
[MemoryDiagnoser]
public class PatchBenchmarks
{
    List<FixtureInstance> _fixtures = null!;

    [Params(10, 100)]
    public int FixtureCount { get; set; }

    [GlobalSetup]
    public void Setup() => _fixtures = Enumerable.Range(0, FixtureCount)
        .Select(i => new FixtureInstance { Name = $"Par {i}", ProfileId = "bench/rgbw", ModeName = "5ch", StartAddress = 1 + i * 5 })
        .ToList();

    [Benchmark(Description = "Validate and patch fixtures")]
    public PatchResult Patch() => FixturePatcher.Patch(_fixtures, [0], BenchmarkProfiles.Library);
}

static class BenchmarkProfiles
{
    public static readonly FixtureProfileLibrary Library = new(
    [
        new FixtureProfile
        {
            Id = "bench/rgbw",
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
            ],
        },
    ]);
}
