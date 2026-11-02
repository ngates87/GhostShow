using System.Text.Json.Nodes;
using GhostShow.Core.Fixtures;
using GhostShow.Core.Storage;
using Microsoft.Extensions.Logging;

namespace GhostShow.Web;

// The Lighting screens on a device: the DMX setup screen (the hardware itself stays here on the show computer: the device
// only sees what was found, and picks from what's offered) and the Control, Channels and Monitor screens' live levels.
public sealed partial class RemoteServer
{
    static readonly HashSet<string> LightingActions =
    [
        "watchOutputs", "scanOutputs", "assignOutput",
        "watchLevels", "setChannel", "clearUniverse", "setFixtureChannel", "setFixtureControl",
    ];

    /// <summary>Sent many times a second while a fader moves, so they aren't logged.</summary>
    static readonly HashSet<string> QuietActions = ["watchLevels", "setChannel", "setFixtureChannel", "setFixtureControl"];

    /// <summary>On the show's thread: a command from a device's Lighting screens. Returns why it couldn't be done, or null.</summary>
    string? LightingCommand(RemoteConnection connection, string action, JsonObject message)
    {
        int Number(string name) => message[name]?.GetValue<int>() ?? throw new FormatException($"No {name}.");
        var levels = _host.Levels;
        switch (action)
        {
            case "watchOutputs":
                bool on = message["on"]?.GetValue<bool>() == true;
                connection.WatchingOutputs = on;
                _host.Outputs.SetWatching(connection, on);
                if (on) connection.Send(OutputsJson());
                return null;

            case "scanOutputs":
                _ = _host.Outputs.ScanAgainAsync();   // takes a moment, and Blackout mustn't wait behind it: the result arrives as "outputs"
                return null;

            case "assignOutput":
                var assigning = _host.Outputs.AssignAsync(Number("universe"), message["choice"]?.GetValue<string>() ?? throw new FormatException("No choice."));
                return assigning.IsCompleted ? assigning.Result : null;   // a refusal is immediate; restarting an output isn't, and Blackout mustn't wait behind it

            case "watchLevels":
                connection.LevelsSent = null;
                connection.WatchingLevels = message["universe"]?.GetValue<int>() ?? 0;
                return null;

            case "setChannel": return levels.SetChannel(Number("universe"), Number("address"), Number("value")).Error;
            case "clearUniverse": return levels.ClearUniverse(Number("universe")).Error;
            case "setFixtureChannel": return levels.SetFixtureChannel(FixtureId(message), Number("channel"), Number("value")).Error;
            case "setFixtureControl":
                var function = Enum.Parse<ChannelFunction>(message["function"]?.GetValue<string>() ?? throw new FormatException("No function."));
                return levels.SetFixtureControl(FixtureId(message), function, Number("value")).Error;
        }
        return $"Unknown command {action}.";
    }

    static Guid FixtureId(JsonObject message) => Guid.Parse(message["fixture"]?.GetValue<string>() ?? throw new FormatException("No fixture."));

    /// <summary>A device went: its DMX setup screen isn't open any more. Not waited for: the server may be stopping, on the show's thread.</summary>
    void ForgetLightingWatch(RemoteConnection connection)
    {
        _ = _host.InvokeAsync(() =>
        {
            _host.Outputs.SetWatching(connection, false);
            return true;
        });
    }

    string OutputsJson() => new JsonObject { ["type"] = "outputs", ["state"] = ShowJson.ToNode(_host.Outputs.State) }.ToJsonString();

    /// <summary>On the show's thread: devices with a DMX setup screen open get the hardware's new state.</summary>
    void OnOutputsChanged()
    {
        List<RemoteConnection> watching;
        lock (_lock) watching = _connections.Where(c => c.WatchingOutputs).ToList();
        if (watching.Count == 0) return;
        string json = OutputsJson();
        foreach (var connection in watching) connection.Send(json);
    }

    // ---- live levels ----

    /// <summary>Up to 20 times a second: a device showing a universe's levels gets them when they change. A slow device gets only the newest.</summary>
    async Task LevelsLoopAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(StatusInterval);
        while (await timer.WaitForNextTickAsync(stopping))
        {
            List<RemoteConnection> watching;
            lock (_lock) watching = _connections.Where(c => c.WatchingLevels != 0).ToList();
            foreach (var group in watching.GroupBy(c => c.WatchingLevels))
            {
                try
                {
                    string json = await _host.InvokeAsync(() => LevelsJson(group.Key));   // on the show's thread: it looks through the running outputs
                    foreach (var connection in group)
                    {
                        if (json == connection.LevelsSent) continue;
                        connection.LevelsSent = json;
                        connection.SetLevels(json);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Couldn't read universe {Universe}'s levels for the web remote.", group.Key);
                }
            }
        }
    }

    /// <summary>A universe's levels now, which channels the timeline controls, and what its output last sent: 512 bytes each, as base64.</summary>
    string LevelsJson(int universe)
    {
        var levels = new byte[512];
        var controlled = new bool[512];
        var sent = new byte[512];
        bool known = _host.Levels.TryCopyLevels(universe, levels);
        _host.Levels.CopyTimelineControlled(universe, controlled);
        string source = _host.Levels.CopyAsSent(universe, sent);
        return new JsonObject
        {
            ["type"] = "levels",
            ["universe"] = universe,
            ["levels"] = known ? Convert.ToBase64String(levels) : null,
            ["controlled"] = Convert.ToBase64String(Array.ConvertAll(controlled, c => (byte)(c ? 1 : 0))),
            ["sent"] = Convert.ToBase64String(sent),
            ["source"] = source,
        }.ToJsonString();
    }
}
