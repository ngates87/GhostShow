using System.Text.Json.Nodes;
using GhostShow.App.ViewModels;
using GhostShow.Core.Inputs;

namespace GhostShow.App.Client;

/// <summary>
/// The show computer's triggers, from this device: they run only there (docs/plan-shared-view.md, decision 1). The
/// Buttons panel's presses, arming, turning a trigger off and resetting one are sent there; how they stand comes back
/// ("triggers"), and while this device waits for a trigger, the inputs that arrive there ("input").
/// </summary>
sealed class RemoteTriggers(RemoteLink link) : IRemoteTriggers
{
    public void Press(Guid button) => link.Send("press", ("button", button.ToString()));
    public void SetArmed(bool armed) => link.Send("armTriggers", ("on", armed));
    public void SetOn(Guid owner, int index, bool on) => link.Send("triggerOn", ("owner", owner.ToString()), ("index", index), ("on", on));
    public void Reset(Guid owner, int index) => link.Send("resetTrigger", ("owner", owner.ToString()), ("index", index));
    public void SetLearning(bool learning) => link.Send("learning", ("on", learning));

    /// <summary>A "triggers" message, as the panel shows it.</summary>
    public static TriggerStates Read(JsonObject message) => new(
        message["armed"]?.GetValue<bool>() ?? true,
        message["status"]?.GetValue<string>() ?? "",
        message["serialListening"]?.GetValue<bool>() == true,
        [.. (message["triggers"] as JsonArray ?? []).OfType<JsonObject>().Select(t => new TriggerState(
            Guid.Parse(t["owner"]!.GetValue<string>()), t["index"]!.GetValue<int>(), t["on"]?.GetValue<bool>() ?? true,
            t["spent"]?.GetValue<bool>() == true, t["state"]?.GetValue<string>() ?? ""))]);

    /// <summary>
    /// An "input" message: learned if the panel is waiting for a trigger, and otherwise shown in the input monitor with
    /// what came of it on the show computer.
    /// </summary>
    public static void OnInput(ButtonsViewModel buttons, JsonObject message)
    {
        var device = message["device"]?.GetValue<string>() is { } id ? Guid.Parse(id) : (Guid?)null;
        string source = message["source"]!.GetValue<string>(), address = message["address"]!.GetValue<string>();
        bool pressed = message["pressed"]?.GetValue<bool>() != false;
        string? value = message["value"]?.GetValue<string>();
        buttons.ShowInput(new InputEvent(source, address, pressed, DateTimeOffset.Now, device, value), message["outcome"]?.GetValue<string>() ?? "");
    }
}
