using GhostShow.Core.Inputs;
using GhostShow.Server;

namespace GhostShow.App.ViewModels;

/// <summary>
/// The show computer's own Buttons panel's way to the triggers: the server's (<see cref="ShowTriggers"/>), in-process.
/// The panel shows them as a device's does, only sooner: how they stand after each change, and each input as it arrives.
/// </summary>
public sealed class LocalTriggers(ShowTriggers triggers) : IRemoteTriggers
{
    public ShowTriggers Triggers => triggers;

    public void Press(Guid button) => triggers.Press(button);
    public void SetArmed(bool armed) => triggers.SetArmed(armed);
    public void SetOn(Guid owner, int index, bool on) => triggers.SetOn(owner, index, on);
    public void Reset(Guid owner, int index) => triggers.Reset(owner, index);
    public void SetLearning(bool learning) => triggers.SetLearning(this, learning);
    public Guid? Input(InputEvent input) => triggers.Input(input);

    public void Attach(ButtonsViewModel panel)
    {
        triggers.Changed += () => panel.ShowStates(triggers.States);
        triggers.InputReceived += panel.ShowInput;
        panel.ShowStates(triggers.States);
    }
}
