using System.Net;
using System.Net.Sockets;
using GhostShow.Dmx.Protocol;

namespace GhostShow.Dmx.Devices;

/// <summary>
/// Art-Net over UDP, to one node (unicast) or everyone on the subnet (broadcast). Carried over from the earlier
/// prototype; not the Phase 1 hardware target and not tested against a real node. Art-Net nodes typically hold the
/// last frame for a few seconds after packets stop, then follow their own loss setting.
/// </summary>
public sealed class ArtNetDevice : IDmxOutputDevice
{
    readonly string? _address;
    readonly int _port;
    IPEndPoint? _endpoint;
    readonly int _artNetUniverse;
    readonly byte[] _packet = new byte[ArtNetPacket.MaxLength];
    UdpClient? _udp;
    byte _sequence;

    /// <param name="address">Node IP for unicast; null broadcasts.</param>
    public ArtNetDevice(string? address, int artNetUniverse, int port = 6454)
    {
        _address = address;
        _artNetUniverse = artNetUniverse;
        _port = port;
        Info = new DmxDeviceInfo("Art-Net", $"{address ?? "broadcast"}, Art-Net universe {artNetUniverse}");
    }

    public DmxDeviceInfo Info { get; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IPAddress target = IPAddress.Broadcast;
        if (_address is not null && !IPAddress.TryParse(_address, out target!))
            throw new DmxDeviceException($"'{_address}' is not an IP address.");
        _endpoint = new IPEndPoint(target, _port);
        try
        {
            _udp = new UdpClient { EnableBroadcast = true };
        }
        catch (SocketException ex)
        {
            throw new DmxDeviceException($"Couldn't open a UDP socket: {ex.Message}", ex);
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    public void SendFrame(ReadOnlySpan<byte> channels)
    {
        if (_udp is not { } udp || _endpoint is null) throw new DmxDeviceException("Art-Net output is not connected.");
        _sequence = (byte)(_sequence >= 255 ? 1 : _sequence + 1);   // 0 means "sequencing disabled"
        int length = ArtNetPacket.Write(_packet, _artNetUniverse, _sequence, channels);
        try
        {
            udp.Send(_packet.AsSpan(0, length), _endpoint);
        }
        catch (SocketException ex)
        {
            throw new DmxDeviceException($"Art-Net send to {_endpoint} failed: {ex.Message}", ex);
        }
    }

    public void Dispose() => Close();

    void Close()
    {
        _udp?.Dispose();
        _udp = null;
    }
}
