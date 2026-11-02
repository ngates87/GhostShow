using System.IO.Ports;
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
/// The DMX break is made by dropping to 90 kbaud and sending one 0x00 byte: 9 low bits = 100 µs break, then the
/// two stop bits are the mark-after-break. SerialPort.BreakState can't be used: on Linux .NET implements it with
/// tcsendbreak, which holds the line low for ~250 ms (measured: under 4 frames/s).
///
/// Identity: the adapter is found by the FTDI chip's USB serial number when one is configured, so it keeps working
/// after being plugged into a different USB socket, even mid-show.
/// </summary>
public sealed class OpenDmxDevice : IDmxOutputDevice
{
    const int DmxBaud = 250_000, BreakBaud = 90_000;

    readonly string _configuredPort;
    readonly string? _usbSerial;
    readonly byte[] _frame = new byte[513];  // [0] is the DMX start code, always 0
    readonly byte[] _breakByte = [0];
    SerialPort? _port;

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
        Info = Info with { Port = port };
    }, cancellationToken);

    public Task DisconnectAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    public void SendFrame(ReadOnlySpan<byte> channels)
    {
        var port = _port ?? throw new DmxDeviceException("Open DMX adapter is not connected.");
        int n = Math.Min(channels.Length, 512);
        channels[..n].CopyTo(_frame.AsSpan(1));
        _frame.AsSpan(1 + n).Clear();

        try
        {
            // Flush returns once the bytes have left the chip; changing baud before that would garble them.
            port.BaudRate = BreakBaud;
            port.Write(_breakByte, 0, 1);
            port.BaseStream.Flush();
            port.BaudRate = DmxBaud;
            port.Write(_frame, 0, _frame.Length);
            port.BaseStream.Flush();
        }
        catch (Exception ex) when (SerialPortOpener.IsDeviceFailure(ex))
        {
            throw new DmxDeviceException($"Lost the Open DMX adapter on {port.PortName}: {ex.Message}", ex);
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
    }
}
