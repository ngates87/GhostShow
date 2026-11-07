using System.IO.Ports;

namespace GhostShow.Inputs;

/// <summary>
/// An open serial connection, read a little at a time. An interface so tests can stand in for a real port.
/// </summary>
public interface ISerialLink : IDisposable
{
    /// <summary>
    /// Waits briefly for bytes. Returns how many arrived, or 0 if none came within the wait (the line is quiet).
    /// Throws IOException (or InvalidOperationException) when the device has gone, e.g. it was unplugged.
    /// </summary>
    int Read(byte[] buffer);
}

/// <summary>A real serial port: 8 data bits, no parity, 1 stop bit, no flow control (what nearly every device uses).</summary>
public sealed class SerialPortLink : ISerialLink
{
    /// <summary>How long a read waits before saying the line is quiet; a line without an ending is taken after this.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromMilliseconds(200);

    readonly SerialPort _port;

    /// <summary>Opens the port. Throws if it isn't there (FileNotFoundException, IOException) or is in use (UnauthorizedAccessException).</summary>
    public SerialPortLink(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One) { ReadTimeout = (int)QuietTime.TotalMilliseconds };
        _port.Open();
    }

    public int Read(byte[] buffer)
    {
        try
        {
            return _port.Read(buffer, 0, buffer.Length);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        try
        {
            _port.Dispose();
        }
        catch (IOException)
        {
            // Already gone (unplugged): nothing left to close.
        }
    }

    /// <summary>
    /// The serial ports a device could be on. Linux also lists 32 built-in UARTs (ttyS*) that are almost never wired to
    /// anything, so only USB ones are offered there; macOS lists each port twice, and only /dev/cu.* opens without waiting.
    /// </summary>
    public static IReadOnlyList<string> PortNames()
    {
        var names = SerialPort.GetPortNames().Distinct();
        if (OperatingSystem.IsLinux()) names = names.Where(p => p.StartsWith("/dev/ttyUSB", StringComparison.Ordinal) || p.StartsWith("/dev/ttyACM", StringComparison.Ordinal));
        if (OperatingSystem.IsMacOS()) names = names.Where(p => !p.StartsWith("/dev/tty.", StringComparison.Ordinal));
        // By length first, so COM2 comes before COM10.
        return names.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
