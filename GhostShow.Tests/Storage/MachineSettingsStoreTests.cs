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
    public async Task Version_1_settings_numbered_from_0_are_renumbered_from_1()
    {
        await File.WriteAllTextAsync(_paths.MachineSettingsFile, """
            { "schemaVersion": 1, "outputs": [ { "universe": 0, "device": { "type": "openDmx", "port": "/dev/ttyUSB0", "serialNumber": "AB0PFDWZ" } } ] }
            """, TestContext.Current.CancellationToken);

        var settings = (await MachineSettingsStore.LoadAsync(_paths, TestContext.Current.CancellationToken)).Settings;

        Assert.Equal(1, Assert.Single(settings.Outputs).Universe);
        Assert.Equal(MachineSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Fact]
    public void Assigning_a_universe_again_replaces_its_device()
    {
        var settings = new MachineSettings();
        settings.SetOutput(0, new SimulatorDeviceConfig());

        settings.SetOutput(0, new OpenDmxDeviceConfig("/dev/ttyUSB0", null));

        Assert.IsType<OpenDmxDeviceConfig>(Assert.Single(settings.Outputs).Device);
    }

    [Fact]
    public void Recent_shows_put_the_latest_first_once_and_keep_ten()
    {
        var settings = new MachineSettings();
        for (int i = 0; i < 12; i++) settings.AddRecentShow(Path.Combine(Path.GetTempPath(), $"show{i}"));
        settings.AddRecentShow(Path.Combine(Path.GetTempPath(), "show5") + Path.DirectorySeparatorChar);   // again, with a trailing slash

        Assert.Equal(MachineSettings.MaxRecentShows, settings.RecentShows.Count);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "show5"), settings.RecentShows[0]);
        Assert.Single(settings.RecentShows, p => p.EndsWith("show5"));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "show11"), settings.RecentShows[1]);

        settings.RemoveRecentShow(settings.RecentShows[0]);
        Assert.DoesNotContain(settings.RecentShows, p => p.EndsWith("show5"));
    }
}
