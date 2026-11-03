using System.IO.Ports;

namespace GhostShow.Dmx.Devices;

/// <summary>Opens serial ports and turns the many ways that fails into one DmxDeviceException with a useful message.</summary>
static class SerialPortOpener
{
    public static SerialPort Open(string portName, int baudRate, StopBits stopBits)
    {
        var port = new SerialPort(portName, baudRate, Parity.None, 8, stopBits) { WriteTimeout = 500 };
        try
        {
            port.Open();
            return port;
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            string hint = OperatingSystem.IsLinux() ? " On Linux, check you are in the 'dialout' group." : "";
            throw new DmxDeviceException($"{portName} is in use by another program, or access was denied.{hint}", ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            throw new DmxDeviceException($"Couldn't open {portName}: {ex.Message}", ex);
        }
    }

    /// <summary>Exceptions a write to an open port throws when the device is unplugged or stalls.</summary>
    public static bool IsDeviceFailure(Exception ex) =>
        ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException;

    public static void CloseQuietly(SerialPort? port)
    {
        if (port is null) return;
        try
        {
            port.Close();
        }
        catch (IOException)
        {
            // The device is already gone (unplugged); there is nothing left to close cleanly.
        }
        port.Dispose();
    }
}
