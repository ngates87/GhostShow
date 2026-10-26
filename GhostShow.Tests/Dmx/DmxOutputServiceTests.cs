using GhostShow.Core.Engine;
using GhostShow.Dmx;
using GhostShow.Dmx.Devices;
using Microsoft.Extensions.Logging;

namespace GhostShow.Tests.Dmx;

public class DmxOutputServiceTests
{
    static readonly DmxOutputOptions FastOptions = new()
    {
        FrameInterval = TimeSpan.FromMilliseconds(5),
        ReconnectDelay = TimeSpan.FromMilliseconds(20),
        BlackoutOnStop = TimeSpan.FromMilliseconds(30),
        StopTimeout = TimeSpan.FromMilliseconds(500),
    };

    readonly ShowEngine _engine = new();
    readonly ListLogger _log = new();

    DmxOutputService Start(IDmxOutputDevice device)
    {
        var service = new DmxOutputService(_engine, 0, device, _log, FastOptions);
        service.Start();
        return service;
    }

    [Fact]
    public async Task Simulator_receives_the_engine_s_values()
    {
        var simulator = new SimulatorDevice();
        var service = Start(simulator);

        _engine.SetChannel(0, 1, 255);
        _engine.SetChannel(0, 512, 7);

        var frame = new byte[512];
        await Eventually.True(() => { simulator.CopyLastFrame(frame); return frame[0] == 255 && frame[511] == 7; },
                              "the simulator shows the new values");
        await service.StopAsync();
    }

    [Fact]
    public async Task Output_keeps_refreshing_even_when_nothing_changes()
    {
        var device = new FakeDevice();
        var service = Start(device);

        await Eventually.True(() => device.FrameCount >= 10, "ten frames have gone out");
        await service.StopAsync();
    }

    [Fact]
    public async Task Rapid_updates_send_the_latest_values_not_a_backlog()
    {
        const int updates = 50_000;
        var device = new FakeDevice();
        var service = Start(device);
        await Eventually.True(() => device.FrameCount > 0, "output started");

        for (int i = 1; i <= updates; i++) _engine.SetChannel(0, 1, i % 256);

        await Eventually.True(() => device.LastFrame[0] == updates % 256, "the final value is output");
        Assert.True(device.FrameCount < updates / 100, $"{device.FrameCount} frames for {updates} updates: frames go out at the frame rate, not once per update");
        await service.StopAsync();
    }

    [Fact]
    public async Task Missing_device_is_reported_as_faulted_and_retried()
    {
        var device = new FakeDevice { RefuseConnect = true };
        var service = Start(device);

        await Eventually.True(() => device.ConnectAttempts >= 3, "connection is retried");
        Assert.Equal(DmxOutputStatus.Faulted, service.Status);
        Assert.Contains("unplugged", service.LastError);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning);   // logged once, not on every retry
        await service.StopAsync();
    }

    [Fact]
    public async Task Device_that_appears_later_is_picked_up()
    {
        var device = new FakeDevice { RefuseConnect = true };
        var service = Start(device);
        await Eventually.True(() => service.Status == DmxOutputStatus.Faulted, "the missing device is reported");

        device.RefuseConnect = false;

        await Eventually.True(() => service.Status == DmxOutputStatus.Connected, "the device connects once it's plugged in");
        await Eventually.True(() => device.FrameCount > 0, "frames flow");
        await service.StopAsync();
    }

    [Fact]
    public async Task Device_lost_mid_show_reconnects_and_output_resumes()
    {
        var device = new FakeDevice();
        var statuses = new List<DmxOutputStatus>();
        var service = new DmxOutputService(_engine, 0, device, _log, FastOptions);
        service.StatusChanged += s => { lock (statuses) statuses.Add(s.Status); };
        service.Start();
        await Eventually.True(() => device.FrameCount > 0, "output started");

        device.FailNextSend = true;
        int framesBefore = device.FrameCount;

        await Eventually.True(() => device.ConnectAttempts == 2 && device.FrameCount > framesBefore + 5, "output resumes after reconnecting");
        lock (statuses) Assert.Equal([DmxOutputStatus.Connecting, DmxOutputStatus.Connected, DmxOutputStatus.Faulted,
                                      DmxOutputStatus.Connecting, DmxOutputStatus.Connected], statuses);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("mid-show"));
        await service.StopAsync();
    }

    [Fact]
    public async Task Stopping_sends_blackout_then_disconnects_and_releases_the_device()
    {
        var device = new FakeDevice();
        var service = Start(device);
        _engine.SetChannel(0, 1, 255);
        await Eventually.True(() => device.FrameCount > 0 && device.LastFrame[0] == 255, "the look is on stage");

        bool stopped = await service.StopAsync();

        Assert.True(stopped);
        Assert.All(device.LastFrame, v => Assert.Equal(0, v));
        Assert.False(device.IsConnected);
        Assert.True(device.Disposed);
        Assert.Equal(DmxOutputStatus.Stopped, service.Status);
    }

    [Fact]
    public async Task Stopping_a_service_that_never_connected_is_clean()
    {
        var device = new FakeDevice { RefuseConnect = true };
        var service = Start(device);
        await Eventually.True(() => service.Status == DmxOutputStatus.Faulted, "the missing device is reported");

        Assert.True(await service.StopAsync());
        Assert.Equal(DmxOutputStatus.Stopped, service.Status);
        Assert.Equal(0, device.FrameCount);
    }

    [Fact]
    public async Task Hung_driver_makes_stop_time_out_and_is_logged()
    {
        var device = new FakeDevice();
        var service = Start(device);
        await Eventually.True(() => device.FrameCount > 0, "output started");
        device.HangOnSend = true;

        bool stopped = await service.StopAsync();

        Assert.False(stopped);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("didn't stop"));
        device.HangOnSend = false;   // let the thread finish so it doesn't outlive the test
    }

    [Fact]
    public async Task Last_sent_frame_is_what_the_device_received()
    {
        var device = new FakeDevice();
        var service = Start(device);
        _engine.SetChannel(0, 10, 99);
        await Eventually.True(() => device.FrameCount > 0 && device.LastFrame[9] == 99, "the value went out");

        var monitor = new byte[512];
        service.CopyLastSentFrame(monitor);

        Assert.Equal(99, monitor[9]);
        await service.StopAsync();
    }
}
