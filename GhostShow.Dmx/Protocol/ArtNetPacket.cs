namespace GhostShow.Dmx.Protocol;

/// <summary>ArtDMX packets (Art-Net 4, protocol version 14).</summary>
public static class ArtNetPacket
{
    public const int HeaderLength = 18;
    public const int MaxLength = HeaderLength + 512;

    /// <summary>Builds an ArtDMX packet. Sequence 0 means "disabled", so use 1-255.</summary>
    public static byte[] Build(int universe, byte sequence, ReadOnlySpan<byte> data)
    {
        var packet = new byte[MaxLength];
        int length = Write(packet, universe, sequence, data);
        return packet[..length];
    }

    /// <summary>Writes the packet into destination (at least <see cref="MaxLength"/> bytes); returns its length.</summary>
    public static int Write(Span<byte> destination, int universe, byte sequence, ReadOnlySpan<byte> data)
    {
        int n = Math.Min(data.Length, 512);
        int length = n % 2 == 0 ? n : n + 1;           // Art-Net requires an even data length
        "Art-Net\0"u8.CopyTo(destination);
        destination[8] = 0x00; destination[9] = 0x50;  // OpOutput / ArtDMX, little-endian
        destination[10] = 0; destination[11] = 14;     // protocol version 14
        destination[12] = sequence;
        destination[13] = 0;                           // physical port
        destination[14] = (byte)(universe & 0xFF);     // SubUni
        destination[15] = (byte)((universe >> 8) & 0x7F); // Net
        destination[16] = (byte)(length >> 8);
        destination[17] = (byte)length;
        data[..n].CopyTo(destination[HeaderLength..]);
        if (length != n) destination[HeaderLength + n] = 0;
        return HeaderLength + length;
    }
}
