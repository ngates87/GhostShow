using GhostShow.Core.Inputs;
using GhostShow.Core.Model;

namespace GhostShow.Core.Storage;

/// <summary>
/// Before show version 8, each computer had one serial port, and a serial trigger was just a command. From 8, the show
/// lists its input devices and a trigger names the one it listens to. Opening an older show with serial triggers gives
/// it one serial device, "Serial input", for them all; the app then puts it on the port this computer used to listen on
/// (<see cref="MachineSettings.SerialInput"/>), so the show works as it did.
/// </summary>
public static class LegacySerialTriggers
{
    public const string DeviceName = "Serial input";

    public static void Apply(Project project)
    {
        var triggers = project.Buttons.SelectMany(b => b.Triggers).Where(t => t.Source == ButtonTrigger.Serial && t.Input is null).ToList();
        if (triggers.Count == 0) return;
        var device = project.Inputs.OfType<SerialInputDevice>().FirstOrDefault();
        if (device is null) project.Inputs.Add(device = new SerialInputDevice { Name = DeviceName });
        foreach (var trigger in triggers) trigger.Input = device.Id;
    }
}
