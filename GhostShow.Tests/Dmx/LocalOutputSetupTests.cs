using GhostShow.Core.Editing;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Model;
using GhostShow.Core.Storage;
using GhostShow.Dmx;
using GhostShow.Dmx.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhostShow.Tests.Dmx;

public class LocalOutputSetupTests : IAsyncDisposable
{
    static readonly DmxAdapterInfo OpenDmx = new("/dev/ttyFAKE0", DmxAdapterKind.OpenDmx, "SN1");
    const string Simulator = "Simulator (no hardware)";

    readonly ShowSession _session = new(new ShowEngine(), new FixtureProfileLibrary([]), NullLogger<ShowSession>.Instance);
    readonly MachineSettings _settings = new();
    readonly FakeAdapterFinder _finder = new();
    readonly List<OutputDeviceConfig> _devicesStarted = [];
    readonly OutputManager _outputs;
    readonly LocalOutputSetup _setup;
    int _saves;

    public LocalOutputSetupTests()
    {
        _outputs = new OutputManager(_session.Engine, new ListLogger(), new DmxOutputOptions { BlackoutOnStop = TimeSpan.Zero }, config =>
        {
            _devicesStarted.Add(config);
            return new FakeDevice();
        });
        _setup = new LocalOutputSetup(_session, _outputs, _settings, () =>
        {
            _saves++;
            return Task.CompletedTask;
        }, _finder);
    }

