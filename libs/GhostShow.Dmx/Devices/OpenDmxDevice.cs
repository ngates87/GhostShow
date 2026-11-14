using System.Diagnostics;
using System.IO.Ports;
using GhostShow.Core.Timing;
using GhostShow.Dmx.Discovery;

namespace GhostShow.Dmx.Devices;

/// <summary>
/// An "Open DMX" adapter: a bare FTDI FT232R with an RS-485 driver (Enttec Open DMX USB and most cheap USB-DMX
/// cables). The chip has no processor or frame memory, so the computer generates every frame, continuously; if
/// frames stop, fixtures see DMX loss and do whatever they're set to (hold last look or go dark).
///
/// Frame timing: SendFrame blocks until the frame has left the chip (~23 ms for 513 bytes at 250 kbaud), which
/// caps output at ~40 frames/s. Measured on an FT232R under Linux: 37 frames/s.
///
/// The DMX break, on Linux and macOS, is made by dropping to 90 kbaud and sending one 0x00 byte: 9 low bits = 100 µs
/// break, then the two stop bits are the mark-after-break. SerialPort.BreakState can't be used there: on Linux .NET
/// implements it with tcsendbreak, which holds the line low for ~250 ms (measured: under 4 frames/s). That relies on
/// Flush waiting until the bytes have left the chip, which tcdrain does.
///
/// On Windows Flush doesn't wait that long: the FTDI driver finishes once the bytes are in the chip's buffer, so the
/// switch back to 250 kbaud lands while the break byte is still going out and garbles it, and fixtures ignore every
/// frame. There BreakState is used instead (SetCommBreak, which holds the line low until cleared), once the last
/// frame has had time to leave the chip, worked out from its length at 250 kbaud.
///
/// Identity: the adapter is found by the FTDI chip's USB serial number when one is configured, so it keeps working
/// after being plugged into a different USB socket, even mid-show.
/// </summary>
public sealed class OpenDmxDevice : IDmxOutputDevice
{
    const int DmxBaud = 250_000;

    readonly string _configuredPort;
    readonly string? _usbSerial;
    readonly OpenDmxFramer _framer = new(useBreakSignal: OperatingSystem.IsWindows());
    SerialPort? _port;
    SerialPortLine? _line;

    public OpenDmxDevice(string port, string? usbSerial)
    {
        _configuredPort = port;
        _usbSerial = usbSerial;
        Info = new DmxDeviceInfo("Open DMX", "FTDI USB-DMX adapter", port, usbSerial);
    }

    public DmxDeviceInfo Info { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        string port = FindPort();
        _port = SerialPortOpener.Open(port, DmxBaud, StopBits.Two);
        _line = new SerialPortLine(_port);
        Info = Info with { Port = port };
    }, cancellationToken);

    public Task DisconnectAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    public void SendFrame(ReadOnlySpan<byte> channels)
    {
        if (_port is null || _line is null) throw new DmxDeviceException("Open DMX adapter is not connected.");
        try
        {
            _framer.Send(_line, channels);
        }
        catch (Exception ex) when (SerialPortOpener.IsDeviceFailure(ex))
        {
            throw new DmxDeviceException($"Lost the Open DMX adapter on {_port.PortName}: {ex.Message}", ex);
        }
    }

    string FindPort()
    {
        if (_usbSerial is null) return _configuredPort;
        return UsbSerialPorts.FindPortByUsbSerial(_usbSerial)
               ?? throw new DmxDeviceException($"Open DMX adapter S/N {_usbSerial} is not plugged in.");
    }

    public void Dispose() => Close();

    void Close()
    {
        SerialPortOpener.CloseQuietly(_port);
        _port = null;
        _line = null;
    }
}

/// <summary>The few serial port calls Open DMX framing makes, so tests can see what reaches the line, and when.</summary>
interface ISerialLine
{
    int BaudRate { set; }
    /// <summary>True holds the line low (a break) until set false again.</summary>
    bool BreakState { set; }
    void Write(byte[] buffer, int offset, int count);
    /// <summary>Waits for written bytes to go: until they've left the chip on Linux, only until they reach it on Windows.</summary>
    void Flush();
}

sealed class SerialPortLine(SerialPort port) : ISerialLine
{
    public int BaudRate { set => port.BaudRate = value; }
    public bool BreakState { set => port.BreakState = value; }
    public void Write(byte[] buffer, int offset, int count) => port.Write(buffer, offset, count);
    public void Flush() => port.BaseStream.Flush();
}

/// <summary>
/// Makes each DMX frame on the line: a break, the mark-after-break, then the start code and 512 channels at 250 kbaud,
/// 8N2. The two ways of making the break, and why each OS gets its own, are in <see cref="OpenDmxDevice"/>'s comment.
/// </summary>
sealed class OpenDmxFramer(bool useBreakSignal)
{
    public const int DmxBaud = 250_000, BreakBaud = 90_000;

    /// <summary>A frame's time on the wire: 513 bytes of 11 bits (start, 8 data, 2 stop) at 250 kbaud, about 22.6 ms.</summary>
    public static readonly TimeSpan FrameWireTime = TimeSpan.FromSeconds(513 * 11 / (double)DmxBaud);

    /// <summary>Allowed on top of <see cref="FrameWireTime"/> for the bytes to get through USB to the chip.</summary>
    public static readonly TimeSpan UsbLatency = TimeSpan.FromMilliseconds(3);

    /// <summary>The least a break signal is held for; DMX needs at least 88 µs.</summary>
    public static readonly TimeSpan BreakTime = TimeSpan.FromMicroseconds(150);

    static readonly ManualResetEventSlim NeverStop = new();
    readonly byte[] _frame = new byte[513];  // [0] is the DMX start code, always 0
    readonly byte[] _breakByte = [0];
    long _lineFreeAt;                        // break signal: when the last frame will have left the chip

    public void Send(ISerialLine line, ReadOnlySpan<byte> channels)
    {
        int n = Math.Min(channels.Length, 512);
        channels[..n].CopyTo(_frame.AsSpan(1));
        _frame.AsSpan(1 + n).Clear();

        if (useBreakSignal) SendWithBreakSignal(line);
        else SendWithSlowZero(line);
    }

    /// <summary>Linux and macOS: a 0x00 at 90 kbaud is 100 µs low, and its stop bits the mark-after-break.</summary>
    void SendWithSlowZero(ISerialLine line)
    {
        // Flush returns once the bytes have left the chip; changing baud before that would garble them.
        line.BaudRate = BreakBaud;
        line.Write(_breakByte, 0, 1);
        line.Flush();
        line.BaudRate = DmxBaud;
        line.Write(_frame, 0, _frame.Length);
        line.Flush();
    }

    /// <summary>Windows: Flush doesn't wait for the wire, so the last frame's wire time is waited out instead.</summary>
    void SendWithBreakSignal(ISerialLine line)
    {
        PreciseDelay.WaitUntil(_lineFreeAt, NeverStop);    // a break now would cut off the end of the last frame
        line.BreakState = true;
        PreciseDelay.WaitUntil(Stopwatch.GetTimestamp() + Ticks(BreakTime), NeverStop);
        line.BreakState = false;                            // the mark-after-break lasts until Write's bytes start
        long writing = Stopwatch.GetTimestamp();
        line.Write(_frame, 0, _frame.Length);
        line.Flush();
        _lineFreeAt = writing + Ticks(FrameWireTime + UsbLatency);
    }

    static long Ticks(TimeSpan time) => (long)(time.TotalSeconds * Stopwatch.Frequency);
}
