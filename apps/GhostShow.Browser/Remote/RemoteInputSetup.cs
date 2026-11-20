using System.Text.Json.Nodes;
using GhostShow.Core.Inputs;
using GhostShow.Core.Storage;

namespace GhostShow.Browser.Remote;

/// <summary>
/// The show computer's serial ports, from a device's Inputs screen: its state arrives as "inputs" messages (sent while
/// this device watches, which it does from each hello), and picking a port, turning a device off and looking again are
/// asked of the show computer. A device can only pick from the ports the show computer offered.
/// </summary>
sealed class RemoteInputSetup(RemoteLink link) : IInputSetup
{
    public InputSetupState State { get; private set; } = InputSetupState.Empty;

    public event Action? Changed;

    /// <summary>After each hello: the ports' state and every input, from now on.</summary>
    public void OnHello() => link.Send("watchInputs", ("on", true));

    public void OnInputs(JsonObject message)
    {
        if (message["state"] is not { } node || ShowJson.FromNode<InputSetupState>(node) is not { } state) return;
        if (state.SameAs(State)) return;
        State = state;
        Changed?.Invoke();
    }

    public void RefreshPorts() => link.Send("refreshPorts");

    public string? PickPort(Guid device, string? port)
    {
        link.Send("pickPort", ("device", device.ToString()), ("port", port));
        return null;
    }

    public string? SetEnabled(Guid device, bool enabled)
    {
        link.Send("enableInput", ("device", device.ToString()), ("on", enabled));
        return null;
    }
}
