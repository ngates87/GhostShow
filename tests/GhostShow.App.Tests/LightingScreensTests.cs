using GhostShow.App.ViewModels;
using GhostShow.Core.Dmx;
using GhostShow.Core.Engine;
using GhostShow.Core.Fixtures;
using GhostShow.Dmx.Discovery;

namespace GhostShow.App.Tests;

/// <summary>For tests that build the real app: no real port is ever opened.</summary>
sealed class NoAdapters : IDmxAdapterFinder
{
    public IReadOnlyList<string> CandidatePorts() => [];
    public DmxAdapterInfo? Probe(string port, int timeoutMs, out bool busy)
    {
        busy = false;
        return null;
    }
}

/// <summary>The show computer's hardware as a device's screen sees it: only what it's told.</summary>
sealed class FakeOutputSetup : IOutputSetup
{
    public OutputSetupState State { get; private set; } = OutputSetupState.Empty;
    public event Action? Changed;
    public readonly HashSet<object> Watching = [];
    public readonly List<(int Universe, string Choice)> Assigned = [];
    public string? RefuseWith;
    public int Scans;

    public void Say(OutputSetupState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void SetWatching(object screen, bool watching)
    {
        if (watching) Watching.Add(screen);
        else Watching.Remove(screen);
    }

    public Task ScanAgainAsync()
    {
        Scans++;
        return Task.CompletedTask;
    }

    public Task<string?> AssignAsync(int universe, string choice)
    {
        Assigned.Add((universe, choice));
        return Task.FromResult(RefuseWith);
    }
}

/// <summary>Levels as a device's screens see them: what it asks for, and what it sets.</summary>
sealed class FakeLiveLevels : ILiveLevels
{
    public int? Watched;
    public readonly List<string> Sets = [];
    public byte[] Levels = new byte[512];
    public string Source = "from the show computer";

    public CommandResult SetChannel(int universe, int address, int value) { Sets.Add($"channel {universe} {address} {value}"); return CommandResult.Ok; }
    public CommandResult ClearUniverse(int universe) { Sets.Add($"clear {universe}"); return CommandResult.Ok; }
    public CommandResult SetFixtureChannel(Guid fixtureId, int channelNumber, int value) { Sets.Add($"fixture channel {channelNumber} {value}"); return CommandResult.Ok; }
    public CommandResult SetFixtureControl(Guid fixtureId, ChannelFunction function, int value) { Sets.Add($"fixture {function} {value}"); return CommandResult.Ok; }
    public bool TryCopyLevels(int universe, Span<byte> destination) { Levels.CopyTo(destination); return true; }
    public void CopyTimelineControlled(int universe, Span<bool> destination) => destination.Clear();
    public string CopyAsSent(int universe, Span<byte> destination) { Levels.CopyTo(destination); return Source; }
    public void Watch(int? universe) => Watched = universe;
}

public class LightingScreensTests : IDisposable
{
    readonly TestRig _rig = new();
    readonly FakeOutputSetup _hardware = new();
    readonly FakeLiveLevels _levels = new();
    readonly List<string> _errors = [];
    readonly LightingViewModel _lighting;

    public LightingScreensTests() =>
        _lighting = new LightingViewModel(_rig.Session, _levels, new OutputsViewModel(_rig.Session, _hardware, _errors.Add), _rig.Setup, _errors.Add);

    public void Dispose() => _rig.Dispose();

    int FirstUniverse => _rig.Session.Project.Universes[0].Number;

    OutputSetupState Offering(params string[] choices) =>
        new("Watching.", [new AdapterRow("Adapter", "Available")], [new UniverseOutput(FirstUniverse, "Connected", false, choices, choices[0])]);

    [Fact]
    public void The_setup_screen_shows_what_the_show_computer_says()
    {
        _hardware.Say(Offering("Simulator", "Adapter"));

        var row = _lighting.Outputs.Universes[0];
        Assert.Equal("Watching.", _lighting.Outputs.DetectStatus);
        Assert.Equal(["Simulator", "Adapter"], row.Choices);
        Assert.Equal("Simulator", row.SelectedDevice);
        Assert.Equal("Connected", row.Status);
        Assert.Empty(_hardware.Assigned);   // showing it isn't picking it
    }

    [Fact]
    public void Picking_a_device_asks_the_show_computer()
    {
        _hardware.Say(Offering("Simulator", "Adapter"));

        _lighting.Outputs.Universes[0].SelectedDevice = "Adapter";

        Assert.Equal([(FirstUniverse, "Adapter")], _hardware.Assigned);
    }

    [Fact]
    public void A_refused_pick_says_why_and_goes_back_to_the_device_it_is_on()
    {
        _hardware.Say(Offering("Simulator", "Adapter"));
        _hardware.RefuseWith = "That adapter was unplugged.";

        _lighting.Outputs.Universes[0].SelectedDevice = "Adapter";

        Assert.Equal(["That adapter was unplugged."], _errors);
        Assert.Equal("Simulator", _lighting.Outputs.Universes[0].SelectedDevice);
    }

    [Fact]
    public void Adapters_are_only_looked_for_while_the_setup_screen_is_on_show()
    {
        Assert.Empty(_hardware.Watching);

        _lighting.IsOnScreen = true;
        Assert.Single(_hardware.Watching);   // the first screen in the row

        _lighting.CurrentPage = Page.Monitor;
        Assert.Empty(_hardware.Watching);

        _lighting.CurrentPage = Page.DmxSetup;
        Assert.Single(_hardware.Watching);

        _lighting.IsOnScreen = false;
        Assert.Empty(_hardware.Watching);
    }

    [Fact]
    public void Levels_are_only_asked_for_while_a_screen_showing_them_is_on_show()
    {
        Assert.Null(_levels.Watched);

        _lighting.IsOnScreen = true;
        Assert.Null(_levels.Watched);   // DMX setup shows no levels

        _lighting.CurrentPage = Page.Channels;
        Assert.Equal(FirstUniverse, _levels.Watched);

        _lighting.IsOnScreen = false;
        Assert.Null(_levels.Watched);
    }

    [Fact]
    public void The_monitor_shows_what_comes_from_the_show_computer()
    {
        _lighting.IsOnScreen = true;
        _lighting.CurrentPage = Page.Monitor;
        _levels.Levels[4] = 200;

        _lighting.Tick();

        Assert.Equal(200, _lighting.Monitor.Values[4]);
        Assert.Equal("from the show computer", _lighting.Monitor.Source);
    }

    [Fact]
    public void Screens_off_show_do_not_refresh()
    {
        _lighting.CurrentPage = Page.Monitor;
        _levels.Levels[4] = 200;

        _lighting.Tick();

        Assert.Equal(0, _lighting.Monitor.Values[4]);
    }
}