    public async ValueTask DisposeAsync()
    {
        await _outputs.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    async Task WatchAsync(string because)
    {
        _setup.SetWatching(this, true);
        await Eventually.True(() => _finder.Listings > 0 && !_setup.State.DetectStatus.StartsWith("Looking") && !_setup.State.DetectStatus.StartsWith("Checking"), because);
    }

    /// <summary>Waits for the next look at the ports to finish (a look already under way is not repeated).</summary>
    async Task LookAgainAsync(Func<bool> until, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!until())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {because}.");
            _setup.Tick();
            await Task.Delay(5);
        }
    }

    [Fact]
    public void No_port_is_touched_until_a_screen_is_watching()
    {
        _setup.Tick();
        _setup.Tick();

        Assert.Equal(0, _finder.Listings);
        Assert.Empty(_finder.Probed);
    }

    [Fact]
    public async Task An_adapter_is_found_and_offered_to_every_universe()
    {
        _finder.Plug(OpenDmx.Port, OpenDmx);

        await WatchAsync("the first look is done");

        Assert.Equal(new[] { Simulator, OpenDmx.ToString() }, _setup.State.Universes.Single().Choices);
        Assert.Equal(Simulator, _setup.State.Universes.Single().Selected);
        Assert.Equal("Available", _setup.State.Adapters.Single().Status);
    }

    [Fact]
    public async Task A_port_is_probed_once_and_an_unplugged_adapter_goes_away()
    {
        _finder.Plug(OpenDmx.Port, OpenDmx);
        await WatchAsync("the first look is done");
        int listings = _finder.Listings;
        await LookAgainAsync(() => _finder.Listings > listings, "it has looked again");
        Assert.Single(_finder.Probed);

        _finder.Unplug(OpenDmx.Port);
        await LookAgainAsync(() => _setup.State.Adapters.Count == 0, "the adapter has gone");

        Assert.Equal(new[] { Simulator }, _setup.State.Universes.Single().Choices);
    }

    [Fact]
    public async Task A_busy_port_is_asked_about_again_later()
    {
        _finder.Plug(OpenDmx.Port, OpenDmx);
        _finder.SetBusy(OpenDmx.Port, true);
        await WatchAsync("the first look is done");
        Assert.Empty(_setup.State.Adapters);

        _finder.SetBusy(OpenDmx.Port, false);
        await LookAgainAsync(() => _setup.State.Adapters.Count == 1, "the port is free and found");
    }

    [Fact]
    public async Task Assigning_an_adapter_starts_its_output_and_saves_this_computer_s_settings()
    {
        _finder.Plug(OpenDmx.Port, OpenDmx);
        await WatchAsync("the first look is done");
        await _setup.ApplyAsync();
        int universe = _setup.State.Universes.Single().Universe;

        Assert.Null(await _setup.AssignAsync(universe, OpenDmx.ToString()));

        Assert.Equal(new OpenDmxDeviceConfig(OpenDmx.Port, "SN1"), _settings.FindOutput(universe));
        Assert.Equal(OpenDmx.ToString(), _setup.State.Universes.Single().Selected);
        Assert.Equal(new OutputDeviceConfig[] { new SimulatorDeviceConfig(), new OpenDmxDeviceConfig(OpenDmx.Port, "SN1") }, _devicesStarted);
        Assert.Equal(1, _saves);
    }

    [Fact]
    public async Task Choosing_the_device_it_is_already_on_changes_nothing()
    {
        await _setup.ApplyAsync();
        int started = _devicesStarted.Count;

        Assert.Null(await _setup.AssignAsync(_setup.State.Universes.Single().Universe, Simulator));

        Assert.Equal(started, _devicesStarted.Count);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public async Task Only_what_was_offered_can_be_assigned()
    {
        int universe = _setup.State.Universes.Single().Universe;

        Assert.Contains("isn't available", await _setup.AssignAsync(universe, "/etc/passwd"));
        Assert.Contains("no universe", await _setup.AssignAsync(99, Simulator));

        Assert.Null(_settings.FindOutput(universe));
    }

    [Fact]
    public void An_assigned_device_that_isn_t_plugged_in_is_still_shown()
    {
        int universe = _setup.State.Universes.Single().Universe;
        _settings.SetOutput(universe, new OpenDmxDeviceConfig("/dev/ttyGONE", "SN9"));

        _setup.Refresh();

        Assert.Contains("(assigned; not detected)", _setup.State.Universes.Single().Selected);
        Assert.Contains("/dev/ttyGONE", _setup.State.Universes.Single().Selected);
    }

    [Fact]
    public async Task An_adapter_on_a_new_port_is_still_the_same_adapter()
    {
        int universe = _setup.State.Universes.Single().Universe;
        _settings.SetOutput(universe, new OpenDmxDeviceConfig("/dev/ttyOLD", "SN1"));
        _finder.Plug(OpenDmx.Port, OpenDmx);

        await WatchAsync("the first look is done");

        Assert.Equal(OpenDmx.ToString(), _setup.State.Universes.Single().Selected);
    }

    [Fact]
    public async Task Universes_added_and_removed_in_the_show_start_and_stop_their_outputs()
    {
        await _setup.ApplyAsync();
        var added = new UniverseConfig { Number = 7, Name = "Seven" };

        _session.Document.Record("Add universe", () => _session.Project.Universes.Add(added), [ShowDocument.Shallow(_session.Project)]);
        await _setup.LastSync;
        Assert.NotNull(_outputs.Find(7));
        Assert.Contains(_setup.State.Universes, u => u.Universe == 7);

        _session.Document.Record("Remove universe", () => _session.Project.Universes.Remove(added), [ShowDocument.Shallow(_session.Project)]);
        await _setup.LastSync;
        Assert.Null(_outputs.Find(7));
        Assert.DoesNotContain(_setup.State.Universes, u => u.Universe == 7);
    }

    [Fact]
    public void Screens_are_only_told_when_something_on_them_changed()
    {
        int told = 0;
        _setup.Changed += () => told++;

        _setup.Refresh();
        _setup.Refresh();
        Assert.Equal(0, told);

        _settings.SetOutput(_setup.State.Universes.Single().Universe, new OpenDmxDeviceConfig("/dev/ttyGONE", "SN9"));
        _setup.Refresh();
        Assert.Equal(1, told);
    }
}
