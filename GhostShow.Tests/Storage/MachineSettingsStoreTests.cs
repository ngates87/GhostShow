using GhostShow.Core.Storage;

namespace GhostShow.Tests.Storage;

public class MachineSettingsStoreTests : IDisposable
{
    readonly TempDirectory _dir = new();
    readonly AppPaths _paths;

    public MachineSettingsStoreTests() => _paths = new AppPaths(_dir.Path);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task No_settings_file_means_defaults_without_an_error()
    {
        var result = await MachineSettingsStore.LoadAsync(_paths, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Empty(result.Settings.Outputs);
    }

    [Fact]
    public async Task Output_assignments_of_every_device_type_survive_a_save_and_reload()
    {
        var settings = new MachineSettings();
        settings.SetOutput(0, new OpenDmxDeviceConfig("/dev/ttyUSB0", "AB0PFDWZ"));
        settings.SetOutput(1, new UsbDmxProDeviceConfig("COM3", "12345678"));
        settings.SetOutput(2, new ArtNetDeviceConfig("10.0.0.5", 4));
        settings.SetOutput(3, new SimulatorDeviceConfig());

        await MachineSettingsStore.SaveAsync(settings, _paths, TestContext.Current.CancellationToken);
        var loaded = (await MachineSettingsStore.LoadAsync(_paths, TestContext.Current.CancellationToken)).Settings;

        Assert.Equal(settings.Outputs, loaded.Outputs);
    }

    [Fact]
    public async Task Invalid_settings_file_is_set_aside_and_defaults_are_used()
    {
        await File.WriteAllTextAsync(_paths.MachineSettingsFile, "{ broken", TestContext.Current.CancellationToken);

        var result = await MachineSettingsStore.LoadAsync(_paths, TestContext.Current.CancellationToken);

        Assert.Contains("isn't valid", result.Error);
        Assert.Empty(result.Settings.Outputs);
        Assert.False(File.Exists(_paths.MachineSettingsFile));
        Assert.Single(Directory.GetFiles(_dir.Path, "machine.json.invalid-*"));
    }

    [Fact]
    public void Assigning_a_universe_again_replaces_its_device()
    {
        var settings = new MachineSettings();
        settings.SetOutput(0, new SimulatorDeviceConfig());

        settings.SetOutput(0, new OpenDmxDeviceConfig("/dev/ttyUSB0", null));

        Assert.IsType<OpenDmxDeviceConfig>(Assert.Single(settings.Outputs).Device);
    }
}
