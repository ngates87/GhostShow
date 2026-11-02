using System.IO.Ports;
using GhostShow.Dmx.Discovery;
using GhostShow.Dmx.Protocol;

namespace GhostShow.Dmx.Devices;

/// <summary>
/// An Enttec DMX USB Pro-compatible adapter (also DMXKing ultraDMX and most clones). The adapter has its own
/// processor: it generates the DMX timing and keeps repeating the last frame it was given, so a stalled computer
/// doesn't cause DMX loss, but a crashed one leaves the last look on stage until the adapter loses power.
/// Not yet tested with real hardware.
/// </summary>
public sealed class UsbProDevice : IDmxOutputDevice
{
    readonly string _configuredPort;
    readonly string? _widgetSerial;
    readonly byte[] _packet = new byte[EnttecProPacket.MaxDmxPacketLength];
    SerialPort? _port;

    public UsbProDevice(string port, string? widgetSerial)
    {
        _configuredPort = port;
        _widgetSerial = widgetSerial;
        Info = new DmxDeviceInfo("USB Pro", "Enttec Pro-compatible adapter", port, widgetSerial);
    }

    public DmxDeviceInfo Info { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        // Finding a moved adapter means probing ports, which takes up to ~0.5 s each.
        string port = DmxAdapterDetector.ResolvePort(DmxAdapterKind.UsbPro, _configuredPort, _widgetSerial)
                      ?? throw new DmxDeviceException($"USB Pro adapter S/N {_widgetSerial} is not plugged in.");
        // The baud rate is ignored (it's a virtual serial port), but 57600 is the documented setting.
        _port = SerialPortOpener.Open(port, 57600, StopBits.One);
        Info = Info with { Port = port };
    }, cancellationToken);

    public Task DisconnectAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    public void SendFrame(ReadOnlySpan<byte> channels)
    {
        var port = _port ?? throw new DmxDeviceException("USB Pro adapter is not connected.");
        int length = EnttecProPacket.WriteDmx(_packet, channels);
        try
        {
            port.Write(_packet, 0, length);
        }
        catch (Exception ex) when (SerialPortOpener.IsDeviceFailure(ex))
        {
            throw new DmxDeviceException($"Lost the USB Pro adapter on {port.PortName}: {ex.Message}", ex);
        }
    }

    public void Dispose() => Close();

    void Close()
    {
        SerialPortOpener.CloseQuietly(_port);
        _port = null;
    }
}
