namespace GhostShow.Dmx.Protocol;

/// <summary>
/// Messages of the Enttec DMX USB Pro API: 0x7E, label, length LSB, length MSB, payload, 0xE7.
/// Also spoken by DMXKing ultraDMX and most "Pro" clones.
/// </summary>
public static class EnttecProPacket
{
    public const byte GetWidgetParameters = 3;
    public const byte SendDmx = 6;
    public const byte GetSerialNumber = 10;

    /// <summary>Length of a label 6 message carrying a full universe.</summary>
    public const int MaxDmxPacketLength = 5 + 1 + 512;

    /// <summary>Label 6 "Output Only Send DMX Packet": the payload is the DMX start code (0) then the channels.</summary>
    public static byte[] Build(ReadOnlySpan<byte> dmx)
    {
        var packet = new byte[MaxDmxPacketLength];
        int length = WriteDmx(packet, dmx);
        return packet[..length];
    }

    /// <summary>Writes a label 6 message into destination (at least <see cref="MaxDmxPacketLength"/>); returns its length.</summary>
    public static int WriteDmx(Span<byte> destination, ReadOnlySpan<byte> dmx)
    {
        int n = Math.Min(dmx.Length, 512);
        int payloadLength = n + 1;
        destination[0] = 0x7E;
        destination[1] = SendDmx;
        destination[2] = (byte)(payloadLength & 0xFF);
        destination[3] = (byte)(payloadLength >> 8);
        destination[4] = 0;                 // DMX start code
        dmx[..n].CopyTo(destination[5..]);
        destination[5 + n] = 0xE7;
        return payloadLength + 5;
    }

    /// <summary>Any message.</summary>
    public static byte[] Build(byte label, ReadOnlySpan<byte> payload)
    {
        var p = new byte[payload.Length + 5];
        p[0] = 0x7E;
        p[1] = label;
        p[2] = (byte)(payload.Length & 0xFF);
        p[3] = (byte)(payload.Length >> 8);
        payload.CopyTo(p.AsSpan(4));
        p[^1] = 0xE7;
        return p;
    }

    /// <summary>
    /// Finds the first complete message in buf, skipping any garbage before it.
    /// consumed = bytes the caller can discard (also set when no message is found, to drop leading garbage).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buf, out byte label, out ReadOnlySpan<byte> payload, out int consumed)
    {
        label = 0; payload = default;
        int start = buf.IndexOf((byte)0x7E);
        consumed = start < 0 ? buf.Length : start;
        if (start < 0 || buf.Length - start < 5) return false;
        int len = buf[start + 2] | buf[start + 3] << 8;
        if (len > 600) { consumed = start + 1; return false; }   // not a real header, resync
        int end = start + 4 + len;
        if (end >= buf.Length) return false;
        if (buf[end] != 0xE7) { consumed = start + 1; return false; }
        label = buf[start + 1];
        payload = buf.Slice(start + 4, len);
        consumed = end + 1;
        return true;
    }
}
