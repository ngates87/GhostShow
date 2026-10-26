using GhostShow.Core.Engine;
using GhostShow.Core.Storage;
using GhostShow.Dmx;

namespace GhostShow.Tests.Dmx;

public class OutputManagerTests
{
    readonly ShowEngine _engine = new([0, 1], []);
    readonly List<(OutputDeviceConfig Config, FakeDevice Device)> _created = [];

    OutputManager CreateManager() => new(_engine, new ListLogger(), new DmxOutputOptions { BlackoutOnStop = TimeSpan.Zero },
        config =>
        {
            var device = new FakeDevice();
            _created.Add((config, device));
            return device;
        });

    [Fact]
    public async Task Universes_without_an_assigned_device_use_the_simulator()
    {
        await using var manager = CreateManager();

        await manager.ApplyAsync([0, 1], new MachineSettings());

        Assert.Equal(2, manager.Services.Count);
        Assert.All(_created, c => Assert.IsType<SimulatorDeviceConfig>(c.Config));
    }

    [Fact]
    public async Task Reapplying_unchanged_settings_restarts_nothing()
    {
        await using var manager = CreateManager();
        var settings = new MachineSettings();
        settings.SetOutput(0, new OpenDmxDeviceConfig("/dev/ttyUSB0", "AB0PFDWZ"));
        await manager.ApplyAsync([0, 1], settings);

        await manager.ApplyAsync([0, 1], settings);

        Assert.Equal(2, _created.Count);
    }

    [Fact]
    public async Task Changing_one_universe_s_device_restarts_only_that_universe()
    {
        await using var manager = CreateManager();
        var settings = new MachineSettings();
        await manager.ApplyAsync([0, 1], settings);
        var universe1Before = manager.Find(1);

        settings.SetOutput(0, new OpenDmxDeviceConfig("/dev/ttyUSB0", "AB0PFDWZ"));
        await manager.ApplyAsync([0, 1], settings);

        Assert.Equal(3, _created.Count);
        Assert.IsType<OpenDmxDeviceConfig>(_created[^1].Config);
        Assert.Same(universe1Before, manager.Find(1));
        Assert.True(_created[0].Device.Disposed, "the replaced device was released");
    }

    [Fact]
    public async Task Removed_universes_are_stopped()
    {
        await using var manager = CreateManager();
        await manager.ApplyAsync([0, 1], new MachineSettings());

        await manager.ApplyAsync([0], new MachineSettings());

        Assert.Single(manager.Services);
        Assert.Null(manager.Find(1));
    }
}
