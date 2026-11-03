using GhostShow.Core.Engine;
using GhostShow.Core.Playback;
using GhostShow.Dmx;
using GhostShow.Dmx.Devices;
using GhostShow.Tests.Dmx;
using static GhostShow.Tests.Timelines.TimelineTestKit;

namespace GhostShow.Tests.Playback;

/// <summary>The timeline reaches DMX output only through the engine and the Phase 1 output service.</summary>
public class TimelineOutputTests
{
    static readonly DmxOutputOptions Fast = new() { FrameInterval = TimeSpan.FromMilliseconds(5), ReconnectDelay = TimeSpan.FromMilliseconds(20), BlackoutOnStop = TimeSpan.Zero };

    readonly ShowEngine _engine = new();
    readonly ManualClock _clock = new();
    readonly ShowPlayer _player;

    public TimelineOutputTests() => _player = new ShowPlayer(_engine, new SilentAudioOutput(), _clock);

    [Fact]
    public async Task The_output_device_receives_timeline_levels_merged_with_manual_levels()
    {
        var simulator = new SimulatorDevice();
        var service = new DmxOutputService(_engine, 1, simulator, new ListLogger(), Fast);
        service.Start();
        _engine.SetChannel(1, 2, 44);   // manual level on a channel the timeline doesn't touch
        _player.Load(Compile(Timeline(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)))));
        _player.Play();

        _clock.Advance(5);
        _player.Tick();

        var frame = new byte[512];
        await Eventually.True(() => { simulator.CopyLastFrame(frame); return frame[0] == 100; }, "the mid-fade level reaches the device");
        Assert.Equal(44, frame[1]);
        await service.StopAsync();
    }

    [Fact]
    public async Task A_failing_output_does_not_affect_playback()
    {
        var device = new FakeDevice { RefuseConnect = true };
        var service = new DmxOutputService(_engine, 1, device, new ListLogger(), Fast);
        service.Start();
        _player.Load(Compile(Timeline(Lights(Raw(0, 1, to: 200, fade: 10, from: 0)))));
        _player.Play();

        for (int i = 0; i < 100; i++) { _clock.Advance(0.05); _player.Tick(); }

        await Eventually.True(() => service.Status == DmxOutputStatus.Faulted, "the output reports the missing device");
        Assert.Equal((TransportState.Playing, S(5)), (_player.Status.State, _player.Status.Position));
        var frame = new byte[512];
        _engine.TryCopyUniverse(1, frame, out _);
        Assert.Equal(100, frame[0]);   // the engine has the right levels, ready for when the device returns
        await service.StopAsync();
    }
}
