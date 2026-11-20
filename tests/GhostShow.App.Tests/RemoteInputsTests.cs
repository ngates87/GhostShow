using GhostShow.App.Controls;
using GhostShow.App.ViewModels;
using GhostShow.Core.Editing;
using GhostShow.Core.Inputs;
using GhostShow.Core.Model;

namespace GhostShow.App.Tests;

/// <summary>
/// The Inputs screen on a device: the ports are the show computer's, so what it shows comes from there
/// (<see cref="IInputSetup"/>), and picking a port or adding a device asks it.
/// </summary>
public class RemoteInputsTests : IDisposable
{
    readonly TimelineRig _rig = new();
    readonly ShowComputer _showComputer = new();
    readonly ButtonsViewModel _buttons;

    /// <summary>The show computer's ports, as a device hears of them: records what it was asked.</summary>
    sealed class ShowComputer : IInputSetup
    {
        public readonly List<string> Calls = [];
        public InputSetupState State { get; private set; } = InputSetupState.Empty;
        public event Action? Changed;

        public void Says(InputSetupState state)
        {
            State = state;
            Changed?.Invoke();
        }

        public void RefreshPorts() => Calls.Add("refresh");

        public string? PickPort(Guid device, string? port)
        {
            Calls.Add($"pick {port}");
            return null;
        }

        public string? SetEnabled(Guid device, bool enabled)
        {
            Calls.Add($"enable {enabled}");
            return null;
        }
    }

    public RemoteInputsTests() => _buttons = new ButtonsViewModel(_rig.Session, _rig.Playback, () => { }, () => { });

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void A_device_shows_the_show_computer_s_ports_and_asks_it_to_use_them()
    {
        var screen = new InputsViewModel(_rig.Session, _showComputer, _buttons);
        var box = new SerialInputDevice { Name = "Ethereal FX" };
        _rig.Session.Document.Record("Add input", () => _rig.Session.Project.Inputs.Add(box), [ShowDocument.Shallow(_rig.Session.Project)]);
        var row = screen.Devices.Single();
        Assert.Equal("", row.StatusLabel);   // nothing heard from the show computer yet
        Assert.Empty(_showComputer.Calls);

        var at = DateTimeOffset.Now.AddSeconds(-4);
        _showComputer.Says(new InputSetupState("", [new FoundPort("COM4", "COM4 · Ethereal FX QC-24", "Ethereal FX", "Ethereal FX QC-24", true)],
            [new InputDeviceSetup(box.Id, "COM4", "A1B2", true, "Ethereal FX QC-24", true,
                [new SerialPortChoice("None", null), new SerialPortChoice("COM4 · Ethereal FX QC-24", "COM4"), new SerialPortChoice("COM5", "COM5")],
                "Listening on COM4 at 9600 baud.", false, true, InputHealth.Listening, "BTN24", at)]));

        Assert.Equal("In use by Ethereal FX", screen.Found.Single().Note);
        Assert.Equal(("Listening on COM4 at 9600 baud.", DeviceState.Healthy, "USB Serial Input · COM4 · 9600 baud"), (row.StatusLabel, row.State, row.Summary));
        Assert.Equal("COM4", row.SelectedPort?.Port);
        Assert.StartsWith("Last: “BTN24” 4 s ago", row.LastReceived);
        Assert.True(_buttons.IsSerialListening);
        Assert.Empty(_showComputer.Calls);   // showing it isn't picking it

        row.SelectedPort = row.Ports.Single(p => p.Port == "COM5");
        row.IsEnabled = false;
        screen.RefreshPortsCommand.Execute(null);
        Assert.Equal(["pick COM5", "enable False", "refresh"], _showComputer.Calls);
    }

    [Fact]
    public void Adding_a_port_puts_the_new_device_on_it_before_it_joins_the_show()
    {
        var screen = new InputsViewModel(_rig.Session, _showComputer, _buttons);
        _showComputer.Says(new InputSetupState("", [new FoundPort("COM4", "COM4", null)], []));

        screen.AddCommand.Execute(screen.Found.Single());

        Assert.Equal(["pick COM4"], _showComputer.Calls);
        Assert.Equal("COM4", _rig.Session.Project.Inputs.Single().Name);
    }
}
