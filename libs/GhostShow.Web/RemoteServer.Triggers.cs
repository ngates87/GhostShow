using System.Text.Json.Nodes;
using GhostShow.Core.Inputs;
using GhostShow.Core.Storage;
using Microsoft.Extensions.Logging;

namespace GhostShow.Web;

// The Buttons panel's triggers and the Inputs screen on a device. Triggers run only here on the show computer
// (docs/plan-shared-view.md, decision 1): devices are sent how they stand ("triggers", a few times a second when that
// changes), arm them, turn one off or reset one; and a device waiting for a trigger, or watching the inputs, is sent the
// inputs that arrive ("input"). The serial ports are here too: a device watching gets their state ("inputs") and puts the
// show's input devices on them.
public sealed partial class RemoteServer
{
    static readonly HashSet<string> TriggerActions = ["armTriggers", "triggerOn", "resetTrigger", "learning", "watchInputs", "refreshPorts", "pickPort", "enableInput"];

    /// <summary>The trigger states last sent (null: send them again, as after a hello).</summary>
    volatile string? _triggersSent;

    /// <summary>On the show's thread: a command from a device's Buttons panel. Returns why it couldn't be done, or null.</summary>
    string? TriggerCommand(RemoteConnection connection, string action, JsonObject message)
    {
        var triggers = _host.Triggers;
        Guid Owner() => Guid.Parse(message["owner"]?.GetValue<string>() ?? throw new FormatException("No owner."));
        Guid Device() => Guid.Parse(message["device"]?.GetValue<string>() ?? throw new FormatException("No device."));
        int Index() => message["index"]?.GetValue<int>() ?? throw new FormatException("No index.");
        bool On() => message["on"]?.GetValue<bool>() ?? throw new FormatException("No on.");
        string? error = action switch
        {
            "armTriggers" => triggers.SetArmed(On()),
            "triggerOn" => triggers.SetOn(Owner(), Index(), On()),
            "resetTrigger" => triggers.Reset(Owner(), Index()),
            "learning" => SetLearning(connection, On()),
            "watchInputs" => WatchInputs(connection, On()),
            "refreshPorts" => RefreshPorts(),
            "pickPort" => _host.Inputs.PickPort(Device(), message["port"]?.GetValue<string>()),
            "enableInput" => _host.Inputs.SetEnabled(Device(), On()),
            _ => $"Unknown command {action}.",
        };
        _triggersSent = null;   // the change shows on every device at the next tick
        return error;
    }

    /// <summary>A device is waiting for a trigger, or stopped: while any is, the show's inputs go to it to learn.</summary>
    string? SetLearning(RemoteConnection connection, bool on)
    {
        connection.Learning = on;
        UpdateLearning();
        return null;
    }

    /// <summary>On the show's thread: whether any device is learning a trigger.</summary>
    void UpdateLearning()
    {
        bool any;
        lock (_lock) any = _connections.Any(c => c.Learning);
        _host.Triggers.SetDeviceLearning(any);
    }

    /// <summary>A device went: if it was learning, it isn't any more. Not waited for (the server may be stopping, on the show's thread).</summary>
    void ForgetLearning(RemoteConnection connection)
    {
        if (!connection.Learning) return;
        _ = _host.InvokeAsync(() =>
        {
            UpdateLearning();
            return true;
        });
    }

    /// <summary>On the show's thread: an input arrived; devices learning a trigger or showing the input monitor get it.</summary>
    void OnInputReceived(InputEvent input, string outcome)
    {
        List<RemoteConnection> learning;
        lock (_lock) learning = _connections.Where(c => c.Learning || c.WatchingInputs).ToList();
        if (learning.Count == 0) return;
        string json = new JsonObject
        {
            ["type"] = "input",
            ["source"] = input.Source,
            ["device"] = input.Input?.ToString(),
            ["address"] = input.Address,
            ["pressed"] = input.IsPressed,
            ["value"] = input.Value,
            ["outcome"] = outcome,
        }.ToJsonString();
        foreach (var connection in learning) connection.Send(json);
    }

    // ---- the Inputs screen: the show computer's serial ports ----

    /// <summary>The Inputs screen or input monitor opened or closed on a device: it gets the ports' state and every input while it's open.</summary>
    string? WatchInputs(RemoteConnection connection, bool on)
    {
        connection.WatchingInputs = on;
        if (on) connection.Send(InputsJson());
        return null;
    }

    string? RefreshPorts()
    {
        _host.Inputs.RefreshPorts();
        return null;
    }

    string InputsJson() => new JsonObject { ["type"] = "inputs", ["state"] = ShowJson.ToNode(_host.Inputs.State) }.ToJsonString();

    /// <summary>On the show's thread: devices showing the Inputs screen get the ports' new state.</summary>
    void OnInputsChanged()
    {
        List<RemoteConnection> watching;
        lock (_lock) watching = _connections.Where(c => c.WatchingInputs).ToList();
        if (watching.Count == 0) return;
        string json = InputsJson();
        foreach (var connection in watching) connection.Send(json);
    }

    /// <summary>From the status loop: every device gets the trigger states when they've changed.</summary>
    async Task SendTriggersIfChangedAsync()
    {
        List<RemoteConnection> connections;
        lock (_lock) connections = [.. _connections];
        if (connections.Count == 0) return;
        string json;
        try
        {
            json = await _host.InvokeAsync(TriggersJson);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Couldn't read the triggers for the web remote.");
            return;
        }
        if (json == _triggersSent) return;
        _triggersSent = json;
        foreach (var connection in connections) connection.Send(json);
    }

    string TriggersJson()
    {
        var states = _host.Triggers.States;
        return new JsonObject
        {
            ["type"] = "triggers",
            ["armed"] = states.Armed,
            ["status"] = states.Status,
            ["serialListening"] = states.SerialListening,
            ["triggers"] = new JsonArray([.. states.Triggers.Select(t => new JsonObject
            {
                ["owner"] = t.Owner.ToString(),
                ["index"] = t.Index,
                ["on"] = t.IsOn,
                ["spent"] = t.IsSpent,
                ["state"] = t.State,
            })]),
        }.ToJsonString();
    }
}
